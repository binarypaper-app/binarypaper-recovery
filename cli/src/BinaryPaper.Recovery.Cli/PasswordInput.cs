// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace BinaryPaper.Recovery.Cli;

internal static class PasswordInput
{
    // A pipe carries bytes, not a Windows console code page. Define one portable encoding
    // before NFC normalization/key derivation. Invalid bytes must not become a different key.
    internal static string? ReadUtf8(Stream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            string? line = reader.ReadLine();
            if (line?.StartsWith('\uFEFF') == true) line = line[1..];
            return string.IsNullOrEmpty(line) ? null : line;
        }
        catch (DecoderFallbackException)
        {
            throw new UsageException("Password input must be UTF-8.");
        }
    }
}
