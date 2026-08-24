// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

namespace BinaryPaper.Recovery;

/// <summary>
/// Stable Recovery Profile 1 — the parameter envelope an official stable creator is permitted to
/// emit. SPEC.md section 12.
/// </summary>
/// <remarks>
/// <para>The wire can represent shapes no creator ever writes, and hostile input can choose
/// expensive-but-valid parameters. This check runs at stage 5, <b>before</b> any symbol table,
/// graph, or residual matrix is allocated, so refusing costs nothing.</para>
///
/// <para>The formulas are admission bounds, not predictions of this implementation's usage. They
/// define the envelope a conforming reader must handle and the envelope beyond which it may safely
/// refuse.</para>
/// </remarks>
public static class RecoveryProfile
{
    /// <summary>
    /// The peak restore working set an official creator designs every backup to fit, so that a
    /// printed backup restores on the weakest supported device. One-way door: it may be raised in a
    /// future profile, never lowered, because sheets already printed must keep restoring.
    /// </summary>
    public const long RestoreBudgetBytes = 84_934_656; // 81 MiB

    public const int LdpcMaxSourceSymbols = 16_000;
    public const int LdpcMinRepairSymbols = 7;
    public const int LdpcMaxRepairSymbols = 8_192;

    /// <summary>The residual solve is refused above this width, which keeps the LDPC cost linear in R.</summary>
    public const int LdpcMaxResidualUnknowns = 8_192;

    public const int LdpcLeftDegree = 7;

    // Writer-approved Argon2id parameters. A reader accepts at or below these and requires an
    // explicit override above them: a capsule declaring a multi-gigabyte memory cost is a
    // denial-of-service attempt, not a stronger backup.
    public const uint KdfMemoryKib = 65_536;
    public const uint KdfIterations = 3;
    public const uint KdfParallelism = 1;
    public const ushort KdfOutputLength = 32;
    public const int MinSaltLength = 16;
    public const int NonceLength = 12;
    public const int AesGcmTagLength = 16;

    /// <summary>Worst-case Reed–Solomon restore peak, evaluated at all R losses being source symbols.</summary>
    public static long PeakReedSolomonBytes(long k, long r, long s) =>
        ((2 * k) + r) * s + (8 * r * r) + (4 * r * k);

    /// <summary>Measured conservative LDPC restore peak. Linear in R, which is why it fits where Reed–Solomon cannot.</summary>
    public static long PeakLdpcBytes(long k, long r, long s) =>
        (2 * (k + r) * s)
        + (((r * LdpcMaxResidualUnknowns) + 7) / 8)
        + (8 * ((7 * k) + (2 * r)))
        + (320 * (k + r))
        + (4L * 1024 * 1024);

    /// <summary>
    /// Checks a declared capsule shape against the profile. Throws
    /// <see cref="RecoveryErrorCategory.ProfileOutOfRange"/> when no official creator could have
    /// emitted it.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> checked here:
    /// <list type="bullet">
    /// <item><description><c>symbol_len</c> has no profile ceiling. A printed backup satisfies a QR
    /// capacity constraint, but the error-correction level is not recoverable from a frame and raw
    /// frame input has no print context at all, so rejecting on an assumed capacity would be wrong.
    /// The cost bound already constrains large S combined with large K.</description></item>
    /// <item><description>Codec choice. A creator picks Reed–Solomon when it fits and LDPC
    /// otherwise, but an LDPC capsule that Reed–Solomon could also have carried is <i>cheaper</i> to
    /// recover, not more dangerous. The profile bounds cost, not taste.</description></item>
    /// </list>
    /// </remarks>
    public static void EnsureInProfile(CapsuleFrame reference)
    {
        long k = reference.SourceSymbolCount;
        long r = reference.RepairSymbolCount;
        long s = reference.SymbolLength;

        if (reference.IsLdpc)
        {
            if (k > LdpcMaxSourceSymbols)
            {
                throw OutOfProfile($"source_symbol_count {k} exceeds the LDPC decoder envelope of {LdpcMaxSourceSymbols}.");
            }

            if (r < LdpcMinRepairSymbols)
            {
                throw OutOfProfile(
                    $"repair_symbol_count {r} is below {LdpcMinRepairSymbols}; the staircase profile "
                    + "needs at least N1 repair symbols to be well-formed.");
            }

            if (r > LdpcMaxRepairSymbols)
            {
                throw OutOfProfile($"repair_symbol_count {r} exceeds the LDPC decoder envelope of {LdpcMaxRepairSymbols}.");
            }

            long peak = PeakLdpcBytes(k, r, s);
            if (peak > RestoreBudgetBytes)
            {
                throw OutOfProfile(
                    $"predicted LDPC restore peak {peak} bytes exceeds the {RestoreBudgetBytes}-byte profile budget.");
            }

            return;
        }

        long rsPeak = PeakReedSolomonBytes(k, r, s);
        if (rsPeak > RestoreBudgetBytes)
        {
            throw OutOfProfile(
                $"predicted Reed-Solomon restore peak {rsPeak} bytes exceeds the "
                + $"{RestoreBudgetBytes}-byte profile budget.");
        }
    }

    /// <summary>
    /// Validates KDF parameters before Argon2id is invoked. Argon2id's whole purpose is to be
    /// expensive, and these values come from the untrusted preamble.
    /// </summary>
    public static void EnsureKdfAffordable(uint memoryKib, uint iterations, uint parallelism, bool allowAboveProfile)
    {
        if (allowAboveProfile)
        {
            return;
        }

        if (memoryKib > KdfMemoryKib)
        {
            throw new RecoveryException(RecoveryErrorCategory.ResourcePolicyRefused, RecoveryStage.Preamble,
                $"Capsule declares an Argon2id memory cost of {memoryKib} KiB, above the profile value of "
                + $"{KdfMemoryKib} KiB. Re-run with --allow-high-kdf-cost if you trust this capsule.");
        }

        if (iterations > KdfIterations * 8 || parallelism > KdfParallelism * 8)
        {
            throw new RecoveryException(RecoveryErrorCategory.ResourcePolicyRefused, RecoveryStage.Preamble,
                $"Capsule declares Argon2id iterations={iterations}, parallelism={parallelism}, far above the "
                + "profile values. Re-run with --allow-high-kdf-cost if you trust this capsule.");
        }
    }

    private static RecoveryException OutOfProfile(string message) =>
        new(RecoveryErrorCategory.ProfileOutOfRange, RecoveryStage.Profile,
            message + " No official stable creator emits this shape.");
}
