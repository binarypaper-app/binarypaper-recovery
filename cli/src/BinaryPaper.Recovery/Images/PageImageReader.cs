// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using StbImageSharp;
using ZXingCpp;

namespace BinaryPaper.Recovery.Images;

public sealed record DecodedSymbol(string Source, byte[] Payload);

public sealed record PageImageResult(
    string Source,
    int Width,
    int Height,
    IReadOnlyList<DecodedSymbol> Symbols,
    string? Failure)
{
    /// <summary>
    /// The page's time budget expired before the pipeline finished, so <see cref="Symbols"/> is
    /// what had been decoded by then rather than everything the page holds.
    /// </summary>
    /// <remarks>
    /// Not a failure. Every symbol reported is a complete QR payload and is still validated as a
    /// frame before it can influence a session, and the recovery threshold is evaluated across the
    /// whole scan rather than per page. It is surfaced so a caller can say so, because a page that
    /// was cut short is worth re-shooting and a page that was read out is not.
    /// </remarks>
    public bool Truncated { get; init; }
}

/// <summary>
/// Progress on one page, reported while it is being worked.
/// </summary>
/// <remarks>
/// A bounded wait that looks exactly like a hang is still a tool the user kills. One page can
/// legitimately take a minute, so the reader says what it is doing rather than going silent.
/// </remarks>
public sealed record PageImageProgress(
    string Source,
    int SymbolsFound,
    int PositionsTried,
    TimeSpan Elapsed);

/// <summary>
/// Turns page images into frame bytes: PNG/JPEG in, QR payloads out.
/// </summary>
/// <remarks>
/// <para>Image decoding is a large attack surface with a long history of memory-safety bugs, and
/// every image handed to this tool is untrusted. Type is determined by content rather than
/// extension, dimensions are checked against a policy before a full decode is attempted, and a
/// failure on one image never discards frames already recovered from another.</para>
///
/// <para>The payload is taken from the decoder's <b>raw bytes</b>, never from <c>Barcode.Text</c>.
/// QR payloads here are arbitrary binary — a frame routinely contains <c>0x00</c> and byte
/// sequences that are not valid text in any encoding — and letting a decoded payload round-trip
/// through a string silently corrupts it.</para>
///
/// <para>The realistic disaster input is a phone photograph, not a flatbed scan, so one pass over
/// the whole page is not enough. A production page carries version-40 symbols (177 modules), and at
/// that size a detector has to hold its sampling grid across the perspective and lens curvature of
/// a hand-held shot. What actually gets the frames out is the second stage below: rectify each
/// located symbol, retry it, and use the ones that decode to predict where their neighbours are.
/// Measured on twelve photographs of a 48-code page, one whole-page pass found 20 frames and the
/// full pipeline found 45.</para>
/// </remarks>
public sealed class PageImageReader(ImagePolicy? policy = null, Action<PageImageProgress>? progress = null)
{
    private readonly ImagePolicy _policy = policy ?? ImagePolicy.Default;

    /// <summary>How often progress is reported. Often enough to look alive, rarely enough to read.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    /// <summary>File signatures, so type comes from content and not from a name anyone can choose.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool LooksLikePng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature);

    public static bool LooksLikeJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    public static bool LooksLikeImage(ReadOnlySpan<byte> bytes) =>
        LooksLikePng(bytes) || LooksLikeJpeg(bytes);

