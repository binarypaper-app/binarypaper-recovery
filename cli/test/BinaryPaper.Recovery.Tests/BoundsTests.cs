// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace BinaryPaper.Recovery.Tests;

/// <summary>
/// Tests for the checks that stand between untrusted input and an allocation.
/// </summary>
/// <remarks>
/// These are the tests worth having. The happy path is covered by the conformance suite, which is
/// authoritative; what unit tests add is direct pressure on the arithmetic and bounds that a vector
/// can only reach indirectly.
/// </remarks>
public sealed class BoundsTests
{
    [Fact]
    public void CrcMatchesTheKnownCastagnoliCheckValue()
    {
        // The standard CRC-32C check value for the ASCII digits "123456789".
        uint crc = Crc32C.Compute("123456789"u8);
        Assert.Equal(0xE3069283u, crc);
    }

    [Fact]
    public void FrameRejectsLengthThatDisagreesWithSymbolLen()
    {
        byte[] frame = BuildFrame(symbolLen: 8);
        byte[] truncated = frame[..^2];
        // Recompute so the digest is not what fails; the length rule must be what rejects it.
        byte[] withFreshDigest = WithDigest(truncated);

        RecoveryException ex = Assert.Throws<RecoveryException>(() => CapsuleFrame.Decode(withFreshDigest));
        Assert.Equal(RecoveryErrorCategory.FrameInvalidField, ex.Category);
        Assert.Equal(RecoveryStage.Frame, ex.Stage);
    }

    [Fact]
    public void FrameRejectsShorterThanFixedOverheadAsMalformed()
    {
        RecoveryException ex = Assert.Throws<RecoveryException>(() => CapsuleFrame.Decode(new byte[10]));
        Assert.Equal(RecoveryErrorCategory.FrameMalformed, ex.Category);
    }

    [Theory]
    [InlineData(0, 0)] // the reserved all-zero version
    [InlineData(0, 5)] // the experimental predecessor
    [InlineData(2, 0)] // a future major
    [InlineData(1, 1)] // a future minor
    public void FrameRejectsEveryVersionButTheSupportedPair(byte major, byte minor)
    {
        byte[] frame = BuildFrame(symbolLen: 8);
        frame[4] = major;
        frame[5] = minor;

        // A real pre-promotion printout carries a valid digest for its own bytes, so the digest is
        // recomputed here. Otherwise this would test the CRC check and pass for the wrong reason.
        RecoveryException ex = Assert.Throws<RecoveryException>(() => CapsuleFrame.Decode(WithDigest(frame)));
        Assert.Equal(RecoveryErrorCategory.FrameUnsupportedVersion, ex.Category);
    }

    [Fact]
    public void FrameRejectsOddSymbolLen()
    {
        byte[] frame = BuildFrame(symbolLen: 7);
        RecoveryException ex = Assert.Throws<RecoveryException>(() => CapsuleFrame.Decode(WithDigest(frame)));
        Assert.Equal(RecoveryErrorCategory.FrameInvalidField, ex.Category);
    }

    [Fact]
    public void PreambleRejectsBodyLengthThatOverflowsA32BitSum()
    {
        // The regression this exists for: header + body_len computed in 32 bits wraps negative for a
        // large declared length, the bound check passes, and the slice that follows is unbounded.
        byte[] stored = BuildPlaintextPreamble(bodyLength: int.MaxValue, storedSize: 256);

        RecoveryException ex = Assert.Throws<RecoveryException>(() =>
        {
            CapsulePreamble preamble = CapsulePreamble.Decode(stored);
            preamble.ExtractBody(stored);
        });

        Assert.Equal(RecoveryErrorCategory.PreambleBodyOutOfBounds, ex.Category);
    }

    [Fact]
    public void PreambleRejectsBodyLengthNearTheUnsignedCeiling()
    {
        byte[] stored = BuildPlaintextPreamble(bodyLength: ulong.MaxValue - 8, storedSize: 256);

        RecoveryException ex = Assert.Throws<RecoveryException>(() =>
        {
            CapsulePreamble preamble = CapsulePreamble.Decode(stored);
            preamble.ExtractBody(stored);
        });

        Assert.Equal(RecoveryErrorCategory.PreambleBodyOutOfBounds, ex.Category);
    }

    [Fact]
    public void ProfileRejectsAShapeNoCreatorCouldEmit()
    {
        // Wire-valid, and catastrophically expensive: the Reed-Solomon 8R^2 term alone dwarfs the
        // budget. This is the case the profile check exists for.
        var frame = new CapsuleFrame
        {
            ErasureAlg = CapsuleFrame.ErasureAlgReedSolomon,
            CapsuleId = [0xDE, 0xAD, 0xBE, 0xEF],
            SourceSymbolCount = 30000,
            RepairSymbolCount = 30000,
            SymbolLength = 2048,
            SymbolIndex = 0,
            SymbolPayload = []
        };

        RecoveryException ex = Assert.Throws<RecoveryException>(() => RecoveryProfile.EnsureInProfile(frame));
        Assert.Equal(RecoveryErrorCategory.ProfileOutOfRange, ex.Category);
        Assert.Equal(RecoveryStage.Profile, ex.Stage);
    }

