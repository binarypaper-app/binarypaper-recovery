// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BinaryPaper.Recovery;
using BinaryPaper.Recovery.Images;
using Xunit;
using Xunit.Abstractions;

namespace BinaryPaper.Recovery.FuzzTests;

/// <summary>
/// Mutation fuzzing of every parser that sees untrusted input.
/// </summary>
/// <remarks>
/// <para>The property under test is deliberately narrow and absolute: <b>a parser may reject
/// anything, but it may never crash, hang, or allocate without bound.</b> A
/// <see cref="RecoveryException"/> is a pass — that is the parser doing its job. Anything else is a
/// failure, including a <see cref="OutOfMemoryException"/>, which is the specific outcome the
/// checked-arithmetic rules exist to prevent.</para>
///
/// <para>Seeded from the public corpus and driven by a fixed PRNG seed, so a failure is
/// reproducible: the test name and iteration index identify the exact input. Randomness that cannot
/// be replayed finds bugs you then cannot fix.</para>
/// </remarks>
public sealed class FuzzTests(ITestOutputHelper output)
{
    /// <summary>
    /// Mutations per target. Override with <c>BP_FUZZ_ITERATIONS</c>.
    /// </summary>
    /// <remarks>
    /// The full run costs minutes per platform, which is the right price nightly and the wrong one
    /// on every pull request. The seed is fixed either way, so a short run is a prefix of the long
    /// one and never explores inputs the long run would not.
    /// </remarks>
    private static readonly int Iterations =
        int.TryParse(Environment.GetEnvironmentVariable("BP_FUZZ_ITERATIONS"), out int configured)
        && configured > 0
            ? configured
            : 3000;

    private const int Seed = 20260824;

    /// <summary>
    /// Per-input budget, scoped to this corpus rather than to the product.
    /// </summary>
    /// <remarks>
    /// These fixtures are around a megapixel, where the slowest input observed takes about nine
    /// seconds; thirty leaves room for a slow machine while still catching a genuine hang, which is
    /// minutes. It is deliberately <b>not</b> the reader's own page budget: a real page image may
    /// legitimately cost far more than any input here, and a test budget set below the cost of
    /// legitimate work fails on the very thing the tool exists to do.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static string VectorsRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "vectors"));

    [Fact]
    public void FrameDecodingSurvivesArbitraryMutation()
    {
        byte[][] corpus = LoadCorpus("*.bpq");
        Assert.NotEmpty(corpus);

        RunFuzz("frame", corpus, input => CapsuleFrame.Decode(input));
    }

    [Fact]
    public void PreambleDecodingSurvivesArbitraryMutation()
    {
        byte[][] corpus = LoadCorpus("stored-payload.bin");
        Assert.NotEmpty(corpus);

        RunFuzz("preamble", corpus, input =>
        {
            CapsulePreamble preamble = CapsulePreamble.Decode(input);
            preamble.ExtractBody(input);
        });
    }

    [Fact]
    public void PackageParsingSurvivesArbitraryMutation()
    {
        byte[][] corpus = LoadCorpus("payload-package.bin");
        Assert.NotEmpty(corpus);

        RunFuzz("package", corpus, input => PayloadPackage.Parse(input));
    }

    [Fact]
    public void CompressionSurvivesArbitraryMutation()
    {
        byte[][] corpus = LoadCorpus("payload-package.bin");
        Assert.NotEmpty(corpus);

        RunFuzz("compression", corpus, input =>
            CapsuleCompression.Decompress(input, CapsulePreamble.CompressionLzma, ResourcePolicy.Default));
    }

