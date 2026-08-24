// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using Erasure16;
using LdpcStaircase;

namespace BinaryPaper.Recovery;

public enum FrameAdmission
{
    /// <summary>A new symbol index joined the session.</summary>
    Accepted,

    /// <summary>Same symbol index, byte-identical frame. Ignored, as the specification requires.</summary>
    ExactDuplicate,

    /// <summary>Same symbol index, different bytes. A conflict: the reader must not choose one.</summary>
    ConflictingDuplicate,

    /// <summary>Same capsule_id, different capsule-identity bytes.</summary>
    IdentityConflict,

    /// <summary>A different capsule_id: this frame belongs to another capsule entirely.</summary>
    ForeignCapsule
}

/// <summary>
/// The set of validated frames collected while recovering one capsule. SPEC.md section 7.
/// </summary>
/// <remarks>
/// A session is keyed by the 15 capsule-identity bytes, not by <c>capsule_id</c> alone. Two frames
/// that share an id but disagree on any identity byte are a conflict, not two sessions — silently
/// picking one would produce plausible-looking garbage from genuinely inconsistent input.
/// </remarks>
public sealed class ScanSession
{
    private readonly Dictionary<ushort, byte[]> _payloads = [];

    /// <summary>
    /// SHA-256 of each accepted frame's full bytes, for exact-duplicate detection.
    /// </summary>
    /// <remarks>
    /// A digest rather than the frame itself. Retaining whole frames doubles the memory a session
    /// holds - at the largest supported shape that is roughly 9 MB of duplicated payload for no
    /// benefit, since the bytes are never needed again once the symbol has been extracted.
    ///
    /// SHA-256 rather than the frame's own CRC-32C: this comparison decides whether two frames
    /// claiming the same symbol index are "the same scan again" or "a conflict", and an attacker who
    /// can find a CRC-32 collision could make a differing frame look like a harmless duplicate.
    /// </remarks>
    private readonly Dictionary<ushort, byte[]> _frameDigests = [];

    private readonly HashSet<ushort> _conflicts = [];

    private CapsuleFrame? _reference;
    private byte[]? _identity;

    public CapsuleFrame? Reference => _reference;

    public string CapsuleIdHex => _reference?.CapsuleIdHex ?? string.Empty;

    public int AcceptedFrameCount => _payloads.Count;

    public int ConflictCount => _conflicts.Count;

    public bool HasConflicts => _conflicts.Count > 0;

    public int SourceSymbolCount => _reference?.SourceSymbolCount ?? 0;

    public int RepairSymbolCount => _reference?.RepairSymbolCount ?? 0;

    public int SymbolLength => _reference?.SymbolLength ?? 0;

    public bool IsLdpc => _reference?.IsLdpc ?? false;

    /// <summary>
    /// True when enough frames are present to attempt recovery.
    /// </summary>
    /// <remarks>
    /// For Reed–Solomon this is an exact promise: any K of the K+R symbols reconstruct the capsule.
    /// For LDPC it is <b>necessary but not sufficient</b> — the code is not MDS, so completion comes
    /// from the decoder and never from this count.
    /// </remarks>
    public bool CanAttemptRecovery =>
        _reference is not null && !HasConflicts && _payloads.Count >= _reference.SourceSymbolCount;

    public FrameAdmission Admit(CapsuleFrame frame, ReadOnlySpan<byte> rawFrame)
    {
        byte[] identity = frame.IdentityBytes();

        if (_reference is null)
        {
            _reference = frame;
            _identity = identity;
        }
        else if (!_identity!.AsSpan().SequenceEqual(identity))
        {
            return _reference.CapsuleId.AsSpan().SequenceEqual(frame.CapsuleId)
                ? FrameAdmission.IdentityConflict
                : FrameAdmission.ForeignCapsule;
        }

        byte[] digest = System.Security.Cryptography.SHA256.HashData(rawFrame);

        if (_frameDigests.TryGetValue(frame.SymbolIndex, out byte[]? existing))
        {
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(existing, digest))
            {
                return FrameAdmission.ExactDuplicate;
            }

            _conflicts.Add(frame.SymbolIndex);
            return FrameAdmission.ConflictingDuplicate;
        }