    /// <summary>
    /// Decodes one page image and returns every QR payload found in it.
    /// </summary>
    /// <remarks>
    /// Never throws for a bad image: the failure is returned on the result so a directory of pages
    /// keeps processing. Losing the frames from twenty good pages because the twenty-first is
    /// corrupt would be the wrong behavior for a recovery tool.
    /// </remarks>
    public PageImageResult Read(string source, byte[] bytes)
    {
        if (!LooksLikeImage(bytes))
        {
            return new PageImageResult(source, 0, 0, [], "not a PNG or JPEG (checked by content, not by file name)");
        }

        if (bytes.LongLength > _policy.MaxEncodedBytes)
        {
            return new PageImageResult(source, 0, 0, [],
                $"image is {bytes.LongLength} bytes, above the {_policy.MaxEncodedBytes}-byte limit");
        }

        // The pixel cap is enforced from the header, before any pixel buffer exists.
        //
        // Encoded size does not predict decoded size and the gap is not marginal: a page image is
        // mostly white and compresses accordingly, so a 439 KB file can legitimately declare 400
        // megapixels. Decoding first and checking afterwards means the allocation the cap exists to
        // prevent has already happened by the time the cap refuses it - measured at 785 MB of
        // working set for that 439 KB file, from an input the reader then correctly rejected.
        ImageInfo? info;
        try
        {
            info = ImageInfo.FromStream(new MemoryStream(bytes, writable: false));
        }
        catch (Exception ex)
        {
            return new PageImageResult(source, 0, 0, [], $"could not be decoded: {ex.Message}");
        }

        if (info is null)
        {
            return new PageImageResult(source, 0, 0, [], "could not be decoded: no readable image header");
        }

        int declaredWidth = info.Value.Width;
        int declaredHeight = info.Value.Height;

        if (declaredWidth <= 0 || declaredHeight <= 0)
        {
            return new PageImageResult(source, declaredWidth, declaredHeight, [], "image has no pixels");
        }

        long pixels = (long)declaredWidth * declaredHeight;
        if (pixels > _policy.MaxPixels)
        {
            return new PageImageResult(source, declaredWidth, declaredHeight, [],
                $"image is {declaredWidth}x{declaredHeight} ({pixels} pixels), above the {_policy.MaxPixels}-pixel limit");
        }

        // Decoding is inside the budget too. It is bounded work, but on a large page it is seconds
        // of it, and a bound that starts after the expensive part is not the bound it claims to be.
        var clock = Stopwatch.StartNew();

        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(bytes, ColorComponents.Grey);
        }
        catch (Exception ex)
        {
            return new PageImageResult(source, declaredWidth, declaredHeight, [],
                $"could not be decoded: {ex.Message}");
        }

        if (image.Width <= 0 || image.Height <= 0)
        {
            return new PageImageResult(source, image.Width, image.Height, [], "image has no pixels");
        }

