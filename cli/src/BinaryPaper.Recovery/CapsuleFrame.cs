// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace BinaryPaper.Recovery;

/// <summary>
/// CRC-32C (Castagnoli), the frame digest defined in SPEC.md section 6.
/// </summary>
/// <remarks>
/// This detects accidental damage — burst errors are the dominant paper-damage mode and CRC-32C is
/// good at those. It is <b>not</b> authentication: it is unkeyed, and recomputing it after altering
/// a header takes microseconds. Never treat a valid digest as evidence about intent.
/// </remarks>
public static class Crc32C
{
    private const uint ReflectedPolynomial = 0x82F63B78;
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ ReflectedPolynomial : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }

        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>The 32-bit value stored big-endian, as it appears on the wire.</summary>
    public static byte[] ComputeBytes(ReadOnlySpan<byte> data)
    {
        var result = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(result, Compute(data));
        return result;
    }
}

/// <summary>
/// One <c>BPQR</c> frame: the byte envelope carried by a single QR code. SPEC.md section 5.
/// </summary>
/// <remarks>
/// Frames arrive from untrusted sources, so <see cref="Decode"/> validates every field against its
/// rule before the frame exists as an object. Nothing here allocates on a declared value that has
/// not already passed its check: <c>symbol_len</c> is bounded by the input length, which is the
/// only bound available at this stage that an attacker does not choose.
/// </remarks>
public sealed record CapsuleFrame
{
    public const string Magic = "BPQR";
    public const byte SupportedFormatMajor = 1;
    public const byte SupportedFormatMinor = 0;

    public const byte ErasureAlgReedSolomon = 1;
    public const byte ErasureAlgLdpc = 2;

    public const int CapsuleIdLength = 4;
    public const int DigestLength = 4;

    /// <summary>magic(4) major(1) minor(1) erasure_alg(1) capsule_id(4) K(2) R(2) S(2) index(2).</summary>
    public const int HeaderLength = 19;

    /// <summary>Header plus digest: the fixed per-frame overhead.</summary>
    public const int FixedOverhead = HeaderLength + DigestLength;

    /// <summary>The u16 count family shared by both codecs.</summary>
    public const int MaxTotalSymbols = 65536;

    public required byte ErasureAlg { get; init; }

    public required byte[] CapsuleId { get; init; }

    public required ushort SourceSymbolCount { get; init; }

    public required ushort RepairSymbolCount { get; init; }

    public required ushort SymbolLength { get; init; }

    public required ushort SymbolIndex { get; init; }

    public required byte[] SymbolPayload { get; init; }

    public int TotalSymbolCount => SourceSymbolCount + RepairSymbolCount;

    public bool IsSourceSymbol => SymbolIndex < SourceSymbolCount;

    public bool IsLdpc => ErasureAlg == ErasureAlgLdpc;

    public string CapsuleIdHex => Convert.ToHexStringLower(CapsuleId);

    /// <summary>
    /// The 15 capsule-identity bytes: everything between the magic and <c>symbol_index</c>.
    /// All frames of one capsule carry these identically.
    /// </summary>
    /// <remarks>
    /// The version pair is included, so a frame of a different wire version can never join a
    /// session even if every other field matches. <c>erasure_alg</c> is included, so Reed–Solomon
    /// and LDPC frames never merge.
    /// </remarks>
    public byte[] IdentityBytes()
    {
        var identity = new byte[15];
        identity[0] = SupportedFormatMajor;
        identity[1] = SupportedFormatMinor;
        identity[2] = ErasureAlg;
        CapsuleId.CopyTo(identity.AsSpan(3));
        BinaryPrimitives.WriteUInt16BigEndian(identity.AsSpan(7), SourceSymbolCount);
        BinaryPrimitives.WriteUInt16BigEndian(identity.AsSpan(9), RepairSymbolCount);
        BinaryPrimitives.WriteUInt16BigEndian(identity.AsSpan(11), SymbolLength);
        return identity;
    }

