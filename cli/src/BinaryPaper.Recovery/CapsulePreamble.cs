// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace BinaryPaper.Recovery;

/// <summary>
/// The <c>BPCP</c> capsule preamble at the front of the stored payload. SPEC.md section 9.
/// </summary>
/// <remarks>
/// Everything here is attacker-chosen until it has passed its rule, and two fields are outright
/// dangerous: <c>body_len</c> is a u64 that sizes a slice, and the KDF parameters drive a function
/// whose entire purpose is to be expensive. Both are validated before use.
/// </remarks>
public sealed record CapsulePreamble
{
    public const string Magic = "BPCP";

    public const byte AeadNone = 0;
    public const byte AeadAesGcm = 1;
    public const byte KdfNone = 0;
    public const byte KdfArgon2id = 1;
    public const byte CompressionNone = 0;
    public const byte CompressionLzma = 1;

    public const int PlaintextDigestLength = 32;

    /// <summary>magic(4) aead(1) kdf(1) compression(1) memory(4) iterations(4) parallelism(4) outLen(2) saltLen(1) nonceLen(1) bodyLen(8).</summary>
    private const int FixedHeaderLength = 31;

    public required byte AeadAlgorithm { get; init; }

    public required byte KdfAlgorithm { get; init; }

    public required byte CompressionAlgorithm { get; init; }

    public required uint KdfMemoryKib { get; init; }

    public required uint KdfIterations { get; init; }

    public required uint KdfParallelism { get; init; }

    public required ushort KdfOutputLength { get; init; }

    public required byte[] Salt { get; init; }

    public required byte[] Nonce { get; init; }

    public required ulong BodyLength { get; init; }

    public required byte[] PlaintextDigest { get; init; }

    public bool IsEncrypted => AeadAlgorithm == AeadAesGcm;

    /// <summary>Bytes before <c>body</c> begins.</summary>
    public int HeaderLength =>
        FixedHeaderLength + Salt.Length + Nonce.Length + (AeadAlgorithm == AeadNone ? PlaintextDigestLength : 0);

    public static CapsulePreamble Decode(ReadOnlySpan<byte> storedPayload)
    {
        if (storedPayload.Length < FixedHeaderLength)
        {
            throw Preamble(RecoveryErrorCategory.PreambleMalformed,
                $"Stored payload is {storedPayload.Length} bytes; the preamble header alone is {FixedHeaderLength}.");
        }

        if (storedPayload[0] != (byte)'B' || storedPayload[1] != (byte)'P'
            || storedPayload[2] != (byte)'C' || storedPayload[3] != (byte)'P')
        {
            throw Preamble(RecoveryErrorCategory.PreambleMalformed,
                $"Stored payload does not begin with the {Magic} capsule preamble magic.");
        }

        byte aead = storedPayload[4];
        byte kdf = storedPayload[5];
        byte compression = storedPayload[6];
        uint memoryKib = BinaryPrimitives.ReadUInt32BigEndian(storedPayload[7..]);
        uint iterations = BinaryPrimitives.ReadUInt32BigEndian(storedPayload[11..]);
        uint parallelism = BinaryPrimitives.ReadUInt32BigEndian(storedPayload[15..]);
        ushort outputLength = BinaryPrimitives.ReadUInt16BigEndian(storedPayload[19..]);
        byte saltLength = storedPayload[21];
        byte nonceLength = storedPayload[22];
        ulong bodyLength = BinaryPrimitives.ReadUInt64BigEndian(storedPayload[23..]);

        if (aead is not (AeadNone or AeadAesGcm))
        {
            throw Preamble(RecoveryErrorCategory.PreambleInvalidAlgorithm, $"Unsupported aead_alg {aead}.");
        }

        if (compression is not (CompressionNone or CompressionLzma))
        {
            throw Preamble(RecoveryErrorCategory.PreambleInvalidAlgorithm,
                $"Unsupported compression_alg {compression}; only 0 (stored) and 1 (LZMA) exist.");
        }

        // Declared lengths are bounds-checked against the bytes actually held before any of them is
        // used to slice.
        int variableStart = FixedHeaderLength;
        int digestLength = aead == AeadNone ? PlaintextDigestLength : 0;
        long declaredHeader = (long)variableStart + saltLength + nonceLength + digestLength;
        if (declaredHeader > storedPayload.Length)
        {
            throw Preamble(RecoveryErrorCategory.PreambleMalformed,
                $"Preamble declares {declaredHeader} header bytes but only {storedPayload.Length} are present.");
        }

        byte[] salt = storedPayload.Slice(variableStart, saltLength).ToArray();
        byte[] nonce = storedPayload.Slice(variableStart + saltLength, nonceLength).ToArray();
        byte[] digest = digestLength == 0
            ? []
            : storedPayload.Slice(variableStart + saltLength + nonceLength, digestLength).ToArray();

        var preamble = new CapsulePreamble
        {
            AeadAlgorithm = aead,
            KdfAlgorithm = kdf,
            CompressionAlgorithm = compression,
            KdfMemoryKib = memoryKib,
            KdfIterations = iterations,
            KdfParallelism = parallelism,
            KdfOutputLength = outputLength,
            Salt = salt,
            Nonce = nonce,
            BodyLength = bodyLength,
            PlaintextDigest = digest
        };

        preamble.Validate();
        return preamble;
    }

