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

    private static byte[] MakePayload(int seed, int length)
    {
        var random = new Random(seed);
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
