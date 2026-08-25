// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using BinaryPaper.Recovery.ImageVectors;
using BinaryPaper.Recovery.Images;
using Xunit;
using ZXing;
using ZXing.QrCode;

namespace BinaryPaper.Recovery.Tests;

/// <summary>
/// Tests for reading a page that was photographed rather than scanned.
/// </summary>
/// <remarks>
/// The image conformance vectors are clean, flat, axis-aligned renderings, and they pass on a
/// decoder that cannot read a photograph at all — that is exactly how the kit came to ship one.
/// What these tests add is a page that is not flat: perspective, blur, and a lattice of symbols
/// with a gutter between them, which is what a phone actually hands you.
///
/// Everything here is synthesised in-process. No photograph is committed, and none is needed.
/// </remarks>
public sealed class PhotographedPageTests
{
    [Fact]
    public void ReadsEverySymbolFromAPageSeenAtAnAngle()
    {
        byte[][] payloads =
        [
            MakePayload(0, 512),
            MakePayload(1, 512),
            MakePayload(2, 512),
            MakePayload(3, 512),
        ];

        byte[] page = SyntheticPage.Photograph(payloads, tilt: 0.06, blur: 1.1);

        PageImageResult result = new PageImageReader().Read("page.png", page);

        Assert.Null(result.Failure);
        var read = result.Symbols.Select(s => Convert.ToHexString(s.Payload)).ToHashSet(StringComparer.Ordinal);
        foreach (byte[] payload in payloads)
        {
            Assert.Contains(Convert.ToHexString(payload), read);
        }
    }

    [Fact]
    public void StillReadsAFlatPage()
    {
        // The photograph path must not cost anything on the input the kit was already good at.
        byte[][] payloads = [MakePayload(7, 256), MakePayload(8, 256)];
        byte[] page = SyntheticPage.Photograph(payloads, tilt: 0.0, blur: 0.0);

        PageImageResult result = new PageImageReader().Read("page.png", page);

        Assert.Null(result.Failure);
        Assert.Equal(payloads.Length, result.Symbols.Count);
    }

    [Fact]
    public void APayloadIsNeverReturnedTwice()
    {
        // A symbol is found again by several variants and again as a predicted neighbour of itself.
        // Handing the capsule layer the same frame repeatedly would inflate the "scanned" count and
        // tell a user they had more of their backup than they do.
        byte[][] payloads = [MakePayload(11, 400), MakePayload(12, 400), MakePayload(13, 400)];
        byte[] page = SyntheticPage.Photograph(payloads, tilt: 0.04, blur: 0.8);

        PageImageResult result = new PageImageReader().Read("page.png", page);

        Assert.Equal(result.Symbols.Count, result.Symbols.Select(s => Convert.ToHexString(s.Payload)).Distinct().Count());
    }

    private static byte[] MakePayload(int seed, int length)
    {
        // Deterministic, and deliberately full-range: a frame is arbitrary binary, so a payload
        // made only of printable bytes would not exercise the byte path.
        var random = new Random(seed);
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}

/// <summary>
/// Tests for the page geometry the photograph path is built on.
/// </summary>
public sealed class PageGeometryTests
{
    [Fact]
    public void WarpPutsTheQuadCornersOnTheDestinationCorners()
    {
        const int width = 400, height = 300, size = 120, padding = 10;
        byte[] source = new byte[width * height];
        Array.Fill(source, (byte)255);

        // A quad seen at an angle: the right edge is shorter than the left.
        Quad quad = QuadFor([40, 300, 300, 40], [30, 60, 240, 270]);

        byte[] warped = PageFilter.Warp(source, width, height, quad, size, padding);

        Assert.Equal(size * size, warped.Length);

        // Mark each source corner and confirm it lands on the matching destination corner.
        (int dx, int dy)[] expected =
        [
            (padding, padding),
            (size - padding - 1, padding),
            (size - padding - 1, size - padding - 1),
            (padding, size - padding - 1),
        ];

        for (int corner = 0; corner < 4; corner++)
        {
            byte[] marked = new byte[width * height];
            Array.Fill(marked, (byte)255);
            Mark(marked, width, height, (int)quad.X[corner], (int)quad.Y[corner]);

            byte[] result = PageFilter.Warp(marked, width, height, quad, size, padding);
            (int dx, int dy) = expected[corner];

            Assert.True(Darkest(result, size, dx, dy) < 200,
                $"corner {corner} did not land near ({dx},{dy})");
        }
    }

