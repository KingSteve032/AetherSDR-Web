using System.Buffers;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleasePublicationFailureCode
{
    None = 0,
    UnsupportedPlatform = 1,
    StagingNotEligible = 2,
    StatusUnavailable = 3,
    StatusMismatch = 4,
    UnsafeDeploymentLayout = 5,
    UnsafeStagingTree = 6,
    TargetAlreadyPresent = 7,
    AtomicPublishFailed = 8,
    PublishedStateRequiresReconciliation = 9
}

public sealed record VerifiedReleasePublicationReport(
    bool Succeeded,
    VerifiedReleasePublicationFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int PackageCount,
    long PublishedBytes,
    bool SourceStagingTreeConsumed,
    bool TargetPublished,
    bool TargetImmutable,
    bool CurrentPointerChanged,
    bool ActivationPerformed,
    bool ReconciliationRequired)
{
    internal VerifiedPublishedRelease? PublishedRelease { get; init; }

    internal static VerifiedReleasePublicationReport Failure(
        VerifiedReleasePublicationFailureCode failureCode,
        string message,
        VerifiedStagedRelease? stagedRelease = null,
        bool sourceConsumed = false,
        bool targetPublished = false,
        bool targetImmutable = false,
        bool reconciliationRequired = false) =>
        new(
            false,
            failureCode,
            message,
            stagedRelease?.Plan.SetupRevision,
            stagedRelease?.Plan.InstalledReleaseIdentity ?? string.Empty,
            stagedRelease?.Plan.TargetReleaseIdentity ?? string.Empty,
            stagedRelease?.Plan.Packages.Count ?? 0,
            stagedRelease?.StagedBytes ?? 0,
            sourceConsumed,
            targetPublished,
            targetImmutable,
            CurrentPointerChanged: false,
            ActivationPerformed: false,
            reconciliationRequired);

    internal static VerifiedReleasePublicationReport Success(
        VerifiedPublishedRelease publishedRelease) =>
        new(
            true,
            VerifiedReleasePublicationFailureCode.None,
            "The verified immutable staging tree was atomically published as an inactive release without changing current.",
            publishedRelease.Plan.SetupRevision,
            publishedRelease.Plan.InstalledReleaseIdentity,
            publishedRelease.Plan.TargetReleaseIdentity,
            publishedRelease.Plan.Packages.Count,
            publishedRelease.PublishedBytes,
            SourceStagingTreeConsumed: true,
            TargetPublished: true,
            TargetImmutable: true,
            CurrentPointerChanged: false,
            ActivationPerformed: false,
            ReconciliationRequired: false)
        {
            PublishedRelease = publishedRelease
        };
}

public sealed record VerifiedReleasePublicationDiagnostics(
    bool Registered,
    bool StatusRevalidationRegistered,
    bool FrozenStagingValidationRegistered,
    bool RootPermissionTransitionRegistered,
    bool AtomicDirectoryPublishRegistered,
    bool PublishedTreeValidationRegistered,
    bool NetworkDownloadRegistered,
    bool ArchiveExtractionRegistered,
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

internal sealed class VerifiedPublishedRelease
{
    internal VerifiedPublishedRelease(
        VerifiedReleaseInstallationPlan plan,
        string publishedPath,
        long publishedBytes)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        PublishedPath = publishedPath ?? string.Empty;
        PublishedBytes = publishedBytes;
    }

    internal VerifiedReleaseInstallationPlan Plan { get; }
    internal string PublishedPath { get; }
    internal long PublishedBytes { get; }
}

/// <summary>
/// Atomically renames one already-verified immutable private staging tree into
/// its absent direct release target after validating the staged bytes and
/// revalidating completed setup, release inventory, and the active current
/// pointer. It never changes current, activates a release, copies files,
/// downloads, extracts, migrates, rolls back, controls services, or touches
/// Admin, browser, radio, watchdog, command, lease, or transmit state.
/// </summary>
public sealed class VerifiedReleasePublicationService
{
    internal const int MaximumDirectoryCount =
        VerifiedReleaseStagingService.MaximumDirectoryCount;

    private const int BufferSize = 128 * 1024;
    private const UnixFileMode AnyWritableUnixModes =
        UnixFileMode.UserWrite |
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;
    private const UnixFileMode ForbiddenSharedWritableUnixModes =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode PrivateWritableDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;
    private const UnixFileMode PrivateImmutableDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserExecute;

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;
    private readonly Action<string, string> m_directoryMove;

    public VerifiedReleasePublicationService(
        ReleaseInstallationStatusReader statusReader)
        : this(CreateStatusReader(statusReader), Directory.Move)
    {
    }

