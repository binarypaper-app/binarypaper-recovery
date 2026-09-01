// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BinaryPaper.Recovery.Images;

namespace BinaryPaper.Recovery.Cli;

/// <summary>
/// Reads one page image in a child process, so that losing native decoding costs one page and not
/// the run.
/// </summary>
/// <remarks>
/// <para>Every QR payload this tool reads is turned into frame bytes by a native decoder, and a
/// native decoder handed a damaged image can corrupt its own memory. When that happens the process
/// is terminated by the runtime — there is no exception to catch and no handler that helps. In one
/// process for the whole run, that means the frames already recovered from every other page die
/// with it, which is exactly what the specification says a per-image failure must never cause.</para>
///
/// <para>Every pass runs each page in its own short-lived child. A child that dies costs that
/// attempt. The parent retries once, then notes the page and carries on. Starting a child fails
/// closed: falling back to native decoding in the parent would erase the safety boundary exactly
/// when the machine is already behaving unexpectedly.</para>
///
/// <para><b>One page per child, not one child per run.</b> Memory damage is discovered long after
/// it is done, so a process that dies while reading page seven may have been wounded by page four.
/// Sharing a child across pages would let one page's damage land on another's read, and would make
/// the page that happens to be in flight look guilty. A child per page keeps each read
/// independent, and is what makes a retry meaningful rather than a rerun under the same
/// wreckage.</para>
///
/// <para>The retry exists for the same reason. The failure is not deterministic — the same bytes
/// have read cleanly on other runs — so a second attempt in a fresh process has a real chance. One
/// retry captures most of that; beyond it we would be spending a whole time budget per attempt to
/// win an argument with one photograph.</para>
/// </remarks>
internal static class PageWorker
{
    /// <summary>The internal subcommand a child runs. Not part of the published interface.</summary>
    public const string Command = "read-page-isolated";

    /// <summary>Attempts per page: the first, plus one retry in a fresh process.</summary>
    private const int Attempts = 2;

    private const string ProgressPrefix = "BINARYPAPER-PROGRESS\t";

    /// <summary>A process plus any arguments that must precede the internal worker command.</summary>
    internal sealed record WorkerLaunch(string FileName, IReadOnlyList<string> PrefixArguments);

