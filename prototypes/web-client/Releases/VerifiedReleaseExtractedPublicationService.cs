using System.Buffers;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseExtractedPublicationFailureCode
{
    None = 0,
    UnsupportedPlatform = 1,
    PlanNotEligible = 2,
    StatusUnavailable = 3,
    StatusMismatch = 4,
    UnsafeDeploymentLayout = 5,
    UnsafeSourceTree = 6,
    TargetAlreadyPresent = 7,
    AtomicPublishFailed = 8,
    PublishedStateRequiresReconciliation = 9
}

public sealed record VerifiedReleaseExtractedPublicationReport(
    bool Succeeded,
    VerifiedReleaseExtractedPublicationFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int PackageCount,
    int FileCount,
    int DirectoryCount,
    long PublishedBytes,
    bool SourceExtractionTreeConsumed,
    bool TargetPublished,
    bool TargetImmutable,
    bool CurrentPointerChanged,
    bool ActivationPerformed,
    bool ReconciliationRequired)
{
    internal VerifiedExtractedPublishedRelease? PublishedRelease { get; init; }

    internal static VerifiedReleaseExtractedPublicationReport Failure(
        VerifiedReleaseExtractedPublicationFailureCode failureCode,
        string message,
        VerifiedReleaseExtractedPublicationPlan? plan = null,
        bool sourceConsumed = false,
        bool targetPublished = false,
        bool targetImmutable = false,
        bool reconciliationRequired = false) =>
        new(
            false,
            failureCode,
            message,
            plan?.Source.Plan.SetupRevision,
            plan?.Source.Plan.InstalledReleaseIdentity ?? string.Empty,
            plan?.Source.Plan.TargetReleaseIdentity ?? string.Empty,
            plan?.Source.Plan.Packages.Count ?? 0,
            plan?.Files.Count ?? 0,
            plan?.DirectoryCount ?? 0,
            plan?.PublicationBytes ?? 0,
            sourceConsumed,
            targetPublished,
            targetImmutable,
            CurrentPointerChanged: false,
            ActivationPerformed: false,
            reconciliationRequired);

    internal static VerifiedReleaseExtractedPublicationReport Success(
        VerifiedExtractedPublishedRelease publishedRelease) =>
        new(
            true,
            VerifiedReleaseExtractedPublicationFailureCode.None,
            "The verified extracted role tree was atomically published as an immutable inactive release without changing current.",
            publishedRelease.Plan.Source.Plan.SetupRevision,
            publishedRelease.Plan.Source.Plan.InstalledReleaseIdentity,
            publishedRelease.Plan.Source.Plan.TargetReleaseIdentity,
            publishedRelease.Plan.Source.Plan.Packages.Count,
            publishedRelease.Plan.Files.Count,
            publishedRelease.Plan.DirectoryCount,
            publishedRelease.PublishedBytes,
            SourceExtractionTreeConsumed: true,
            TargetPublished: true,
            TargetImmutable: true,
            CurrentPointerChanged: false,
            ActivationPerformed: false,
            ReconciliationRequired: false)
        {
            PublishedRelease = publishedRelease
        };
}

public sealed record VerifiedReleaseExtractedPublicationDiagnostics(
    bool Registered,
    bool StatusRevalidationRegistered,
    bool VerifiedPlanInputRegistered,
    bool ImmutableSourceValidationRegistered,
    bool ExecutableIntentValidationRegistered,
    bool RootPermissionTransitionRegistered,
    bool AtomicDirectoryPublishRegistered,
    bool PublishedTreeValidationRegistered,
    bool NetworkDownloadRegistered,
    bool ArchiveExtractionExecutionRegistered,
    bool FileCopyRegistered,
    bool CurrentPointerMutationRegistered,
    bool ActivationRegistered,
    bool RollbackRegistered,
    bool MigrationExecutionRegistered,
    bool ServiceControlRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed class VerifiedExtractedPublishedRelease
{
    internal VerifiedExtractedPublishedRelease(
        VerifiedReleaseExtractedPublicationPlan plan,
        string publishedPath,
        long publishedBytes)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        PublishedPath = publishedPath ?? string.Empty;
        PublishedBytes = publishedBytes;
    }

    internal VerifiedReleaseExtractedPublicationPlan Plan { get; }
    internal string PublishedPath { get; }
    internal long PublishedBytes { get; }
}

/// <summary>
/// Atomically renames one already-verified immutable extracted role tree into
/// its absent direct inactive-release target. It accepts only the internal
/// exact publication plan, revalidates every file, digest, executable mode,
/// directory, setup field, release-inventory entry, and current pointer before
/// and after rename, and reconciles ambiguous rename outcomes. It never copies
/// files, extracts archives, changes current, activates, rolls back, migrates,
/// controls services, or touches Admin, browser, radio, watchdog, command,
/// lease, keying, or transmit state.
/// </summary>
public sealed class VerifiedReleaseExtractedPublicationService
{
    private const int BufferSize = 128 * 1024;
    private const UnixFileMode PrivateWritableDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;
    private const UnixFileMode PrivateImmutableDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateImmutableFileMode = UnixFileMode.UserRead;
    private const UnixFileMode PrivateImmutableExecutableFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserExecute;
    private const UnixFileMode PublishedImmutableDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserExecute |
        UnixFileMode.GroupRead |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherExecute;
    private const UnixFileMode PublishedImmutableFileMode =
        UnixFileMode.UserRead |
        UnixFileMode.GroupRead |
        UnixFileMode.OtherRead;
    private const UnixFileMode PublishedImmutableExecutableFileMode =
        PublishedImmutableDirectoryMode;
    private const UnixFileMode RequiredOwnerDirectoryModes =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;
    private const UnixFileMode ForbiddenSharedWritableUnixModes =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private static readonly ReleasePackageRole[] RequiredRoles =
    [
        ReleasePackageRole.GatewayWeb,
        ReleasePackageRole.Broker,
        ReleasePackageRole.AetherRemoteAgent,
        ReleasePackageRole.StationEngine
    ];

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;
    private readonly Action<string, string> m_directoryMove;

