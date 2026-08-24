// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using BinaryPaper.Recovery;
using BinaryPaper.Recovery.Cli;
using BinaryPaper.Recovery.Images;

return CommandLine.Run(args);

namespace BinaryPaper.Recovery.Cli
{
    /// <summary>
    /// The <c>binarypaper</c> reference recovery tool.
    /// </summary>
    /// <remarks>
    /// <para>Recovery only. This tool never creates a capsule, never reads a PDF, never opens a
    /// camera, and never touches the network. Those are not omissions to be filled in later; they
    /// are the scope.</para>
    ///
    /// <para>stdout carries results, stderr carries diagnostics, and the exit code is stable enough
    /// to branch on. Human-readable text may improve freely; <c>--json</c> output and exit codes are
    /// the contract.</para>
    /// </remarks>
    internal static class CommandLine
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return args.Length == 0 ? ExitCodes.UsageError : ExitCodes.Success;
            }

            if (args[0] is "--version")
            {
                Console.WriteLine($"binarypaper {ToolVersion}");
                Console.WriteLine("capsule wire 1.0 (format_major = 1, format_minor = 0)");
                return ExitCodes.Success;
            }

            try
            {
                return args[0] switch
                {
                    "inspect" => Inspect(args),
                    "recover" => Recover(args),
                    "verify-vectors" => VerifyVectors.Run(args),
                    _ => UnknownCommand(args[0])
                };
            }
            catch (RecoveryException ex)
            {
                Console.Error.WriteLine($"error [{ex.CategoryName} at {ex.StageName}]: {ex.Message}");
                return RecoveryErrors.ExitCode(ex.Category);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.UsageError;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"error [io.failed]: {ex.Message}");
                return RecoveryErrors.ExitCode(RecoveryErrorCategory.IoFailed);
            }
        }

        private const string ToolVersion = "0.1.0-dev";

        // ------------------------------------------------------------------ inspect

        private static int Inspect(string[] args)
        {
            Options options = Options.Parse(args);
            CollectedInput collected = FrameInput.Collect(options.Inputs, options.ImagePolicy);
            List<(string Source, byte[] Bytes)> frames = collected.Frames;

            ReportImages(collected.Images);

            if (frames.Count == 0)
            {
                Console.Error.WriteLine(
                    "error: no frames were found. Inputs may be .bpq frame files, or PNG/JPEG page images.");
                return collected.Images.Count > 0 ? ExitCodes.InputDecodeError : ExitCodes.NoFrames;
            }

            FrameIngestResult ingest = CapsuleRecovery.Ingest(frames);
            var summaries = ingest.Sessions
                .Where(s => s.Reference is not null)
                .Select(CapsuleRecovery.Summarize)
                .ToList();

            if (summaries.Count == 0)
            {
                Console.Error.WriteLine("error: no readable BinaryPaper frames were found.");
                return ExitCodes.NoFrames;
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    tool = ToolVersion,
                    framesRead = frames.Count,
                    framesAccepted = ingest.AcceptedFrameCount,
                    framesDuplicate = ingest.DuplicateFrameCount,
                    framesRejected = ingest.Rejected.Count,
                    capsules = summaries.Select(s => new
                    {
                        capsuleId = s.CapsuleIdHex,
                        erasureAlg = s.ErasureAlg,
                        erasureAlgName = s.ErasureAlgName,
                        sourceSymbolCount = s.SourceSymbolCount,
                        repairSymbolCount = s.RepairSymbolCount,
                        symbolLen = s.SymbolLength,
                        framesAccepted = s.AcceptedFrameCount,
                        conflicts = s.ConflictCount,
                        hasEnoughFrames = s.HasEnoughFrames,
                        completionIsGuaranteed = !s.IsLdpc
                    })
                }, JsonOptions));

                return ExitCodes.Success;
            }

            Console.WriteLine($"Read {frames.Count} frame file(s): {ingest.AcceptedFrameCount} accepted, "
                + $"{ingest.DuplicateFrameCount} duplicate, {ingest.Rejected.Count} rejected.");

            foreach (RejectedFrame rejected in ingest.Rejected)
            {
                Console.Error.WriteLine($"  rejected {rejected.Source}: [{rejected.Category}] {rejected.Message}");
            }

            foreach (CapsuleSummary summary in summaries)
            {
                Console.WriteLine();
                Console.WriteLine($"Capsule {summary.CapsuleIdHex}");
                Console.WriteLine($"  erasure code   {summary.ErasureAlgName} (erasure_alg = {summary.ErasureAlg})");
                Console.WriteLine($"  symbols        {summary.SourceSymbolCount} source + {summary.RepairSymbolCount} repair");
                Console.WriteLine($"  symbol_len     {summary.SymbolLength} bytes");
                Console.WriteLine($"  scanned        {summary.AcceptedFrameCount} of {summary.SourceSymbolCount + summary.RepairSymbolCount}");

                if (summary.ConflictCount > 0)
                {
                    Console.WriteLine($"  conflicts      {summary.ConflictCount} (recovery is blocked until these are resolved)");
                }
                else if (!summary.HasEnoughFrames)
                {
                    int missing = summary.SourceSymbolCount - summary.AcceptedFrameCount;
                    Console.WriteLine($"  status         need at least {missing} more code(s)");
                }
                else if (summary.IsLdpc)
                {
                    // Being honest here matters: LDPC is not MDS, so "enough" is a lower bound. A
                    // user deciding whether to keep scanning deserves to know that before they stop.
                    Console.WriteLine("  status         enough codes to attempt recovery — but this capsule uses");
                    Console.WriteLine("                 LDPC, where reaching the source count is necessary and not");
                    Console.WriteLine("                 sufficient. Scan more if recovery reports it is incomplete.");
                }
                else
                {
                    Console.WriteLine("  status         enough codes: any source count of the total recovers this capsule");
                }
            }

            Console.WriteLine();
            Console.WriteLine("inspect reports only what is readable before decryption. Nothing above comes from");
            Console.WriteLine("inside the capsule: file names and contents stay sealed until recovery.");
            return ExitCodes.Success;
        }

        // ------------------------------------------------------------------ recover

        private static int Recover(string[] args)
        {
            Options options = Options.Parse(args);

            if (options.OutputDirectory is null)
            {
                throw new UsageException("recover requires --output <directory>.");
            }

            CollectedInput collected = FrameInput.Collect(options.Inputs, options.ImagePolicy);
            List<(string Source, byte[] Bytes)> frames = collected.Frames;

            ReportImages(collected.Images);

            if (frames.Count == 0)
            {
                Console.Error.WriteLine(
                    "error: no frames were found. Inputs may be .bpq frame files, or PNG/JPEG page images.");
                return collected.Images.Count > 0 ? ExitCodes.InputDecodeError : ExitCodes.NoFrames;
            }

            FrameIngestResult ingest = CapsuleRecovery.Ingest(frames);
            List<ScanSession> sessions = ingest.Sessions.Where(s => s.Reference is not null).ToList();

            if (sessions.Count == 0)
            {
                Console.Error.WriteLine("error: no readable BinaryPaper frames were found.");
                return ExitCodes.NoFrames;
            }

            if (sessions.Count > 1)
            {
                // Silently recovering "the biggest one" would be a guess presented as a result.
                Console.Error.WriteLine(
                    $"error [session.multiple-capsules]: the input holds {sessions.Count} different capsules:");
                foreach (ScanSession session in sessions)
                {
                    Console.Error.WriteLine($"  {session.CapsuleIdHex} ({session.AcceptedFrameCount} frames)");
                }

                Console.Error.WriteLine("Separate them and recover one at a time.");
                return RecoveryErrors.ExitCode(RecoveryErrorCategory.SessionMultipleCapsules);
            }

            ScanSession only = sessions[0];
            string? password = options.ReadPassword();

            RestoredContent content = CapsuleRecovery.Recover(only, password, options.Policy);
            PublishedOutput published = OutputPublisher.Publish(content, options.OutputDirectory, options.Overwrite);

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    tool = ToolVersion,
                    result = "success",
                    capsuleId = only.CapsuleIdHex,
                    payloadKind = content.Kind.ToString(),
                    displayName = content.DisplayName,
                    outputDirectory = published.Directory,
                    files = published.Files
                }, JsonOptions));

                return ExitCodes.Success;
            }

            Console.WriteLine($"Recovered {content.Kind} \"{content.DisplayName}\" from capsule {only.CapsuleIdHex}.");
            Console.WriteLine($"Wrote {published.Files.Count} file(s) to {published.Directory}");
            foreach (string file in published.Files)
            {
                Console.WriteLine($"  {file}");
            }

            return ExitCodes.Success;
        }

        /// <summary>
        /// Reports what each page image yielded. A page that decoded nothing is worth saying out
        /// loud - it usually means a scan is too low-resolution or too skewed - but it never stops
        /// the frames recovered from other pages being used.
        /// </summary>
        private static void ReportImages(IReadOnlyList<PageImageResult> images)
        {
            foreach (PageImageResult image in images)
            {
                if (image.Failure is not null)
                {
                    Console.Error.WriteLine($"  image {image.Source}: {image.Failure}");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"  image {image.Source}: {image.Symbols.Count} QR code(s) in {image.Width}x{image.Height}");
                }
            }
        }

        private static int UnknownCommand(string command)
        {
            Console.Error.WriteLine($"error: unknown command '{command}'.");
            PrintUsage();
            return ExitCodes.UsageError;
        }

        internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static void PrintUsage()
        {
            Console.WriteLine("""
            binarypaper — reference recovery tool for the BinaryPaper capsule protocol

            USAGE
              binarypaper inspect <input>... [--json]
              binarypaper recover <input>... --output <directory> [options]
              binarypaper verify-vectors <suite-directory> [--json]
              binarypaper --version

            INPUTS
              PNG or JPEG page images, raw .bpq frame files, or a directory containing
              them (searched recursively, in a deterministic order). File type is
              determined by content, not by extension.

            OPTIONS
              --output <dir>          where to write recovered content (recover only)
              --overwrite             replace a non-empty output directory
              --password-stdin        read the password from standard input
              --json                  machine-readable output on stdout
              --max-output-bytes <n>  cap the decompressed package size
              --max-image-pixels <n>  cap the decoded pixel count per page image
              --allow-high-kdf-cost   accept Argon2id parameters above the stable profile

            PASSWORDS
              A password is read from an interactive prompt, or from standard input with
              --password-stdin. There is deliberately no --password flag: command lines are
              visible to other processes and are recorded in shell history.

            SCOPE
              Recovery only. This tool reads page images and raw frames. It does not create
              backups, does not read PDFs, does not use a camera, and never accesses the
              network.

            EXIT CODES
              0   success                     22  not enough codes / decode incomplete
              2   usage error                 30  refused by resource policy
              10  no frames found             40  authentication failed
              11  input decode error          41  plaintext integrity failed
              20  invalid or unsupported      50  compression or package invalid
              21  capsule or session conflict 51  unsafe output destination
                                              60  local I/O failure
            """);
        }
    }

    internal sealed class UsageException(string message) : Exception(message);

    internal sealed record Options
    {
        public required IReadOnlyList<string> Inputs { get; init; }

        public string? OutputDirectory { get; init; }

        public bool Json { get; init; }

        public bool Overwrite { get; init; }

        public bool PasswordFromStdin { get; init; }

        public ResourcePolicy Policy { get; init; } = ResourcePolicy.Default;

        public ImagePolicy ImagePolicy { get; init; } = ImagePolicy.Default;

        public static Options Parse(string[] args)
        {
            var inputs = new List<string>();
            string? output = null;
            bool json = false;
            bool overwrite = false;
            bool passwordStdin = false;
            bool allowHighKdf = false;
            long maxOutput = ResourcePolicy.Default.MaxDecompressedBytes;
            long maxPixels = ImagePolicy.Default.MaxPixels;

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--json":
                        json = true;
                        break;
                    case "--overwrite":
                        overwrite = true;
                        break;
                    case "--password-stdin":
                        passwordStdin = true;
                        break;
                    case "--allow-high-kdf-cost":
                        allowHighKdf = true;
                        break;
                    case "--output":
                        output = NextValue(args, ref i, "--output");
                        break;
                    case "--max-image-pixels":
                        if (!long.TryParse(NextValue(args, ref i, "--max-image-pixels"), out maxPixels) || maxPixels <= 0)
                        {
                            throw new UsageException("--max-image-pixels requires a positive integer.");
                        }

                        break;
                    case "--max-output-bytes":
                        if (!long.TryParse(NextValue(args, ref i, "--max-output-bytes"), out maxOutput) || maxOutput <= 0)
                        {
                            throw new UsageException("--max-output-bytes requires a positive integer.");
                        }

                        break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new UsageException($"unknown option '{arg}'.");
                        }

                        inputs.Add(arg);
                        break;
                }
            }

            if (inputs.Count == 0)
            {
                throw new UsageException("at least one input file or directory is required.");
            }

            return new Options
            {
                Inputs = inputs,
                OutputDirectory = output,
                Json = json,
                Overwrite = overwrite,
                PasswordFromStdin = passwordStdin,
                Policy = new ResourcePolicy
                {
                    MaxDecompressedBytes = maxOutput,
                    AllowHighKdfCost = allowHighKdf
                },
                ImagePolicy = new ImagePolicy { MaxPixels = maxPixels }
            };
        }

        private static string NextValue(string[] args, ref int i, string option)
        {
            if (i + 1 >= args.Length)
            {
                throw new UsageException($"{option} requires a value.");
            }

            return args[++i];
        }

        /// <summary>
        /// Reads the password, or returns null for a capsule that turns out not to need one.
        /// </summary>
        /// <remarks>
        /// Never echoed, never logged, never accepted as a command-line argument.
        /// </remarks>
        public string? ReadPassword()
        {
            if (PasswordFromStdin)
            {
                string? line = Console.In.ReadLine();
                return string.IsNullOrEmpty(line) ? null : line;
            }

            if (Console.IsInputRedirected)
            {
                // No terminal to prompt on and no --password-stdin: treat the capsule as unencrypted
                // and let stage 8 report honestly if it is not.
                return null;
            }

            Console.Error.Write("Password (leave empty if the capsule is not encrypted): ");
            var password = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.Error.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password.Length--;
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                }
            }

            return password.Length == 0 ? null : password.ToString();
        }
    }

    /// <summary>The frames found in the inputs, plus what happened to each page image.</summary>
    internal sealed record CollectedInput(
        List<(string Source, byte[] Bytes)> Frames,
        List<PageImageResult> Images);

    /// <summary>
    /// Collects frames from the given paths, in a deterministic order.
    /// </summary>
    /// <remarks>
    /// Two kinds of input produce frames: a raw <c>.bpq</c> file, and a page image containing QR
    /// codes. Both are read here so the rest of the pipeline never has to care which one a frame
    /// came from.
    /// </remarks>
    internal static class FrameInput
    {
        private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

        public static CollectedInput Collect(IReadOnlyList<string> inputs, ImagePolicy policy)
        {
            var files = new List<string>();

            foreach (string input in inputs)
            {
                if (Directory.Exists(input))
                {
                    files.AddRange(Directory.GetFiles(input, "*", SearchOption.AllDirectories)
                        .Where(IsCandidate));
                }
                else if (File.Exists(input))
                {
                    files.Add(input);
                }
                else
                {
                    throw new UsageException($"input '{input}' does not exist.");
                }
            }

            // Ordinal sort so repeated runs and repeated inputs behave identically everywhere.
            files.Sort(StringComparer.Ordinal);

            var frames = new List<(string, byte[])>();
            var images = new List<PageImageResult>();
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            var reader = new PageImageReader(policy);

            foreach (string file in files)
            {
                string full = Path.GetFullPath(file);
                if (!seenPaths.Add(full))
                {
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(file);
                string name = Path.GetFileName(file);

                // Content decides, not the extension. A .bpq holding a PNG is a PNG.
                if (PageImageReader.LooksLikeImage(bytes))
                {
                    PageImageResult result = reader.Read(name, bytes);
                    images.Add(result);
                    foreach (DecodedSymbol symbol in result.Symbols)
                    {
                        frames.Add(($"{name}#qr", symbol.Payload));
                    }

                    continue;
                }

                frames.Add((name, bytes));
            }

            return new CollectedInput(frames, images);
        }

        private static bool IsCandidate(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".bpq" || ImageExtensions.Contains(extension);
        }
    }
}
