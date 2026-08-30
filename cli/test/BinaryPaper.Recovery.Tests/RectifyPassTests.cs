// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using BinaryPaper.Recovery.Images;
using Xunit;

namespace BinaryPaper.Recovery.Tests;

/// <summary>
/// Tests for reading a page without the rectify-and-retry stage.
/// </summary>
/// <remarks>
/// The stage is expensive and its value depends entirely on how much the detector is struggling:
/// it rescues symbols that were located but not read. Callers therefore run the cheap whole-page
/// sweep over everything first and turn rectification on only for pages that did not yield enough,
/// so the switch has to leave the cheap path intact and honest about what it did.
/// </remarks>
public sealed class RectifyPassTests
{
    [Fact]
    public void TheSweepAloneStillReadsAFlatPage()
    {
        byte[][] payloads = [MakePayload(81, 256), MakePayload(82, 256)];
        byte[] page = SyntheticPage.Photograph(payloads, tilt: 0.0, blur: 0.0);

        PageImageResult result = new PageImageReader(new ImagePolicy { RectifySymbols = false })
            .Read("page.png", page);

        Assert.Null(result.Failure);
        Assert.Equal(payloads.Length, result.Symbols.Count);
    }

    [Fact]
    public void SkippingTheStageIsNotReportedAsTruncation()
    {
        // Truncated means "cut short before finishing". Choosing a cheaper method is not that, and
        // conflating them would tell a user to re-shoot a page that was read perfectly well.
        byte[][] payloads = [MakePayload(91, 256)];
        byte[] page = SyntheticPage.Photograph(payloads, tilt: 0.0, blur: 0.0);

        PageImageResult result = new PageImageReader(new ImagePolicy { RectifySymbols = false })
            .Read("page.png", page);

        Assert.False(result.Truncated);
    }

    [Fact]
    public void TheStageIsOnByDefault()
    {
        // The switch exists for callers that have decided the cheap pass was enough. Anyone
        // constructing a reader without an opinion gets the thorough behaviour.
        Assert.True(ImagePolicy.Default.RectifySymbols);
    }

    private static byte[] MakePayload(int seed, int length)
    {
        var random = new Random(seed);
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