    public VerifiedReleaseExtractedPublicationService(
        ReleaseInstallationStatusReader statusReader)
        : this(CreateStatusReader(statusReader), Directory.Move)
    {
    }

    internal VerifiedReleaseExtractedPublicationService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Action<string, string> directoryMove)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_directoryMove = directoryMove ??
            throw new ArgumentNullException(nameof(directoryMove));
        Snapshot = new VerifiedReleaseExtractedPublicationDiagnostics(
            Registered: true,
            StatusRevalidationRegistered: true,
            VerifiedPlanInputRegistered: true,
            ImmutableSourceValidationRegistered: true,
            ExecutableIntentValidationRegistered: true,
            RootPermissionTransitionRegistered: true,
            AtomicDirectoryPublishRegistered: true,
            PublishedTreeValidationRegistered: true,
            NetworkDownloadRegistered: false,
            ArchiveExtractionExecutionRegistered: false,
            FileCopyRegistered: false,
            CurrentPointerMutationRegistered: false,
            ActivationRegistered: false,
            RollbackRegistered: false,
            MigrationExecutionRegistered: false,
            ServiceControlRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseExtractedPublicationDiagnostics Snapshot { get; }

    [SupportedOSPlatform("linux")]
    internal async Task<VerifiedReleaseExtractedPublicationReport> PublishAsync(
        VerifiedReleaseExtractedPublicationPlanCompositionResult composition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(composition);
        cancellationToken.ThrowIfCancellationRequested();

        VerifiedReleaseExtractedPublicationPlan? plan =
            ValidateCompositionResult(composition);
        if (!OperatingSystem.IsLinux())
        {
            return VerifiedReleaseExtractedPublicationReport.Failure(
                VerifiedReleaseExtractedPublicationFailureCode.UnsupportedPlatform,
                "Verified extracted release publication requires a supported Linux runtime.",
                plan);
        }
        if (plan is null)
        {
            return VerifiedReleaseExtractedPublicationReport.Failure(
                VerifiedReleaseExtractedPublicationFailureCode.PlanNotEligible,
                "A successful exact verified extracted-publication plan is required.");
        }

        ReleaseStatusReadResult beforeStatus =
            await m_statusReader(cancellationToken).ConfigureAwait(false);
        VerifiedReleaseExtractedPublicationReport? statusFailure =
            ValidateStatusBeforePublish(beforeStatus, plan);
        if (statusFailure is not null)
        {
            return statusFailure;
        }

        try
        {
            ValidateDeploymentLayout(plan);
            EnsureTargetAbsent(plan);
            await ValidateImmutableTreeAsync(
                plan,
                plan.SourcePath,
                sourceTree: true,
                publishedTree: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PublicationException exception)
        {
            return VerifiedReleaseExtractedPublicationReport.Failure(
                exception.FailureCode,
                exception.Message,
                plan);
        }
        catch (Exception exception)
            when (IsExpectedFileSystemException(exception))
        {
            return VerifiedReleaseExtractedPublicationReport.Failure(
                VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
                "The verified extracted source tree could not be safely validated for publication.",
                plan);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            MakeRootWritableForMove(plan.SourcePath);
            m_directoryMove(plan.SourcePath, plan.TargetPath);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            return await ReconcileMoveExceptionAsync(
                plan,
                beforeStatus).ConfigureAwait(false);
        }

        return await ReconcileCompletedMoveAsync(
            plan,
            beforeStatus).ConfigureAwait(false);
    }

    [SupportedOSPlatform("linux")]
    private async Task<VerifiedReleaseExtractedPublicationReport>
        ReconcileMoveExceptionAsync(
            VerifiedReleaseExtractedPublicationPlan plan,
            ReleaseStatusReadResult beforeStatus)
    {
        if (!TryObserveMovePaths(
                plan,
                out bool sourcePresent,
                out bool targetPresent))
        {
            return ReconciliationFailure(
                plan,
                sourceConsumed: false,
                targetPublished: false,
                targetImmutable: false,
                "The atomic publication paths could not be safely inspected after rename failure.");
        }

        if (sourcePresent && !targetPresent)
        {
            if (TryFreezeRoot(plan.SourcePath))
            {
                return VerifiedReleaseExtractedPublicationReport.Failure(
                    VerifiedReleaseExtractedPublicationFailureCode.AtomicPublishFailed,
                    "The verified extracted role tree could not be atomically published into the release inventory.",
                    plan);
            }
            return ReconciliationFailure(
                plan,
                sourceConsumed: false,
                targetPublished: false,
                targetImmutable: false,
                "The failed publication source could not be restored to its immutable state.");
        }

        if (!sourcePresent && targetPresent)
        {
            return await ReconcileCompletedMoveAsync(
                plan,
                beforeStatus).ConfigureAwait(false);
        }

        if (sourcePresent)
        {
            TryFreezeRoot(plan.SourcePath);
        }
        if (targetPresent)
        {
            TryFreezeRoot(plan.TargetPath);
        }
        return ReconciliationFailure(
            plan,
            sourceConsumed: !sourcePresent,
            targetPublished: targetPresent,
            targetImmutable: false,
            "The atomic publication outcome is ambiguous and requires local reconciliation before another update attempt.");
    }

    [SupportedOSPlatform("linux")]
    private async Task<VerifiedReleaseExtractedPublicationReport>
        ReconcileCompletedMoveAsync(
            VerifiedReleaseExtractedPublicationPlan plan,
            ReleaseStatusReadResult beforeStatus)
    {
        bool targetImmutable = false;
        try
        {
            if (!TryObserveMovePaths(
                    plan,
                    out bool sourcePresent,
                    out bool targetPresent) ||
                sourcePresent ||
                !targetPresent)
            {
                return ReconciliationFailure(
                    plan,
                    sourceConsumed: !sourcePresent,
                    targetPublished: targetPresent,
                    targetImmutable: false,
                    "The atomic publication paths do not show one consumed extraction tree and one published target.");
            }

            if (!TryFreezeRoot(plan.TargetPath))
            {
                return ReconciliationFailure(
                    plan,
                    sourceConsumed: true,
                    targetPublished: true,
                    targetImmutable: false,
                    "The published target root could not be restored to its immutable mode after atomic rename.");
            }
            await ValidateImmutableTreeAsync(
                plan,
                plan.TargetPath,
                sourceTree: false,
                publishedTree: false,
                CancellationToken.None).ConfigureAwait(false);
            ExposePublishedServiceTree(plan);
            await ValidateImmutableTreeAsync(
                plan,
                plan.TargetPath,
                sourceTree: false,
                publishedTree: true,
                CancellationToken.None).ConfigureAwait(false);
            targetImmutable = true;

            ReleaseStatusReadResult afterStatus =
                await m_statusReader(CancellationToken.None).ConfigureAwait(false);
            if (!IsExactPublishedStatus(beforeStatus, afterStatus, plan.Source.Plan))
            {
                return ReconciliationFailure(
                    plan,
                    sourceConsumed: true,
                    targetPublished: true,
                    targetImmutable,
                    "The release inventory or current pointer changed unexpectedly after atomic publication.");
            }

            return VerifiedReleaseExtractedPublicationReport.Success(
                new VerifiedExtractedPublishedRelease(
                    plan,
                    plan.TargetPath,
                    plan.PublicationBytes));
        }
        catch (Exception exception)
            when (exception is PublicationException ||
                IsExpectedFileSystemException(exception))
        {
            if (!TryObserveMovePaths(
                    plan,
                    out bool sourcePresent,
                    out bool targetPresent))
            {
                return ReconciliationFailure(
                    plan,
                    sourceConsumed: false,
                    targetPublished: false,
                    targetImmutable: false,
                    "The published extracted release paths could not be safely inspected after its atomic rename.");
            }
            if (sourcePresent)
            {
                TryFreezeRoot(plan.SourcePath);
            }
            else if (targetPresent && !targetImmutable)
            {
                TryFreezeRoot(plan.TargetPath);
            }
            return ReconciliationFailure(
                plan,
                sourceConsumed: !sourcePresent,
                targetPublished: targetPresent,
                targetImmutable,
                "The published extracted release could not be fully reconciled after its atomic rename.");
        }
    }

    private static VerifiedReleaseExtractedPublicationReport ReconciliationFailure(
        VerifiedReleaseExtractedPublicationPlan plan,
        bool sourceConsumed,
        bool targetPublished,
        bool targetImmutable,
        string message) =>
        VerifiedReleaseExtractedPublicationReport.Failure(
            VerifiedReleaseExtractedPublicationFailureCode
                .PublishedStateRequiresReconciliation,
            message,
            plan,
            sourceConsumed,
            targetPublished,
            targetImmutable,
            reconciliationRequired: true);

    private static VerifiedReleaseExtractedPublicationPlan?
        ValidateCompositionResult(
            VerifiedReleaseExtractedPublicationPlanCompositionResult result)
    {
        try
        {
            VerifiedReleaseExtractedPublicationPlan? plan = result.Plan;
            if (!result.Succeeded ||
                result.FailureCode !=
                    VerifiedReleaseExtractedPublicationPlanFailureCode.None ||
                !result.ManifestIncluded ||
                !result.ImmutableSourceRequired ||
                !result.AtomicDirectoryPublishRequired ||
                result.CurrentPointerChanged ||
                result.ActivationPerformed ||
                plan is null ||
                !ValidatePlan(plan) ||
                result.SetupRevision != plan.Source.Plan.SetupRevision ||
                !string.Equals(
                    result.InstalledReleaseIdentity,
                    plan.Source.Plan.InstalledReleaseIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    result.TargetReleaseIdentity,
                    plan.Source.Plan.TargetReleaseIdentity,
                    StringComparison.Ordinal) ||
                result.PackageCount != plan.Source.Plan.Packages.Count ||
                result.FileCount != plan.Files.Count ||
                result.DirectoryCount != plan.DirectoryCount ||
                result.PublicationBytes != plan.PublicationBytes)
            {
                return null;
            }
            return plan;
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return null;
        }
    }

    private static bool ValidatePlan(
        VerifiedReleaseExtractedPublicationPlan plan)
    {
        VerifiedExtractedRelease source = plan.Source;
        VerifiedReleaseInstallationPlan installation = source.Plan;
        if (!ValidateInstallationPlan(installation) ||
            !IsCanonicalAbsolutePath(plan.SourcePath) ||
            !IsCanonicalAbsolutePath(plan.TargetPath) ||
            !PathEquals(plan.SourcePath, source.ExtractionPath) ||
            !PathEquals(plan.TargetPath, installation.TargetReleasePath) ||
            !PathEquals(
                Path.GetDirectoryName(plan.TargetPath),
                installation.ReleaseRootPath) ||
            !IsCanonicalExtractionPath(installation, plan.SourcePath) ||
            !IsCanonicalSourceStagingPath(
                installation,
                source.SourceStagedRelease.StagingPath) ||
            plan.Files.Count is < 5 or >
                VerifiedReleaseArchiveExtractionService.MaximumExtractedFileCount ||
            plan.Files.Count != source.Files.Count ||
            plan.DirectoryCount is < 4 or >
                VerifiedReleaseArchiveExtractionService.MaximumExtractedDirectoryCount ||
            plan.DirectoryCount != source.DirectoryCount ||
            plan.PublicationBytes <= 0 ||
            plan.PublicationBytes >
                VerifiedReleaseArchiveExtractionService.MaximumExpandedBytes ||
            plan.PublicationBytes != source.ExpandedBytes)
        {
            return false;
        }

        Dictionary<string, VerifiedExtractedReleaseFile> sourceFiles =
            source.Files.ToDictionary(
                file => file.RelativePath,
                StringComparer.Ordinal);
        if (sourceFiles.Count != source.Files.Count)
        {
            return false;
        }

        HashSet<string> relativePaths = new(StringComparer.Ordinal);
        HashSet<string> sourcePaths = new(StringComparer.Ordinal);
        HashSet<string> targetPaths = new(StringComparer.Ordinal);
        HashSet<string> directories = new(StringComparer.Ordinal);
        HashSet<ReleasePackageRole> roles = [];
        string previous = string.Empty;
        bool manifestFound = false;
        long total = 0;

        foreach (VerifiedReleaseExtractedPublicationFilePlan file in plan.Files)
        {
            if (!ReleasePackagePath.IsSafe(file.RelativePath) ||
                !relativePaths.Add(file.RelativePath) ||
                !sourcePaths.Add(file.SourcePath) ||
                !targetPaths.Add(file.TargetPath) ||
                file.Length < 0 ||
                file.Length >
                    VerifiedReleaseArchiveExtractionService
                        .MaximumExtractedFileLength ||
                file.Sha256.Length != 32 ||
                previous.Length > 0 &&
                    string.CompareOrdinal(previous, file.RelativePath) >= 0 ||
                !sourceFiles.TryGetValue(
                    file.RelativePath,
                    out VerifiedExtractedReleaseFile? sourceFile) ||
                sourceFile.Role != file.Role ||
                sourceFile.Length != file.Length ||
                sourceFile.Executable != file.Executable ||
                !sourceFile.Sha256.SequenceEqual(file.Sha256))
            {
                return false;
            }
            previous = file.RelativePath;

            string expectedSource = CanonicalFilePath(
                plan.SourcePath,
                file.RelativePath);
            string expectedTarget = CanonicalFilePath(
                plan.TargetPath,
                file.RelativePath);
            if (!PathEquals(file.SourcePath, expectedSource) ||
                !PathEquals(file.TargetPath, expectedTarget) ||
                !IsStrictDescendant(plan.SourcePath, expectedSource) ||
                !IsStrictDescendant(plan.TargetPath, expectedTarget))
            {
                return false;
            }

            bool manifest = string.Equals(
                file.RelativePath,
                LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                StringComparison.Ordinal);
            if (manifest)
            {
                if (manifestFound ||
                    file.Role != ReleasePackageRole.Unknown ||
                    file.Executable ||
                    file.Length != installation.ManifestLength ||
                    !file.Sha256.SequenceEqual(installation.ManifestSha256))
                {
                    return false;
                }
                manifestFound = true;
            }
            else
            {
                if (!RequiredRoles.Contains(file.Role) ||
                    !HasExactRoleRoot(file.Role, file.RelativePath))
                {
                    return false;
                }
                roles.Add(file.Role);
            }

            AddParentDirectories(file.RelativePath, directories);
            total = checked(total + file.Length);
            if (total >
                VerifiedReleaseArchiveExtractionService.MaximumExpandedBytes)
            {
                return false;
            }
        }

        return manifestFound &&
            roles.SetEquals(RequiredRoles) &&
            directories.Count == plan.DirectoryCount &&
            total == plan.PublicationBytes;
    }

    private static bool ValidateInstallationPlan(
        VerifiedReleaseInstallationPlan plan)
    {
        if (plan.SetupRevision < 1 ||
            !IsCanonicalReleaseIdentity(plan.InstalledReleaseIdentity) ||
            !IsCanonicalReleaseIdentity(plan.TargetReleaseIdentity) ||
            string.Equals(
                plan.InstalledReleaseIdentity,
                plan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            !ReleaseSemanticVersion.TryParse(
                plan.TargetVersion,
                out ReleaseSemanticVersion parsedVersion) ||
            !string.Equals(
                plan.TargetVersion,
                CanonicalSemanticVersion(parsedVersion),
                StringComparison.Ordinal) ||
            plan.Architecture is not ReleaseManifestArchitecture.LinuxX64 and
                not ReleaseManifestArchitecture.LinuxArm64 ||
            plan.UpdateChannel is not InstallationUpdateChannel.Stable and
                not InstallationUpdateChannel.Beta and
                not InstallationUpdateChannel.Pinned ||
            plan.ManifestLength is < 1 or >
                SignedReleaseManifestJson.MaximumManifestBytes ||
            plan.ManifestSha256.Length != 32 ||
            !IsCanonicalAbsolutePath(plan.BundleDirectory) ||
            !IsCanonicalAbsolutePath(plan.ReleaseRootPath) ||
            !IsCanonicalAbsolutePath(plan.DeploymentRootPath) ||
            !IsCanonicalAbsolutePath(plan.TargetReleasePath) ||
            !PathEquals(
                Path.GetDirectoryName(plan.ReleaseRootPath),
                plan.DeploymentRootPath) ||
            !PathEquals(
                Path.GetDirectoryName(plan.TargetReleasePath),
                plan.ReleaseRootPath) ||
            !string.Equals(
                Path.GetFileName(plan.TargetReleasePath),
                plan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            plan.Packages.Count != RequiredRoles.Length ||
            plan.InstallTransmitSupport != plan.TxSupportCapable)
        {
            return false;
        }

        if (plan.UpdateChannel == InstallationUpdateChannel.Pinned)
        {
            if (!string.Equals(
                    plan.PinnedReleaseIdentity,
                    plan.TargetReleaseIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (!string.IsNullOrEmpty(plan.PinnedReleaseIdentity))
        {
            return false;
        }

        HashSet<ReleasePackageRole> roles = [];
        HashSet<string> identities = new(StringComparer.Ordinal);
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (VerifiedReleaseInstallationPackagePlan package in plan.Packages)
        {
            if (!RequiredRoles.Contains(package.Role) ||
                !roles.Add(package.Role) ||
                string.IsNullOrWhiteSpace(package.PackageIdentity) ||
                !string.Equals(
                    package.PackageIdentity,
                    package.PackageIdentity.Trim(),
                    StringComparison.Ordinal) ||
                !identities.Add(package.PackageIdentity) ||
                !ReleasePackagePath.IsSafe(package.SourceRelativePath) ||
                !paths.Add(package.SourceRelativePath) ||
                package.Length is < 1 or >
                    SignedReleaseManifestVerifier.MaximumDeclaredPackageLength ||
                package.Sha256.Length != 32 ||
                !IsCanonicalAbsolutePath(package.TargetPath))
            {
                return false;
            }

            string expectedTarget = CanonicalFilePath(
                plan.TargetReleasePath,
                package.SourceRelativePath);
            if (!PathEquals(package.TargetPath, expectedTarget) ||
                !IsStrictDescendant(plan.TargetReleasePath, expectedTarget))
            {
                return false;
            }
        }
        return roles.SetEquals(RequiredRoles);
    }

    private static VerifiedReleaseExtractedPublicationReport?
        ValidateStatusBeforePublish(
            ReleaseStatusReadResult status,
            VerifiedReleaseExtractedPublicationPlan plan)
    {
        if (!status.Succeeded)
        {
            return VerifiedReleaseExtractedPublicationReport.Failure(
                VerifiedReleaseExtractedPublicationFailureCode.StatusUnavailable,
                "Local installation status is unavailable for extracted release publication.",
                plan);
        }
        if (!StatusMatchesPlan(status, plan.Source.Plan) ||
            status.AvailableReleaseIdentities.Contains(
                plan.Source.Plan.TargetReleaseIdentity,
                StringComparer.Ordinal))
        {
            return VerifiedReleaseExtractedPublicationReport.Failure(
                VerifiedReleaseExtractedPublicationFailureCode.StatusMismatch,
                "Completed setup, release inventory, or the active current pointer no longer matches the extracted publication plan.",
                plan);
        }
        return null;
    }

    private static bool StatusMatchesPlan(
        ReleaseStatusReadResult status,
        VerifiedReleaseInstallationPlan plan) =>
        status.Succeeded &&
        status.SetupComplete &&
        status.SetupLockMode == InstallationSetupLockMode.Complete &&
        status.LastCompletedStep == InstallationSetupStep.Administrator &&
        status.SetupRevision == plan.SetupRevision &&
        status.UpdateChannel == plan.UpdateChannel &&
        string.Equals(
            status.PinnedReleaseIdentity,
            plan.PinnedReleaseIdentity,
            StringComparison.Ordinal) &&
        status.InstallTransmitSupport == plan.InstallTransmitSupport &&
        status.ReleaseDirectoryPresent &&
        status.AvailableReleaseCount ==
            status.AvailableReleaseIdentities.Count &&
        status.AvailableReleaseCount is >= 1 and <=
            ReleaseInstallationStatusReader.MaximumReleaseCount &&
        status.AvailableReleaseIdentities
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .SequenceEqual(
                status.AvailableReleaseIdentities,
                StringComparer.Ordinal) &&
        status.AvailableReleaseIdentities.Distinct(StringComparer.Ordinal).Count() ==
            status.AvailableReleaseCount &&
        status.AvailableReleaseIdentities.Contains(
            plan.InstalledReleaseIdentity,
            StringComparer.Ordinal) &&
        status.CurrentPointerPresent &&
        string.Equals(
            status.ActiveReleaseIdentity,
            plan.InstalledReleaseIdentity,
            StringComparison.Ordinal) &&
        !status.RollbackCandidateKnown;

    private static bool IsExactPublishedStatus(
        ReleaseStatusReadResult before,
        ReleaseStatusReadResult after,
        VerifiedReleaseInstallationPlan plan)
    {
        if (!StatusMatchesPlan(after, plan) ||
            before.SetupSchemaVersion != after.SetupSchemaVersion ||
            before.SetupRevision != after.SetupRevision ||
            before.SetupComplete != after.SetupComplete ||
            before.SetupLockMode != after.SetupLockMode ||
            before.LastCompletedStep != after.LastCompletedStep ||
            before.UpdateChannel != after.UpdateChannel ||
            !string.Equals(
                before.PinnedReleaseIdentity,
                after.PinnedReleaseIdentity,
                StringComparison.Ordinal) ||
            before.InstallTransmitSupport != after.InstallTransmitSupport ||
            before.CurrentPointerPresent != after.CurrentPointerPresent ||
            !string.Equals(
                before.ActiveReleaseIdentity,
                after.ActiveReleaseIdentity,
                StringComparison.Ordinal) ||
            before.RollbackCandidateKnown != after.RollbackCandidateKnown ||
            before.AvailableReleaseCount + 1 != after.AvailableReleaseCount ||
            after.AvailableReleaseCount > ReleaseInstallationStatusReader.MaximumReleaseCount)
        {
            return false;
        }

        string[] expected = before.AvailableReleaseIdentities
            .Append(plan.TargetReleaseIdentity)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        return expected.SequenceEqual(
            after.AvailableReleaseIdentities,
            StringComparer.Ordinal);
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateDeploymentLayout(
        VerifiedReleaseExtractedPublicationPlan plan)
    {
        ValidateSharedDirectory(
            plan.Source.Plan.DeploymentRootPath,
            "The deployment root is unsafe for extracted release publication.");
        ValidateSharedDirectory(
            plan.Source.Plan.ReleaseRootPath,
            "The release root is unsafe for extracted release publication.");

        string sourceRoot = Path.GetDirectoryName(plan.SourcePath) ??
            throw Failure(
                VerifiedReleaseExtractedPublicationFailureCode
                    .UnsafeDeploymentLayout,
                "The extracted publication source has no private staging root.");
        ValidatePrivateWritableDirectory(
            sourceRoot,
            "The private extraction staging root is unsafe for publication.");
    }

    private static void EnsureTargetAbsent(
        VerifiedReleaseExtractedPublicationPlan plan)
    {
        if (PathEntryExists(plan.TargetPath))
        {
            throw Failure(
                VerifiedReleaseExtractedPublicationFailureCode.TargetAlreadyPresent,
                "The inactive target release already exists and will not be overwritten.");
        }
        if (!PathEntryExists(plan.SourcePath))
        {
            throw Failure(
                VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
                "The verified extracted source tree is no longer present.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task ValidateImmutableTreeAsync(
        VerifiedReleaseExtractedPublicationPlan plan,
        string rootPath,
        bool sourceTree,
        bool publishedTree,
        CancellationToken cancellationToken)
    {
        DirectoryInfo root = new(rootPath);
        ValidateImmutableDirectory(root, publishedTree);

        Dictionary<string, FileInfo> files = new(StringComparer.Ordinal);
        HashSet<string> directories = new(StringComparer.Ordinal);
        Stack<DirectoryInfo> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = pending.Pop();
            ValidateImmutableDirectory(directory, publishedTree);
            bool isRoot = PathEquals(directory.FullName, rootPath);
            FileSystemInfo[] entries = directory.GetFileSystemInfos();
            if (!isRoot && entries.Length == 0)
            {
                throw Failure(
                    VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
                    "The immutable extracted release tree contains an empty directory.");
            }

            if (!isRoot)
            {
                string relativeDirectory = RelativePath(rootPath, directory.FullName);
                if (!ReleasePackagePath.IsSafe(relativeDirectory) ||
                    !directories.Add(relativeDirectory) ||
                    directories.Count >
                        VerifiedReleaseArchiveExtractionService
                            .MaximumExtractedDirectoryCount)
                {
                    throw Failure(
                        VerifiedReleaseExtractedPublicationFailureCode
                            .UnsafeSourceTree,
                        "The immutable extracted release tree contains an unsafe or excessive directory inventory.");
                }
            }

            foreach (FileSystemInfo entry in entries)
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        VerifiedReleaseExtractedPublicationFailureCode
                            .UnsafeSourceTree,
                        "The immutable extracted release tree contains a symbolic link or reparse point.");
                }
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                    continue;
                }
                if (entry is not FileInfo file ||
                    (file.Attributes & FileAttributes.Directory) != 0)
                {
                    throw Failure(
                        VerifiedReleaseExtractedPublicationFailureCode
                            .UnsafeSourceTree,
                        "The immutable extracted release tree contains a non-regular entry.");
                }

                string relativePath = RelativePath(rootPath, file.FullName);
                if (!files.TryAdd(relativePath, file) ||
                    files.Count >
                        VerifiedReleaseArchiveExtractionService
                            .MaximumExtractedFileCount)
                {
                    throw Failure(
                        VerifiedReleaseExtractedPublicationFailureCode
                            .UnsafeSourceTree,
                        "The immutable extracted release tree contains a duplicate or excessive file inventory.");
                }
            }
        }

        HashSet<string> expectedDirectories = new(StringComparer.Ordinal);
        foreach (VerifiedReleaseExtractedPublicationFilePlan expected in plan.Files)
        {
            AddParentDirectories(expected.RelativePath, expectedDirectories);
        }
        if (files.Count != plan.Files.Count ||
            directories.Count != plan.DirectoryCount ||
            !directories.SetEquals(expectedDirectories))
        {
            throw Failure(
                VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
                "The immutable extracted release tree does not contain the exact verified file and directory inventory.");
        }

        foreach (VerifiedReleaseExtractedPublicationFilePlan expected in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(expected.RelativePath, out FileInfo? file))
            {
                throw Failure(
                    VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
                    "The immutable extracted release tree is missing a verified file.");
            }
            string expectedPath = sourceTree
                ? expected.SourcePath
                : expected.TargetPath;
            if (!PathEquals(file.FullName, expectedPath))
            {
                throw Failure(
                    VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
                    "An immutable extracted release file is outside its exact publication mapping.");
            }
            await ValidateImmutableFileAsync(
                file,
                expected,
                publishedTree,
                cancellationToken).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task ValidateImmutableFileAsync(
        FileInfo file,
        VerifiedReleaseExtractedPublicationFilePlan expected,
        bool publishedTree,
        CancellationToken cancellationToken)
    {
        FileState before = ReadImmutableFileState(
            file,
            expected.Executable,
            publishedTree);
        if (before.Length != expected.Length)
        {
            throw Failure(
                VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
                "An immutable extracted release file length no longer matches its verified publication plan.");
        }

        using FileStream stream = OpenRead(file.FullName);
        if (stream.Length != before.Length)
        {
            throw Changed();
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long bytesRead = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                bytesRead = checked(bytesRead + read);
                if (bytesRead > before.Length)
                {
                    throw Changed();
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (bytesRead != before.Length)
        {
            throw Changed();
        }
        byte[] digest = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(digest, expected.Sha256))
        {
            throw Failure(
                VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
                "An immutable extracted release file digest no longer matches its verified publication plan.");
        }

        FileState after = ReadImmutableFileState(
            file,
            expected.Executable,
            publishedTree);
        if (after != before || stream.Length != before.Length)
        {
            throw Changed();
        }
    }

    [SupportedOSPlatform("linux")]
    private static FileState ReadImmutableFileState(
        FileInfo file,
        bool executable,
        bool publishedTree)
    {
        file.Refresh();
        UnixFileMode expectedMode = publishedTree
            ? executable
                ? PublishedImmutableExecutableFileMode
                : PublishedImmutableFileMode
            : executable
                ? PrivateImmutableExecutableFileMode
                : PrivateImmutableFileMode;
        if (!file.Exists ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.LinkTarget is not null ||
            File.GetUnixFileMode(file.FullName) != expectedMode)
        {
            throw Failure(
                VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
                "The immutable extracted release tree contains an unsafe file or executable-mode mismatch.");
        }
        return new FileState(
            file.Length,
            file.LastWriteTimeUtc,
            expectedMode);
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutableDirectory(
        DirectoryInfo directory,
        bool publishedTree)
    {
        directory.Refresh();
        UnixFileMode expectedMode = publishedTree
            ? PublishedImmutableDirectoryMode
            : PrivateImmutableDirectoryMode;
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            File.GetUnixFileMode(directory.FullName) != expectedMode)
        {
            throw Failure(
                VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
                "The immutable extracted release tree contains an unsafe or writable directory.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ExposePublishedServiceTree(
        VerifiedReleaseExtractedPublicationPlan plan)
    {
        HashSet<string> directories = new(StringComparer.Ordinal)
        {
            plan.TargetPath
        };
        foreach (VerifiedReleaseExtractedPublicationFilePlan file in plan.Files)
        {
            FileInfo target = new(file.TargetPath);
            target.Refresh();
            if (!target.Exists ||
                target.LinkTarget is not null ||
                (target.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "A verified published release file became unsafe before service exposure.");
            }
            File.SetUnixFileMode(
                file.TargetPath,
                file.Executable
                    ? PublishedImmutableExecutableFileMode
                    : PublishedImmutableFileMode);

            string? parent = Path.GetDirectoryName(file.TargetPath);
            while (parent is not null && !PathEquals(parent, plan.TargetPath))
            {
                if (!directories.Add(parent))
                {
                    break;
                }
                parent = Path.GetDirectoryName(parent);
            }
        }
        foreach (string path in directories)
        {
            DirectoryInfo directory = new(path);
            directory.Refresh();
            if (!directory.Exists ||
                directory.LinkTarget is not null ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "A verified published release directory became unsafe before service exposure.");
            }
            File.SetUnixFileMode(path, PublishedImmutableDirectoryMode);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateSharedDirectory(string path, string message)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        UnixFileMode mode = directory.Exists
            ? File.GetUnixFileMode(path)
            : UnixFileMode.None;
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            (mode & RequiredOwnerDirectoryModes) != RequiredOwnerDirectoryModes ||
            (mode & ForbiddenSharedWritableUnixModes) != 0)
        {
            throw Failure(
                VerifiedReleaseExtractedPublicationFailureCode
                    .UnsafeDeploymentLayout,
                message);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidatePrivateWritableDirectory(
        string path,
        string message)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            File.GetUnixFileMode(path) != PrivateWritableDirectoryMode)
        {
            throw Failure(
                VerifiedReleaseExtractedPublicationFailureCode
                    .UnsafeDeploymentLayout,
                message);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void MakeRootWritableForMove(string path)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            File.GetUnixFileMode(path) != PrivateImmutableDirectoryMode)
        {
            throw new IOException(
                "The verified extracted publication root is no longer an immutable regular directory.");
        }

        File.SetUnixFileMode(path, PrivateWritableDirectoryMode);
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            File.GetUnixFileMode(path) != PrivateWritableDirectoryMode)
        {
            throw new IOException(
                "The verified extracted publication root could not enter its bounded rename mode.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static bool TryFreezeRoot(string path)
    {
        try
        {
            DirectoryInfo directory = new(path);
            directory.Refresh();
            if (!directory.Exists ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                directory.LinkTarget is not null)
            {
                return false;
            }
            File.SetUnixFileMode(path, PrivateImmutableDirectoryMode);
            return File.GetUnixFileMode(path) ==
                PrivateImmutableDirectoryMode;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryObserveMovePaths(
        VerifiedReleaseExtractedPublicationPlan plan,
        out bool sourcePresent,
        out bool targetPresent)
    {
        sourcePresent = false;
        targetPresent = false;
        return TryPathEntryExists(plan.SourcePath, out sourcePresent) &&
            TryPathEntryExists(plan.TargetPath, out targetPresent);
    }

    private static bool TryPathEntryExists(string path, out bool present)
    {
        try
        {
            present = PathEntryExists(path);
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            present = false;
            return false;
        }
    }

    private static bool PathEntryExists(string path)
    {
        FileSystemInfo info = new DirectoryInfo(path);
        info.Refresh();
        return Directory.Exists(path) || File.Exists(path) ||
            info.LinkTarget is not null;
    }

    private static FileStream OpenRead(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = BufferSize,
                Options = FileOptions.SequentialScan
            });

    private static Func<CancellationToken, Task<ReleaseStatusReadResult>>
        CreateStatusReader(ReleaseInstallationStatusReader statusReader)
    {
        ArgumentNullException.ThrowIfNull(statusReader);
        return statusReader.ReadAsync;
    }

    private static bool IsCanonicalExtractionPath(
        VerifiedReleaseInstallationPlan plan,
        string path)
    {
        string root = Path.GetFullPath(
            Path.Combine(
                plan.DeploymentRootPath,
                VerifiedReleaseArchiveExtractionService
                    .ExtractionStagingDirectoryName));
        if (!PathEquals(Path.GetDirectoryName(root), plan.DeploymentRootPath) ||
            !PathEquals(Path.GetDirectoryName(path), root))
        {
            return false;
        }
        return HasCanonicalTransactionName(
            path,
            plan.TargetReleaseIdentity);
    }

    private static bool IsCanonicalSourceStagingPath(
        VerifiedReleaseInstallationPlan plan,
        string path)
    {
        string root = Path.GetFullPath(
            Path.Combine(
                plan.DeploymentRootPath,
                VerifiedReleaseStagingService.StagingDirectoryName));
        return IsCanonicalAbsolutePath(path) &&
            PathEquals(Path.GetDirectoryName(root), plan.DeploymentRootPath) &&
            PathEquals(Path.GetDirectoryName(path), root) &&
            HasCanonicalTransactionName(path, plan.TargetReleaseIdentity);
    }

    private static bool HasCanonicalTransactionName(
        string path,
        string releaseIdentity)
    {
        string name = Path.GetFileName(path);
        string prefix = releaseIdentity + ".";
        return name.StartsWith(prefix, StringComparison.Ordinal) &&
            name.Length == prefix.Length + 32 &&
            name.AsSpan(prefix.Length).ToString().All(IsLowerHex);
    }

    private static string CanonicalFilePath(
        string root,
        string relativePath) =>
        Path.GetFullPath(
            Path.Combine(
                root,
                relativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));

    private static void AddParentDirectories(
        string relativePath,
        ISet<string> directories)
    {
        string[] segments = relativePath.Split('/');
        string current = string.Empty;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            current = current.Length == 0
                ? segments[index]
                : $"{current}/{segments[index]}";
            directories.Add(current);
        }
    }

    private static bool HasExactRoleRoot(
        ReleasePackageRole role,
        string relativePath)
    {
        string prefix = RoleDirectoryName(role) + "/";
        return relativePath.StartsWith(prefix, StringComparison.Ordinal) &&
            relativePath.Length > prefix.Length;
    }

    private static string RoleDirectoryName(ReleasePackageRole role) =>
        role switch
        {
            ReleasePackageRole.GatewayWeb => "gateway-web",
            ReleasePackageRole.Broker => "broker",
            ReleasePackageRole.AetherRemoteAgent => "aetherremote-agent",
            ReleasePackageRole.StationEngine => "station-engine",
            _ => throw new InvalidOperationException(
                "The extracted publication role is unsupported.")
        };

    private static bool IsStrictDescendant(string root, string path) =>
        path.StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

    private static bool IsCanonicalAbsolutePath(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            !Path.IsPathFullyQualified(value))
        {
            return false;
        }
        try
        {
            return PathEquals(Path.GetFullPath(value), value);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsCanonicalReleaseIdentity(string value)
    {
        try
        {
            return string.Equals(
                InstallationReleaseIdentity.Parse(value),
                value,
                StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string CanonicalSemanticVersion(
        ReleaseSemanticVersion version) =>
        $"{version.Major}.{version.Minor}.{version.Patch}" +
        (version.Prerelease.Length == 0
            ? string.Empty
            : $"-{version.Prerelease}") +
        (version.BuildMetadata.Length == 0
            ? string.Empty
            : $"+{version.BuildMetadata}");

    private static string RelativePath(string rootPath, string entryPath)
    {
        string relative = Path.GetRelativePath(rootPath, entryPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
        }
        return relative;
    }

    private static bool IsExpectedFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            SecurityException or CryptographicException or ArgumentException or
            NotSupportedException or PathTooLongException or OverflowException;

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool PathEquals(string? left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static PublicationException Changed() =>
        Failure(
            VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree,
            "The immutable extracted release tree changed while it was being validated.");

    private static PublicationException Failure(
        VerifiedReleaseExtractedPublicationFailureCode failureCode,
        string message) =>
        new(failureCode, message);

    private readonly record struct FileState(
        long Length,
        DateTime LastWriteTimeUtc,
        UnixFileMode Mode);

    private sealed class PublicationException(
        VerifiedReleaseExtractedPublicationFailureCode failureCode,
        string message) : Exception(message)
    {
        internal VerifiedReleaseExtractedPublicationFailureCode FailureCode { get; } =
            failureCode;
    }
}
