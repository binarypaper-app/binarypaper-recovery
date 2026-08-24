// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;
using BinaryPaper.Recovery;

namespace BinaryPaper.Recovery.Cli;

/// <summary>
/// Runs a conformance suite and reports whether this build matches every expectation.
/// </summary>
/// <remarks>
/// This is how the claim "passes suite X" is made checkable by anyone, including someone who does
/// not trust this tool. It is also how the tool proves it has not drifted from the specification: if
/// the two disagree, the specification is right and this build is the thing that changed.
/// </remarks>
internal static class VerifyVectors
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            throw new UsageException("verify-vectors requires a suite directory.");
        }

        string suiteRoot = args[1];
        bool json = args.Contains("--json");

        string aggregatePath = Path.Combine(suiteRoot, "MANIFEST.json");
        if (!File.Exists(aggregatePath))
        {
            throw new UsageException($"no MANIFEST.json under '{suiteRoot}'.");
        }

        using JsonDocument aggregate = JsonDocument.Parse(File.ReadAllBytes(aggregatePath));
        JsonElement vectors = aggregate.RootElement.GetProperty("vectors");

        var results = new List<VectorResult>();

        foreach (JsonElement entry in vectors.EnumerateArray())
        {
            string id = entry.GetProperty("id").GetString()!;
            string relative = entry.GetProperty("manifest").GetString()!;
            string manifestPath = Path.Combine(suiteRoot, relative.Replace('/', Path.DirectorySeparatorChar));

            // The aggregate hashes every manifest, so an edited expectation is caught rather than
            // believed. A suite that trusts its own manifests proves nothing.
            string actual = Sha256Hex(File.ReadAllBytes(manifestPath));
            if (actual != entry.GetProperty("sha256").GetString())
            {
                results.Add(new VectorResult(id, false, "manifest hash does not match MANIFEST.json"));
                continue;
            }

            try
            {
                VerifyOne(manifestPath);
                results.Add(new VectorResult(id, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new VectorResult(id, false, ex.Message));
            }
        }

        int passed = results.Count(r => r.Passed);
        int failed = results.Count - passed;

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                suite = Path.GetFullPath(suiteRoot),
                total = results.Count,
                passed,
                failed,
                results = results.Select(r => new { id = r.Id, passed = r.Passed, detail = r.Detail })
            }, CommandLine.JsonOptions));
        }
        else
        {
            Console.WriteLine($"Vectors passed: {passed}/{results.Count}");
            foreach (VectorResult result in results.Where(r => !r.Passed))
            {
                Console.Error.WriteLine($"  FAIL {result.Id}: {result.Detail}");
            }
        }

        return failed == 0 ? ExitCodes.Success : 1;
    }

    private static void VerifyOne(string manifestPath)
    {
        string directory = Path.GetDirectoryName(manifestPath)!;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = doc.RootElement;

        var frames = new List<(string, byte[])>();
        foreach (JsonElement input in root.GetProperty("inputs").EnumerateArray())
        {
            string path = input.GetProperty("path").GetString()!;
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, path));

            if (bytes.Length != input.GetProperty("length").GetInt32())
            {
                throw new InvalidOperationException($"input '{path}' length mismatch");
            }

            if (Sha256Hex(bytes) != input.GetProperty("sha256").GetString())
            {
                throw new InvalidOperationException($"input '{path}' hash mismatch");
            }

            if (input.TryGetProperty("role", out JsonElement role) && role.GetString() == "frame")
            {
                frames.Add((path, bytes));
            }
        }

        string? password = root.TryGetProperty("password", out JsonElement pw)
            ? pw.GetProperty("value").GetString()
            : null;

        JsonElement expected = root.GetProperty("expected");
        string expectedResult = expected.GetProperty("result").GetString()!;
        string operation = root.GetProperty("operation").GetString()!;

        if (operation == "inspect")
        {
            VerifyInspect(frames, expected, expectedResult);
            return;
        }

        try
        {
            FrameIngestResult ingest = CapsuleRecovery.Ingest(frames);
            List<ScanSession> sessions = ingest.Sessions.Where(s => s.Reference is not null).ToList();

            if (sessions.Count == 0)
            {
                // Every frame was rejected. The suite expects the first rejection's category.
                RejectedFrame first = ingest.Rejected.FirstOrDefault()
                    ?? throw new InvalidOperationException("no frames and no rejections");
                AssertFailure(expectedResult, expected, first.Category, StageOf(first.Category), first.Message);
                return;
            }

            if (sessions.Count > 1)
            {
                AssertFailure(expectedResult, expected, "session.multiple-capsules", "session",
                    "input holds more than one capsule");
                return;
            }

            // A conflict recorded during ingest is a session failure even though ingest itself
            // continues; the suite expects it to surface here.
            RejectedFrame? conflict = ingest.Rejected.FirstOrDefault(r => r.Category.StartsWith("session.", StringComparison.Ordinal));
            if (conflict is not null)
            {
                AssertFailure(expectedResult, expected, conflict.Category, "session", conflict.Message);
                return;
            }

            RestoredContent content = CapsuleRecovery.Recover(sessions[0], password, ResourcePolicy.Default);

            if (expectedResult != "success")
            {
                throw new InvalidOperationException($"expected {expectedResult}, but recovery succeeded");
            }

            VerifyOutputs(directory, expected, content);
        }
        catch (RecoveryException ex)
        {
            AssertFailure(expectedResult, expected, ex.CategoryName, ex.StageName, ex.Message);
        }
    }

    private static void VerifyInspect(List<(string Path, byte[] Bytes)> frames, JsonElement expected, string expectedResult)
    {
        CapsuleFrame frame;
        try
        {
            frame = CapsuleFrame.Decode(frames[0].Bytes);
        }
        catch (RecoveryException ex)
        {
            AssertFailure(expectedResult, expected, ex.CategoryName, ex.StageName, ex.Message);
            return;
        }

        if (expectedResult != "success")
        {
            throw new InvalidOperationException($"expected {expectedResult}, but the frame parsed");
        }

        if (!expected.TryGetProperty("capsule", out JsonElement capsule))
        {
            return;
        }

        Check("capsuleId", frame.CapsuleIdHex, capsule.GetProperty("capsuleId").GetString()!);
        Check("erasureAlg", frame.ErasureAlg.ToString(), capsule.GetProperty("erasureAlg").GetInt32().ToString());
        Check("sourceSymbolCount", frame.SourceSymbolCount.ToString(), capsule.GetProperty("sourceSymbolCount").GetInt32().ToString());
        Check("repairSymbolCount", frame.RepairSymbolCount.ToString(), capsule.GetProperty("repairSymbolCount").GetInt32().ToString());
        Check("symbolLen", frame.SymbolLength.ToString(), capsule.GetProperty("symbolLen").GetInt32().ToString());

        static void Check(string field, string actual, string declared)
        {
            if (actual != declared)
            {
                throw new InvalidOperationException($"{field} {actual} != declared {declared}");
            }
        }
    }

    private static void VerifyOutputs(string directory, JsonElement expected, RestoredContent content)
    {
        if (expected.TryGetProperty("payloadKind", out JsonElement kind))
        {
            string actual = content.Kind switch
            {
                PayloadKind.TextNote => "text-note",
                PayloadKind.SingleFile => "single-file",
                _ => "file-tree"
            };

            if (actual != kind.GetString())
            {
                throw new InvalidOperationException($"payload kind {actual} != {kind.GetString()}");
            }
        }

        if (expected.TryGetProperty("displayName", out JsonElement name) && content.DisplayName != name.GetString())
        {
            throw new InvalidOperationException($"display name '{content.DisplayName}' != '{name.GetString()}'");
        }
    }

    private static void AssertFailure(
        string expectedResult, JsonElement expected, string actualCategory, string actualStage, string detail)
    {
        if (expectedResult == "success")
        {
            throw new InvalidOperationException($"expected success, got {actualCategory} at {actualStage}: {detail}");
        }

        if (actualCategory != expectedResult)
        {
            throw new InvalidOperationException($"expected {expectedResult}, got {actualCategory} ({detail})");
        }

        string expectedStage = expected.GetProperty("stage").GetString()!;
        if (actualStage != expectedStage)
        {
            throw new InvalidOperationException($"expected stage {expectedStage}, got {actualStage}");
        }
    }

    private static string StageOf(string category) => category.Split('.')[0] switch
    {
        "frame" => "frame",
        "session" => "session",
        "profile" => "profile",
        "recovery" => "recovery",
        "preamble" => "preamble",
        "auth" => "authentication",
        "compression" => "compression",
        "package" => "package",
        "output" => "output",
        _ => "frame"
    };

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record VectorResult(string Id, bool Passed, string? Detail);
}
