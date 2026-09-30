// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using BinaryPaper.Recovery.ImageVectors;
using BinaryPaper.Recovery.Images;
using StbImageSharp;
using Xunit;
using ZXingCpp;

namespace BinaryPaper.Recovery.Tests;

/// <summary>
/// Tests for a page packed as tightly as a printed sheet: equal-size symbols a few modules apart.
/// </summary>
/// <remarks>
/// A lattice offers the detector far more plausible finder-pattern triples than it holds symbols,
/// because patterns from neighbouring symbols line up just like a symbol's own. A clean rendering
/// of such a page once decoded nothing at all, while each of its symbols read instantly on its own.
/// </remarks>
public sealed class DensePageTests
{
    [Fact]
    public void TheWholePageStageReadsEverySymbolOfAFullSheet()
    {
        byte[][] payloads = [.. Enumerable.Range(0, DensePageRenderer.Columns * 7).Select(i => MakePayload(i, 900))];
        (byte[] png, byte[] pixels, int width, int height) = DensePageRenderer.Render(payloads);

        // The page must actually be dense enough to matter, or this test proves nothing.
        Assert.True(DensePageRenderer.CountCrowdedRead(pixels, width, height) < payloads.Length);

        PageImageResult result = new PageImageReader(new ImagePolicy { RectifySymbols = false }).Read("page.png", png);

        Assert.Null(result.Failure);
        var read = result.Symbols.Select(s => Convert.ToHexString(s.Payload)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(payloads.Length, read.Count);
        foreach (byte[] payload in payloads)
        {
            Assert.Contains(Convert.ToHexString(payload), read);
        }
    }

    [Fact]
    public void AFullSheetNeedsNothingFromTheCloserLook()
    {
        byte[][] payloads = [.. Enumerable.Range(100, DensePageRenderer.Columns * 3).Select(i => MakePayload(i, 900))];
        (byte[] png, _, _, _) = DensePageRenderer.Render(payloads);

        PageImageResult result = new PageImageReader().Read("page.png", png);

        Assert.Null(result.Failure);
        Assert.False(result.Truncated);
        Assert.Equal(payloads.Length, result.Symbols.Count);
    }

    [Fact]
    public void PredictedCellsOnBlankPaperFallBelowTheGate()
    {
        // One symbol alone: every predicted neighbour is margin, and must be passed over.
        (byte[] png, _, _, _) = DensePageRenderer.Render([MakePayload(200, 900)]);
        (byte[] grey, int width, int height, Quad symbol) = LocateFirst(Pad(png, 600));

        double own = PageFilter.Texture(grey, width, height, symbol);
        Assert.True(own > 0);

        foreach (Quad neighbour in symbol.Neighbours(width, height))
        {
            Assert.True(PageFilter.Texture(grey, width, height, neighbour) < own * 0.25);
        }
    }

    [Fact]
    public void PredictedCellsOverSymbolsClearTheGate()
    {
        // The centre of a 3x3 block: every neighbour is a symbol, and none may be skipped.
        byte[][] payloads = [.. Enumerable.Range(300, 9).Select(i => MakePayload(i, 900))];
        (byte[] png, _, _, _) = DensePageRenderer.Render(payloads, columns: 3);
        (byte[] grey, int width, int height, Quad[] symbols) = LocateAll(png);

        Quad centre = symbols.OrderBy(q => Math.Abs(q.CentreX - (width / 2.0)) + Math.Abs(q.CentreY - (height / 2.0))).First();
        double own = PageFilter.Texture(grey, width, height, centre);

        foreach (Quad neighbour in centre.Neighbours(width, height))
        {
            Assert.True(PageFilter.Texture(grey, width, height, neighbour) >= own * 0.25);
        }
    }

    private static (byte[] Grey, int Width, int Height, Quad Symbol) LocateFirst(byte[] png)
    {
        (byte[] grey, int width, int height, Quad[] symbols) = LocateAll(png);
        return (grey, width, height, Assert.Single(symbols));
    }

    private static (byte[] Grey, int Width, int Height, Quad[] Symbols) LocateAll(byte[] png)
    {
        ImageResult image = ImageResult.FromMemory(png, ColorComponents.Grey);
        var symbols = new List<Quad>();

        using var options = new ReaderOptions
        {
            Formats = BarcodeFormat.QRCode,
            TryHarder = true,
            TryInvert = false,
            MaxNumberOfSymbols = 255,
        };

        NativeBarcodeReader.Read(image.Data, image.Width, image.Height, ImageFormat.Lum, options, barcode =>
        {
            if (barcode.IsValid && Quad.From(barcode.Position) is { } quad)
            {
                symbols.Add(quad);
            }
        });

        return (image.Data, image.Width, image.Height, [.. symbols]);
    }

    /// <summary>Surrounds a page with white so every neighbour of its symbols lies on the page.</summary>
    private static byte[] Pad(byte[] png, int border)
    {
        ImageResult image = ImageResult.FromMemory(png, ColorComponents.Grey);
        int width = image.Width + (2 * border);
        int height = image.Height + (2 * border);
        byte[] pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);
        for (int y = 0; y < image.Height; y++)
        {
            Array.Copy(image.Data, y * image.Width, pixels, ((y + border) * width) + border, image.Width);
        }

        return PngWriter.WriteGrey(pixels, width, height);
    }

    private static byte[] MakePayload(int seed, int length)
    {
        var random = new Random(seed);
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
