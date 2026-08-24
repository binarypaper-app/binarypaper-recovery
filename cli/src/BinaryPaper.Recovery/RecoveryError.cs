// Copyright 2026 BinaryPaper
// SPDX-License-Identifier: Apache-2.0

namespace BinaryPaper.Recovery;

/// <summary>
/// The validation stages of the recovery pipeline, in order. See SPEC.md section 11.
/// </summary>
/// <remarks>
/// The stage matters as much as the category. It is what tells a user whether their pages are
/// damaged, their password is wrong, or the backup was never valid — three very different next
/// actions. Reporting a failure at the wrong stage is a conformance defect even when the input is
/// correctly rejected.
/// </remarks>
public enum RecoveryStage
{
    Frame,
    Session,
    Profile,
    RecoveryDecode,
    Preamble,
    Authentication,
    Compression,
    Package,
    Output
}

/// <summary>
/// The stable failure categories. These identifiers are part of the recovery contract: conformance
/// vectors name them and scripts read them, so they are never renamed.
/// </summary>
public enum RecoveryErrorCategory
{
    FrameMalformed,
    FrameUnsupportedVersion,
    FrameUnsupportedAlgorithm,
    FrameInvalidField,
    FrameDigestMismatch,
    SessionIdentityConflict,
    SessionDuplicateConflict,
    SessionMultipleCapsules,
    ProfileOutOfRange,
    ResourcePolicyRefused,
    RecoveryInsufficientFrames,
    RecoveryIncomplete,
    PreambleMalformed,
    PreambleInvalidAlgorithm,
    PreambleInvalidParameter,
    PreambleBodyOutOfBounds,
    AuthFailed,
    AuthDigestMismatch,
    CompressionMalformed,
    PackageMalformed,
    PackageInvalidEntry,
    PackageManifestInvalid,
    OutputUnsafeDestination,
    IoFailed
}

/// <summary>
/// A recovery failure: what went wrong, where, and in terms stable enough for a script to branch
/// on.
/// </summary>
public sealed class RecoveryException : Exception
{
    public RecoveryException(RecoveryErrorCategory category, RecoveryStage stage, string message)
        : base(message)
    {
        Category = category;
        Stage = stage;
    }

    public RecoveryErrorCategory Category { get; }

    public RecoveryStage Stage { get; }

    /// <summary>The stable wire name, e.g. <c>frame.digest-mismatch</c>.</summary>
    public string CategoryName => RecoveryErrors.Name(Category);

    /// <summary>The stable stage name, e.g. <c>authentication</c>.</summary>
    public string StageName => RecoveryErrors.Name(Stage);
}

public static class RecoveryErrors
{
    public static string Name(RecoveryErrorCategory category) => category switch
    {
        RecoveryErrorCategory.FrameMalformed => "frame.malformed",
        RecoveryErrorCategory.FrameUnsupportedVersion => "frame.unsupported-version",
        RecoveryErrorCategory.FrameUnsupportedAlgorithm => "frame.unsupported-algorithm",
        RecoveryErrorCategory.FrameInvalidField => "frame.invalid-field",
        RecoveryErrorCategory.FrameDigestMismatch => "frame.digest-mismatch",
        RecoveryErrorCategory.SessionIdentityConflict => "session.identity-conflict",
        RecoveryErrorCategory.SessionDuplicateConflict => "session.duplicate-conflict",
        RecoveryErrorCategory.SessionMultipleCapsules => "session.multiple-capsules",
        RecoveryErrorCategory.ProfileOutOfRange => "profile.out-of-range",
        RecoveryErrorCategory.ResourcePolicyRefused => "resource.policy-refused",
        RecoveryErrorCategory.RecoveryInsufficientFrames => "recovery.insufficient-frames",
        RecoveryErrorCategory.RecoveryIncomplete => "recovery.incomplete",
        RecoveryErrorCategory.PreambleMalformed => "preamble.malformed",
        RecoveryErrorCategory.PreambleInvalidAlgorithm => "preamble.invalid-algorithm",
        RecoveryErrorCategory.PreambleInvalidParameter => "preamble.invalid-parameter",
        RecoveryErrorCategory.PreambleBodyOutOfBounds => "preamble.body-out-of-bounds",
        RecoveryErrorCategory.AuthFailed => "auth.failed",
        RecoveryErrorCategory.AuthDigestMismatch => "auth.digest-mismatch",
        RecoveryErrorCategory.CompressionMalformed => "compression.malformed",
        RecoveryErrorCategory.PackageMalformed => "package.malformed",
        RecoveryErrorCategory.PackageInvalidEntry => "package.invalid-entry",
        RecoveryErrorCategory.PackageManifestInvalid => "package.manifest-invalid",
        RecoveryErrorCategory.OutputUnsafeDestination => "output.unsafe-destination",
        RecoveryErrorCategory.IoFailed => "io.failed",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    public static string Name(RecoveryStage stage) => stage switch
    {
        RecoveryStage.Frame => "frame",
        RecoveryStage.Session => "session",
        RecoveryStage.Profile => "profile",
        RecoveryStage.RecoveryDecode => "recovery",
        RecoveryStage.Preamble => "preamble",
        RecoveryStage.Authentication => "authentication",
        RecoveryStage.Compression => "compression",
        RecoveryStage.Package => "package",
        RecoveryStage.Output => "output",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    /// <summary>
    /// Maps a failure to its process exit code. The families are stable so scripts can branch on
    /// them; the human-readable message may improve freely.
    /// </summary>
    public static int ExitCode(RecoveryErrorCategory category) => category switch
    {
        RecoveryErrorCategory.FrameMalformed => 20,
        RecoveryErrorCategory.FrameUnsupportedVersion => 20,
        RecoveryErrorCategory.FrameUnsupportedAlgorithm => 20,
        RecoveryErrorCategory.FrameInvalidField => 20,
        RecoveryErrorCategory.FrameDigestMismatch => 20,
        RecoveryErrorCategory.PreambleMalformed => 20,
        RecoveryErrorCategory.PreambleInvalidAlgorithm => 20,
        RecoveryErrorCategory.PreambleInvalidParameter => 20,
        RecoveryErrorCategory.PreambleBodyOutOfBounds => 20,
        RecoveryErrorCategory.ProfileOutOfRange => 20,

        RecoveryErrorCategory.SessionIdentityConflict => 21,
        RecoveryErrorCategory.SessionDuplicateConflict => 21,
        RecoveryErrorCategory.SessionMultipleCapsules => 21,

        RecoveryErrorCategory.RecoveryInsufficientFrames => 22,
        RecoveryErrorCategory.RecoveryIncomplete => 22,

        RecoveryErrorCategory.ResourcePolicyRefused => 30,

        RecoveryErrorCategory.AuthFailed => 40,
        RecoveryErrorCategory.AuthDigestMismatch => 41,

        RecoveryErrorCategory.CompressionMalformed => 50,
        RecoveryErrorCategory.PackageMalformed => 50,
        RecoveryErrorCategory.PackageInvalidEntry => 50,
        RecoveryErrorCategory.PackageManifestInvalid => 50,

        RecoveryErrorCategory.OutputUnsafeDestination => 51,
        RecoveryErrorCategory.IoFailed => 60,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}

/// <summary>Stable process exit codes. See the CLI reference in the repository README.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int UsageError = 2;
    public const int NoFrames = 10;
    public const int InputDecodeError = 11;
}
