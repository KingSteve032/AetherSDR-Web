using System.Buffers;
using System.Collections.ObjectModel;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseArchiveExtractionFailureCode
{
    None = 0,
    UnsupportedPlatform = 1,
    StagingNotEligible = 2,
    InvalidPlan = 3,
    StatusUnavailable = 4,
    StatusMismatch = 5,
    TargetAlreadyPresent = 6,
    UnsafeDeploymentLayout = 7,
    UnsafeExtractionRoot = 8,
    UnsafeSourceStaging = 9,
    SourceChanged = 10,
    InvalidArchive = 11,
    UnsafeArchiveEntry = 12,
    EntryLimitExceeded = 13,
    ExpandedContentTooLarge = 14,
    ExtractionWriteFailed = 15,
    IntegrityMismatch = 16,
    ExtractionFreezeFailed = 17,
    StatusChangedDuringExtraction = 18,
    CleanupFailed = 19
}

public sealed record VerifiedReleaseArchiveExtractionReport(
    bool Succeeded,
    VerifiedReleaseArchiveExtractionFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int PackageCount,
    int ExtractedFileCount,
    int ExtractedDirectoryCount,
    long ExpandedBytes,
    bool SourceArchivesVerified,
    bool ManifestCopied,
    bool ImmutableExtractionTree,
    bool TargetPublished,
    bool CurrentPointerChanged,
    bool CleanupRequired)
{
    internal VerifiedExtractedRelease? ExtractedRelease { get; init; }
    internal string CleanupPath { get; init; } = string.Empty;

    internal static VerifiedReleaseArchiveExtractionReport Failure(
        VerifiedReleaseArchiveExtractionFailureCode failureCode,
        string message,
        VerifiedReleaseInstallationPlan? plan = null,
        int extractedFileCount = 0,
        int extractedDirectoryCount = 0,
        long expandedBytes = 0,
        bool sourceArchivesVerified = false,
        bool manifestCopied = false,
        bool cleanupRequired = false,
        string cleanupPath = "") =>
        new(
            false,
            failureCode,
            message,
            plan?.SetupRevision,
            plan?.InstalledReleaseIdentity ?? string.Empty,
            plan?.TargetReleaseIdentity ?? string.Empty,
            plan?.Packages.Count ?? 0,
            extractedFileCount,
            extractedDirectoryCount,
            expandedBytes,
            sourceArchivesVerified,
            manifestCopied,
            ImmutableExtractionTree: false,
            TargetPublished: false,
            CurrentPointerChanged: false,
            cleanupRequired)
        {
            CleanupPath = cleanupPath
        };

    internal static VerifiedReleaseArchiveExtractionReport Success(
        VerifiedExtractedRelease extractedRelease) =>
        new(
            true,
            VerifiedReleaseArchiveExtractionFailureCode.None,
            "The verified release archives were extracted into one private immutable staging tree without publication or activation.",
            extractedRelease.Plan.SetupRevision,
            extractedRelease.Plan.InstalledReleaseIdentity,
            extractedRelease.Plan.TargetReleaseIdentity,
            extractedRelease.Plan.Packages.Count,
            extractedRelease.Files.Count,
            extractedRelease.DirectoryCount,
            extractedRelease.ExpandedBytes,
            SourceArchivesVerified: true,
            ManifestCopied: true,
            ImmutableExtractionTree: true,
            TargetPublished: false,
            CurrentPointerChanged: false,
            CleanupRequired: false)
        {
            ExtractedRelease = extractedRelease
        };
}