        _payloads[frame.SymbolIndex] = frame.SymbolPayload;
        _frameDigests[frame.SymbolIndex] = digest;
        return FrameAdmission.Accepted;
    }

    /// <summary>
    /// Reconstructs the K source symbols and concatenates them into the padded stored payload.
    /// </summary>
    public byte[] RecoverStoredPayload()
    {
        if (_reference is null)
        {
            throw new RecoveryException(RecoveryErrorCategory.RecoveryInsufficientFrames,
                RecoveryStage.RecoveryDecode, "No frames were accepted.");
        }

        if (HasConflicts)
        {
            throw new RecoveryException(RecoveryErrorCategory.SessionDuplicateConflict, RecoveryStage.Session,
                $"{_conflicts.Count} symbol index(es) were scanned with conflicting bytes.");
        }

        int k = _reference.SourceSymbolCount;
        int r = _reference.RepairSymbolCount;
        int s = _reference.SymbolLength;

        if (_payloads.Count < k)
        {
            throw new RecoveryException(RecoveryErrorCategory.RecoveryInsufficientFrames,
                RecoveryStage.RecoveryDecode,
                $"{_payloads.Count} of {k} required symbols are present.");
        }

        byte[]?[] symbols = new byte[]?[k + r];
        foreach ((ushort index, byte[] payload) in _payloads)
        {
            symbols[index] = payload;
        }

        byte[][] source = _reference.IsLdpc ? RecoverLdpc(symbols, k, r, s) : RecoverReedSolomon(symbols, k, r, s);

        // K * S is computed in 64-bit: with the field ceilings alone it reaches 65535 * 65534.
        long paddedLength = (long)k * s;
        if (paddedLength > int.MaxValue)
        {
            throw new RecoveryException(RecoveryErrorCategory.ResourcePolicyRefused, RecoveryStage.RecoveryDecode,
                $"Stored payload would be {paddedLength} bytes, beyond what this implementation addresses.");
        }

        byte[] stored = new byte[(int)paddedLength];
        for (int i = 0; i < k; i++)
        {
            source[i].CopyTo(stored.AsSpan(i * s));
        }

        return stored;
    }

    private byte[][] RecoverReedSolomon(byte[]?[] symbols, int k, int r, int s)
    {
        // R = 0 has no repair symbols to substitute, so every source frame must be present. Short
        // circuit rather than invoking a codec that has nothing to do.
        if (r == 0)
        {
            var direct = new byte[k][];
            for (int i = 0; i < k; i++)
            {
                direct[i] = symbols[i]
                    ?? throw new RecoveryException(RecoveryErrorCategory.RecoveryInsufficientFrames,
                        RecoveryStage.RecoveryDecode,
                        $"Source symbol {i} is missing and this capsule carries no repair symbols.");
            }

            return direct;
        }

        // The coder reads the length from the present shards and allocates the missing ones, so
        // absent slots stay null.
        byte[][] working = new byte[k + r][];
        bool[] present = new bool[k + r];
        for (int i = 0; i < symbols.Length; i++)
        {
            if (symbols[i] is not null)
            {
                working[i] = symbols[i]!;
                present[i] = true;
            }
        }

        try
        {
            ReedSolomon16.Create(k, r).ReconstructData(working, present);
        }
        catch (Erasure16Exception ex)
        {
            throw new RecoveryException(RecoveryErrorCategory.RecoveryIncomplete, RecoveryStage.RecoveryDecode,
                $"Reed-Solomon reconstruction failed: {ex.Message}");
        }

        var recovered = new byte[k][];
        for (int i = 0; i < k; i++)
        {
            recovered[i] = working[i]
                ?? throw new RecoveryException(RecoveryErrorCategory.RecoveryIncomplete,
                    RecoveryStage.RecoveryDecode, $"Source symbol {i} was not reconstructed.");
        }

        return recovered;
    }

    private byte[][] RecoverLdpc(byte[]?[] symbols, int k, int r, int s)
    {
        int seed = (int)DeriveLdpcSeed(_reference!.CapsuleId);

        byte[][] working = new byte[k + r][];
        bool[] present = new bool[k + r];
        for (int i = 0; i < symbols.Length; i++)
        {
            if (symbols[i] is not null)
            {
                working[i] = symbols[i]!;
                present[i] = true;
            }
        }

        DecodeResult result;
        try
        {
            DecodeOptions options = DecodeOptions.Default
                .WithMaxResidualUnknowns(RecoveryProfile.LdpcMaxResidualUnknowns);
            result = LdpcStaircaseCodec.Create(k, r, seed).Decode(working, present, options);
        }
        catch (LdpcStaircaseException ex)
        {
            throw new RecoveryException(RecoveryErrorCategory.RecoveryIncomplete, RecoveryStage.RecoveryDecode,
                $"LDPC decode failed: {ex.Message}");
        }

        // Completion comes from the decoder, never from a received count: the code is not MDS, so a
        // session holding K or more valid frames may still be incomplete. Saying otherwise here
        // would be the single most damaging bug this tool could have - it would report a truncated
        // recovery as a successful one.
        if (!result.IsComplete)
        {
            throw new RecoveryException(RecoveryErrorCategory.RecoveryIncomplete, RecoveryStage.RecoveryDecode,
                $"LDPC decode resolved {result.RecoveredSourceCount} of {k} source symbols from the "
                + $"{_payloads.Count} frames supplied. Scan more codes: this code is probabilistic, so "
                + "holding K frames is necessary but not sufficient.");
        }

        var recovered = new byte[k][];
        for (int i = 0; i < k; i++)
        {
            recovered[i] = working[i]
                ?? throw new RecoveryException(RecoveryErrorCategory.RecoveryIncomplete,
                    RecoveryStage.RecoveryDecode,
                    $"Source symbol {i} was reported complete but is missing.");
        }

        return recovered;
    }

    /// <summary>
    /// Derives the LDPC graph seed from <c>capsule_id</c>. SPEC.md section 8.3.
    /// </summary>
    /// <remarks>
    /// No seed field exists on the wire; every decoder rebuilds the identical parity-check matrix
    /// from the frame header alone.
    /// </remarks>
    public static uint DeriveLdpcSeed(ReadOnlySpan<byte> capsuleId)
    {
        const uint modulus = 0x7FFFFFFE;
        uint c = ((uint)capsuleId[0] << 24) | ((uint)capsuleId[1] << 16)
            | ((uint)capsuleId[2] << 8) | capsuleId[3];
        return (c % modulus) + 1;
    }
}