    [Fact]
    public void ImageDecodingSurvivesArbitraryMutation()
    {
        byte[][] corpus = [.. LoadCorpus("*.png"), .. LoadCorpus("*.jpg")];
        Assert.NotEmpty(corpus);

        // Configured below the harness budget on purpose, so this target tests something stronger
        // than "did not hang": the reader must honour the bound it was given, with ten seconds of
        // slack for a loaded machine before the harness calls it a runaway.
        var reader = new PageImageReader(new ImagePolicy { MaxDuration = Budget - TimeSpan.FromSeconds(10) });

        string? replayInput = Environment.GetEnvironmentVariable("BP_FUZZ_REPLAY_INPUT");
        if (!string.IsNullOrEmpty(replayInput))
        {
            byte[] bytes = File.ReadAllBytes(replayInput);
            output.WriteLine($"Replaying {bytes.Length} bytes, SHA256 {Convert.ToHexString(SHA256.HashData(bytes))}");
            PageImageResult result = reader.Read("fuzz", bytes);
            Assert.True(result.Failure is not null || result.Symbols.Count > 0);
            return;
        }

        // The image reader is contractually total: it reports failures on the result rather than
        // throwing, so that one bad page never discards frames recovered from other pages. The
        // property here is that the contract actually holds under mutation.
        RunFuzz("image", corpus, input =>
        {
            PageImageResult result = reader.Read("fuzz", input);
            Assert.True(result.Failure is not null || result.Symbols.Count > 0);
        }, allowSuccess: true, checkpointPath: Environment.GetEnvironmentVariable("BP_FUZZ_CHECKPOINT"));
    }

    [Fact]
    public void SessionIngestSurvivesArbitraryMutation()
    {
        byte[][] corpus = LoadCorpus("*.bpq");
        Assert.NotEmpty(corpus);

        // Ingest is total by design - it collects and classifies rather than throwing - so any
        // escaping exception is a failure.
        RunFuzz("session", corpus, input =>
        {
            FrameIngestResult ingest = CapsuleRecovery.Ingest([("fuzz-a", input), ("fuzz-b", input)]);
            foreach (ScanSession session in ingest.Sessions.Where(s => s.Reference is not null))
            {
                _ = CapsuleRecovery.Summarize(session);
            }
        }, allowSuccess: true);
    }

    // ------------------------------------------------------------------ harness