    [Fact]
    public void BlurIsIdentityAtZeroAndPreservesSize()
    {
        byte[] source = new byte[64 * 64];
        new Random(3).NextBytes(source);

        Assert.Same(source, PageFilter.Blur(source, 64, 64, 0));

        byte[] blurred = PageFilter.Blur(source, 64, 64, 1.5);
        Assert.Equal(source.Length, blurred.Length);
        Assert.NotEqual(Convert.ToHexString(source), Convert.ToHexString(blurred));
    }

    [Fact]
    public void NeighboursCoverTheEightSurroundingCells()
    {
        // A 100-wide symbol in the middle of a large page: nothing is clipped, so every gutter
        // guess should offer all eight neighbours.
        Quad quad = QuadFor([500, 600, 600, 500], [500, 500, 600, 600]);

        var neighbours = quad.Neighbours(2000, 2000).ToList();
        Assert.Equal(24, neighbours.Count);

        // The cell directly to the right sits one symbol side plus the gutter away.
        const double gutter = 0.055;
        Assert.Contains(neighbours, n =>
            Math.Abs(n.CentreX - (quad.CentreX + (100 * (1 + gutter)))) < 0.001 &&
            Math.Abs(n.CentreY - quad.CentreY) < 0.001);
    }

    [Fact]
    public void NeighboursOffThePageAreDropped()
    {
        // A symbol in the top-left corner: the cells above and to the left of it do not exist.
        Quad quad = QuadFor([10, 110, 110, 10], [10, 10, 110, 110]);

        var neighbours = quad.Neighbours(2000, 2000).ToList();

        Assert.NotEmpty(neighbours);
        Assert.All(neighbours, n => Assert.True(n.CentreX > 0 && n.CentreY > 0));
        // Only right, down, and down-right stay on the page; the other five directions step off it
        // by more than the slack allows. Three surviving directions for each of the three gutter
        // guesses.
        Assert.Equal(9, neighbours.Count);
    }

    private static Quad QuadFor(double[] xs, double[] ys)
    {
        var position = new ZXingCpp.Position
        {
            TopLeft = new ZXingCpp.PointI { X = (int)xs[0], Y = (int)ys[0] },
            TopRight = new ZXingCpp.PointI { X = (int)xs[1], Y = (int)ys[1] },
            BottomRight = new ZXingCpp.PointI { X = (int)xs[2], Y = (int)ys[2] },
            BottomLeft = new ZXingCpp.PointI { X = (int)xs[3], Y = (int)ys[3] },
        };

        Quad? quad = Quad.From(position);
        Assert.NotNull(quad);
        return quad;
    }

    private static void Mark(byte[] pixels, int width, int height, int x, int y)
    {
        for (int dy = -3; dy <= 3; dy++)
        {
            for (int dx = -3; dx <= 3; dx++)
            {
                int px = Math.Clamp(x + dx, 0, width - 1);
                int py = Math.Clamp(y + dy, 0, height - 1);
                pixels[(py * width) + px] = 0;
            }
        }
    }

    private static byte Darkest(byte[] pixels, int size, int x, int y)
    {
        byte darkest = 255;
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int px = Math.Clamp(x + dx, 0, size - 1);
                int py = Math.Clamp(y + dy, 0, size - 1);
                darkest = Math.Min(darkest, pixels[(py * size) + px]);
            }
        }

        return darkest;
    }
}

/// <summary>
/// Builds a page of QR symbols and then abuses it the way a camera does.
/// </summary>
internal static class SyntheticPage
{
    private const int ModuleSize = 5;
    private const int Quiet = 4;
    private const int Gutter = 30;

