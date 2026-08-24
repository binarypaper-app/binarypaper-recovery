// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// Measures recovery cost at the boundary shapes the production planner actually accepts.
//
// This answers a different question from the conformance suite. The suite asks "are the bytes and
// the rejection rules right?"; this asks "can an implementation recover everything the product
// writes, within a sane resource budget, on an ordinary machine?" Both are needed and they must not
// be conflated - which is why the corpus lives here and not in vectors/.
//
// The CLI is run as a separate process and observed from outside. In-process counters measure the
// harness as much as the subject, and a runtime that has not yet collected looks identical to one
// that cannot.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: benchmarks <corpus-directory> <cli-executable> [--output <results.json>]");
    return 2;
}

string corpusRoot = args[0];
string cliPath = args[1];
string? resultsPath = OptionalOption(args, "--output");

string corpusManifest = Path.Combine(corpusRoot, "corpus.json");
if (!File.Exists(corpusManifest))
{
    Console.Error.WriteLine($"No corpus.json under '{corpusRoot}'. Generate the corpus first.");
    return 2;
}

JsonArray cases = JsonNode.Parse(File.ReadAllText(corpusManifest))!.AsArray();
var results = new JsonArray();
int failures = 0;

Console.WriteLine($"{"case",-26} {"codec",-13} {"K",6} {"R",6} {"S",5} {"peak MiB",9} {"pred MiB",9} {"secs",7}  result");
Console.WriteLine(new string('-', 104));

foreach (JsonNode? node in cases)
{
    JsonObject c = node!.AsObject();
    string id = c["id"]!.GetValue<string>();
    int k = c["sourceSymbolCount"]!.GetValue<int>();
    int r = c["repairSymbolCount"]!.GetValue<int>();
    int s = c["symbolLen"]!.GetValue<int>();
    string codec = c["erasureAlg"]!.GetValue<int>() == 2 ? "ldpc" : "reed-solomon";
    long predicted = c["predictedPeakRestoreBytes"]!.GetValue<long>();
    string expectedHash = c["expectedOutputSha256"]!.GetValue<string>();

    string caseDirectory = Path.Combine(corpusRoot, id);
    string framesDirectory = Path.Combine(caseDirectory, "frames");
    string outputDirectory = Path.Combine(caseDirectory, "recovered");

    long extractedBytes = ExtractFrames(Path.Combine(caseDirectory, "frames.bin"), framesDirectory);

    if (Directory.Exists(outputDirectory))
    {
        Directory.Delete(outputDirectory, recursive: true);
    }

    (int exitCode, long peakBytes, TimeSpan elapsed, string stderr) =
        RunObserved(cliPath, ["recover", framesDirectory, "--output", outputDirectory]);

    string actualHash = "-";
    bool exact = false;
    if (exitCode == 0)
    {
        string? recovered = Directory.Exists(outputDirectory)
            ? Directory.GetFiles(outputDirectory).FirstOrDefault(f => !f.EndsWith("RECOVERED.txt", StringComparison.Ordinal))
            : null;

        if (recovered is not null)
        {
            actualHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(recovered)));
            exact = actualHash == expectedHash;
        }
    }

    bool passed = exitCode == 0 && exact;
    if (!passed)
    {
        failures++;
    }

    string verdict = passed
        ? "exact"
        : exitCode != 0 ? $"exit {exitCode}" : "OUTPUT DIFFERS";

    Console.WriteLine($"{id,-26} {codec,-13} {k,6} {r,6} {s,5} "
        + $"{peakBytes / 1024.0 / 1024.0,9:F1} {predicted / 1024.0 / 1024.0,9:F1} "
        + $"{elapsed.TotalSeconds,7:F1}  {verdict}");

    if (!passed && stderr.Length > 0)
    {
        Console.Error.WriteLine($"    {stderr.Split('\n')[0].Trim()}");
    }

    results.Add(new JsonObject
    {
        ["id"] = id,
        ["codec"] = codec,
        ["sourceSymbolCount"] = k,
        ["repairSymbolCount"] = r,
        ["symbolLen"] = s,
        ["frameBytesOnDisk"] = extractedBytes,
        ["peakWorkingSetBytes"] = peakBytes,
        ["predictedPeakRestoreBytes"] = predicted,
        ["withinPrediction"] = peakBytes <= predicted,
        ["elapsedSeconds"] = Math.Round(elapsed.TotalSeconds, 2),
        ["exitCode"] = exitCode,
        ["outputExact"] = exact
    });

    // These directories are large. Clear them as we go rather than needing the whole corpus
    // expanded on disk at once.
    TryDelete(framesDirectory);
    TryDelete(outputDirectory);
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"All {cases.Count} boundary case(s) recovered exactly."
    : $"{failures} of {cases.Count} boundary case(s) FAILED.");