    internal VerifiedReleasePublicationService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Action<string, string> directoryMove)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_directoryMove = directoryMove ??
            throw new ArgumentNullException(nameof(directoryMove));
        Snapshot = new VerifiedReleasePublicationDiagnostics(
            Registered: true,
            StatusRevalidationRegistered: true,
            FrozenStagingValidationRegistered: true,
            RootPermissionTransitionRegistered: true,
            AtomicDirectoryPublishRegistered: true,
            PublishedTreeValidationRegistered: true,
            NetworkDownloadRegistered: false,
            ArchiveExtractionRegistered: false,
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

    public VerifiedReleasePublicationDiagnostics Snapshot { get; }

    [SupportedOSPlatform("linux")]
    internal async Task<VerifiedReleasePublicationReport> PublishAsync(
        VerifiedReleaseStagingReport stagingReport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagingReport);
        cancellationToken.ThrowIfCancellationRequested();

        VerifiedStagedRelease? stagedRelease =
            ValidateStagingReport(stagingReport);
        if (!OperatingSystem.IsLinux())
        {
            return VerifiedReleasePublicationReport.Failure(
                VerifiedReleasePublicationFailureCode.UnsupportedPlatform,
                "Verified release publication requires a supported Linux runtime.",
                stagedRelease);
        }
        if (stagedRelease is null)
        {
            return VerifiedReleasePublicationReport.Failure(
                VerifiedReleasePublicationFailureCode.StagingNotEligible,
                "A successful immutable verified staging result is required for publication.");
        }

        VerifiedReleaseInstallationPlan plan = stagedRelease.Plan;
        ReleaseStatusReadResult beforeStatus =
            await m_statusReader(cancellationToken);
        VerifiedReleasePublicationReport? statusFailure =
            ValidateStatusBeforePublish(beforeStatus, stagedRelease);
        if (statusFailure is not null)
        {
            return statusFailure;
        }

        List<ExpectedFile> expectedFiles = CreateExpectedFiles(plan);
        try
        {
            ValidateDeploymentLayout(stagedRelease);
            EnsureTargetAbsent(plan.TargetReleasePath, stagedRelease);
            await ValidateImmutableTreeAsync(
                stagedRelease.StagingPath,
                expectedFiles,
                cancellationToken);
        }
        catch (PublicationException exception)
        {
            return VerifiedReleasePublicationReport.Failure(
                exception.FailureCode,
                exception.Message,
                stagedRelease);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or CryptographicException or ArgumentException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return VerifiedReleasePublicationReport.Failure(
                VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                "The verified staging tree could not be safely validated for publication.",
                stagedRelease);
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool renameReturned = false;
        try
        {
            File.SetUnixFileMode(
                stagedRelease.StagingPath,
                PrivateWritableDirectoryMode);
            m_directoryMove(
                stagedRelease.StagingPath,
                plan.TargetReleasePath);
            renameReturned = true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            bool targetPresent = PathEntryExists(plan.TargetReleasePath);
            bool sourcePresent = PathEntryExists(stagedRelease.StagingPath);
            bool sourceRefrozen =
                !sourcePresent || TryFreezeRoot(stagedRelease.StagingPath);
            bool targetRefrozen =
                !targetPresent ||
                (!sourcePresent && TryFreezeRoot(plan.TargetReleasePath));
            if (targetPresent || !sourcePresent ||
                !sourceRefrozen || !targetRefrozen)
            {
                return VerifiedReleasePublicationReport.Failure(
                    VerifiedReleasePublicationFailureCode.PublishedStateRequiresReconciliation,
                    "The atomic publication outcome is ambiguous and requires local reconciliation before another update attempt.",
                    stagedRelease,
                    sourceConsumed: !sourcePresent,
                    targetPublished: targetPresent,
                    targetImmutable: false,
                    reconciliationRequired: true);
            }

            return VerifiedReleasePublicationReport.Failure(
                VerifiedReleasePublicationFailureCode.AtomicPublishFailed,
                "The verified staging tree could not be atomically published into the release inventory.",
                stagedRelease);
        }

        if (!renameReturned)
        {
            throw new InvalidOperationException(
                "The publication rename did not return or throw.");
        }

        bool targetImmutable = false;
        try
        {
            if (PathEntryExists(stagedRelease.StagingPath) ||
                !PathEntryExists(plan.TargetReleasePath))
            {
                throw Failure(
                    VerifiedReleasePublicationFailureCode.PublishedStateRequiresReconciliation,
                    "The atomic publication paths do not show one consumed staging tree and one published target.");
            }

            File.SetUnixFileMode(
                plan.TargetReleasePath,
                PrivateImmutableDirectoryMode);
            await ValidateImmutableTreeAsync(
                plan.TargetReleasePath,
                expectedFiles,
                CancellationToken.None);
            targetImmutable = true;

            ReleaseStatusReadResult afterStatus =
                await m_statusReader(CancellationToken.None);
            if (!IsExactPublishedStatus(beforeStatus, afterStatus, plan))
            {
                throw Failure(
                    VerifiedReleasePublicationFailureCode.PublishedStateRequiresReconciliation,
                    "The release inventory or current pointer changed unexpectedly after atomic publication.");
            }

            return VerifiedReleasePublicationReport.Success(
                new VerifiedPublishedRelease(
                    plan,
                    plan.TargetReleasePath,
                    stagedRelease.StagedBytes));
        }
        catch (PublicationException exception)
        {
            bool sourcePresent = PathEntryExists(stagedRelease.StagingPath);
            bool targetPresent = PathEntryExists(plan.TargetReleasePath);
            if (sourcePresent)
            {
                TryFreezeRoot(stagedRelease.StagingPath);
            }
            else if (targetPresent && !targetImmutable)
            {
                TryFreezeRoot(plan.TargetReleasePath);
            }
            return VerifiedReleasePublicationReport.Failure(
                VerifiedReleasePublicationFailureCode.PublishedStateRequiresReconciliation,
                exception.Message,
                stagedRelease,
                sourceConsumed: !sourcePresent,
                targetPublished: targetPresent,
                targetImmutable,
                reconciliationRequired: true);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or CryptographicException or ArgumentException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            bool sourcePresent = PathEntryExists(stagedRelease.StagingPath);
            bool targetPresent = PathEntryExists(plan.TargetReleasePath);
            if (sourcePresent)
            {
                TryFreezeRoot(stagedRelease.StagingPath);
            }
            else if (targetPresent && !targetImmutable)
            {
                TryFreezeRoot(plan.TargetReleasePath);
            }
            return VerifiedReleasePublicationReport.Failure(
                VerifiedReleasePublicationFailureCode.PublishedStateRequiresReconciliation,
                "The published release could not be fully reconciled after its atomic rename.",
                stagedRelease,
                sourceConsumed: !sourcePresent,
                targetPublished: targetPresent,
                targetImmutable,
                reconciliationRequired: true);
        }
    }

    private static VerifiedStagedRelease? ValidateStagingReport(
        VerifiedReleaseStagingReport report)
    {
        try
        {
            VerifiedStagedRelease? staged = report.StagedRelease;
            if (!report.Succeeded ||
                report.FailureCode != VerifiedReleaseStagingFailureCode.None ||
                !report.ManifestStaged ||
                !report.ImmutableStagingTree ||
                report.TargetPublished ||
                report.CurrentPointerChanged ||
                report.CleanupRequired ||
                staged is null ||
                report.SetupRevision != staged.Plan.SetupRevision ||
                !string.Equals(
                    report.InstalledReleaseIdentity,
                    staged.Plan.InstalledReleaseIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    report.TargetReleaseIdentity,
                    staged.Plan.TargetReleaseIdentity,
                    StringComparison.Ordinal) ||
                report.PackageCount != staged.Plan.Packages.Count ||
                report.StagedBytes != staged.StagedBytes ||
                staged.StagedBytes != ExpectedByteCount(staged.Plan) ||
                !ValidatePlanPaths(staged.Plan, staged.StagingPath))
            {
                return null;
            }
            return staged;
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return null;
        }
    }

    private static bool ValidatePlanPaths(
        VerifiedReleaseInstallationPlan plan,
        string stagingPath)
    {
        try
        {
            if (plan.SetupRevision < 1 ||
                !IsCanonicalReleaseIdentity(plan.InstalledReleaseIdentity) ||
                !IsCanonicalReleaseIdentity(plan.TargetReleaseIdentity) ||
                string.Equals(
                    plan.InstalledReleaseIdentity,
                    plan.TargetReleaseIdentity,
                    StringComparison.Ordinal) ||
                !IsCanonicalAbsolutePath(plan.DeploymentRootPath) ||
                !IsCanonicalAbsolutePath(plan.ReleaseRootPath) ||
                !IsCanonicalAbsolutePath(plan.TargetReleasePath) ||
                !IsCanonicalAbsolutePath(stagingPath) ||
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
                plan.Packages.Count !=
                    LocalOfflineReleaseBundleVerificationService.RequiredPackageCount)
            {
                return false;
            }

            string stagingRoot = Path.GetFullPath(
                Path.Combine(
                    plan.DeploymentRootPath,
                    VerifiedReleaseStagingService.StagingDirectoryName));
            if (!PathEquals(Path.GetDirectoryName(stagingRoot), plan.DeploymentRootPath) ||
                !PathEquals(Path.GetDirectoryName(stagingPath), stagingRoot))
            {
                return false;
            }

            string name = Path.GetFileName(stagingPath);
            string prefix = plan.TargetReleaseIdentity + ".";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                name.Length != prefix.Length + 32)
            {
                return false;
            }
            return name.AsSpan(prefix.Length).ToString().All(IsLowerHex);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return false;
        }
    }

    private static long ExpectedByteCount(VerifiedReleaseInstallationPlan plan)
    {
        long total = plan.ManifestLength;
        foreach (VerifiedReleaseInstallationPackagePlan package in plan.Packages)
        {
            total = checked(total + package.Length);
        }
        return total;
    }

    private static VerifiedReleasePublicationReport? ValidateStatusBeforePublish(
        ReleaseStatusReadResult status,
        VerifiedStagedRelease stagedRelease)
    {
        VerifiedReleaseInstallationPlan plan = stagedRelease.Plan;
        if (!status.Succeeded)
        {
            return VerifiedReleasePublicationReport.Failure(
                VerifiedReleasePublicationFailureCode.StatusUnavailable,
                "Local installation status is unavailable for verified release publication.",
                stagedRelease);
        }
        if (!StatusMatchesPlan(status, plan) ||
            status.AvailableReleaseIdentities.Contains(
                plan.TargetReleaseIdentity,
                StringComparer.Ordinal))
        {
            return VerifiedReleasePublicationReport.Failure(
                VerifiedReleasePublicationFailureCode.StatusMismatch,
                "Completed setup, release inventory, or the active current pointer no longer matches the staged release plan.",
                stagedRelease);
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
    private static void ValidateDeploymentLayout(VerifiedStagedRelease stagedRelease)
    {
        VerifiedReleaseInstallationPlan plan = stagedRelease.Plan;
        ValidateSharedDirectory(
            plan.DeploymentRootPath,
            VerifiedReleasePublicationFailureCode.UnsafeDeploymentLayout,
            "The deployment root is unsafe for verified release publication.");
        ValidateSharedDirectory(
            plan.ReleaseRootPath,
            VerifiedReleasePublicationFailureCode.UnsafeDeploymentLayout,
            "The release root is unsafe for verified release publication.");

        string stagingRoot = Path.GetDirectoryName(stagedRelease.StagingPath) ??
            throw Failure(
                VerifiedReleasePublicationFailureCode.UnsafeDeploymentLayout,
                "The staged release has no private staging root.");
        ValidatePrivateDirectory(
            stagingRoot,
            VerifiedReleasePublicationFailureCode.UnsafeDeploymentLayout,
            "The private staging root is unsafe for verified release publication.");
    }

    private static void EnsureTargetAbsent(
        string targetPath,
        VerifiedStagedRelease stagedRelease)
    {
        if (PathEntryExists(targetPath))
        {
            throw Failure(
                VerifiedReleasePublicationFailureCode.TargetAlreadyPresent,
                "The target release already exists and publication will not overwrite it.");
        }
        if (!PathEntryExists(stagedRelease.StagingPath))
        {
            throw Failure(
                VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                "The verified staging tree is no longer present.");
        }
    }

    private static List<ExpectedFile> CreateExpectedFiles(
        VerifiedReleaseInstallationPlan plan)
    {
        List<ExpectedFile> files =
        [
            new ExpectedFile(
                LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                plan.ManifestLength,
                plan.ManifestSha256.ToArray())
        ];
        files.AddRange(
            plan.Packages.Select(package =>
                new ExpectedFile(
                    package.SourceRelativePath,
                    package.Length,
                    package.Sha256.ToArray())));
        return files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    [SupportedOSPlatform("linux")]
    private static async Task ValidateImmutableTreeAsync(
        string rootPath,
        IReadOnlyList<ExpectedFile> expectedFiles,
        CancellationToken cancellationToken)
    {
        DirectoryInfo root = new(rootPath);
        ValidateImmutableDirectory(root);

        Dictionary<string, FileInfo> files = new(StringComparer.Ordinal);
        Stack<DirectoryInfo> pending = new();
        pending.Push(root);
        int directoryCount = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = pending.Pop();
            ValidateImmutableDirectory(directory);
            if (++directoryCount > MaximumDirectoryCount)
            {
                throw Failure(
                    VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                    "The immutable release tree exceeds its bounded directory count.");
            }

            FileSystemInfo[] entries = directory.GetFileSystemInfos();
            if (!PathEquals(directory.FullName, rootPath) && entries.Length == 0)
            {
                throw Failure(
                    VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                    "The immutable release tree contains an empty directory.");
            }

            foreach (FileSystemInfo entry in entries)
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                        "The immutable release tree contains a symbolic link or reparse point.");
                }
                if (entry is DirectoryInfo child)
                {
                    string relativeDirectory = RelativePath(rootPath, child.FullName);
                    if (!ReleasePackagePath.IsSafe(relativeDirectory))
                    {
                        throw Failure(
                            VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                            "The immutable release tree contains an unsafe directory path.");
                    }
                    pending.Push(child);
                    continue;
                }
                if (entry is not FileInfo file ||
                    (file.Attributes & FileAttributes.Directory) != 0)
                {
                    throw Failure(
                        VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                        "The immutable release tree contains a non-regular entry.");
                }

                string relativePath = RelativePath(rootPath, file.FullName);
                if (!files.TryAdd(relativePath, file))
                {
                    throw Failure(
                        VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                        "The immutable release tree contains a duplicate file path.");
                }
            }
        }

        if (files.Count != expectedFiles.Count)
        {
            throw Failure(
                VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                "The immutable release tree does not contain the exact verified files.");
        }
        foreach (ExpectedFile expected in expectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(expected.RelativePath, out FileInfo? file))
            {
                throw Failure(
                    VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                    "The immutable release tree is missing a verified file.");
            }
            await ValidateImmutableFileAsync(
                file,
                expected,
                cancellationToken);
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task ValidateImmutableFileAsync(
        FileInfo file,
        ExpectedFile expected,
        CancellationToken cancellationToken)
    {
        FileState before = ReadImmutableFileState(file);
        if (before.Length != expected.Length)
        {
            throw Failure(
                VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                "An immutable release file length no longer matches the verified plan.");
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
                    cancellationToken);
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
                VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                "An immutable release file digest no longer matches the verified plan.");
        }

        FileState after = ReadImmutableFileState(file);
        if (after != before || stream.Length != before.Length)
        {
            throw Changed();
        }
    }

    [SupportedOSPlatform("linux")]
    private static FileState ReadImmutableFileState(FileInfo file)
    {
        file.Refresh();
        if (!file.Exists ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.LinkTarget is not null ||
            (File.GetUnixFileMode(file.FullName) & AnyWritableUnixModes) != 0)
        {
            throw Failure(
                VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                "The immutable release tree contains an unsafe or writable file.");
        }
        return new FileState(file.Length, file.LastWriteTimeUtc);
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutableDirectory(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            (File.GetUnixFileMode(directory.FullName) & AnyWritableUnixModes) != 0)
        {
            throw Failure(
                VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
                "The immutable release tree contains an unsafe or writable directory.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateSharedDirectory(
        string path,
        VerifiedReleasePublicationFailureCode failureCode,
        string message)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            (File.GetUnixFileMode(path) & ForbiddenSharedWritableUnixModes) != 0)
        {
            throw Failure(failureCode, message);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidatePrivateDirectory(
        string path,
        VerifiedReleasePublicationFailureCode failureCode,
        string message)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        UnixFileMode mode = directory.Exists
            ? File.GetUnixFileMode(path)
            : 0;
        UnixFileMode required =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute;
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            (mode & required) != required ||
            (mode & (UnixFileMode.GroupRead |
                     UnixFileMode.GroupWrite |
                     UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead |
                     UnixFileMode.OtherWrite |
                     UnixFileMode.OtherExecute)) != 0)
        {
            throw Failure(failureCode, message);
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
            return (File.GetUnixFileMode(path) & AnyWritableUnixModes) == 0;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            return false;
        }
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

    private static bool PathEntryExists(string path)
    {
        FileSystemInfo info = new DirectoryInfo(path);
        info.Refresh();
        return Directory.Exists(path) || File.Exists(path) ||
            info.LinkTarget is not null;
    }

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

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool PathEquals(string? left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static PublicationException Changed() =>
        Failure(
            VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
            "The immutable release tree changed while it was being validated.");

    private static PublicationException Failure(
        VerifiedReleasePublicationFailureCode failureCode,
        string message) =>
        new(failureCode, message);

    private sealed record ExpectedFile(
        string RelativePath,
        long Length,
        byte[] Sha256);

    private readonly record struct FileState(
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed class PublicationException(
        VerifiedReleasePublicationFailureCode failureCode,
        string message) : Exception(message)
    {
        internal VerifiedReleasePublicationFailureCode FailureCode { get; } =
            failureCode;
    }
}