    /// <summary>
    /// Decodes and fully validates a frame. Every rejection carries the category and stage the
    /// specification assigns it.
    /// </summary>
    public static CapsuleFrame Decode(ReadOnlySpan<byte> bytes)
    {
        // Stage 1: structure.
        if (bytes.Length < FixedOverhead)
        {
            throw Frame(RecoveryErrorCategory.FrameMalformed,
                $"Frame is {bytes.Length} bytes; the fixed overhead alone is {FixedOverhead}.");
        }

        if (bytes[0] != (byte)'B' || bytes[1] != (byte)'P' || bytes[2] != (byte)'Q' || bytes[3] != (byte)'R')
        {
            throw Frame(RecoveryErrorCategory.FrameMalformed, $"Frame magic must be {Magic}.");
        }

        byte major = bytes[4];
        byte minor = bytes[5];
        if (major != SupportedFormatMajor || minor != SupportedFormatMinor)
        {
            throw Frame(RecoveryErrorCategory.FrameUnsupportedVersion,
                $"Unsupported capsule wire version {major}.{minor}; this tool recovers "
                + $"{SupportedFormatMajor}.{SupportedFormatMinor}.");
        }

        byte erasureAlg = bytes[6];
        if (erasureAlg is not (ErasureAlgReedSolomon or ErasureAlgLdpc))
        {
            throw Frame(RecoveryErrorCategory.FrameUnsupportedAlgorithm,
                $"Unsupported erasure_alg {erasureAlg}.");
        }

        byte[] capsuleId = bytes.Slice(7, CapsuleIdLength).ToArray();
        ushort k = BinaryPrimitives.ReadUInt16BigEndian(bytes[11..]);
        ushort r = BinaryPrimitives.ReadUInt16BigEndian(bytes[13..]);
        ushort symbolLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[15..]);
        ushort symbolIndex = BinaryPrimitives.ReadUInt16BigEndian(bytes[17..]);

        // Stage 2: fields. Every one of these bounds a later allocation or loop.
        if (symbolLength == 0)
        {
            throw Frame(RecoveryErrorCategory.FrameInvalidField, "symbol_len must be greater than 0.");
        }

        if (symbolLength % 2 != 0)
        {
            throw Frame(RecoveryErrorCategory.FrameInvalidField,
                "symbol_len must be even; the Reed-Solomon layer reads symbols as 16-bit words.");
        }

        if (k < 1)
        {
            throw Frame(RecoveryErrorCategory.FrameInvalidField, "source_symbol_count must be at least 1.");
        }

        // Both operands are u16, so the sum reaches 131070 and must not be computed in 16 bits.
        int total = k + r;
        if (total > MaxTotalSymbols)
        {
            throw Frame(RecoveryErrorCategory.FrameInvalidField,
                $"source_symbol_count + repair_symbol_count is {total}; the maximum is {MaxTotalSymbols}.");
        }

        if (symbolIndex >= total)
        {
            throw Frame(RecoveryErrorCategory.FrameInvalidField,
                $"symbol_index {symbolIndex} is not below source + repair ({total}).");
        }

        // The declared length must account for the input exactly. This is what makes symbol_len
        // safe to use as an allocation size: it is bounded by bytes we already hold.
        int expectedLength = FixedOverhead + symbolLength;
        if (bytes.Length != expectedLength)
        {
            throw Frame(RecoveryErrorCategory.FrameInvalidField,
                $"Frame is {bytes.Length} bytes but symbol_len {symbolLength} requires exactly {expectedLength}.");
        }

        // Stage 3: digest.
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(bytes[(bytes.Length - DigestLength)..]);
        uint computed = Crc32C.Compute(bytes[..(bytes.Length - DigestLength)]);
        if (declared != computed)
        {
            throw Frame(RecoveryErrorCategory.FrameDigestMismatch,
                $"Frame digest {declared:x8} does not match the computed CRC-32C {computed:x8}.");
        }

        return new CapsuleFrame
        {
            ErasureAlg = erasureAlg,
            CapsuleId = capsuleId,
            SourceSymbolCount = k,
            RepairSymbolCount = r,
            SymbolLength = symbolLength,
            SymbolIndex = symbolIndex,
            SymbolPayload = bytes.Slice(HeaderLength, symbolLength).ToArray()
        };
    }

    private static RecoveryException Frame(RecoveryErrorCategory category, string message) =>
        new(category, RecoveryStage.Frame, message);
}