if (resultsPath is not null)
{
    var document = new JsonObject
    {
        ["tool"] = "BinaryPaper.Recovery.Benchmarks",
        ["measuredUtc"] = DateTime.UtcNow.ToString("O"),
        ["machine"] = new JsonObject
        {
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["processorCount"] = Environment.ProcessorCount,
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        },
        ["note"] = "Peak working set is observed from outside the process. These numbers describe "
            + "this machine and this build; they are reproducible evidence, not normative protocol "
            + "behaviour.",
        ["cases"] = results
    };

    File.WriteAllText(resultsPath,
        document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            .Replace("\r\n", "\n") + "\n",
        new UTF8Encoding(false));

    Console.WriteLine($"Wrote {resultsPath}");
}

return failures == 0 ? 0 : 1;

// ------------------------------------------------------------------ helpers

static long ExtractFrames(string containerPath, string outputDirectory)
{
    if (Directory.Exists(outputDirectory))
    {
        Directory.Delete(outputDirectory, recursive: true);
    }

    Directory.CreateDirectory(outputDirectory);

    using var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
    byte[] header = new byte[4];
    stream.ReadExactly(header);
    uint count = BinaryPrimitives.ReadUInt32BigEndian(header);

    long total = 0;
    for (uint i = 0; i < count; i++)
    {
        stream.ReadExactly(header);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        byte[] frame = new byte[length];
        stream.ReadExactly(frame);
        File.WriteAllBytes(Path.Combine(outputDirectory, $"frame-{i:D6}.bpq"), frame);
        total += length;
    }

    return total;
}

/// <summary>
/// Runs the CLI and observes its peak working set from the parent process.
/// </summary>
static (int ExitCode, long PeakBytes, TimeSpan Elapsed, string StdErr) RunObserved(string executable, string[] arguments)
{
    var info = new ProcessStartInfo(executable)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        UseShellExecute = false
    };

    foreach (string argument in arguments)
    {
        info.ArgumentList.Add(argument);
    }

    var stopwatch = Stopwatch.StartNew();
    using var process = Process.Start(info)!;

    // Close stdin immediately: these capsules are unencrypted, and a tool waiting on a password
    // prompt would look exactly like a hang.
    process.StandardInput.Close();

    var stderr = new StringBuilder();
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };
    process.BeginErrorReadLine();
    process.BeginOutputReadLine();

    long peak = 0;
    while (!process.WaitForExit(50))
    {
        try
        {
            process.Refresh();
            peak = Math.Max(peak, process.PeakWorkingSet64);
        }
        catch (InvalidOperationException)
        {
            break; // exited between the check and the refresh
        }
    }

    process.WaitForExit();
    stopwatch.Stop();

    try
    {
        peak = Math.Max(peak, process.PeakWorkingSet64);
    }
    catch (InvalidOperationException)
    {
        // Peak is unavailable once the process object is fully torn down; keep the sampled value.
    }

    return (process.ExitCode, peak, stopwatch.Elapsed, stderr.ToString());
}

static void TryDelete(string directory)
{
    try
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    catch
    {
        // Best effort; a leftover benchmark directory is noise, not a failure.
    }
}

static string? OptionalOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return null;
}
