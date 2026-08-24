// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

namespace BinaryPaper.Recovery;

/// <summary>Public metadata a reader may report before authentication. SPEC.md section 13.</summary>
public sealed record CapsuleSummary(
    string CapsuleIdHex,
    int ErasureAlg,
    int SourceSymbolCount,
    int RepairSymbolCount,
    int SymbolLength,
    int AcceptedFrameCount,
    int ConflictCount,
    bool IsLdpc)
{
    public string ErasureAlgName => ErasureAlg == CapsuleFrame.ErasureAlgLdpc ? "ldpc-staircase" : "reed-solomon";

    /// <summary>
    /// Whether recovery can be attempted. For LDPC this is necessary but not sufficient, and the
    /// distinction is surfaced rather than hidden: telling a user "you have enough codes" when the
    /// decoder may still fail is the wrong promise to make while they still have the pages in hand.
    /// </summary>
    public bool HasEnoughFrames => AcceptedFrameCount >= SourceSymbolCount && ConflictCount == 0;
}

/// <summary>
/// The complete recovery pipeline, in the order SPEC.md section 11 requires.
/// </summary>
public static class CapsuleRecovery
{
    /// <summary>
    /// Groups frames into sessions. Frames that fail validation are dropped and counted rather than
    /// aborting the run: discarding a damaged code and continuing is exactly what the erasure layer
    /// exists for.
    /// </summary>
    public static FrameIngestResult Ingest(IEnumerable<(string Source, byte[] Bytes)> frames)
    {
        var sessions = new List<ScanSession>();
        var rejected = new List<RejectedFrame>();
        int accepted = 0;
        int duplicates = 0;

        foreach ((string source, byte[] bytes) in frames)
        {
            CapsuleFrame frame;
            try
            {
                frame = CapsuleFrame.Decode(bytes);
            }
            catch (RecoveryException ex)
            {
                rejected.Add(new RejectedFrame(source, ex.CategoryName, ex.Message));
                continue;
            }

            ScanSession? target = sessions.FirstOrDefault(
                s => s.Reference is not null && s.Reference.CapsuleId.AsSpan().SequenceEqual(frame.CapsuleId));

            if (target is null)
            {
                target = new ScanSession();
                sessions.Add(target);
            }

            switch (target.Admit(frame, bytes))
            {
                case FrameAdmission.Accepted:
                    accepted++;
                    break;
                case FrameAdmission.ExactDuplicate:
                    duplicates++;
                    break;
                case FrameAdmission.ConflictingDuplicate:
                    rejected.Add(new RejectedFrame(source, "session.duplicate-conflict",
                        $"Symbol index {frame.SymbolIndex} was already scanned with different bytes."));
                    break;
                case FrameAdmission.IdentityConflict:
                    rejected.Add(new RejectedFrame(source, "session.identity-conflict",
                        "Frame shares this capsule's id but disagrees on its identity bytes."));
                    break;
                case FrameAdmission.ForeignCapsule:
                    // Cannot happen: sessions are selected by capsule_id above.
                    rejected.Add(new RejectedFrame(source, "session.multiple-capsules",
                        "Frame belongs to a different capsule."));
                    break;
            }
        }

        return new FrameIngestResult(sessions, rejected, accepted, duplicates);
    }

    /// <summary>Runs stages 5 through 10 on one session and returns the restored content.</summary>
    public static RestoredContent Recover(ScanSession session, string? password, ResourcePolicy policy)
    {
        if (session.Reference is null)
        {
            throw new RecoveryException(RecoveryErrorCategory.RecoveryInsufficientFrames,
                RecoveryStage.RecoveryDecode, "No frames were accepted.");
        }

        // Stage 5, before any decoder state exists.
        RecoveryProfile.EnsureInProfile(session.Reference);

        // Stage 6 and 7.
        byte[] stored = session.RecoverStoredPayload();
        CapsulePreamble preamble = CapsulePreamble.Decode(stored);

        if (preamble.IsEncrypted)
        {
            RecoveryProfile.EnsureKdfAffordable(
                preamble.KdfMemoryKib, preamble.KdfIterations, preamble.KdfParallelism, policy.AllowHighKdfCost);
        }

        byte[] body = preamble.ExtractBody(stored);

        // Stage 8: the boundary. Nothing below this line sees an unauthenticated byte.
        byte[] authenticated = CapsuleCrypto.Authenticate(body, preamble, password);

        // Stages 9 and 10.
        byte[] packageBytes = CapsuleCompression.Decompress(authenticated, preamble.CompressionAlgorithm, policy);
        return PayloadPackage.Parse(packageBytes);
    }

    public static CapsuleSummary Summarize(ScanSession session)
    {
        CapsuleFrame reference = session.Reference
            ?? throw new RecoveryException(RecoveryErrorCategory.FrameMalformed, RecoveryStage.Frame,
                "No frames were accepted.");

        return new CapsuleSummary(
            reference.CapsuleIdHex,
            reference.ErasureAlg,
            reference.SourceSymbolCount,
            reference.RepairSymbolCount,
            reference.SymbolLength,
            session.AcceptedFrameCount,
            session.ConflictCount,
            reference.IsLdpc);
    }
}

public sealed record RejectedFrame(string Source, string Category, string Message);

public sealed record FrameIngestResult(
    IReadOnlyList<ScanSession> Sessions,
    IReadOnlyList<RejectedFrame> Rejected,
    int AcceptedFrameCount,
    int DuplicateFrameCount);