    [Fact]
    public void ProfileAcceptsTheDocumentedAnchorShape()
    {
        // K=16000, R=5600, S=358 is a real creator plan and must remain inside the profile. If this
        // ever fails, the profile has drifted away from what the product actually writes.
        var frame = new CapsuleFrame
        {
            ErasureAlg = CapsuleFrame.ErasureAlgLdpc,
            CapsuleId = [1, 2, 3, 4],
            SourceSymbolCount = 16000,
            RepairSymbolCount = 5600,
            SymbolLength = 358,
            SymbolIndex = 0,
            SymbolPayload = []
        };

        RecoveryProfile.EnsureInProfile(frame);
    }

    [Fact]
    public void ProfileRejectsLdpcBelowTheMinimumRepairCount()
    {
        var frame = new CapsuleFrame
        {
            ErasureAlg = CapsuleFrame.ErasureAlgLdpc,
            CapsuleId = [1, 2, 3, 4],
            SourceSymbolCount = 8,
            RepairSymbolCount = 3,
            SymbolLength = 34,
            SymbolIndex = 0,
            SymbolPayload = []
        };

        RecoveryException ex = Assert.Throws<RecoveryException>(() => RecoveryProfile.EnsureInProfile(frame));
        Assert.Equal(RecoveryErrorCategory.ProfileOutOfRange, ex.Category);
    }

    [Fact]
    public void LdpcSeedMatchesTheSpecifiedDerivation()
    {
        // The wrap-around rows are the interesting ones: they are where an implementation that used
        // the wrong modulus or forgot the +1 would diverge.
        Assert.Equal(1u, ScanSession.DeriveLdpcSeed([0x00, 0x00, 0x00, 0x00]));
        Assert.Equal(2u, ScanSession.DeriveLdpcSeed([0x00, 0x00, 0x00, 0x01]));
        Assert.Equal(1u, ScanSession.DeriveLdpcSeed([0x7F, 0xFF, 0xFF, 0xFE]));
        Assert.Equal(3u, ScanSession.DeriveLdpcSeed([0x80, 0x00, 0x00, 0x00]));
        Assert.Equal(4u, ScanSession.DeriveLdpcSeed([0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.Equal(1588444914u, ScanSession.DeriveLdpcSeed([0xDE, 0xAD, 0xBE, 0xEF]));
        Assert.Equal(84215046u, ScanSession.DeriveLdpcSeed([0x05, 0x05, 0x05, 0x05]));
    }

    [Fact]
    public void PasswordIsNormalizedToNfcBeforeEncoding()
    {
        // "é" composed (U+00E9) and decomposed (U+0065 U+0301) must derive the same key material,
        // or a user who types the same password on a different keyboard cannot open their backup.
        byte[] composed = CapsuleCrypto.EncodePassword("café");
        byte[] decomposed = CapsuleCrypto.EncodePassword("café");
        Assert.Equal(composed, decomposed);
    }

    // ------------------------------------------------------------------ builders

    private static byte[] BuildFrame(ushort symbolLen)
    {
        byte[] frame = new byte[CapsuleFrame.FixedOverhead + symbolLen];
        Encoding.ASCII.GetBytes(CapsuleFrame.Magic).CopyTo(frame, 0);
        frame[4] = CapsuleFrame.SupportedFormatMajor;
        frame[5] = CapsuleFrame.SupportedFormatMinor;
        frame[6] = CapsuleFrame.ErasureAlgReedSolomon;
        frame[7] = 0xF0;
        frame[8] = 0xE1;
        frame[9] = 0xD2;
        frame[10] = 0xC3;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(11), 2);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(15), symbolLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(17), 0);
        return WithDigest(frame);
    }

    private static byte[] WithDigest(byte[] frame)
    {
        byte[] copy = (byte[])frame.Clone();
        Crc32C.ComputeBytes(copy.AsSpan(0, copy.Length - CapsuleFrame.DigestLength))
            .CopyTo(copy.AsSpan(copy.Length - CapsuleFrame.DigestLength));
        return copy;
    }

    private static byte[] BuildPlaintextPreamble(ulong bodyLength, int storedSize)
    {
        byte[] stored = new byte[storedSize];
        Encoding.ASCII.GetBytes("BPCP").CopyTo(stored, 0);
        stored[4] = CapsulePreamble.AeadNone;
        stored[5] = CapsulePreamble.KdfNone;
        stored[6] = CapsulePreamble.CompressionNone;
        // KDF fields stay zero, salt_len and nonce_len stay zero.
        BinaryPrimitives.WriteUInt64BigEndian(stored.AsSpan(23), bodyLength);
        return stored;
    }
}