        try
        {
            List<DecodedSymbol> symbols = DecodeSymbols(
                source, image.Data, image.Width, image.Height, clock, out bool truncated);

            string? failure = (symbols.Count, truncated) switch
            {
                (0, true) => $"no QR codes found before the {_policy.MaxDuration.TotalSeconds:F0}s "
                    + "budget for this page expired",
                (0, false) => "no QR codes found",
                _ => null,
            };

            return new PageImageResult(source, image.Width, image.Height, symbols, failure)
            {
                Truncated = truncated,
            };
        }
        catch (Exception ex)
        {
            return new PageImageResult(source, image.Width, image.Height, [], $"QR detection failed: {ex.Message}");
        }
        finally
        {
            // Page scans are large. Drop the pixel buffer promptly rather than holding every page's
            // bitmap alive while a directory is processed.
            image.Data = [];
        }
    }

    private static ReaderOptions Options(Binarizer binarizer, bool returnErrors) => new()
    {
        Formats = BarcodeFormat.QRCode,
        TryHarder = true,
        TryRotate = true,
        TryDownscale = true,
        // A BinaryPaper page is dark-on-light. Searching for the inverse doubles the work for a
        // case the format never produces.
        TryInvert = false,
        MaxNumberOfSymbols = MaxSymbolsPerRead,
        ReturnErrors = returnErrors,
        Binarizer = binarizer,
    };

    private const int MaxSymbolsPerRead = 64;

    /// <summary>How far the neighbour prediction is allowed to walk out from a decoded symbol.</summary>
    private const int LatticePasses = 4;

    private List<DecodedSymbol> DecodeSymbols(
        string source, byte[] grey, int width, int height, Stopwatch clock, out bool truncated)
    {
        truncated = false;
        var symbols = new List<DecodedSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The budget is wall-clock rather than a work count on purpose. The same 400 rectify
        // attempts cost about a second on a page that reads and twenty-odd minutes on one that
        // does not, because the ladder stops at the first clean decode and so pays its full price
        // only where there is nothing to find. Time is the unit that tracks what the user waits
        // for; bytes and pixels bound the other dimensions.
        //
        // The clock is started by the caller, before the image is decoded, so decoding is inside
        // the budget rather than free.
        TimeSpan budget = _policy.MaxDuration;
        var lastReport = TimeSpan.Zero;
        int positionsTried = 0;

        bool Expired() => budget > TimeSpan.Zero && clock.Elapsed >= budget;

        bool reportedOnce = false;

        void Report()
        {
            // The first report always goes out, however fast the page is: naming the page being
            // worked the moment work starts is most of the value. After that it is throttled.
            if (progress is null || (reportedOnce && clock.Elapsed - lastReport < ProgressInterval))
            {
                return;
            }

            reportedOnce = true;
            lastReport = clock.Elapsed;
            progress(new PageImageProgress(source, symbols.Count, positionsTried, clock.Elapsed));
        }

        void Accept(Barcode barcode)
        {
            // Only a clean decode. zxing-cpp will report a symbol whose Reed-Solomon stage failed,
            // and its bytes look plausible; admitting those would push frames at the capsule layer
            // that can only fail their CRC-32C, which reads like damaged paper rather than a
            // decoder that guessed.
            if (!barcode.IsValid)
            {
                return;
            }

            byte[]? payload = barcode.Bytes;
            if (payload is null || payload.Length == 0)
            {
                return;
            }

            if (seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))))
            {
                symbols.Add(new DecodedSymbol(source, payload));
            }
        }

        // ---------------------------------------------------------------- stage 1: the whole page
        //
        // Cheap, and on a clean scan it is the whole job. On a photograph it mostly serves to locate
        // symbols: a detection that failed to decode still carries a usable position, and stage 2
        // needs somewhere to start.
        var cells = new List<Quad>();
        foreach (double sigma in new[] { 0.0, 1.4 })
        {
            byte[] plane = PageFilter.Blur(grey, width, height, sigma);
            var view = new ImageView(plane, width, height, ImageFormat.Lum);

            foreach (Binarizer binarizer in new[] { Binarizer.LocalAverage, Binarizer.GlobalHistogram })
            {
                foreach (Barcode barcode in BarcodeReader.Read(view, Options(binarizer, returnErrors: true)))
                {
                    Accept(barcode);
                    Quad? quad = Quad.From(barcode.Position);
                    if (quad is not null && quad.Side >= MinimumSymbolSide && !IsNear(cells, quad))
                    {
                        cells.Add(quad);
                    }
                }

                // Nothing references the view once the read has its pointer, so without this the
                // finalizer - which deletes the native view - is free to run while the read is
                // still using it.
                GC.KeepAlive(view);

                Report();

                // A full-page read at the pixel cap is minutes on its own, so the budget has to be
                // checked between passes here and not only in stage 2.
                if (Expired())
                {
                    truncated = true;
                    return symbols;
                }
            }
        }

        // ------------------------------------------------- stage 2: rectify, retry, walk the lattice
        //
        // A page is a lattice of equal-sized symbols. Once one of them decodes, its neighbours sit a
        // fixed step away along the same two axes, so a symbol whose own finder patterns were never
        // detected — clipped by the frame edge, washed out by glare — can still be lifted out of the
        // page by prediction. That is what carries the yield from a fifth of the page to nearly all
        // of it.
        if (!_policy.RectifySymbols)
        {
            return symbols;
        }

        var work = cells;
        var attempted = new List<Quad>();

        for (int pass = 0; pass < LatticePasses && work.Count > 0; pass++)
        {
            var next = new List<Quad>();

            foreach (Quad cell in work)
            {
                if (attempted.Count >= _policy.MaxSymbolAttempts)
                {
                    // A crafted image can otherwise multiply predicted cells without bound.
                    truncated = true;
                    return symbols;
                }

                if (Expired())
                {
                    truncated = true;
                    return symbols;
                }

                if (IsNear(attempted, cell))
                {
                    continue;
                }

                attempted.Add(cell);
                positionsTried++;
                Report();

                if (!TryDecodeCell(grey, width, height, cell, Accept))
                {
                    continue;
                }

                // Only a cell that actually decoded is trusted enough to predict from. Stepping out
                // from a bad quad would spread its error across the page.
                next.AddRange(cell.Neighbours(width, height));
            }

            work = next;
        }

        return symbols;
    }

    /// <summary>
    /// Rectifies one symbol and retries it across sampling densities, blur levels and binarizers.
    /// </summary>
    /// <remarks>
    /// The sweep is not superstition. A photograph of a screen carries moiré at the display's pixel
    /// pitch, and a photograph of paper carries print grain; the blur level that removes one keeps
    /// the modules of the other. Rather than guess which page this is, the cheap variants are tried
    /// in the order that succeeds most often and the search stops at the first clean decode.
    /// </remarks>
    private static bool TryDecodeCell(byte[] grey, int width, int height, Quad cell, Action<Barcode> accept)
    {
        bool decoded = false;

        foreach (int pixelsPerModule in new[] { 4, 6, 3, 5, 8 })
        {
            // Sized for the largest symbol a BinaryPaper page carries. A smaller symbol simply
            // arrives oversampled, which costs time and never costs accuracy.
            int quiet = 6;
            int size = (MaxModules + 2 * quiet) * pixelsPerModule;
            byte[] warp = PageFilter.Warp(grey, width, height, cell, size, quiet * pixelsPerModule);

            foreach (double sigma in new[] { 0.0, 1.0, 1.6, 0.6, 2.2, 1.3, 2.8 })
            {
                byte[] plane = PageFilter.Blur(warp, size, size, sigma);
                var view = new ImageView(plane, size, size, ImageFormat.Lum);

                foreach (Binarizer binarizer in
                         new[] { Binarizer.LocalAverage, Binarizer.GlobalHistogram, Binarizer.FixedThreshold })
                {
                    foreach (Barcode barcode in BarcodeReader.Read(view, Options(binarizer, returnErrors: false)))
                    {
                        if (!barcode.IsValid)
                        {
                            continue;
                        }

                        accept(barcode);
                        decoded = true;
                    }
                }

                GC.KeepAlive(view);

                if (decoded)
                {
                    return true;
                }
            }
        }

        return decoded;
    }

    /// <summary>QR version 40: the largest symbol, and the one a full BinaryPaper page uses.</summary>
    private const int MaxModules = 177;

    /// <summary>
    /// Below this a detection is not a page symbol worth rectifying — it is noise, or a symbol so
    /// small that the whole-page pass has already read it as well as it ever will.
    /// </summary>
    private const int MinimumSymbolSide = 250;

    private static bool IsNear(List<Quad> quads, Quad candidate)
    {
        foreach (Quad quad in quads)
        {
            if (Math.Abs(quad.CentreX - candidate.CentreX) < 120 && Math.Abs(quad.CentreY - candidate.CentreY) < 120)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// One symbol's four corners in page pixels, clockwise from the top left.
/// </summary>
internal sealed class Quad
{
    private Quad(double[] xs, double[] ys)
    {
        X = xs;
        Y = ys;
    }

    public double[] X { get; }

    public double[] Y { get; }

    public double CentreX => (X[0] + X[1] + X[2] + X[3]) / 4.0;

    public double CentreY => (Y[0] + Y[1] + Y[2] + Y[3]) / 4.0;

    public double Side => Math.Sqrt(((X[1] - X[0]) * (X[1] - X[0])) + ((Y[1] - Y[0]) * (Y[1] - Y[0])));

    public static Quad? From(Position position)
    {
        double[] xs = [position.TopLeft.X, position.TopRight.X, position.BottomRight.X, position.BottomLeft.X];
        double[] ys = [position.TopLeft.Y, position.TopRight.Y, position.BottomRight.Y, position.BottomLeft.Y];

        foreach (double v in xs.Concat(ys))
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                return null;
            }
        }

        return new Quad(xs, ys);
    }

    /// <summary>
    /// The eight lattice cells around this one.
    /// </summary>
    /// <remarks>
    /// The gutter between printed symbols is a layout choice this kit does not own and must not
    /// assume, so a few plausible fractions of a symbol side are tried rather than one constant.
    /// A wrong guess costs one failed rectification; a missing guess costs a frame.
    /// </remarks>
    public IEnumerable<Quad> Neighbours(int width, int height)
    {
        double ux = X[1] - X[0], uy = Y[1] - Y[0];
        double vx = X[3] - X[0], vy = Y[3] - Y[0];

        foreach (double gutter in new[] { 0.03, 0.055, 0.08 })
        {
            for (int du = -1; du <= 1; du++)
            {
                for (int dv = -1; dv <= 1; dv++)
                {
                    if (du == 0 && dv == 0)
                    {
                        continue;
                    }

                    double sx = du * (1 + gutter);
                    double sy = dv * (1 + gutter);

                    var xs = new double[4];
                    var ys = new double[4];
                    bool offPage = false;

                    for (int i = 0; i < 4; i++)
                    {
                        xs[i] = X[i] + (sx * ux) + (sy * vx);
                        ys[i] = Y[i] + (sx * uy) + (sy * vy);

                        // A little slack: a symbol clipped by the frame edge is exactly the case
                        // prediction exists to rescue, so "slightly outside" is not "not there".
                        if (xs[i] < -Slack || ys[i] < -Slack || xs[i] > width + Slack || ys[i] > height + Slack)
                        {
                            offPage = true;
                        }
                    }

                    if (!offPage)
                    {
                        yield return new Quad(xs, ys);
                    }
                }
            }
        }
    }

    private const int Slack = 60;
}