public sealed record VerifiedReleaseArchiveExtractionDiagnostics(
    bool Registered,
    bool StatusRevalidationRegistered,
    bool VerifiedStagingInputRegistered,
    bool SourceArchiveDigestVerificationRegistered,
    bool GzipDecompressionRegistered,
    bool TarArchiveReadRegistered,
    bool ArchiveExtractionRegistered,
    bool PrivateStagingWriteRegistered,
    bool ExpandedContentHashRegistered,
    bool ImmutableFreezeRegistered,
    bool CleanupRegistered,
    bool NetworkDownloadRegistered,
    bool PersistentDownloadRegistered,
    bool PublicationRegistered,
    bool InstallationExecutionRegistered,
    bool ActivationRegistered,
    bool CurrentPointerMutationRegistered,
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

internal sealed class VerifiedExtractedReleaseFile
{
    private readonly byte[] m_sha256;

    internal VerifiedExtractedReleaseFile(
        ReleasePackageRole role,
        string relativePath,
        long length,
        ReadOnlySpan<byte> sha256,
        bool executable)
    {
        if (string.IsNullOrEmpty(relativePath) ||
            length < 0 ||
            sha256.Length != 32)
        {
            throw new ArgumentException(
                "A verified extracted file requires one safe path, bounded length, and SHA-256 digest.");
        }

        Role = role;
        RelativePath = relativePath;
        Length = length;
        m_sha256 = sha256.ToArray();
        Executable = executable;
    }

    internal ReleasePackageRole Role { get; }
    internal string RelativePath { get; }
    internal long Length { get; }
    internal ReadOnlySpan<byte> Sha256 => m_sha256;
    internal bool Executable { get; }
}

internal sealed class VerifiedExtractedRelease
{
    private readonly ReadOnlyCollection<VerifiedExtractedReleaseFile> m_files;

    internal VerifiedExtractedRelease(
        VerifiedStagedRelease sourceStagedRelease,
        string extractionPath,
        IReadOnlyList<VerifiedExtractedReleaseFile> files,
        int directoryCount,
        long expandedBytes)
    {
        SourceStagedRelease = sourceStagedRelease ??
            throw new ArgumentNullException(nameof(sourceStagedRelease));
        ExtractionPath = extractionPath ?? string.Empty;
        m_files = Array.AsReadOnly(files.ToArray());
        DirectoryCount = directoryCount;
        ExpandedBytes = expandedBytes;
    }

    internal VerifiedStagedRelease SourceStagedRelease { get; }
    internal VerifiedReleaseInstallationPlan Plan => SourceStagedRelease.Plan;
    internal string ExtractionPath { get; }
    internal IReadOnlyList<VerifiedExtractedReleaseFile> Files => m_files;
    internal int DirectoryCount { get; }
    internal long ExpandedBytes { get; }
}

/// <summary>
/// Extracts the four archives from one successful immutable verified-staging
/// result into a second private staging tree. Every compressed source is checked
/// against the retained signed package digest before decompression. Only regular
/// files and directories with bounded safe relative paths are accepted. The
/// completed tree is hashed, frozen owner-only/non-writable, and revalidated.
/// This boundary does not publish a release, install, switch current, activate,
/// roll back, migrate, control services, or touch Admin, browser, radio,
/// watchdog, command, lease, keying, or transmit state.
/// </summary>
public sealed class VerifiedReleaseArchiveExtractionService
{
    internal const string ExtractionStagingDirectoryName =
        ".release-extraction-staging";
    internal const int MaximumArchiveEntryCount = 16_384;
    internal const int MaximumExtractedFileCount = 12_000;
    internal const int MaximumExtractedDirectoryCount = 2_048;
    internal const int MaximumPathDepth = 32;
    internal const int MaximumRelativePathLength = 1_024;
    internal const long MaximumExtractedFileLength = 512L * 1024 * 1024;
    internal const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;

    private const int BufferSize = 128 * 1024;
    private const int MaximumTrailingTarPaddingBytes = 1024 * 1024;
    private const UnixFileMode AllPermissionBits =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute |
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;
    private const UnixFileMode ForbiddenSharedWritableUnixModes =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode AnyWritableUnixModes =
        UnixFileMode.UserWrite |
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;
    private const UnixFileMode SharedPermissionUnixModes =
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;
    private const UnixFileMode AnyExecutableUnixModes =
        UnixFileMode.UserExecute |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherExecute;
    private const UnixFileMode PrivateWritableDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;
    private const UnixFileMode PrivateImmutableDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateWritableFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateImmutableFileMode =
        UnixFileMode.UserRead;
    private const UnixFileMode PrivateImmutableExecutableFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserExecute;

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;

    public VerifiedReleaseArchiveExtractionService(
        ReleaseInstallationStatusReader statusReader)
        : this(CreateStatusReader(statusReader))
    {
    }

    internal VerifiedReleaseArchiveExtractionService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        Snapshot = new VerifiedReleaseArchiveExtractionDiagnostics(
            Registered: true,
            StatusRevalidationRegistered: true,
            VerifiedStagingInputRegistered: true,
            SourceArchiveDigestVerificationRegistered: true,
            GzipDecompressionRegistered: true,
            TarArchiveReadRegistered: true,
            ArchiveExtractionRegistered: true,
            PrivateStagingWriteRegistered: true,
            ExpandedContentHashRegistered: true,
            ImmutableFreezeRegistered: true,
            CleanupRegistered: true,
            NetworkDownloadRegistered: false,
            PersistentDownloadRegistered: false,
            PublicationRegistered: false,
            InstallationExecutionRegistered: false,
            ActivationRegistered: false,
            CurrentPointerMutationRegistered: false,
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

    public VerifiedReleaseArchiveExtractionDiagnostics Snapshot { get; }

    [SupportedOSPlatform("linux")]
    internal async Task<VerifiedReleaseArchiveExtractionReport> ExtractAsync(
        VerifiedReleaseStagingReport stagingReport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagingReport);
        cancellationToken.ThrowIfCancellationRequested();

        VerifiedStagedRelease? staged = ValidateStagingReport(stagingReport);
        VerifiedReleaseInstallationPlan? plan = staged?.Plan;
        if (!OperatingSystem.IsLinux())
        {
            return VerifiedReleaseArchiveExtractionReport.Failure(
                VerifiedReleaseArchiveExtractionFailureCode.UnsupportedPlatform,
                "Verified release archive extraction requires a supported Linux runtime.",
                plan);
        }
        if (staged is null)
        {
            return VerifiedReleaseArchiveExtractionReport.Failure(
                VerifiedReleaseArchiveExtractionFailureCode.StagingNotEligible,
                "A successful immutable verified-staging result is required for archive extraction.");
        }
        if (!ValidatePlan(plan!))
        {
            return VerifiedReleaseArchiveExtractionReport.Failure(
                VerifiedReleaseArchiveExtractionFailureCode.InvalidPlan,
                "The verified release installation plan is incomplete or non-canonical.",
                plan);
        }

        ReleaseStatusReadResult beforeStatus =
            await m_statusReader(cancellationToken).ConfigureAwait(false);
        VerifiedReleaseArchiveExtractionReport? statusFailure =
            ValidateStatusAgainstPlan(beforeStatus, plan!);
        if (statusFailure is not null)
        {
            return statusFailure;
        }

        string extractionPath = string.Empty;
        int extractedFileCount = 0;
        int extractedDirectoryCount = 0;
        long expandedBytes = 0;
        bool sourceArchivesVerified = false;
        bool manifestCopied = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDeploymentLayout(plan!);
            EnsureTargetAbsent(plan!.TargetReleasePath);
            IReadOnlyDictionary<string, SourceFile> sourceFiles =
                ReadExactSourceStagingLayout(staged);
            string extractionRoot = PrepareExtractionRoot(plan.DeploymentRootPath);
            extractionPath = CreatePrivateExtractionDirectory(
                extractionRoot,
                plan.TargetReleaseIdentity);

            List<VerifiedExtractedReleaseFile> extractedFiles = [];
            HashSet<string> extractedDirectories = new(StringComparer.Ordinal);
            SourceFile manifest = sourceFiles[
                LocalOfflineReleaseBundleVerificationService.ManifestFileName];
            string manifestDestination = CreateSafeDestination(
                extractionPath,
                LocalOfflineReleaseBundleVerificationService.ManifestFileName);
            long manifestBytes = await CopyVerifiedFileAsync(
                manifest,
                manifestDestination,
                plan.ManifestLength,
                plan.ManifestSha256.ToArray(),
                cancellationToken).ConfigureAwait(false);
            expandedBytes = checked(expandedBytes + manifestBytes);
            extractedFiles.Add(
                await SnapshotExtractedFileAsync(
                    ReleasePackageRole.Unknown,
                    extractionPath,
                    manifestDestination,
                    executable: false,
                    cancellationToken).ConfigureAwait(false));
            manifestCopied = true;

            foreach (VerifiedReleaseInstallationPackagePlan package in
                     plan.Packages.OrderBy(value => value.Role))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string roleDirectoryName = RoleDirectoryName(package.Role);
                string roleDirectory = CreateSafeDestination(
                    extractionPath,
                    roleDirectoryName);
                CreatePrivateDirectory(roleDirectory);
                extractedDirectories.Add(roleDirectoryName);

                ArchiveExtractionResult archive = await ExtractArchiveAsync(
                    sourceFiles[package.SourceRelativePath],
                    package,
                    extractionPath,
                    roleDirectory,
                    roleDirectoryName,
                    extractedFiles,
                    extractedDirectories,
                    expandedBytes,
                    cancellationToken).ConfigureAwait(false);
                expandedBytes = archive.ExpandedBytes;
            }
            sourceArchivesVerified = true;
            extractedFileCount = extractedFiles.Count;
            extractedDirectoryCount = extractedDirectories.Count;

            IReadOnlyDictionary<string, SourceFile> sourceFilesAfter =
                ReadExactSourceStagingLayout(staged);
            if (!EquivalentSourceLayout(sourceFiles, sourceFilesAfter))
            {
                throw Changed();
            }
            await ReverifySourceDigestsAsync(
                sourceFilesAfter,
                plan,
                cancellationToken).ConfigureAwait(false);

            FreezeExtractionTree(extractionPath);
            await ValidateFrozenExtractionTreeAsync(
                extractionPath,
                extractedFiles,
                extractedDirectories,
                cancellationToken).ConfigureAwait(false);

            ReleaseStatusReadResult afterStatus =
                await m_statusReader(cancellationToken).ConfigureAwait(false);
            if (!EquivalentStatus(beforeStatus, afterStatus) ||
                afterStatus.AvailableReleaseIdentities.Contains(
                    plan.TargetReleaseIdentity,
                    StringComparer.Ordinal) ||
                PathEntryExists(plan.TargetReleasePath))
            {
                throw Failure(
                    VerifiedReleaseArchiveExtractionFailureCode.StatusChangedDuringExtraction,
                    "Local installation status changed while the verified release archives were extracted.");
            }

            return VerifiedReleaseArchiveExtractionReport.Success(
                new VerifiedExtractedRelease(
                    staged,
                    extractionPath,
                    extractedFiles,
                    extractedDirectories.Count,
                    expandedBytes));
        }
        catch (OperationCanceledException)
        {
            if (TryCleanup(extractionPath))
            {
                throw;
            }
            return VerifiedReleaseArchiveExtractionReport.Failure(
                VerifiedReleaseArchiveExtractionFailureCode.CleanupFailed,
                "Cancelled archive extraction could not remove its private staging tree.",
                plan,
                extractedFileCount,
                extractedDirectoryCount,
                expandedBytes,
                sourceArchivesVerified,
                manifestCopied,
                cleanupRequired: true,
                cleanupPath: extractionPath);
        }
        catch (ExtractionException exception)
        {
            return FailureWithCleanup(
                exception.FailureCode,
                exception.Message,
                plan!,
                extractionPath,
                extractedFileCount,
                extractedDirectoryCount,
                expandedBytes,
                sourceArchivesVerified,
                manifestCopied);
        }
        catch (Exception exception)
            when (exception is IOException or InvalidDataException or
                UnauthorizedAccessException or SecurityException or
                CryptographicException or ArgumentException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return FailureWithCleanup(
                VerifiedReleaseArchiveExtractionFailureCode.ExtractionWriteFailed,
                "The verified release archives could not be extracted safely.",
                plan!,
                extractionPath,
                extractedFileCount,
                extractedDirectoryCount,
                expandedBytes,
                sourceArchivesVerified,
                manifestCopied);
        }
    }

    [SupportedOSPlatform("linux")]
    private static VerifiedReleaseArchiveExtractionReport FailureWithCleanup(
        VerifiedReleaseArchiveExtractionFailureCode failureCode,
        string message,
        VerifiedReleaseInstallationPlan plan,
        string extractionPath,
        int extractedFileCount,
        int extractedDirectoryCount,
        long expandedBytes,
        bool sourceArchivesVerified,
        bool manifestCopied)
    {
        if (TryCleanup(extractionPath))
        {
            return VerifiedReleaseArchiveExtractionReport.Failure(
                failureCode,
                message,
                plan,
                extractedFileCount,
                extractedDirectoryCount,
                expandedBytes,
                sourceArchivesVerified,
                manifestCopied);
        }
        return VerifiedReleaseArchiveExtractionReport.Failure(
            VerifiedReleaseArchiveExtractionFailureCode.CleanupFailed,
            "Failed archive extraction also could not remove its private staging tree.",
            plan,
            extractedFileCount,
            extractedDirectoryCount,
            expandedBytes,
            sourceArchivesVerified,
            manifestCopied,
            cleanupRequired: true,
            cleanupPath: extractionPath);
    }

    private static VerifiedStagedRelease? ValidateStagingReport(
        VerifiedReleaseStagingReport report)
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
            string.IsNullOrEmpty(staged.StagingPath))
        {
            return null;
        }
        return staged;
    }

    private static bool ValidatePlan(VerifiedReleaseInstallationPlan plan)
    {
        if (plan.SetupRevision < 1 ||
            !IsCanonicalReleaseIdentity(plan.InstalledReleaseIdentity) ||
            !IsCanonicalReleaseIdentity(plan.TargetReleaseIdentity) ||
            string.Equals(
                plan.InstalledReleaseIdentity,
                plan.TargetReleaseIdentity,
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
            plan.Packages.Count !=
                LocalOfflineReleaseBundleVerificationService.RequiredPackageCount)
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
        if (plan.InstallTransmitSupport != plan.TxSupportCapable)
        {
            return false;
        }

        HashSet<ReleasePackageRole> roles = [];
        HashSet<string> sourcePaths = new(StringComparer.Ordinal);
        foreach (VerifiedReleaseInstallationPackagePlan package in plan.Packages)
        {
            if (!roles.Add(package.Role) ||
                !sourcePaths.Add(package.SourceRelativePath) ||
                !ReleasePackagePath.IsSafe(package.SourceRelativePath) ||
                package.Length is < 1 or >
                    SignedReleaseManifestVerifier.MaximumDeclaredPackageLength ||
                package.Sha256.Length != 32)
            {
                return false;
            }
        }
        return roles.SetEquals(
            [
                ReleasePackageRole.GatewayWeb,
                ReleasePackageRole.Broker,
                ReleasePackageRole.AetherRemoteAgent,
                ReleasePackageRole.StationEngine
            ]);
    }

    private static VerifiedReleaseArchiveExtractionReport? ValidateStatusAgainstPlan(
        ReleaseStatusReadResult status,
        VerifiedReleaseInstallationPlan plan)
    {
        if (!status.Succeeded)
        {
            return VerifiedReleaseArchiveExtractionReport.Failure(
                VerifiedReleaseArchiveExtractionFailureCode.StatusUnavailable,
                "Local release status is unavailable for archive extraction.",
                plan);
        }
        if (!status.SetupComplete ||
            status.SetupLockMode != InstallationSetupLockMode.Complete ||
            status.LastCompletedStep != InstallationSetupStep.Administrator ||
            status.SetupRevision != plan.SetupRevision ||
            status.UpdateChannel != plan.UpdateChannel ||
            !string.Equals(
                status.PinnedReleaseIdentity,
                plan.PinnedReleaseIdentity,
                StringComparison.Ordinal) ||
            status.InstallTransmitSupport != plan.InstallTransmitSupport ||
            !status.CurrentPointerPresent ||
            !string.Equals(
                status.ActiveReleaseIdentity,
                plan.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            status.AvailableReleaseIdentities.Contains(
                plan.TargetReleaseIdentity,
                StringComparer.Ordinal))
        {
            return VerifiedReleaseArchiveExtractionReport.Failure(
                VerifiedReleaseArchiveExtractionFailureCode.StatusMismatch,
                "Completed setup, release inventory, or the active pointer no longer matches the verified plan.",
                plan);
        }
        return null;
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateDeploymentLayout(
        VerifiedReleaseInstallationPlan plan)
    {
        ValidateExistingDirectory(
            plan.DeploymentRootPath,
            privateDirectory: false,
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeDeploymentLayout,
            "The deployment root is unsafe for archive extraction.");
        ValidateExistingDirectory(
            plan.ReleaseRootPath,
            privateDirectory: false,
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeDeploymentLayout,
            "The release root is unsafe for archive extraction.");
    }

    private static void EnsureTargetAbsent(string targetPath)
    {
        if (PathEntryExists(targetPath))
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.TargetAlreadyPresent,
                "The target release path already exists and archive extraction will not overwrite it.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static string PrepareExtractionRoot(string deploymentRootPath)
    {
        string extractionRoot = Path.GetFullPath(
            Path.Combine(deploymentRootPath, ExtractionStagingDirectoryName));
        if (!PathEquals(Path.GetDirectoryName(extractionRoot), deploymentRootPath))
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.UnsafeExtractionRoot,
                "The private extraction root is not a direct deployment child.");
        }
        if (!PathEntryExists(extractionRoot))
        {
            Directory.CreateDirectory(extractionRoot);
            File.SetUnixFileMode(extractionRoot, PrivateWritableDirectoryMode);
        }
        ValidateExistingDirectory(
            extractionRoot,
            privateDirectory: true,
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeExtractionRoot,
            "The private extraction root is unsafe.");
        return extractionRoot;
    }

    [SupportedOSPlatform("linux")]
    private static string CreatePrivateExtractionDirectory(
        string extractionRoot,
        string targetReleaseIdentity)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            string candidate = Path.GetFullPath(
                Path.Combine(
                    extractionRoot,
                    $"{targetReleaseIdentity}.{Guid.NewGuid():N}"));
            if (!PathEquals(Path.GetDirectoryName(candidate), extractionRoot) ||
                PathEntryExists(candidate))
            {
                continue;
            }
            Directory.CreateDirectory(candidate);
            File.SetUnixFileMode(candidate, PrivateWritableDirectoryMode);
            ValidateExistingDirectory(
                candidate,
                privateDirectory: true,
                VerifiedReleaseArchiveExtractionFailureCode.UnsafeExtractionRoot,
                "The private extraction transaction directory is unsafe.");
            return Path.TrimEndingDirectorySeparator(candidate);
        }
        throw Failure(
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeExtractionRoot,
            "A unique private extraction transaction directory could not be created.");
    }

    [SupportedOSPlatform("linux")]
    private static IReadOnlyDictionary<string, SourceFile>
        ReadExactSourceStagingLayout(VerifiedStagedRelease staged)
    {
        VerifiedReleaseInstallationPlan plan = staged.Plan;
        string expectedStagingRoot = Path.GetFullPath(
            Path.Combine(
                plan.DeploymentRootPath,
                VerifiedReleaseStagingService.StagingDirectoryName));
        string sourceRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(staged.StagingPath));
        if (!PathEquals(Path.GetDirectoryName(sourceRoot), expectedStagingRoot))
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.UnsafeSourceStaging,
                "The verified source staging path is outside its reviewed private staging root.");
        }

        HashSet<string> expected = new(StringComparer.Ordinal)
        {
            LocalOfflineReleaseBundleVerificationService.ManifestFileName
        };
        foreach (VerifiedReleaseInstallationPackagePlan package in plan.Packages)
        {
            expected.Add(package.SourceRelativePath);
        }

        Dictionary<string, SourceFile> files = new(StringComparer.Ordinal);
        Stack<DirectoryInfo> pending = new();
        pending.Push(new DirectoryInfo(sourceRoot));
        int directoryCount = 0;
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            ValidateImmutableSourceDirectory(directory);
            if (++directoryCount > VerifiedReleaseStagingService.MaximumDirectoryCount)
            {
                throw Failure(
                    VerifiedReleaseArchiveExtractionFailureCode.UnsafeSourceStaging,
                    "The verified source staging tree exceeds its directory bound.");
            }

            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.UnsafeSourceStaging,
                        "The verified source staging tree contains a symbolic link or reparse point.");
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
                        VerifiedReleaseArchiveExtractionFailureCode.UnsafeSourceStaging,
                        "The verified source staging tree contains a non-regular entry.");
                }
                string relative = RelativePath(sourceRoot, file.FullName);
                if (!expected.Contains(relative) ||
                    !files.TryAdd(relative, ValidateImmutablePrivateFile(file)))
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.UnsafeSourceStaging,
                        "The verified source staging contents no longer match the retained plan.");
                }
            }
        }
        if (!expected.SetEquals(files.Keys))
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.UnsafeSourceStaging,
                "The verified source staging tree is missing a planned manifest or archive.");
        }
        return files;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<ArchiveExtractionResult> ExtractArchiveAsync(
        SourceFile source,
        VerifiedReleaseInstallationPackagePlan package,
        string extractionRoot,
        string roleDirectory,
        string roleDirectoryName,
        List<VerifiedExtractedReleaseFile> extractedFiles,
        HashSet<string> extractedDirectories,
        long expandedBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream input = OpenSource(source.Path);
        await VerifyOpenSourceDigestAsync(
            input,
            source,
            package.Length,
            package.Sha256.ToArray(),
            cancellationToken).ConfigureAwait(false);
        input.Position = 0;

        int archiveEntryCount = 0;
        int archiveFileCount = 0;
        using GZipStream gzip = new(
            input,
            CompressionMode.Decompress,
            leaveOpen: true);
        using (TarReader reader = new(gzip, leaveOpen: true))
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++archiveEntryCount > MaximumArchiveEntryCount)
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.EntryLimitExceeded,
                        "A verified release archive exceeds its entry-count bound.");
                }

                string normalized = NormalizeArchivePath(entry.Name);
                if (normalized.Length == 0)
                {
                    if (entry.EntryType != TarEntryType.Directory ||
                        entry.Length != 0 ||
                        !string.IsNullOrEmpty(entry.LinkName))
                    {
                        throw UnsafeEntry();
                    }
                    ValidateArchiveMode(entry.Mode);
                    continue;
                }
                ValidateArchiveMode(entry.Mode);
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        {
                            if (entry.Length != 0 ||
                                !string.IsNullOrEmpty(entry.LinkName))
                            {
                                throw UnsafeEntry();
                            }
                            string relative = $"{roleDirectoryName}/{normalized}";
                            string destination = CreateSafeDestination(
                                roleDirectory,
                                normalized);
                            if (!extractedDirectories.Add(relative) ||
                                extractedFiles.Any(file => string.Equals(
                                    file.RelativePath,
                                    relative,
                                    StringComparison.Ordinal)))
                            {
                                throw UnsafeEntry();
                            }
                            CreatePrivateDirectory(destination);
                            break;
                        }
                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                        {
                            if (entry.Length < 0 ||
                                entry.Length > MaximumExtractedFileLength ||
                                entry.DataStream is null && entry.Length != 0 ||
                                !string.IsNullOrEmpty(entry.LinkName))
                            {
                                throw UnsafeEntry();
                            }
                            if (++archiveFileCount > MaximumExtractedFileCount ||
                                extractedFiles.Count >= MaximumExtractedFileCount)
                            {
                                throw Failure(
                                    VerifiedReleaseArchiveExtractionFailureCode.EntryLimitExceeded,
                                    "The extracted release exceeds its file-count bound.");
                            }
                            expandedBytes = checked(expandedBytes + entry.Length);
                            if (expandedBytes > MaximumExpandedBytes)
                            {
                                throw Failure(
                                    VerifiedReleaseArchiveExtractionFailureCode.ExpandedContentTooLarge,
                                    "The extracted release exceeds its expanded-byte bound.");
                            }

                            string relative = $"{roleDirectoryName}/{normalized}";
                            if (extractedFiles.Any(file => string.Equals(
                                    file.RelativePath,
                                    relative,
                                    StringComparison.Ordinal)) ||
                                extractedDirectories.Contains(relative))
                            {
                                throw UnsafeEntry();
                            }
                            string destination = CreateSafeDestination(
                                roleDirectory,
                                normalized);
                            EnsurePrivateParentDirectories(
                                extractionRoot,
                                roleDirectoryName,
                                roleDirectory,
                                destination,
                                extractedDirectories);
                            bool executable =
                                (entry.Mode & AnyExecutableUnixModes) != 0;
                            VerifiedExtractedReleaseFile file =
                                await WriteEntryAsync(
                                    package.Role,
                                    relative,
                                    destination,
                                    entry.DataStream ?? Stream.Null,
                                    entry.Length,
                                    executable,
                                    cancellationToken).ConfigureAwait(false);
                            extractedFiles.Add(file);
                            break;
                        }
                    default:
                        throw UnsafeEntry();
                }
                if (extractedDirectories.Count > MaximumExtractedDirectoryCount)
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.EntryLimitExceeded,
                        "The extracted release exceeds its directory-count bound.");
                }
            }
        }

        DrainTrailingTarPadding(gzip);
        if (archiveFileCount == 0)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.InvalidArchive,
                "A verified release archive contains no regular files.");
        }
        if (input.Position != source.Length)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.InvalidArchive,
                "A verified release archive was not consumed exactly.");
        }
        ValidateSourceUnchanged(source, input.Length);
        return new ArchiveExtractionResult(expandedBytes);
    }

    private static void DrainTrailingTarPadding(Stream stream)
    {
        int trailingBytes = 0;
        while (true)
        {
            int value = stream.ReadByte();
            if (value == -1)
            {
                return;
            }
            if (value != 0 || ++trailingBytes > MaximumTrailingTarPaddingBytes)
            {
                throw Failure(
                    VerifiedReleaseArchiveExtractionFailureCode.InvalidArchive,
                    "A verified release archive contains unsafe trailing content.");
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task VerifyOpenSourceDigestAsync(
        FileStream input,
        SourceFile source,
        long expectedLength,
        ReadOnlyMemory<byte> expectedSha256,
        CancellationToken cancellationToken)
    {
        if (input.Length != source.Length ||
            source.Length != expectedLength ||
            expectedSha256.Length != 32)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.IntegrityMismatch,
                "A verified source archive no longer matches its retained length.");
        }
        byte[] digest = await HashStreamAsync(
            input,
            expectedLength,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    digest,
                    expectedSha256.Span))
            {
                throw Failure(
                    VerifiedReleaseArchiveExtractionFailureCode.IntegrityMismatch,
                    "A verified source archive no longer matches its retained digest.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<long> CopyVerifiedFileAsync(
        SourceFile source,
        string destination,
        long expectedLength,
        ReadOnlyMemory<byte> expectedSha256,
        CancellationToken cancellationToken)
    {
        await using FileStream input = OpenSource(source.Path);
        if (input.Length != source.Length ||
            source.Length != expectedLength ||
            expectedSha256.Length != 32)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.IntegrityMismatch,
                "The verified source manifest no longer matches its retained length.");
        }
        await using FileStream output = OpenNewOutput(destination);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long copied = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                copied = checked(copied + read);
                if (copied > expectedLength)
                {
                    throw Changed();
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (copied != expectedLength)
        {
            throw Changed();
        }
        byte[] digest = hash.GetHashAndReset();
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    digest,
                    expectedSha256.Span))
            {
                throw Failure(
                    VerifiedReleaseArchiveExtractionFailureCode.IntegrityMismatch,
                    "The verified source manifest no longer matches its retained digest.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
        ValidateSourceUnchanged(source, input.Length);
        File.SetUnixFileMode(destination, PrivateImmutableFileMode);
        return copied;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<VerifiedExtractedReleaseFile> WriteEntryAsync(
        ReleasePackageRole role,
        string relativePath,
        string destination,
        Stream source,
        long expectedLength,
        bool executable,
        CancellationToken cancellationToken)
    {
        await using FileStream output = OpenNewOutput(destination);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long written = 0;
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                written = checked(written + read);
                if (written > expectedLength)
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.InvalidArchive,
                        "An archive entry exceeded its declared length while extracting.");
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (written != expectedLength)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.InvalidArchive,
                "An archive entry was truncated while extracting.");
        }
        byte[] digest = hash.GetHashAndReset();
        UnixFileMode mode = executable
            ? PrivateImmutableExecutableFileMode
            : PrivateImmutableFileMode;
        File.SetUnixFileMode(destination, mode);
        return new VerifiedExtractedReleaseFile(
            role,
            relativePath,
            written,
            digest,
            executable);
    }

    [SupportedOSPlatform("linux")]
    private static void EnsurePrivateParentDirectories(
        string extractionRoot,
        string roleDirectoryName,
        string roleDirectory,
        string filePath,
        HashSet<string> extractedDirectories)
    {
        string parent = Path.GetDirectoryName(filePath) ??
            throw UnsafeEntry();
        string relative = Path.GetRelativePath(roleDirectory, parent);
        if (relative == ".")
        {
            return;
        }

        string current = roleDirectory;
        string currentRelative = roleDirectoryName;
        foreach (string segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsSafeArchiveSegment(segment))
            {
                throw UnsafeEntry();
            }
            current = Path.Combine(current, segment);
            currentRelative = $"{currentRelative}/{segment}";
            if (!Path.GetFullPath(current).StartsWith(
                    extractionRoot + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                throw UnsafeEntry();
            }
            if (!PathEntryExists(current))
            {
                CreatePrivateDirectory(current);
                extractedDirectories.Add(currentRelative);
            }
            else
            {
                ValidateExistingDirectory(
                    current,
                    privateDirectory: true,
                    VerifiedReleaseArchiveExtractionFailureCode.UnsafeArchiveEntry,
                    "An archive destination directory is unsafe.");
            }
        }
    }

    private static string NormalizeArchivePath(string? value)
    {
        string path = value ?? string.Empty;
        if (path.Length == 0 ||
            path.Length > MaximumRelativePathLength + 2 ||
            path.IndexOf('\\') >= 0 ||
            path.IndexOf('\0') >= 0 ||
            path.StartsWith("/", StringComparison.Ordinal))
        {
            throw UnsafeEntry();
        }
        if (path is "." or "./")
        {
            return string.Empty;
        }
        if (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        path = path.TrimEnd('/');
        if (path.Length == 0 ||
            path.Length > MaximumRelativePathLength ||
            path.StartsWith("./", StringComparison.Ordinal) ||
            path.Contains("//", StringComparison.Ordinal))
        {
            throw UnsafeEntry();
        }

        string[] segments = path.Split('/');
        if (segments.Length > MaximumPathDepth ||
            segments.Any(segment => !IsSafeArchiveSegment(segment)))
        {
            throw UnsafeEntry();
        }
        return string.Join('/', segments);
    }

    private static bool IsSafeArchiveSegment(string segment) =>
        segment.Length is > 0 and <= 255 &&
        segment is not "." and not ".." &&
        segment.All(character =>
            !char.IsControl(character) &&
            character != Path.DirectorySeparatorChar &&
            character != Path.AltDirectorySeparatorChar);

    private static void ValidateArchiveMode(UnixFileMode mode)
    {
        if ((mode & ~AllPermissionBits) != 0)
        {
            throw UnsafeEntry();
        }
    }

    private static string RoleDirectoryName(ReleasePackageRole role) =>
        role switch
        {
            ReleasePackageRole.GatewayWeb => "gateway-web",
            ReleasePackageRole.Broker => "broker",
            ReleasePackageRole.AetherRemoteAgent => "aetherremote-agent",
            ReleasePackageRole.StationEngine => "station-engine",
            _ => throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.InvalidPlan,
                "The verified installation plan contains an unsupported package role.")
        };

    private static string CreateSafeDestination(string root, string relativePath)
    {
        string destination = Path.GetFullPath(
            Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw UnsafeEntry();
        }
        return destination;
    }

    [SupportedOSPlatform("linux")]
    private static void CreatePrivateDirectory(string path)
    {
        if (PathEntryExists(path))
        {
            throw UnsafeEntry();
        }
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, PrivateWritableDirectoryMode);
        ValidateExistingDirectory(
            path,
            privateDirectory: true,
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeArchiveEntry,
            "An archive destination directory is unsafe.");
    }

    [SupportedOSPlatform("linux")]
    private static async Task<VerifiedExtractedReleaseFile>
        SnapshotExtractedFileAsync(
            ReleasePackageRole role,
            string root,
            string path,
            bool executable,
            CancellationToken cancellationToken)
    {
        FileInfo file = new(path);
        file.Refresh();
        if (!file.Exists || file.Length < 0)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.ExtractionWriteFailed,
                "An extracted file could not be read back safely.");
        }
        await using FileStream input = OpenSource(path);
        byte[] digest = await HashStreamAsync(
            input,
            file.Length,
            cancellationToken).ConfigureAwait(false);
        return new VerifiedExtractedReleaseFile(
            role,
            RelativePath(root, path),
            file.Length,
            digest,
            executable);
    }

    [SupportedOSPlatform("linux")]
    private static async Task ReverifySourceDigestsAsync(
        IReadOnlyDictionary<string, SourceFile> sourceFiles,
        VerifiedReleaseInstallationPlan plan,
        CancellationToken cancellationToken)
    {
        await VerifySourceFileDigestAsync(
            sourceFiles[
                LocalOfflineReleaseBundleVerificationService.ManifestFileName],
            plan.ManifestLength,
            plan.ManifestSha256.ToArray(),
            cancellationToken).ConfigureAwait(false);
        foreach (VerifiedReleaseInstallationPackagePlan package in plan.Packages)
        {
            await VerifySourceFileDigestAsync(
                sourceFiles[package.SourceRelativePath],
                package.Length,
                package.Sha256.ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task VerifySourceFileDigestAsync(
        SourceFile source,
        long expectedLength,
        ReadOnlyMemory<byte> expectedSha256,
        CancellationToken cancellationToken)
    {
        await using FileStream input = OpenSource(source.Path);
        if (input.Length != source.Length ||
            source.Length != expectedLength ||
            expectedSha256.Length != 32)
        {
            throw Changed();
        }
        byte[] digest = await HashStreamAsync(
            input,
            expectedLength,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    digest,
                    expectedSha256.Span))
            {
                throw Changed();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
        ValidateSourceUnchanged(source, input.Length);
    }

    private static async Task<byte[]> HashStreamAsync(
        Stream input,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long readTotal = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                readTotal = checked(readTotal + read);
                if (readTotal > expectedLength)
                {
                    throw Changed();
                }
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (readTotal != expectedLength)
        {
            throw Changed();
        }
        return hash.GetHashAndReset();
    }

    [SupportedOSPlatform("linux")]
    private static void FreezeExtractionTree(string root)
    {
        try
        {
            PrivateTree tree = CollectPrivateTree(root);
            foreach (string directory in tree.Directories
                         .OrderByDescending(path => path.Length))
            {
                File.SetUnixFileMode(
                    directory,
                    PrivateImmutableDirectoryMode);
            }
            File.SetUnixFileMode(root, PrivateImmutableDirectoryMode);
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                "The private extraction tree could not be frozen immutable.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task ValidateFrozenExtractionTreeAsync(
        string root,
        IReadOnlyList<VerifiedExtractedReleaseFile> expectedFiles,
        IReadOnlySet<string> expectedDirectories,
        CancellationToken cancellationToken)
    {
        ValidateImmutablePrivateDirectory(new DirectoryInfo(root));
        Dictionary<string, FileInfo> actualFiles = new(StringComparer.Ordinal);
        HashSet<string> actualDirectories = new(StringComparer.Ordinal);
        Stack<DirectoryInfo> pending = new();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = pending.Pop();
            ValidateImmutablePrivateDirectory(directory);
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                        "The frozen extraction tree contains a symbolic link or reparse point.");
                }
                if (entry is DirectoryInfo child)
                {
                    string relative = RelativePath(root, child.FullName);
                    if (!actualDirectories.Add(relative))
                    {
                        throw Failure(
                            VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                            "The frozen extraction tree contains a duplicate directory.");
                    }
                    pending.Push(child);
                    continue;
                }
                if (entry is not FileInfo file ||
                    (file.Attributes & FileAttributes.Directory) != 0)
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                        "The frozen extraction tree contains a non-regular entry.");
                }
                string fileRelative = RelativePath(root, file.FullName);
                if (!actualFiles.TryAdd(fileRelative, file))
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                        "The frozen extraction tree contains a duplicate file.");
                }
            }
        }
        if (!actualDirectories.SetEquals(expectedDirectories) ||
            actualFiles.Count != expectedFiles.Count)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                "The frozen extraction tree does not match the extracted inventory.");
        }

        foreach (VerifiedExtractedReleaseFile expected in expectedFiles)
        {
            if (!actualFiles.TryGetValue(expected.RelativePath, out FileInfo? file))
            {
                throw Failure(
                    VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                    "The frozen extraction tree is missing an extracted file.");
            }
            ValidateImmutablePrivateFile(file, expected.Executable);
            if (file.Length != expected.Length)
            {
                throw Failure(
                    VerifiedReleaseArchiveExtractionFailureCode.IntegrityMismatch,
                    "A frozen extracted file does not match its recorded length.");
            }
            await using FileStream input = OpenSource(file.FullName);
            byte[] digest = await HashStreamAsync(
                input,
                expected.Length,
                cancellationToken).ConfigureAwait(false);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        digest,
                        expected.Sha256))
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.IntegrityMismatch,
                        "A frozen extracted file does not match its recorded digest.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static PrivateTree CollectPrivateTree(string root)
    {
        DirectoryInfo rootInfo = new(root);
        ValidateWritablePrivateDirectory(rootInfo);
        List<string> directories = [];
        Stack<DirectoryInfo> pending = new();
        pending.Push(rootInfo);
        int directoryCount = 0;
        int fileCount = 0;
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            ValidateWritablePrivateDirectory(directory);
            if (++directoryCount > MaximumExtractedDirectoryCount + 1)
            {
                throw Failure(
                    VerifiedReleaseArchiveExtractionFailureCode.EntryLimitExceeded,
                    "The private extraction tree exceeds its directory-count bound.");
            }
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                        "The private extraction tree contains a symbolic link or reparse point.");
                }
                if (entry is DirectoryInfo child)
                {
                    directories.Add(child.FullName);
                    pending.Push(child);
                    continue;
                }
                if (entry is not FileInfo file ||
                    (file.Attributes & FileAttributes.Directory) != 0 ||
                    ++fileCount > MaximumExtractedFileCount)
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                        "The private extraction tree contains an unsupported or excessive file entry.");
                }
                UnixFileMode mode = File.GetUnixFileMode(file.FullName);
                if ((mode & AnyWritableUnixModes) != 0 ||
                    (mode & SharedPermissionUnixModes) != 0)
                {
                    throw Failure(
                        VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                        "The private extraction tree contains an unsafe file mode.");
                }
            }
        }
        return new PrivateTree(directories);
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateExistingDirectory(
        string path,
        bool privateDirectory,
        VerifiedReleaseArchiveExtractionFailureCode failureCode,
        string message)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null)
        {
            throw Failure(failureCode, message);
        }
        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode & ForbiddenSharedWritableUnixModes) != 0 ||
            privateDirectory && (mode & SharedPermissionUnixModes) != 0)
        {
            throw Failure(failureCode, message);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateWritablePrivateDirectory(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            File.GetUnixFileMode(directory.FullName) !=
                PrivateWritableDirectoryMode)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                "The private extraction tree contains an unsafe writable directory.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutableSourceDirectory(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            File.GetUnixFileMode(directory.FullName) !=
                PrivateImmutableDirectoryMode)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.UnsafeSourceStaging,
                "The verified source staging tree contains an unsafe immutable directory.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutablePrivateDirectory(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            File.GetUnixFileMode(directory.FullName) !=
                PrivateImmutableDirectoryMode)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                "The private extraction tree contains an unsafe immutable directory.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static SourceFile ValidateImmutablePrivateFile(FileInfo file)
    {
        file.Refresh();
        if (!file.Exists || file.Length < 1 ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.LinkTarget is not null ||
            File.GetUnixFileMode(file.FullName) != PrivateImmutableFileMode)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.UnsafeSourceStaging,
                "The verified source staging tree contains an unsafe immutable file.");
        }
        return new SourceFile(
            file.FullName,
            file.Length,
            file.LastWriteTimeUtc);
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutablePrivateFile(
        FileInfo file,
        bool executable)
    {
        file.Refresh();
        UnixFileMode expected = executable
            ? PrivateImmutableExecutableFileMode
            : PrivateImmutableFileMode;
        if (!file.Exists || file.Length < 0 ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.LinkTarget is not null ||
            File.GetUnixFileMode(file.FullName) != expected)
        {
            throw Failure(
                VerifiedReleaseArchiveExtractionFailureCode.ExtractionFreezeFailed,
                "The frozen extraction tree contains an unsafe immutable file.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateSourceUnchanged(
        SourceFile source,
        long streamLength)
    {
        SourceFile after = ValidateImmutablePrivateFile(new FileInfo(source.Path));
        if (streamLength != source.Length ||
            after.Length != source.Length ||
            after.LastWriteTimeUtc != source.LastWriteTimeUtc)
        {
            throw Changed();
        }
    }

    private static bool EquivalentSourceLayout(
        IReadOnlyDictionary<string, SourceFile> first,
        IReadOnlyDictionary<string, SourceFile> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }
        foreach ((string path, SourceFile source) in first)
        {
            if (!second.TryGetValue(path, out SourceFile? candidate) ||
                !string.Equals(
                    source.Path,
                    candidate.Path,
                    StringComparison.Ordinal) ||
                source.Length != candidate.Length ||
                source.LastWriteTimeUtc != candidate.LastWriteTimeUtc)
            {
                return false;
            }
        }
        return true;
    }

    private static bool EquivalentStatus(
        ReleaseStatusReadResult first,
        ReleaseStatusReadResult second) =>
        first.Succeeded &&
        second.Succeeded &&
        first.SetupSchemaVersion == second.SetupSchemaVersion &&
        first.SetupRevision == second.SetupRevision &&
        first.SetupComplete == second.SetupComplete &&
        first.SetupLockMode == second.SetupLockMode &&
        first.LastCompletedStep == second.LastCompletedStep &&
        first.UpdateChannel == second.UpdateChannel &&
        string.Equals(
            first.PinnedReleaseIdentity,
            second.PinnedReleaseIdentity,
            StringComparison.Ordinal) &&
        first.InstallTransmitSupport == second.InstallTransmitSupport &&
        first.ReleaseDirectoryPresent == second.ReleaseDirectoryPresent &&
        first.CurrentPointerPresent == second.CurrentPointerPresent &&
        string.Equals(
            first.ActiveReleaseIdentity,
            second.ActiveReleaseIdentity,
            StringComparison.Ordinal) &&
        first.AvailableReleaseIdentities.SequenceEqual(
            second.AvailableReleaseIdentities,
            StringComparer.Ordinal);

    private static FileStream OpenSource(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous |
                    FileOptions.SequentialScan
            });

    [SupportedOSPlatform("linux")]
    private static FileStream OpenNewOutput(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous |
                    FileOptions.SequentialScan,
                UnixCreateMode = PrivateWritableFileMode
            });

    [SupportedOSPlatform("linux")]
    private static bool TryCleanup(string path)
    {
        if (string.IsNullOrEmpty(path) || !PathEntryExists(path))
        {
            return true;
        }
        try
        {
            DirectoryInfo root = new(path);
            root.Refresh();
            if (!root.Exists ||
                (root.Attributes & FileAttributes.ReparsePoint) != 0 ||
                root.LinkTarget is not null)
            {
                return false;
            }

            List<FileInfo> files = [];
            List<DirectoryInfo> directories = [];
            Stack<DirectoryInfo> pending = new();
            pending.Push(root);
            int directoryCount = 0;
            int fileCount = 0;
            while (pending.Count > 0)
            {
                DirectoryInfo directory = pending.Pop();
                directory.Refresh();
                if (!directory.Exists ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    directory.LinkTarget is not null ||
                    ++directoryCount > MaximumExtractedDirectoryCount + 1)
                {
                    return false;
                }
                foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
                {
                    entry.Refresh();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        entry.LinkTarget is not null)
                    {
                        return false;
                    }
                    if (entry is DirectoryInfo child)
                    {
                        directories.Add(child);
                        pending.Push(child);
                    }
                    else if (entry is FileInfo file &&
                             (file.Attributes & FileAttributes.Directory) == 0 &&
                             ++fileCount <= MaximumExtractedFileCount)
                    {
                        files.Add(file);
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            File.SetUnixFileMode(root.FullName, PrivateWritableDirectoryMode);
            foreach (DirectoryInfo directory in directories)
            {
                File.SetUnixFileMode(
                    directory.FullName,
                    PrivateWritableDirectoryMode);
            }
            foreach (FileInfo file in files)
            {
                File.SetUnixFileMode(
                    file.FullName,
                    PrivateWritableFileMode);
                File.Delete(file.FullName);
            }
            foreach (DirectoryInfo directory in directories
                         .OrderByDescending(value => value.FullName.Length))
            {
                Directory.Delete(directory.FullName, recursive: false);
            }
            Directory.Delete(root.FullName, recursive: false);
            return !PathEntryExists(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsCanonicalAbsolutePath(string value)
    {
        if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value))
        {
            return false;
        }
        try
        {
            return string.Equals(
                Path.GetFullPath(value),
                value,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
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

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static bool PathEquals(string? left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool PathEntryExists(string path) =>
        Directory.Exists(path) || File.Exists(path) ||
        new DirectoryInfo(path).LinkTarget is not null ||
        new FileInfo(path).LinkTarget is not null;

    private static Func<CancellationToken, Task<ReleaseStatusReadResult>>
        CreateStatusReader(ReleaseInstallationStatusReader statusReader)
    {
        ArgumentNullException.ThrowIfNull(statusReader);
        return statusReader.ReadAsync;
    }

    private static ExtractionException UnsafeEntry() =>
        Failure(
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeArchiveEntry,
            "A verified release archive contains an unsafe or unsupported entry.");

    private static ExtractionException Changed() =>
        Failure(
            VerifiedReleaseArchiveExtractionFailureCode.SourceChanged,
            "The verified source staging tree changed while archives were being extracted.");

    private static ExtractionException Failure(
        VerifiedReleaseArchiveExtractionFailureCode failureCode,
        string message) =>
        new(failureCode, message);

    private sealed record SourceFile(
        string Path,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed record ArchiveExtractionResult(long ExpandedBytes);

    private sealed record PrivateTree(IReadOnlyList<string> Directories);

    private sealed class ExtractionException(
        VerifiedReleaseArchiveExtractionFailureCode failureCode,
        string message) : Exception(message)
    {
        internal VerifiedReleaseArchiveExtractionFailureCode FailureCode { get; } =
            failureCode;
    }
}