    private void Validate()
    {
        if (BodyLength == 0)
        {
            throw Preamble(RecoveryErrorCategory.PreambleInvalidParameter, "body_len must be greater than 0.");
        }

        if (AeadAlgorithm == AeadAesGcm)
        {
            if (KdfAlgorithm != KdfArgon2id)
            {
                throw Preamble(RecoveryErrorCategory.PreambleInvalidAlgorithm,
                    "An AES-256-GCM capsule must declare kdf_alg = 1 (Argon2id).");
            }

            // A zero cost is malformed, not merely weak, and it must be caught before Argon2id runs.
            if (KdfMemoryKib == 0 || KdfIterations == 0 || KdfParallelism == 0 || KdfOutputLength == 0)
            {
                throw Preamble(RecoveryErrorCategory.PreambleInvalidParameter,
                    "An encrypted capsule must declare non-zero Argon2id parameters.");
            }

            if (Salt.Length < RecoveryProfile.MinSaltLength)
            {
                throw Preamble(RecoveryErrorCategory.PreambleInvalidParameter,
                    $"salt_len is {Salt.Length}; the minimum is {RecoveryProfile.MinSaltLength}.");
            }

            if (Nonce.Length != RecoveryProfile.NonceLength)
            {
                throw Preamble(RecoveryErrorCategory.PreambleInvalidParameter,
                    $"AES-GCM nonce_len must be exactly {RecoveryProfile.NonceLength}, was {Nonce.Length}.");
            }

            if (PlaintextDigest.Length != 0)
            {
                throw Preamble(RecoveryErrorCategory.PreambleInvalidParameter,
                    "An encrypted capsule must not carry a plaintext digest.");
            }

            if (BodyLength < RecoveryProfile.AesGcmTagLength)
            {
                throw Preamble(RecoveryErrorCategory.PreambleInvalidParameter,
                    $"body_len {BodyLength} cannot even contain the {RecoveryProfile.AesGcmTagLength}-byte GCM tag.");
            }

            return;
        }

        if (KdfAlgorithm != KdfNone)
        {
            throw Preamble(RecoveryErrorCategory.PreambleInvalidAlgorithm,
                "A plaintext capsule must declare kdf_alg = 0.");
        }

        if (KdfMemoryKib != 0 || KdfIterations != 0 || KdfParallelism != 0 || KdfOutputLength != 0)
        {
            throw Preamble(RecoveryErrorCategory.PreambleInvalidParameter,
                "A plaintext capsule must declare zeroed KDF parameters.");
        }

        if (Salt.Length != 0 || Nonce.Length != 0)
        {
            throw Preamble(RecoveryErrorCategory.PreambleInvalidParameter,
                "A plaintext capsule must not carry a salt or nonce.");
        }

        if (PlaintextDigest.Length != PlaintextDigestLength)
        {
            throw Preamble(RecoveryErrorCategory.PreambleInvalidParameter,
                $"A plaintext capsule requires a {PlaintextDigestLength}-byte payload digest.");
        }
    }

    /// <summary>Extracts the body from the padded stored payload.</summary>
    public byte[] ExtractBody(ReadOnlySpan<byte> storedPayload)
    {
        // Subtract rather than add. body_len is a u64, so header + body_len overflows even in
        // 64-bit unsigned arithmetic for a declared length near the ceiling - the sum wraps to a
        // small number, the bound check passes, and the slice that follows is unbounded. Comparing
        // against the remaining space cannot overflow because the remainder is already bounded by
        // the input we hold.
        if (storedPayload.Length < HeaderLength)
        {
            throw Preamble(RecoveryErrorCategory.PreambleBodyOutOfBounds,
                $"Stored payload is {storedPayload.Length} bytes, shorter than the {HeaderLength}-byte preamble.");
        }

        if (BodyLength > (ulong)(storedPayload.Length - HeaderLength))
        {
            throw Preamble(RecoveryErrorCategory.PreambleBodyOutOfBounds,
                $"Preamble declares a {BodyLength}-byte body at offset {HeaderLength}, beyond the "
                + $"{storedPayload.Length}-byte stored payload.");
        }

        if (BodyLength > int.MaxValue)
        {
            throw new RecoveryException(RecoveryErrorCategory.ResourcePolicyRefused, RecoveryStage.Preamble,
                $"body_len {BodyLength} is beyond what this implementation addresses.");
        }

        return storedPayload.Slice(HeaderLength, (int)BodyLength).ToArray();
    }

    private static RecoveryException Preamble(RecoveryErrorCategory category, string message) =>
        new(category, RecoveryStage.Preamble, message);
}