/// <summary>
/// The two pixel operations the page pipeline needs, written here rather than taken from an
/// imaging package: a separable Gaussian blur and a perspective warp.
/// </summary>
internal static class PageFilter
{
    /// <summary>Separable Gaussian blur. <paramref name="sigma"/> of zero returns the input.</summary>
    public static byte[] Blur(byte[] source, int width, int height, double sigma)
    {
        if (sigma <= 0)
        {
            return source;
        }

        int radius = (int)Math.Ceiling(sigma * 2.5);
        var kernel = new double[(2 * radius) + 1];
        double total = 0;
        for (int i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = Math.Exp(-(i * i) / (2 * sigma * sigma));
            total += kernel[i + radius];
        }

        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= total;
        }

        var horizontal = new byte[source.Length];
        var result = new byte[source.Length];

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double acc = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    acc += kernel[i + radius] * source[row + Math.Clamp(x + i, 0, width - 1)];
                }

                horizontal[row + x] = (byte)Math.Clamp(acc, 0, 255);
            }
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double acc = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    acc += kernel[i + radius] * horizontal[(Math.Clamp(y + i, 0, height - 1) * width) + x];
                }

                result[(y * width) + x] = (byte)Math.Clamp(acc, 0, 255);
            }
        }

        return result;
    }

    /// <summary>
    /// Warps <paramref name="quad"/> out of the page into a square of <paramref name="size"/>
    /// pixels, leaving <paramref name="padding"/> pixels of quiet zone on every side.
    /// </summary>
    /// <remarks>
    /// Pixels outside the source read as white, not black: a symbol clipped by the frame edge
    /// should present as missing paper, which a decoder can reject cleanly, rather than as a wall
    /// of dark modules it might try to interpret.
    /// </remarks>
    public static byte[] Warp(byte[] source, int width, int height, Quad quad, int size, int padding)
    {
        double[][] destination =
        [
            [padding, padding],
            [size - padding - 1, padding],
            [size - padding - 1, size - padding - 1],
            [padding, size - padding - 1],
        ];

        // Eight unknowns, the standard homography solve, destination -> source so every output
        // pixel is sampled rather than scattered.
        var a = new double[8, 8];
        var b = new double[8];

        for (int i = 0; i < 4; i++)
        {
            double x = destination[i][0], y = destination[i][1];
            double u = quad.X[i], v = quad.Y[i];

            a[i * 2, 0] = x; a[i * 2, 1] = y; a[i * 2, 2] = 1;
            a[i * 2, 6] = -x * u; a[i * 2, 7] = -y * u;
            b[i * 2] = u;

            a[(i * 2) + 1, 3] = x; a[(i * 2) + 1, 4] = y; a[(i * 2) + 1, 5] = 1;
            a[(i * 2) + 1, 6] = -x * v; a[(i * 2) + 1, 7] = -y * v;
            b[(i * 2) + 1] = v;
        }

        double[] m = Solve(a, b);
        var output = new byte[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double denominator = (m[6] * x) + (m[7] * y) + 1;
                if (Math.Abs(denominator) < 1e-12)
                {
                    output[(y * size) + x] = 255;
                    continue;
                }

                double sx = ((m[0] * x) + (m[1] * y) + m[2]) / denominator;
                double sy = ((m[3] * x) + (m[4] * y) + m[5]) / denominator;
                output[(y * size) + x] = Bilinear(source, width, height, sx, sy);
            }
        }

        return output;
    }

    private static byte Bilinear(byte[] source, int width, int height, double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y) || x < 0 || y < 0 || x > width - 1 || y > height - 1)
        {
            return 255;
        }

        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
        double fx = x - x0, fy = y - y0;

        double top = (source[(y0 * width) + x0] * (1 - fx)) + (source[(y0 * width) + x1] * fx);
        double bottom = (source[(y1 * width) + x0] * (1 - fx)) + (source[(y1 * width) + x1] * fx);
        return (byte)Math.Clamp((top * (1 - fy)) + (bottom * fy), 0, 255);
    }

    /// <summary>Gauss-Jordan with partial pivoting. Eight unknowns; conditioning is not a concern.</summary>
    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;

        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < n; row++)
            {
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column]))
                {
                    pivot = row;
                }
            }

            for (int k = 0; k < n; k++)
            {
                (a[column, k], a[pivot, k]) = (a[pivot, k], a[column, k]);
            }

            (b[column], b[pivot]) = (b[pivot], b[column]);

            double diagonal = a[column, column];
            if (Math.Abs(diagonal) < 1e-12)
            {
                // Degenerate quad. Return what we have; Warp's bounds check turns the result into
                // white pixels, which decode as nothing rather than as something wrong.
                return b;
            }

            for (int k = 0; k < n; k++)
            {
                a[column, k] /= diagonal;
            }

            b[column] /= diagonal;

            for (int row = 0; row < n; row++)
            {
                if (row == column)
                {
                    continue;
                }

                double factor = a[row, column];
                if (factor == 0)
                {
                    continue;
                }

                for (int k = 0; k < n; k++)
                {
                    a[row, k] -= factor * a[column, k];
                }

                b[row] -= factor * b[column];
            }
        }

        return b;
    }
}

