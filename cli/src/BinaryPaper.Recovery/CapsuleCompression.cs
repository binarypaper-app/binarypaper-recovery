// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using SharpCompress.Compressors.LZMA;

namespace BinaryPaper.Recovery;

/// <summary>
/// LZMA decoding for <c>compression_alg = 1</c>. SPEC.md section 10.5.
/// </summary>
/// <remarks>
/// <para>Decode only. This tool never creates a capsule, so no encoder is needed or wanted.</para>
///
/// <para>These bytes are already authenticated when they arrive here — stage 8 precedes stage 9 —
/// which removes the classic attacker-supplied decompression bomb for anyone without the key. The
/// declared output size is still preflighted against a resource policy, because a legitimate
/// capsule can also be larger than the machine at hand can hold.</para>
/// </remarks>
public static class CapsuleCompression
{
    /// <summary>props(5) + uncompressed size(8) of the legacy `.lzma` alone container.</summary>
    private const int AloneHeaderLength = 13;

    public static byte[] Decompress(byte[] body, byte compressionAlgorithm, ResourcePolicy policy) =>
        compressionAlgorithm switch
        {
            CapsulePreamble.CompressionNone => body,
            CapsulePreamble.CompressionLzma => DecompressAlone(body, policy),
            _ => throw new RecoveryException(RecoveryErrorCategory.CompressionMalformed, RecoveryStage.Compression,
                $"Unsupported compression_alg {compressionAlgorithm}.")
        };

    private static byte[] DecompressAlone(byte[] alone, ResourcePolicy policy)
    {
        if (alone.Length < AloneHeaderLength)
        {
            throw Compression($"LZMA stream is {alone.Length} bytes, too short for the {AloneHeaderLength}-byte header.");
        }

        byte[] properties = alone[..5];
        ulong declaredSize = BinaryPrimitives.ReadUInt64LittleEndian(alone.AsSpan(5));

        // Preflight the declared size before allocating it. Note what is deliberately absent: any
        // fixed low ceiling. A legitimate highly compressible backup produces a large package from a
        // small capsule, and refusing it would silently make a valid backup unrecoverable - the
        // exact failure this format exists to prevent. Bound by policy, not by a guess.
        policy.EnsureDecompressedSizeAllowed(declaredSize);

        if (declaredSize > int.MaxValue)
        {
            throw new RecoveryException(RecoveryErrorCategory.ResourcePolicyRefused, RecoveryStage.Compression,
                $"LZMA declares {declaredSize} output bytes, beyond what this implementation addresses.");
        }

        long compressedSize = alone.Length - AloneHeaderLength;
        byte[] result = new byte[declaredSize];

        try
        {
            using var compressed = new MemoryStream(alone, AloneHeaderLength, (int)compressedSize, writable: false);
            using LzmaStream stream = LzmaStream.Create(
                properties, compressed, compressedSize, (long)declaredSize, leaveOpen: true);

            int offset = 0;
            while (offset < result.Length)
            {
                int read = stream.Read(result, offset, result.Length - offset);
                if (read <= 0)
                {
                    throw Compression(
                        $"LZMA stream ended after {offset} of the {declaredSize} bytes it declared.");
                }

                offset += read;
            }
        }
        catch (RecoveryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Compression($"LZMA stream could not be decoded: {ex.Message}");
        }

        return result;
    }

    private static RecoveryException Compression(string message) =>
        new(RecoveryErrorCategory.CompressionMalformed, RecoveryStage.Compression, message);
}

/// <summary>
/// Operator-configurable limits: what this machine is willing to spend, as distinct from what the
/// wire can represent and what an official creator emits.
/// </summary>
public sealed record ResourcePolicy
{
    /// <summary>
    /// Maximum decompressed package size. Generous by default and overridable, because the wrong
    /// failure here is refusing a real backup.
    /// </summary>
    public long MaxDecompressedBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Accept KDF costs above the profile values. Off by default.</summary>
    public bool AllowHighKdfCost { get; init; }

    public static ResourcePolicy Default { get; } = new();

    public void EnsureDecompressedSizeAllowed(ulong declaredSize)
    {
        if (declaredSize > (ulong)MaxDecompressedBytes)
        {
            throw new RecoveryException(RecoveryErrorCategory.ResourcePolicyRefused, RecoveryStage.Compression,
                $"The capsule declares {declaredSize} decompressed bytes, above the configured limit of "
                + $"{MaxDecompressedBytes}. Raise --max-output-bytes if this backup really is that large.");
        }
    }
}
