// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;
using BinaryPaper.Recovery.Images;

namespace BinaryPaper.Recovery.Cli;

/// <summary>
/// Reads one page image in a child process, so that losing it costs one page and not the run.
/// </summary>
/// <remarks>
/// <para>Every QR payload this tool reads is turned into frame bytes by a native decoder, and a
/// native decoder handed a damaged image can corrupt its own memory. When that happens the process
/// is terminated by the runtime — there is no exception to catch and no handler that helps. In one
/// process for the whole run, that means the frames already recovered from every other page die
/// with it, which is exactly what the specification says a per-image failure must never cause.</para>
///
/// <para>So the second, expensive pass runs each page in its own short-lived child. A child that
/// dies costs that page. The parent notes it and carries on.</para>
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

    /// <summary>
    /// Runs one page in a child process, retrying once if the child dies.
    /// </summary>
    /// <returns>The page result, or <c>null</c> if every attempt died.</returns>
    public static PageImageResult? Read(string path, string source, ImagePolicy policy)
    {
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            PageImageResult? result = RunOnce(path, source, policy);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static PageImageResult? RunOnce(string path, string source, ImagePolicy policy)
    {
        string? executable = Environment.ProcessPath;
        if (executable is null)
        {
            // No path to re-invoke ourselves with. Fall back to reading in this process: the
            // isolation is a safety net, and losing it is better than refusing to read the page.
            return new PageImageReader(policy).Read(source, File.ReadAllBytes(path));
        }

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add(Command);
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(policy.MaxPixels.ToString());
        start.ArgumentList.Add(policy.MaxDuration.TotalSeconds.ToString("R"));

        using Process? process = Process.Start(start);
        if (process is null)
        {
            return new PageImageReader(policy).Read(source, File.ReadAllBytes(path));
        }

        // Read stdout before waiting: a child that fills the pipe while the parent waits would
        // deadlock, and this child can emit a page's worth of payloads.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        // The child bounds its own work, so this only catches a child that stopped bounding it.
        TimeSpan patience = policy.MaxDuration > TimeSpan.Zero
            ? policy.MaxDuration + TimeSpan.FromSeconds(30)
            : TimeSpan.FromHours(1);

        if (!process.WaitForExit((int)patience.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout and the kill. Nothing to do.
            }

            return null;
        }

        if (process.ExitCode != 0)
        {
            return null;
        }

        try
        {
            return Parse(stdout.Result, source);
        }
        catch (JsonException)
        {
            // A child that died mid-write leaves truncated output. Treat it as a death.
            return null;
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
        if (args.Length < 4)
        {
            return ExitCodes.UsageError;
        }

        string path = args[1];
        var policy = new ImagePolicy
        {
            MaxPixels = long.Parse(args[2]),
            MaxDuration = TimeSpan.FromSeconds(double.Parse(
                args[3], System.Globalization.CultureInfo.InvariantCulture)),
            RectifySymbols = true,
        };

        PageImageResult result = new PageImageReader(policy).Read(
            Path.GetFileName(path), File.ReadAllBytes(path));

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