/// <summary>Bounds on image work, before any expensive allocation happens.</summary>
public sealed record ImagePolicy
{
    /// <summary>Encoded file size cap. A 600 dpi A4 page scan is comfortably inside this.</summary>
    public long MaxEncodedBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Decoded pixel cap. Roughly a 1200 dpi A4 page with headroom.</summary>
    public long MaxPixels { get; init; } = 200_000_000;

    /// <summary>
    /// How many symbol positions one page may have rectified and retried.
    /// </summary>
    /// <remarks>
    /// The pixel cap bounds decoding; this bounds detection. Each decoded symbol proposes eight
    /// neighbours at three gutter guesses, so without a ceiling a crafted image that keeps
    /// producing plausible detections could keep the tool working indefinitely. A real page of a
    /// full-size backup holds a few dozen symbols, so this leaves generous headroom.
    /// </remarks>
    public int MaxSymbolAttempts { get; init; } = 400;

    /// <summary>
    /// How long one page image may be worked before the reader returns what it has.
    /// </summary>
    /// <remarks>
    /// <para>The pixel cap bounds decoding and <see cref="MaxSymbolAttempts"/> bounds detection,
    /// but neither bounds their product, which is what a user actually waits for. The rectify
    /// ladder stops at the first clean decode, so it costs least on pages that read and most on
    /// pages that do not — a photograph of something that is not a backup page at all is the
    /// expensive case, not the rare one.</para>
    ///
    /// <para>Two minutes is six times the slowest legitimate page measured at the pixel cap
    /// (a 1200 dpi A4 scan reads out in about twenty seconds), which leaves room for hardware
    /// several times slower than a development machine while cutting the pathological case from
    /// tens of minutes to two. <see cref="TimeSpan.Zero"/> disables the bound.</para>
    /// </remarks>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Whether to rectify located symbols and retry them, and to predict their lattice neighbours.
    /// </summary>
    /// <remarks>
    /// <para>This is the expensive half of the pipeline and it is not always worth its price.
    /// Measured on a corpus of twelve photographs of a 48-symbol page, the whole-page pass alone
    /// recovered 45 symbols and rectification added none; against a weaker decoder the same stage
    /// took 6 to 9. It rescues symbols the detector located but could not read, so its value tracks
    /// how much the detector is struggling, not how hard the page is in general.</para>
    ///
    /// <para>Callers therefore run the cheap pass over everything first and turn this on only for
    /// a second look at pages that did not yield enough. When it is off, the page is not
    /// <see cref="PageImageResult.Truncated"/> - nothing was cut short; a cheaper method was
    /// deliberately chosen.</para>
    /// </remarks>
    public bool RectifySymbols { get; init; } = true;

    public static ImagePolicy Default { get; } = new();
}
