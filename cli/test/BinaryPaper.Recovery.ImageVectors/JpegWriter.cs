// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

namespace BinaryPaper.Recovery.ImageVectors;

/// <summary>
/// A minimal baseline JPEG encoder for a single greyscale component, used only to generate image
/// fixtures.
/// </summary>
/// <remarks>
/// <para>Written here rather than taken from a library because the shipped tool's imaging
/// dependency is decode-only; pulling in an encoder purely to build fixtures would enlarge the
/// audit surface of the released binary for no runtime benefit. It is also why this lives in the
/// test tree.</para>
///
/// <para>Correctness is not assumed. The generator decodes every image it writes and asserts the QR
/// payloads come back byte-exact, so a mistake here fails loudly at generation time instead of
/// producing a fixture that quietly tests nothing.</para>
///
/// <para>Structure: SOI, JFIF APP0, one quantisation table, baseline SOF0 with a single 1x1-sampled
/// component, the Annex K luminance Huffman tables, SOS, entropy-coded 8x8 blocks, EOI.</para>
/// </remarks>
internal static class JpegWriter
{
    // Annex K.1 luminance quantisation table, scaled by quality.
    private static readonly int[] BaseQuantization =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99
    ];

    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    ];

    // Annex K.3 luminance DC table.
    private static readonly byte[] DcBits = [0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    // Annex K.3 luminance AC table.
    private static readonly byte[] AcBits = [0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];

    private static readonly byte[] AcValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA
    ];

    public static byte[] WriteGrey(byte[] pixels, int width, int height, int quality)
    {
        int[] quant = ScaleQuantization(quality);
        (int[] dcCodes, int[] dcLengths) = BuildHuffman(DcBits, DcValues);
        (int[] acCodes, int[] acLengths) = BuildHuffman(AcBits, AcValues);

        using var output = new MemoryStream();
        WriteMarker(output, 0xD8); // SOI

        // APP0 JFIF
        WriteSegment(output, 0xE0,
        [
            (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
            1, 1,       // version 1.1
            0,          // no density units
            0, 1, 0, 1, // 1x1 density
            0, 0        // no thumbnail
        ]);

        // DQT: 8-bit precision, table 0, in zig-zag order.
        byte[] dqt = new byte[65];
        dqt[0] = 0x00;
        for (int i = 0; i < 64; i++)
        {
            dqt[1 + i] = (byte)quant[ZigZag[i]];
        }

        WriteSegment(output, 0xDB, dqt);

        // SOF0: baseline, 8-bit, one component, 1x1 sampling, quantisation table 0.
        WriteSegment(output, 0xC0,
        [
            8,
            (byte)(height >> 8), (byte)(height & 0xFF),
            (byte)(width >> 8), (byte)(width & 0xFF),
            1,
            1, 0x11, 0
        ]);

        WriteSegment(output, 0xC4, [.. new byte[] { 0x00 }, .. DcBits[1..], .. DcValues]);
        WriteSegment(output, 0xC4, [.. new byte[] { 0x10 }, .. AcBits[1..], .. AcValues]);

        // SOS: one component, DC table 0, AC table 0.
        WriteSegment(output, 0xDA, [1, 1, 0x00, 0, 63, 0]);

        var writer = new BitWriter(output);
        int previousDc = 0;
        var block = new double[64];
        var coefficients = new int[64];

        for (int blockY = 0; blockY < height; blockY += 8)
        {
            for (int blockX = 0; blockX < width; blockX += 8)
            {
                // Level shift by -128, replicating edge pixels for partial blocks.
                for (int y = 0; y < 8; y++)
                {
                    int sy = Math.Min(blockY + y, height - 1);
                    for (int x = 0; x < 8; x++)
                    {
                        int sx = Math.Min(blockX + x, width - 1);
                        block[(y * 8) + x] = pixels[(sy * width) + sx] - 128.0;
                    }
                }

                ForwardDct(block);

                for (int i = 0; i < 64; i++)
                {
                    coefficients[i] = (int)Math.Round(block[i] / quant[i]);
                }

                previousDc = EncodeBlock(writer, coefficients, previousDc, dcCodes, dcLengths, acCodes, acLengths);
            }
        }

        writer.Flush();
        WriteMarker(output, 0xD9); // EOI
        return output.ToArray();
    }

    private static int[] ScaleQuantization(int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        int scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);

        var table = new int[64];
        for (int i = 0; i < 64; i++)
        {
            table[i] = Math.Clamp(((BaseQuantization[i] * scale) + 50) / 100, 1, 255);
        }

        return table;
    }

    /// <summary>Separable floating-point forward DCT-II with the JPEG normalisation.</summary>
    private static void ForwardDct(double[] block)
    {
        var temp = new double[64];

        for (int u = 0; u < 8; u++)
        {
            for (int x = 0; x < 8; x++)
            {
                Cosines[(u * 8) + x] = Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
            }
        }

        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        sum += block[(y * 8) + x] * Cosines[(u * 8) + x] * Cosines[(v * 8) + y];
                    }
                }

                double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                double cv = v == 0 ? 1 / Math.Sqrt(2) : 1;
                temp[(v * 8) + u] = 0.25 * cu * cv * sum;
            }
        }

        temp.CopyTo(block, 0);
    }

    private static readonly double[] Cosines = new double[64];

    private static int EncodeBlock(
        BitWriter writer, int[] coefficients, int previousDc,
        int[] dcCodes, int[] dcLengths, int[] acCodes, int[] acLengths)
    {
        int dc = coefficients[0];
        int diff = dc - previousDc;
        int category = Category(diff);
        writer.Write(dcCodes[category], dcLengths[category]);
        if (category > 0)
        {
            writer.Write(Amplitude(diff, category), category);
        }

        int run = 0;
        for (int i = 1; i < 64; i++)
        {
            int value = coefficients[ZigZag[i]];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                writer.Write(acCodes[0xF0], acLengths[0xF0]); // ZRL
                run -= 16;
            }

            int size = Category(value);
            int symbol = (run << 4) | size;
            writer.Write(acCodes[symbol], acLengths[symbol]);
            writer.Write(Amplitude(value, size), size);
            run = 0;
        }

        if (run > 0)
        {
            writer.Write(acCodes[0x00], acLengths[0x00]); // EOB
        }

        return dc;
    }

    private static int Category(int value)
    {
        int magnitude = Math.Abs(value);
        int category = 0;
        while (magnitude > 0)
        {
            magnitude >>= 1;
            category++;
        }

        return category;
    }

    private static int Amplitude(int value, int size) =>
        value >= 0 ? value : value + (1 << size) - 1;

    private static (int[] Codes, int[] Lengths) BuildHuffman(byte[] bits, byte[] values)
    {
        var codes = new int[256];
        var lengths = new int[256];

        int code = 0;
        int k = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < bits[length]; i++)
            {
                codes[values[k]] = code;
                lengths[values[k]] = length;
                code++;
                k++;
            }

            code <<= 1;
        }

        return (codes, lengths);
    }

    private static void WriteMarker(Stream output, byte marker)
    {
        output.WriteByte(0xFF);
        output.WriteByte(marker);
    }

    private static void WriteSegment(Stream output, byte marker, byte[] payload)
    {
        WriteMarker(output, marker);
        int length = payload.Length + 2;
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)(length & 0xFF));
        output.Write(payload);
    }

    /// <summary>MSB-first bit writer with the mandatory 0xFF byte stuffing.</summary>
    private sealed class BitWriter(Stream output)
    {
        private int _buffer;
        private int _count;

        public void Write(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((value >> i) & 1);
                _count++;

                if (_count != 8)
                {
                    continue;
                }

                Emit((byte)_buffer);
                _buffer = 0;
                _count = 0;
            }
        }

        public void Flush()
        {
            while (_count != 0)
            {
                Write(1, 1); // pad with 1 bits, as the standard requires
            }
        }

        private void Emit(byte value)
        {
            output.WriteByte(value);
            if (value == 0xFF)
            {
                // A 0xFF in entropy-coded data is followed by 0x00 so it is not read as a marker.
                output.WriteByte(0x00);
            }
        }
    }
}