    private void RunFuzz(
        string name,
        byte[][] corpus,
        Action<byte[]> parse,
        bool allowSuccess = false,
        string? checkpointPath = null)
    {
        var random = new Random(Seed);
        int rejected = 0;
        int accepted = 0;
        var slowest = TimeSpan.Zero;
        // Fingerprint ordered corpus bytes so replay can detect an incompatible corpus/runtime.
        string corpusHash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", corpus.Select(b => Convert.ToHexString(SHA256.HashData(b)))))));

        for (int i = 0; i < Iterations; i++)
        {
            byte[] input = Mutate(random, corpus[random.Next(corpus.Length)]);
            var stopwatch = Stopwatch.StartNew();

            WriteCheckpoint(checkpointPath, name, i, input, corpusHash);

            try
            {
                parse(input);
                accepted++;
            }
            catch (RecoveryException)
            {
                // The parser refused. That is the correct outcome for hostile input.
                rejected++;
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{name} iteration {i} (seed {Seed}) threw {ex.GetType().Name} rather than "
                    + $"RecoveryException: {ex.Message}\ninput ({input.Length} bytes): {Preview(input)}");
            }
            finally
            {
                stopwatch.Stop();
                if (stopwatch.Elapsed > slowest)
                {
                    slowest = stopwatch.Elapsed;
                }
            }

            if (stopwatch.Elapsed > Budget)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{name} iteration {i} (seed {Seed}) took {stopwatch.Elapsed.TotalSeconds:F1}s, "
                    + $"beyond the {Budget.TotalSeconds:F0}s budget. Input ({input.Length} bytes): {Preview(input)}");
            }
        }

        output.WriteLine($"{name}: {Iterations} mutations, {rejected} rejected, {accepted} accepted, "
            + $"slowest {slowest.TotalMilliseconds:F0}ms");

        if (checkpointPath is not null)
        {
            // The same resolution WriteCheckpoint used, so a completed run cannot leave a stale
            // checkpoint behind for the next failure to be blamed on.
            File.Delete(Path.GetFullPath(checkpointPath));
            File.Delete(Path.GetFullPath(checkpointPath) + ".input.bin");
        }

        if (!allowSuccess)
        {
            // A fuzzer whose every input is accepted is testing nothing. This catches the case
            // where a corpus loader silently returns empty or the mutator stops mutating.
            Assert.True(rejected > Iterations / 10,
                $"{name}: only {rejected} of {Iterations} mutations were rejected; the fuzzer is probably not mutating.");
        }
    }

    private static void WriteCheckpoint(string? path, string target, int iteration, byte[] input, string corpusHash)
    {
        if (path is null)
        {
            return;
        }

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // Keep one bounded synthetic input, never the entire mutation history or process memory.
        bool inputSaved = input.Length <= 512 * 1024;
        if (inputSaved)
        {
            File.WriteAllBytes(fullPath + ".input.bin.tmp", input);
            File.Move(fullPath + ".input.bin.tmp", fullPath + ".input.bin", overwrite: true);
        }
        else { File.Delete(fullPath + ".input.bin"); }
        // A crash during checkpoint I/O must leave the previous complete JSON readable.
        File.WriteAllText(fullPath + ".tmp", JsonSerializer.Serialize(new
        {
            target,
            seed = Seed,
            iteration,
            inputLength = input.Length,
            inputSha256 = Convert.ToHexString(SHA256.HashData(input)),
            corpusSha256 = corpusHash,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            inputSaved,
            prefix = Preview(input),
        }));
        File.Move(fullPath + ".tmp", fullPath, overwrite: true);
    }

    /// <summary>
    /// Mutations chosen to reach the checks that matter: header fields, declared lengths, and
    /// truncation. Purely random bytes would spend almost every iteration failing at the magic.
    /// </summary>
    private static byte[] Mutate(Random random, byte[] seed)
    {
        byte[] input = (byte[])seed.Clone();
        if (input.Length == 0)
        {
            return input;
        }

        switch (random.Next(6))
        {
            case 0: // flip a handful of bits anywhere
                for (int i = 0; i < 1 + random.Next(8); i++)
                {
                    input[random.Next(input.Length)] ^= (byte)(1 << random.Next(8));
                }

                break;

            case 1: // truncate
                input = input[..random.Next(1, input.Length)];
                break;

            case 2: // extend with noise
                byte[] extended = new byte[input.Length + random.Next(1, 64)];
                input.CopyTo(extended, 0);
                random.NextBytes(extended.AsSpan(input.Length));
                input = extended;
                break;

            case 3: // saturate a field-sized window: the shape that reaches length arithmetic
                int start = random.Next(input.Length);
                int length = Math.Min(random.Next(1, 9), input.Length - start);
                input.AsSpan(start, length).Fill(0xFF);
                break;

            case 4: // zero a window
                int zeroStart = random.Next(input.Length);
                int zeroLength = Math.Min(random.Next(1, 9), input.Length - zeroStart);
                input.AsSpan(zeroStart, zeroLength).Clear();
                break;

            default: // splice a chunk from elsewhere in the same input
                if (input.Length > 8)
                {
                    int from = random.Next(input.Length - 4);
                    int to = random.Next(input.Length - 4);
                    int size = Math.Min(4, input.Length - Math.Max(from, to));
                    input.AsSpan(from, size).CopyTo(input.AsSpan(to, size));
                }

                break;
        }

        return input;
    }

    private static byte[][] LoadCorpus(string pattern)
    {
        string root = VectorsRoot;
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(File.ReadAllBytes)
            .Where(b => b.Length > 0)];
    }

    private static string Preview(byte[] input) =>
        Convert.ToHexString(input.AsSpan(0, Math.Min(48, input.Length))) + (input.Length > 48 ? "..." : string.Empty);
}
