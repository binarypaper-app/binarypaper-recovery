// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using StbImageSharp;
using ZXing;
using ZXing.Common;
using ZXing.Multi;
using ZXing.Multi.QrCode;

namespace BinaryPaper.Recovery.Images;

public sealed record DecodedSymbol(string Source, byte[] Payload);

public sealed record PageImageResult(
    string Source,
    int Width,
    int Height,
    IReadOnlyList<DecodedSymbol> Symbols,
    string? Failure);

/// <summary>
/// Turns page images into frame bytes: PNG/JPEG in, QR payloads out.
/// </summary>
/// <remarks>
/// <para>Image decoding is a large attack surface with a long history of memory-safety bugs, and
/// every image handed to this tool is untrusted. Type is determined by content rather than
/// extension, dimensions are checked against a policy before a full decode is attempted, and a
/// failure on one image never discards frames already recovered from another.</para>
///
/// <para>The payload is taken from the decoder's <b>raw byte segments</b>, never from
/// <c>Result.Text</c>. QR payloads here are arbitrary binary — a frame routinely contains
/// <c>0x00</c> and byte sequences that are not valid text in any encoding — and letting a decoded
/// payload round-trip through a string silently corrupts it.</para>
/// </remarks>
public sealed class PageImageReader(ImagePolicy? policy = null)
{
    private readonly ImagePolicy _policy = policy ?? ImagePolicy.Default;

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

        ImageResult image;
        try
        {
            // Dimensions are validated below, but the decoder allocates first, so the byte budget is
            // the bound that actually protects us here.
            if (bytes.LongLength > _policy.MaxEncodedBytes)
            {
                return new PageImageResult(source, 0, 0, [],
                    $"image is {bytes.LongLength} bytes, above the {_policy.MaxEncodedBytes}-byte limit");
            }

            image = ImageResult.FromMemory(bytes, ColorComponents.Grey);
        }
        catch (Exception ex)
        {
            return new PageImageResult(source, 0, 0, [], $"could not be decoded: {ex.Message}");
        }

        if (image.Width <= 0 || image.Height <= 0)
        {
            return new PageImageResult(source, image.Width, image.Height, [], "image has no pixels");
        }

        long pixels = (long)image.Width * image.Height;
        if (pixels > _policy.MaxPixels)
        {
            return new PageImageResult(source, image.Width, image.Height, [],
                $"image is {image.Width}x{image.Height} ({pixels} pixels), above the {_policy.MaxPixels}-pixel limit");
        }

        try
        {
            List<DecodedSymbol> symbols = DecodeSymbols(source, image);
            return new PageImageResult(source, image.Width, image.Height, symbols,
                symbols.Count == 0 ? "no QR codes found" : null);
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

    private List<DecodedSymbol> DecodeSymbols(string source, ImageResult image)
    {
        var luminance = new GreyLuminanceSource(image.Data, image.Width, image.Height);
        var bitmap = new BinaryBitmap(new HybridBinarizer(luminance));

        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
            [DecodeHintType.TRY_HARDER] = true
        };

        var reader = new QRCodeMultiReader();
        Result[]? results = reader.decodeMultiple(bitmap, hints);

        var symbols = new List<DecodedSymbol>();
        if (results is null)
        {
            return symbols;
        }

        foreach (Result result in results)
        {
            byte[]? payload = ExtractBinaryPayload(result);
            if (payload is not null)
            {
                symbols.Add(new DecodedSymbol(source, payload));
            }
        }

        return symbols;
    }

    /// <summary>
    /// Pulls the raw bytes out of a decode result.
    /// </summary>
    /// <remarks>
    /// <c>Result.Text</c> is deliberately not consulted, not even as a fallback. A frame payload is
    /// arbitrary binary; interpreting it as text and encoding it back produces bytes that are
    /// subtly wrong, and the resulting frame would then fail its CRC-32C with no indication that
    /// the decoder — not the paper — was at fault. Returning nothing is the honest outcome.
    /// </remarks>
    private static byte[]? ExtractBinaryPayload(Result result)
    {
        if (result.ResultMetadata is null
            || !result.ResultMetadata.TryGetValue(ResultMetadataType.BYTE_SEGMENTS, out object? raw)
            || raw is not IEnumerable<byte[]> segments)
        {
            return null;
        }

        List<byte[]> list = [.. segments];
        if (list.Count == 0)
        {
            return null;
        }

        if (list.Count == 1)
        {
            return list[0];
        }

        // A frame is written as a single Byte Mode segment, but a decoder may report several; they
        // concatenate in order.
        int total = list.Sum(s => s.Length);
        byte[] joined = new byte[total];
        int offset = 0;
        foreach (byte[] segment in list)
        {
            segment.CopyTo(joined, offset);
            offset += segment.Length;
        }

        return joined;
    }
}

/// <summary>Bounds on image work, before any expensive allocation happens.</summary>
public sealed record ImagePolicy
{
    /// <summary>Encoded file size cap. A 600 dpi A4 page scan is comfortably inside this.</summary>
    public long MaxEncodedBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Decoded pixel cap. Roughly a 1200 dpi A4 page with headroom.</summary>
    public long MaxPixels { get; init; } = 200_000_000;

    public static ImagePolicy Default { get; } = new();
}

/// <summary>
/// A ZXing luminance source over an 8-bit greyscale buffer.
/// </summary>
/// <remarks>
/// Written here rather than taken from a ZXing image binding: the bindings each pull in a full
/// imaging stack, and the only thing actually needed is "here are the grey bytes". This keeps the
/// dependency surface to two packages with no transitive dependencies at all.
/// </remarks>
internal sealed class GreyLuminanceSource : LuminanceSource
{
    private readonly byte[] _luminance;

    public GreyLuminanceSource(byte[] luminance, int width, int height)
        : base(width, height)
    {
        if (luminance.Length < (long)width * height)
        {
            throw new ArgumentException("Luminance buffer is smaller than the declared dimensions.", nameof(luminance));
        }

        _luminance = luminance;
    }

    public override byte[] Matrix => _luminance;

    public override byte[] getRow(int y, byte[]? row)
    {
        if (y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        if (row is null || row.Length < Width)
        {
            row = new byte[Width];
        }

        Array.Copy(_luminance, y * Width, row, 0, Width);
        return row;
    }
}
