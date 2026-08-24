// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BinaryPaper.Recovery;

public enum PayloadKind
{
    TextNote,
    SingleFile,
    FileTree
}

public sealed record PayloadEntry(string Path, byte[] Bytes);

public sealed record RestoredContent(
    PayloadKind Kind,
    string DisplayName,
    IReadOnlyList<PayloadEntry> Entries,
    string? TextNote);

/// <summary>
/// The ZIP payload package and its <c>BPMF</c> manifest. SPEC.md section 10.6.
/// </summary>
/// <remarks>
/// <para>These bytes are authenticated by the time they arrive, but authenticated only means some
/// creator produced them — not that the creator was an official one, and not that the person who
/// handed you the pages is the person who made them. Every structural rule is still enforced.</para>
///
/// <para>The path rules here are security requirements, not tidiness. A payload package is a list of
/// attacker-influenced file names that will be written to a filesystem.</para>
/// </remarks>
public static class PayloadPackage
{
    public const string ManifestEntryName = "binarypaper-manifest.bin";
    private const string ManifestMagic = "BPMF";
    private const byte ManifestVersion = 1;
    private const ushort StoredMethod = 0;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static RestoredContent Parse(byte[] packageBytes)
    {
        Dictionary<string, ushort> methods = ReadCentralDirectoryMethods(packageBytes);

        using var memory = new MemoryStream(packageBytes, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: StrictUtf8);
        }
        catch (Exception ex)
        {
            throw Package(RecoveryErrorCategory.PackageMalformed, $"Payload package is not a readable ZIP: {ex.Message}");
        }

        using (archive)
        {
            var entries = new List<PayloadEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            byte[]? manifestBytes = null;
            int manifestCount = 0;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                // A trailing separator is how ZIP marks a directory. Skip those rather than treating
                // them as empty files.
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                string path = ValidatePath(entry.FullName);
                EnsureStored(methods, entry);

                if (!seen.Add(path))
                {
                    throw Package(RecoveryErrorCategory.PackageInvalidEntry,
                        $"Payload package declares '{path}' more than once.");
                }

                byte[] bytes = ReadEntry(entry);

                if (path == ManifestEntryName)
                {
                    manifestCount++;
                    manifestBytes = bytes;
                    continue;
                }

                entries.Add(new PayloadEntry(path, bytes));
            }

            if (manifestCount != 1 || manifestBytes is null)
            {
                throw Package(RecoveryErrorCategory.PackageManifestInvalid,
                    $"Payload package must contain exactly one {ManifestEntryName}; found {manifestCount}.");
            }

            (bool isTextNote, string displayName) = ParseManifest(manifestBytes);

            if (entries.Count == 0)
            {
                throw Package(RecoveryErrorCategory.PackageMalformed,
                    "Payload package contains a manifest but no content entries.");
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

            if (isTextNote)
            {
                if (entries.Count != 1)
                {
                    throw Package(RecoveryErrorCategory.PackageMalformed,
                        $"A text note must have exactly one content entry; this package has {entries.Count}.");
                }

                string text;
                try
                {
                    text = StrictUtf8.GetString(entries[0].Bytes);
                }
                catch (DecoderFallbackException)
                {
                    throw Package(RecoveryErrorCategory.PackageInvalidEntry,
                        "The text-note entry is not valid UTF-8.");
                }

                return new RestoredContent(PayloadKind.TextNote, displayName, entries, text);
            }

            PayloadKind kind = entries.Count == 1 ? PayloadKind.SingleFile : PayloadKind.FileTree;
            return new RestoredContent(kind, displayName, entries, null);
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        try
        {
            using Stream stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            throw Package(RecoveryErrorCategory.PackageMalformed,
                $"Payload entry '{entry.FullName}' could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates an entry path and returns it <b>unchanged</b>.
    /// </summary>
    /// <remarks>
    /// Deliberately rejects rather than normalizes. Rewriting a backslash into a separator lets two
    /// distinct declared paths collide, so what should have been a rejection silently becomes a
    /// duplicate that overwrites the first entry.
    /// </remarks>
    private static string ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw Package(RecoveryErrorCategory.PackageInvalidEntry, "A payload entry has an empty path.");
        }

        if (path.Contains('\\'))
        {
            throw Package(RecoveryErrorCategory.PackageInvalidEntry,
                $"Payload entry path '{path}' contains a backslash; paths use '/' only.");
        }

        if (path.StartsWith('/'))
        {
            throw Package(RecoveryErrorCategory.PackageInvalidEntry,
                $"Payload entry path '{path}' is absolute.");
        }

        if (path.Length >= 2 && path[1] == ':')
        {
            throw Package(RecoveryErrorCategory.PackageInvalidEntry,
                $"Payload entry path '{path}' is a drive path.");
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0)
            {
                throw Package(RecoveryErrorCategory.PackageInvalidEntry,
                    $"Payload entry path '{path}' has an empty segment.");
            }

            if (segment is "." or "..")
            {
                throw Package(RecoveryErrorCategory.PackageInvalidEntry,
                    $"Payload entry path '{path}' contains a '{segment}' segment.");
            }
        }

        return path;
    }

