// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using BinaryPaper.Recovery.Images;
using Xunit;

namespace BinaryPaper.Recovery.Tests;

/// <summary>
/// Tests for the bound on how long one page image may be worked.
/// </summary>
/// <remarks>
/// The rectify-and-retry ladder stops at the first clean decode, so it costs least on pages that
/// read and most on pages that do not. That makes an ordinary photograph of something which is not
/// a backup page the expensive input, not a rare crafted one, and the encoded-byte and pixel caps
/// bound neither: the most expensive legitimate page measured is a 1.4 MB file.
///
/// What these tests pin is the contract around the bound, not a duration. A test that asserted "a
/// page takes under N seconds" would fail on a loaded machine and teach everyone to ignore it.
/// </remarks>
public sealed class ImageBudgetTests
{
    [Fact]
    public void AnExpiredBudgetReturnsWhatWasDecodedRatherThanFailing()
    {
        byte[][] payloads = [MakePayload(21, 384), MakePayload(22, 384), MakePayload(23, 384)];
        byte[] page = SyntheticPage.Photograph(payloads, tilt: 0.05, blur: 0.9);

        // A budget no real page can meet, so the reader is guaranteed to stop early.
        var policy = new ImagePolicy { MaxDuration = TimeSpan.FromMilliseconds(1) };
        PageImageResult result = new PageImageReader(policy).Read("page.png", page);

        Assert.True(result.Truncated);

        // Whatever it did read is real and usable. Every symbol is a complete QR payload; the
        // capsule layer still validates each one as a frame, and the recovery threshold is met
        // across the whole scan rather than per page.
        foreach (DecodedSymbol symbol in result.Symbols)
        {
            Assert.NotEmpty(symbol.Payload);
        }
    }

    [Fact]
    public void AnExpiredBudgetSaysSoRatherThanClaimingThereWereNoCodes()
    {
        byte[][] payloads = [MakePayload(31, 384)];
        byte[] page = SyntheticPage.Photograph(payloads, tilt: 0.05, blur: 0.9);

        var policy = new ImagePolicy { MaxDuration = TimeSpan.FromMilliseconds(1) };
        PageImageResult result = new PageImageReader(policy).Read("page.png", page);

        // "No QR codes found" would send someone off to re-shoot a page that was never read.
        if (result.Symbols.Count == 0)
        {
            Assert.NotNull(result.Failure);
            Assert.Contains("budget", result.Failure);
        }
    }

    [Fact]
    public void ANormalPageIsNotReportedAsTruncated()
    {
        byte[][] payloads = [MakePayload(41, 256), MakePayload(42, 256)];
        byte[] page = SyntheticPage.Photograph(payloads, tilt: 0.0, blur: 0.0);

        PageImageResult result = new PageImageReader().Read("page.png", page);

        Assert.Null(result.Failure);
        Assert.False(result.Truncated);
        Assert.Equal(payloads.Length, result.Symbols.Count);
    }

    [Fact]
    public void ZeroDisablesTheBound()
    {
        byte[][] payloads = [MakePayload(51, 256), MakePayload(52, 256)];
        byte[] page = SyntheticPage.Photograph(payloads, tilt: 0.0, blur: 0.0);

        var policy = new ImagePolicy { MaxDuration = TimeSpan.Zero };
        PageImageResult result = new PageImageReader(policy).Read("page.png", page);

        Assert.False(result.Truncated);
        Assert.Equal(payloads.Length, result.Symbols.Count);
    }

    [Fact]
    public void ProgressIsReportedWhileAPageIsWorked()
    {
        // A bounded wait that looks exactly like a hang is still a tool the user kills, so the
        // reader has to be able to say what it is doing.
        byte[][] payloads = [MakePayload(61, 384), MakePayload(62, 384), MakePayload(63, 384)];
        byte[] page = SyntheticPage.Photograph(payloads, tilt: 0.05, blur: 0.9);

        var reported = new List<PageImageProgress>();
        var reader = new PageImageReader(ImagePolicy.Default, reported.Add);

        PageImageResult result = reader.Read("page.png", page);

        Assert.NotEmpty(reported);
        Assert.All(reported, p => Assert.Equal("page.png", p.Source));
        Assert.All(reported, p => Assert.True(p.SymbolsFound <= result.Symbols.Count));
    }

    [Fact]
    public void AnOversizedImageIsRefusedWithoutDecodingIt()
    {
        // 400 megapixels, twice the default cap, in a file of a few hundred kilobytes: page images
        // are mostly white and compress accordingly, so encoded size predicts nothing about
        // decoded size. Deciding after the decode means the allocation the cap exists to prevent
        // has already happened - measured at 785 MB of working set for this input before the fix.
        byte[] page = OversizedPng(20000, 20000);
        Assert.True(page.Length < 1_000_000, "the point of this fixture is that it is small");

        long before = GC.GetTotalAllocatedBytes(precise: true);
        PageImageResult result = new PageImageReader().Read("oversize.png", page);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Empty(result.Symbols);
        Assert.NotNull(result.Failure);
        Assert.Contains("pixel limit", result.Failure);
        Assert.Equal(20000, result.Width);
        Assert.Equal(20000, result.Height);

        // The refusal must be cheap. A full decode of this page is 400 MB; anything in that region
        // means the cap is being applied after the allocation again.
        Assert.True(allocated < 32 * 1024 * 1024,
            $"refusing an oversized image allocated {allocated / (1024 * 1024)} MB; it should not decode it at all");
    }

    /// <summary>
    /// A valid 8-bit greyscale PNG of the given size, all white, written directly so the fixture
    /// stays a few hundred kilobytes rather than the size of its pixels.
    /// </summary>
    private static byte[] OversizedPng(int width, int height)
    {
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new MemoryStream();
        WriteBigEndian(header, width);
        WriteBigEndian(header, height);
        header.Write([8, 0, 0, 0, 0]); // 8-bit, greyscale, deflate, adaptive filtering, no interlace
        WriteChunk(png, "IHDR"u8, header.ToArray());

        var raw = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(
            raw, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] row = new byte[width + 1];
            row[0] = 0; // filter type: none
            Array.Fill(row, (byte)0xFF, 1, width);
            for (int y = 0; y < height; y++)
            {
                deflate.Write(row);
            }
        }

        WriteChunk(png, "IDAT"u8, raw.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream target, ReadOnlySpan<byte> type, byte[] data)
    {
        WriteBigEndian(target, data.Length);
        target.Write(type);
        target.Write(data);

        byte[] crcInput = [.. type, .. data];
        WriteBigEndian(target, unchecked((int)Crc32(crcInput)));
    }

    private static void WriteBigEndian(Stream target, int value) =>
        target.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    /// <summary>PNG's CRC-32 (IEEE), which is not the CRC-32C the capsule format uses.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
            }
        }

        return ~crc;
    }

    private static byte[] MakePayload(int seed, int length)
    {
        var random = new Random(seed);
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