    /// <summary>
    /// Runs one page in a child process, retrying once if the child dies.
    /// </summary>
    /// <returns>The page result, or <c>null</c> if every attempt died.</returns>
    public static PageImageResult? Read(
        string path,
        string source,
        ImagePolicy policy,
        Action<PageImageProgress>? progress = null,
        WorkerLaunch? launch = null)
    {
        launch ??= ResolveLaunch();
        if (launch is null)
        {
            return null;
        }

        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            PageImageResult? result = RunOnce(path, source, policy, progress, launch);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static WorkerLaunch? ResolveLaunch()
    {
        string? executable = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(executable) ? null : new WorkerLaunch(executable, []);
    }

    private static PageImageResult? RunOnce(
        string path,
        string source,
        ImagePolicy policy,
        Action<PageImageProgress>? progress,
        WorkerLaunch launch)
    {
        var start = new ProcessStartInfo(launch.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string prefixArgument in launch.PrefixArguments)
        {
            start.ArgumentList.Add(prefixArgument);
        }

        start.ArgumentList.Add(Command);
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(policy.MaxEncodedBytes.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(policy.MaxPixels.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(policy.MaxSymbolAttempts.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(policy.MaxDuration.TotalSeconds.ToString("R", CultureInfo.InvariantCulture));
        start.ArgumentList.Add(policy.RectifySymbols ? "1" : "0");

        Process? started;
        try
        {
            started = Process.Start(start);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        using Process? process = started;
        if (process is null)
        {
            return null;
        }

        // Read stdout before waiting: a child that fills the pipe while the parent waits would
        // deadlock, and this child can emit a page's worth of payloads.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task stderr = RelayProgress(process.StandardError, source, progress);

        // The child bounds its own work, so this only catches a child that stopped bounding it.
        TimeSpan patience = policy.MaxDuration > TimeSpan.Zero
            ? policy.MaxDuration + TimeSpan.FromSeconds(30)
            : TimeSpan.FromHours(1);
        int waitMilliseconds = patience.TotalMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Ceiling(patience.TotalMilliseconds);

        if (!process.WaitForExit(waitMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout and the kill. Nothing to do.
            }

            process.WaitForExit();
            _ = stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();

            return null;
        }

        string output = stdout.GetAwaiter().GetResult();
        stderr.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            return null;
        }

        try
        {
            return Parse(output, source);
        }
        catch (JsonException)
        {
            // A child that died mid-write leaves truncated output. Treat it as a death.
            return null;
        }
    }

    private static async Task RelayProgress(
        StreamReader error,
        string source,
        Action<PageImageProgress>? progress)
    {
        string? line;
        while ((line = await error.ReadLineAsync()) is not null)
        {
            if (progress is null || !line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = line[ProgressPrefix.Length..].Split('\t');
            if (fields.Length == 3
                && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int symbols)
                && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int positions)
                && long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
            {
                progress(new PageImageProgress(source, symbols, positions, TimeSpan.FromTicks(ticks)));
            }
        }
    }

    private static PageImageResult Parse(string json, string source)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        var symbols = new List<DecodedSymbol>();
        foreach (JsonElement payload in root.GetProperty("symbols").EnumerateArray())
        {
            symbols.Add(new DecodedSymbol(source, Convert.FromBase64String(payload.GetString()!)));
        }

        string? failure = root.GetProperty("failure").ValueKind == JsonValueKind.Null
            ? null
            : root.GetProperty("failure").GetString();

        return new PageImageResult(
            source,
            root.GetProperty("width").GetInt32(),
            root.GetProperty("height").GetInt32(),
            symbols,
            failure)
        {
            Truncated = root.GetProperty("truncated").GetBoolean(),
        };
    }

    /// <summary>
    /// The child side: read one page, write the result to stdout, exit.
    /// </summary>
    /// <remarks>
    /// Deliberately does almost nothing else. Everything this process touches is untrusted, and the
    /// whole point of being a separate process is that it is allowed to die.
    /// </remarks>
    public static int RunChild(string[] args)
    {
        if (args.Length < 7
            || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out long maxEncodedBytes)
            || !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out long maxPixels)
            || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out int maxSymbolAttempts)
            || !double.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double maxSeconds)
            || args[6] is not ("0" or "1")
            || maxEncodedBytes <= 0
            || maxPixels <= 0
            || maxSymbolAttempts <= 0
            || maxSeconds < 0
            || !double.IsFinite(maxSeconds))
        {
            return ExitCodes.UsageError;
        }

        string path = args[1];
        var policy = new ImagePolicy
        {
            MaxEncodedBytes = maxEncodedBytes,
            MaxPixels = maxPixels,
            MaxSymbolAttempts = maxSymbolAttempts,
            MaxDuration = TimeSpan.FromSeconds(maxSeconds),
            RectifySymbols = args[6] == "1",
        };

        void Report(PageImageProgress update)
        {
            Console.Error.WriteLine(
                $"{ProgressPrefix}{update.SymbolsFound.ToString(CultureInfo.InvariantCulture)}\t"
                + $"{update.PositionsTried.ToString(CultureInfo.InvariantCulture)}\t"
                + update.Elapsed.Ticks.ToString(CultureInfo.InvariantCulture));
        }

        PageImageResult result;
        try
        {
            result = new PageImageReader(policy, Report).Read(
                Path.GetFileName(path), File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            // The parent treats every non-success exit the same way: retry in a fresh worker, then
            // skip the page. An ordinary I/O failure does not need to become a process crash.
            return RecoveryErrors.ExitCode(RecoveryErrorCategory.IoFailed);
        }

        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput());
        writer.WriteStartObject();
        writer.WriteNumber("width", result.Width);
        writer.WriteNumber("height", result.Height);
        writer.WriteBoolean("truncated", result.Truncated);

        if (result.Failure is null)
        {
            writer.WriteNull("failure");
        }
        else
        {
            writer.WriteString("failure", result.Failure);
        }

        writer.WriteStartArray("symbols");
        foreach (DecodedSymbol symbol in result.Symbols)
        {
            writer.WriteBase64StringValue(symbol.Payload);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return ExitCodes.Success;
    }
}