    /// <summary>
    /// Renders <paramref name="payloads"/> as a lattice, then applies a keystone of
    /// <paramref name="tilt"/> (as a fraction of page width) and a Gaussian <paramref name="blur"/>.
    /// </summary>
    public static byte[] Photograph(IReadOnlyList<byte[]> payloads, double tilt, double blur)
    {
        List<bool[,]> matrices = [.. payloads.Select(Encode)];

        int cell = (matrices.Max(m => m.GetLength(0)) * ModuleSize) + (2 * Quiet * ModuleSize);
        int columns = Math.Min(2, matrices.Count);
        int rows = (matrices.Count + columns - 1) / columns;

        int width = (columns * cell) + ((columns + 1) * Gutter);
        int height = (rows * cell) + ((rows + 1) * Gutter);

        byte[] page = new byte[width * height];
        Array.Fill(page, (byte)255);

        for (int i = 0; i < matrices.Count; i++)
        {
            Blit(page, width, matrices[i],
                Gutter + ((i % columns) * (cell + Gutter)),
                Gutter + ((i / columns) * (cell + Gutter)));
        }

        if (tilt > 0)
        {
            page = Keystone(page, width, height, tilt);
        }

        if (blur > 0)
        {
            page = GaussianBlur(page, width, height, blur);
        }

        return PngWriter.WriteGrey(page, width, height);
    }

    /// <summary>A keystone: the top edge is squeezed inward, as when the camera looks down at a page.</summary>
    private static byte[] Keystone(byte[] source, int width, int height, double tilt)
    {
        byte[] result = new byte[width * height];
        Array.Fill(result, (byte)255);
        double inset = width * tilt;

        for (int y = 0; y < height; y++)
        {
            // Full squeeze at the top, none at the bottom.
            double t = 1.0 - ((double)y / (height - 1));
            double left = inset * t;
            double scale = (width - (2 * inset * t)) / width;

            for (int x = 0; x < width; x++)
            {
                double sx = (x - left) / scale;
                if (sx < 0 || sx > width - 1)
                {
                    continue;
                }

                int x0 = (int)sx;
                int x1 = Math.Min(x0 + 1, width - 1);
                double fx = sx - x0;
                double value = (source[(y * width) + x0] * (1 - fx)) + (source[(y * width) + x1] * fx);
                result[(y * width) + x] = (byte)Math.Clamp(value, 0, 255);
            }
        }

        return result;
    }

    private static byte[] GaussianBlur(byte[] source, int width, int height, double sigma)
    {
        // The reader's own blur, used here as a stand-in for camera softness. It is separable and
        // symmetric, which is all this needs to be.
        return PageFilter.Blur(source, width, height, sigma);
    }

    private static bool[,] Encode(byte[] payload)
    {
        // ISO-8859-1 is the round-trip-safe single-byte mapping ZXing uses to accept arbitrary
        // bytes through its string-shaped encoder API.
        var latin1 = System.Text.Encoding.GetEncoding("ISO-8859-1");
        var writer = new QRCodeWriter();
        var matrix = writer.encode(
            latin1.GetString(payload), BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.CHARACTER_SET] = "ISO-8859-1",
                [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
                [EncodeHintType.MARGIN] = 0,
            });

        var bits = new bool[matrix.Height, matrix.Width];
        for (int y = 0; y < matrix.Height; y++)
        {
            for (int x = 0; x < matrix.Width; x++)
            {
                bits[y, x] = matrix[x, y];
            }
        }

        return bits;
    }

    private static void Blit(byte[] page, int pageWidth, bool[,] matrix, int originX, int originY)
    {
        int modules = matrix.GetLength(0);
        for (int my = 0; my < modules; my++)
        {
            for (int mx = 0; mx < modules; mx++)
            {
                if (!matrix[my, mx])
                {
                    continue;
                }

                int x0 = originX + ((mx + Quiet) * ModuleSize);
                int y0 = originY + ((my + Quiet) * ModuleSize);
                for (int y = y0; y < y0 + ModuleSize; y++)
                {
                    for (int x = x0; x < x0 + ModuleSize; x++)
                    {
                        page[(y * pageWidth) + x] = 0;
                    }
                }
            }
        }
    }
}