    private static void EnsureStored(IReadOnlyDictionary<string, ushort> methods, ZipArchiveEntry entry)
    {
        if (!methods.TryGetValue(entry.FullName, out ushort method))
        {
            throw Package(RecoveryErrorCategory.PackageMalformed,
                $"Payload entry '{entry.FullName}' has no central-directory record.");
        }

        if (method != StoredMethod)
        {
            throw Package(RecoveryErrorCategory.PackageInvalidEntry,
                $"Payload entry '{entry.FullName}' uses compression method {method}. The capsule compresses "
                + "the whole package once with LZMA; per-entry compression is never emitted.");
        }
    }

    /// <summary>
    /// Reads each entry's compression method from the ZIP central directory.
    /// </summary>
    /// <remarks>
    /// <c>ZipArchiveEntry</c> does not expose the method, and inferring it from
    /// <c>CompressedLength == Length</c> is a heuristic rather than a check. A rule published as a
    /// MUST deserves an actual read.
    /// </remarks>
    private static Dictionary<string, ushort> ReadCentralDirectoryMethods(ReadOnlySpan<byte> package)
    {
        const uint EndOfCentralDirectorySignature = 0x06054B50;
        const uint CentralFileHeaderSignature = 0x02014B50;
        const int EndOfCentralDirectoryMinLength = 22;

        var methods = new Dictionary<string, ushort>(StringComparer.Ordinal);

        if (package.Length < EndOfCentralDirectoryMinLength)
        {
            throw Package(RecoveryErrorCategory.PackageMalformed, "Payload package is too short to be a ZIP archive.");
        }

        int eocd = -1;
        int floor = Math.Max(0, package.Length - (EndOfCentralDirectoryMinLength + ushort.MaxValue));
        for (int i = package.Length - EndOfCentralDirectoryMinLength; i >= floor; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(package[i..]) == EndOfCentralDirectorySignature)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            throw Package(RecoveryErrorCategory.PackageMalformed,
                "Payload package has no ZIP end-of-central-directory record.");
        }

        ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(package[(eocd + 10)..]);
        uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(package[(eocd + 16)..]);
        if (directoryOffset > (uint)package.Length)
        {
            throw Package(RecoveryErrorCategory.PackageMalformed, "ZIP central directory offset is out of range.");
        }

        int position = (int)directoryOffset;
        for (int i = 0; i < entryCount; i++)
        {
            if (position + 46 > package.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(package[position..]) != CentralFileHeaderSignature)
            {
                throw Package(RecoveryErrorCategory.PackageMalformed, "ZIP central directory is malformed.");
            }

            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(package[(position + 10)..]);
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(package[(position + 28)..]);
            ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(package[(position + 30)..]);
            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(package[(position + 32)..]);

            int nameStart = position + 46;
            if (nameStart + nameLength > package.Length)
            {
                throw Package(RecoveryErrorCategory.PackageMalformed, "ZIP central directory entry name is out of range.");
            }

            string name;
            try
            {
                name = StrictUtf8.GetString(package.Slice(nameStart, nameLength));
            }
            catch (DecoderFallbackException)
            {
                throw Package(RecoveryErrorCategory.PackageInvalidEntry,
                    "A payload entry name is not valid UTF-8.");
            }

            methods[name] = method;
            position = nameStart + nameLength + extraLength + commentLength;
        }

        return methods;
    }

    private static (bool IsTextNote, string DisplayName) ParseManifest(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            throw Package(RecoveryErrorCategory.PackageManifestInvalid,
                $"Manifest is {bytes.Length} bytes; the fixed header alone is 8.");
        }

        if (bytes[0] != (byte)'B' || bytes[1] != (byte)'P' || bytes[2] != (byte)'M' || bytes[3] != (byte)'F')
        {
            throw Package(RecoveryErrorCategory.PackageManifestInvalid, $"Manifest magic must be {ManifestMagic}.");
        }

        if (bytes[4] != ManifestVersion)
        {
            throw Package(RecoveryErrorCategory.PackageManifestInvalid,
                $"Unsupported manifest version {bytes[4]}.");
        }

        byte isTextNote = bytes[5];
        if (isTextNote is not (0x00 or 0x01))
        {
            throw Package(RecoveryErrorCategory.PackageManifestInvalid,
                $"Manifest is_text_note must be 0 or 1, was {isTextNote}.");
        }

        ushort nameLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6));
        if (8 + nameLength != bytes.Length)
        {
            throw Package(RecoveryErrorCategory.PackageManifestInvalid,
                $"Manifest declares a {nameLength}-byte display name but the entry is {bytes.Length} bytes.");
        }

        if (nameLength == 0)
        {
            throw Package(RecoveryErrorCategory.PackageManifestInvalid, "Manifest display name is required.");
        }

        string displayName;
        try
        {
            displayName = StrictUtf8.GetString(bytes.AsSpan(8, nameLength));
        }
        catch (DecoderFallbackException)
        {
            throw Package(RecoveryErrorCategory.PackageManifestInvalid,
                "Manifest display name is not valid UTF-8.");
        }

        return (isTextNote == 0x01, displayName);
    }

    private static RecoveryException Package(RecoveryErrorCategory category, string message) =>
        new(category, RecoveryStage.Package, message);
}
