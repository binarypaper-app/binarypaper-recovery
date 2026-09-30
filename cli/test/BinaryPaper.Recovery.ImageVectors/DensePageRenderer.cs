// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using BinaryPaper.Recovery.Images;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace BinaryPaper.Recovery.ImageVectors;

/// <summary>
/// Lays same-version symbols out the way a printed sheet does: in rows, a few modules apart.
/// </summary>
internal static class DensePageRenderer
{
    /// <summary>A mid-size version, the size a sheet of kilobyte payloads carries.</summary>
    public const int Version = 26;

    /// <summary>Within the byte-mode capacity of a version-26 ECC M symbol, with headroom.</summary>
    public const int MaxFrameBytes = 1000;

    /// <summary>Seven 26 mm symbols across, as on an A4 sheet.</summary>
    public const int Columns = 7;

    /// <summary>Edge-to-edge spacing between neighbours, which doubles as their quiet zone.</summary>
    public const int GapModules = 6;

    /// <summary>Close to a 300 dpi scan of a 0.2 mm module, kept integral so the rendering is exact.</summary>
    public const int ModuleSize = 3;

    private const int MarginModules = 12;

    public static (byte[] Png, byte[] Pixels, int Width, int Height) Render(IReadOnlyList<byte[]> frames, int columns = Columns)
    {
        List<bool[,]> matrices = [.. frames.Select(Encode)];
        int modules = matrices[0].GetLength(0);
        int rows = (matrices.Count + columns - 1) / columns;

        int pitch = (modules + GapModules) * ModuleSize;
        int margin = MarginModules * ModuleSize;
        int width = (2 * margin) + (Math.Min(columns, matrices.Count) * pitch) - (GapModules * ModuleSize);
        int height = (2 * margin) + (rows * pitch) - (GapModules * ModuleSize);

        byte[] pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);

        for (int i = 0; i < matrices.Count; i++)
        {
            int originX = margin + ((i % columns) * pitch);
            int originY = margin + ((i / columns) * pitch);
            for (int my = 0; my < modules; my++)
            {
                for (int mx = 0; mx < modules; mx++)
                {
                    if (!matrices[i][mx, my])
                    {
                        continue;
                    }

                    for (int dy = 0; dy < ModuleSize; dy++)
                    {
                        int offset = ((originY + (my * ModuleSize) + dy) * width) + originX + (mx * ModuleSize);
                        Array.Fill(pixels, (byte)0, offset, ModuleSize);
                    }
                }
            }
        }

        return (PngWriter.WriteGrey(pixels, width, height), pixels, width, height);
    }

    /// <summary>
    /// Clean decodes from one whole-page read that also reports failed detections, capped at 64
    /// results: the read that candidate crowding defeats.
    /// </summary>
    public static int CountCrowdedRead(byte[] pixels, int width, int height)
    {
        using var options = new ZXingCpp.ReaderOptions
        {
            Formats = ZXingCpp.BarcodeFormat.QRCode,
            TryHarder = true,
            TryRotate = true,
            TryDownscale = true,
            TryInvert = false,
            MaxNumberOfSymbols = 64,
            ReturnErrors = true,
            Binarizer = ZXingCpp.Binarizer.LocalAverage,
        };

        int valid = 0;
        NativeBarcodeReader.Read(pixels, width, height, ZXingCpp.ImageFormat.Lum, options, barcode =>
        {
            if (barcode.IsValid)
            {
                valid++;
            }
        });

        return valid;
    }

    private static bool[,] Encode(byte[] frame)
    {
        var latin1 = Encoding.GetEncoding(28591);
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.CHARACTER_SET] = "ISO-8859-1",
            [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
            [EncodeHintType.QR_VERSION] = Version,
            [EncodeHintType.MARGIN] = 0
        };

        BitMatrix matrix = new QRCodeWriter().encode(latin1.GetString(frame), BarcodeFormat.QR_CODE, 0, 0, hints);
        var result = new bool[matrix.Width, matrix.Height];
        for (int y = 0; y < matrix.Height; y++)
        {
            for (int x = 0; x < matrix.Width; x++)
            {
                result[x, y] = matrix[x, y];
            }
        }

        return result;
    }
}
