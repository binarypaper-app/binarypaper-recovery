// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace BinaryPaper.Recovery;

public sealed record PublishedOutput(string Directory, IReadOnlyList<string> Files);

/// <summary>
/// Writes restored content to disk. SPEC.md security considerations section 5.
/// </summary>
/// <remarks>
/// <para>Two rules drive the design. Nothing is published until every earlier stage has passed, and
/// a failure leaves no directory that looks like a successful recovery — because a user who sees
/// output may throw the paper away, and half-written output is worse than a clean failure.</para>
///
/// <para>Content is therefore staged in a sibling temporary directory and moved into place only
/// once every byte is written.</para>
/// </remarks>
public static class OutputPublisher
{
    public static PublishedOutput Publish(RestoredContent content, string destination, bool overwrite)
    {
        string full = Path.GetFullPath(destination);

        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !overwrite)
        {
            throw new RecoveryException(RecoveryErrorCategory.OutputUnsafeDestination, RecoveryStage.Output,
                $"'{full}' already exists and is not empty. Choose an empty or new directory, or pass "
                + "--overwrite if you intend to replace its contents.");
        }

        if (File.Exists(full))
        {
            throw new RecoveryException(RecoveryErrorCategory.OutputUnsafeDestination, RecoveryStage.Output,
                $"'{full}' is a file, not a directory.");
        }

        string staging = full + ".partial-" + Guid.NewGuid().ToString("n")[..8];

        try
        {
            Directory.CreateDirectory(staging);
            List<string> written = WriteContent(content, staging);

            if (Directory.Exists(full) && overwrite)
            {
                Directory.Delete(full, recursive: true);
            }

            Directory.Move(staging, full);
            return new PublishedOutput(full, written);
        }
        catch (RecoveryException)
        {
            TryRemove(staging);
            throw;
        }
        catch (Exception ex)
        {
            TryRemove(staging);
            throw new RecoveryException(RecoveryErrorCategory.IoFailed, RecoveryStage.Output,
                $"Could not write recovered content to '{full}': {ex.Message}");
        }
    }

    private static List<string> WriteContent(RestoredContent content, string staging)
    {
        var written = new List<string>();

        foreach (PayloadEntry entry in content.Entries)
        {
            // The path was validated at parse time, but the containment check is repeated here
            // against the resolved destination. A path check that lives only in the parser is one
            // refactor away from being bypassed, and the cost of repeating it is nothing.
            string target = Path.GetFullPath(Path.Combine(staging, entry.Path));
            string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new RecoveryException(RecoveryErrorCategory.OutputUnsafeDestination, RecoveryStage.Output,
                    $"Payload entry '{entry.Path}' resolves outside the output directory.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, entry.Bytes);
            written.Add(entry.Path);
        }

        // A short sidecar naming the payload, so a recovered directory is self-describing years
        // later. The display name is untrusted text and is never used as a path.
        var summary = new StringBuilder();
        summary.Append("BinaryPaper recovered content\n");
        summary.Append($"kind: {content.Kind}\n");
        summary.Append($"display name: {content.DisplayName}\n");
        summary.Append($"entries: {content.Entries.Count}\n");
        File.WriteAllText(Path.Combine(staging, "RECOVERED.txt"), summary.ToString(), new UTF8Encoding(false));
        written.Add("RECOVERED.txt");

        return written;
    }

    private static void TryRemove(string directory)
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
            // Best effort. The staging directory is named .partial-* precisely so that a leftover is
            // obviously not a successful recovery if it cannot be removed.
        }
    }
}
