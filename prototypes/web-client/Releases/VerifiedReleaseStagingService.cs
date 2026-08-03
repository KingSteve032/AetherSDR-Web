using System.Buffers;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseStagingFailureCode
{
    None = 0,
    UnsupportedPlatform = 1,
    InvalidPlan = 2,
    StatusUnavailable = 3,
    StatusMismatch = 4,
    TargetAlreadyPresent = 5,
    UnsafeDeploymentLayout = 6,
    UnsafeStagingRoot = 7,
    UnsafeBundle = 8,
    SourceChanged = 9,
    IntegrityMismatch = 10,
    StagingWriteFailed = 11,
    StagingFreezeFailed = 12,
    StatusChangedDuringStaging = 13,
    CleanupFailed = 14
}

public sealed record VerifiedReleaseStagingReport(
    bool Succeeded,
    VerifiedReleaseStagingFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int PackageCount,
    long StagedBytes,
    bool ManifestStaged,
    bool ImmutableStagingTree,
    bool TargetPublished,
    bool CurrentPointerChanged,
    bool CleanupRequired)
{
    internal VerifiedStagedRelease? StagedRelease { get; init; }
    internal string CleanupPath { get; init; } = string.Empty;

    internal static VerifiedReleaseStagingReport Failure(
        VerifiedReleaseStagingFailureCode failureCode,
        string message,
        VerifiedReleaseInstallationPlan? plan = null,
        long stagedBytes = 0,
        bool manifestStaged = false,
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
            stagedBytes,
            manifestStaged,
            ImmutableStagingTree: false,
            TargetPublished: false,
            CurrentPointerChanged: false,
            cleanupRequired)
        {
            CleanupPath = cleanupPath
        };

    internal static VerifiedReleaseStagingReport Success(
        VerifiedStagedRelease stagedRelease) =>
        new(
            true,
            VerifiedReleaseStagingFailureCode.None,
            "The verified release was copied into one private immutable staging tree without publishing or activation.",
            stagedRelease.Plan.SetupRevision,
            stagedRelease.Plan.InstalledReleaseIdentity,
            stagedRelease.Plan.TargetReleaseIdentity,
            stagedRelease.Plan.Packages.Count,
            stagedRelease.StagedBytes,
            ManifestStaged: true,
            ImmutableStagingTree: true,
            TargetPublished: false,
            CurrentPointerChanged: false,
            CleanupRequired: false)
        {
            StagedRelease = stagedRelease
        };
}

public sealed record VerifiedReleaseStagingDiagnostics(
    bool Registered,
    bool StatusRevalidationRegistered,
    bool VerifiedBundleReadRegistered,
    bool FileWriteRegistered,
    bool StagingExecutionRegistered,
    bool ImmutableFreezeRegistered,
    bool CleanupRegistered,
    bool NetworkDownloadRegistered,
    bool ArchiveExtractionRegistered,
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

internal sealed class VerifiedStagedRelease
{
    internal VerifiedStagedRelease(
        VerifiedReleaseInstallationPlan plan,
        string stagingPath,
        long stagedBytes)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        StagingPath = stagingPath ?? string.Empty;
        StagedBytes = stagedBytes;
    }

    internal VerifiedReleaseInstallationPlan Plan { get; }
    internal string StagingPath { get; }
    internal long StagedBytes { get; }
}

/// <summary>
/// Copies one already-verified offline bundle into a new private staging tree,
/// verifies every copied byte against the retained manifest/package digests,
/// freezes the completed tree immutable, and revalidates setup, inventory, and
/// the active current pointer before returning. It does not publish the target
/// release, switch current, extract archives, install, activate, roll back,
/// execute migrations, control services, or touch radio, watchdog, command,
/// lease, browser, or transmit state.
/// </summary>
public sealed class VerifiedReleaseStagingService
{
    internal const string StagingDirectoryName = ".release-staging";
    internal const int MaximumDirectoryCount = 16;

    private const int BufferSize = 128 * 1024;
    private const UnixFileMode ForbiddenWritableUnixModes =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode AnyWritableUnixModes =
        UnixFileMode.UserWrite |
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;
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

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;

    public VerifiedReleaseStagingService(
        ReleaseInstallationStatusReader statusReader)
        : this(CreateStatusReader(statusReader))
    {
    }

    internal VerifiedReleaseStagingService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        Snapshot = new VerifiedReleaseStagingDiagnostics(
            Registered: true,
            StatusRevalidationRegistered: true,
            VerifiedBundleReadRegistered: true,
            FileWriteRegistered: true,
            StagingExecutionRegistered: true,
            ImmutableFreezeRegistered: true,
            CleanupRegistered: true,
            NetworkDownloadRegistered: false,
            ArchiveExtractionRegistered: false,
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

    public VerifiedReleaseStagingDiagnostics Snapshot { get; }

    [SupportedOSPlatform("linux")]
    internal async Task<VerifiedReleaseStagingReport> StageAsync(
        VerifiedReleaseInstallationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsLinux())
        {
            return VerifiedReleaseStagingReport.Failure(
                VerifiedReleaseStagingFailureCode.UnsupportedPlatform,
                "Verified release staging requires a supported Linux runtime.",
                plan);
        }
        if (!ValidatePlan(plan))
        {
            return VerifiedReleaseStagingReport.Failure(
                VerifiedReleaseStagingFailureCode.InvalidPlan,
                "The verified release installation plan is incomplete or non-canonical.",
                plan);
        }

        ReleaseStatusReadResult beforeStatus =
            await m_statusReader(cancellationToken);
        VerifiedReleaseStagingReport? statusFailure =
            ValidateStatusAgainstPlan(beforeStatus, plan);
        if (statusFailure is not null)
        {
            return statusFailure;
        }

        string stagingPath = string.Empty;
        long stagedBytes = 0;
        bool manifestStaged = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDeploymentLayout(plan);
            EnsureTargetAbsent(plan.TargetReleasePath);
            string stagingRoot = PrepareStagingRoot(plan.DeploymentRootPath);
            stagingPath = CreatePrivateStagingDirectory(
                stagingRoot,
                plan.TargetReleaseIdentity);

            IReadOnlyDictionary<string, SourceFile> sourceFiles =
                ReadExactBundleLayout(plan);
            List<ExpectedFile> expectedFiles = CreateExpectedFiles(plan);
            foreach (ExpectedFile expected in expectedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceFile source = sourceFiles[expected.RelativePath];
                string destinationPath = CreateDestinationPath(
                    stagingPath,
                    expected.RelativePath);
                long copied = await CopyVerifiedFileAsync(
                    source,
                    destinationPath,
                    expected.Length,
                    expected.Sha256,
                    cancellationToken);
                stagedBytes = checked(stagedBytes + copied);
                manifestStaged |= string.Equals(
                    expected.RelativePath,
                    LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                    StringComparison.Ordinal);
            }

            IReadOnlyDictionary<string, SourceFile> sourceFilesAfterCopy =
                ReadExactBundleLayout(plan);
            if (!EquivalentSourceLayout(sourceFiles, sourceFilesAfterCopy))
            {
                throw Changed();
            }
            FreezeStagingTree(stagingPath);
            ValidateFrozenStagingTree(stagingPath, expectedFiles);

            ReleaseStatusReadResult afterStatus =
                await m_statusReader(cancellationToken);
            if (!EquivalentStatus(beforeStatus, afterStatus) ||
                afterStatus.AvailableReleaseIdentities.Contains(
                    plan.TargetReleaseIdentity,
                    StringComparer.Ordinal) ||
                PathEntryExists(plan.TargetReleasePath))
            {
                throw Failure(
                    VerifiedReleaseStagingFailureCode.StatusChangedDuringStaging,
                    "Local installation status changed while the verified release was staged.");
            }

            return VerifiedReleaseStagingReport.Success(
                new VerifiedStagedRelease(plan, stagingPath, stagedBytes));
        }
        catch (OperationCanceledException)
        {
            if (TryCleanup(stagingPath))
            {
                throw;
            }
            return VerifiedReleaseStagingReport.Failure(
                VerifiedReleaseStagingFailureCode.CleanupFailed,
                "Cancelled release staging could not remove its private temporary tree.",
                plan,
                stagedBytes,
                manifestStaged,
                cleanupRequired: true,
                cleanupPath: stagingPath);
        }
        catch (StagingException exception)
        {
            return FailureWithCleanup(
                exception.FailureCode,
                exception.Message,
                plan,
                stagingPath,
                stagedBytes,
                manifestStaged);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or CryptographicException or ArgumentException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return FailureWithCleanup(
                VerifiedReleaseStagingFailureCode.StagingWriteFailed,
                "The verified release could not be copied into private staging.",
                plan,
                stagingPath,
                stagedBytes,
                manifestStaged);
        }
    }

    [SupportedOSPlatform("linux")]
    private static VerifiedReleaseStagingReport FailureWithCleanup(
        VerifiedReleaseStagingFailureCode failureCode,
        string message,
        VerifiedReleaseInstallationPlan plan,
        string stagingPath,
        long stagedBytes,
        bool manifestStaged)
    {
        if (TryCleanup(stagingPath))
        {
            return VerifiedReleaseStagingReport.Failure(
                failureCode,
                message,
                plan,
                stagedBytes,
                manifestStaged);
        }
        return VerifiedReleaseStagingReport.Failure(
            VerifiedReleaseStagingFailureCode.CleanupFailed,
            "Failed release staging also could not remove its private temporary tree.",
            plan,
            stagedBytes,
            manifestStaged,
            cleanupRequired: true,
            cleanupPath: stagingPath);
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
        HashSet<string> relativePaths = new(StringComparer.Ordinal);
        foreach (VerifiedReleaseInstallationPackagePlan package in plan.Packages)
        {
            if (!roles.Add(package.Role) ||
                !ReleasePackagePath.IsSafe(package.SourceRelativePath) ||
                !relativePaths.Add(package.SourceRelativePath) ||
                package.Length is < 1 or >
                    SignedReleaseManifestVerifier.MaximumDeclaredPackageLength ||
                package.Sha256.Length != 32 ||
                !IsCanonicalAbsolutePath(package.TargetPath))
            {
                return false;
            }

            string expectedTarget;
            try
            {
                expectedTarget = Path.GetFullPath(
                    Path.Combine(
                        plan.TargetReleasePath,
                        package.SourceRelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or
                    PathTooLongException)
            {
                return false;
            }
            if (!PathEquals(package.TargetPath, expectedTarget) ||
                !expectedTarget.StartsWith(
                    plan.TargetReleasePath + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
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

    private static VerifiedReleaseStagingReport? ValidateStatusAgainstPlan(
        ReleaseStatusReadResult status,
        VerifiedReleaseInstallationPlan plan)
    {
        if (!status.Succeeded)
        {
            return VerifiedReleaseStagingReport.Failure(
                VerifiedReleaseStagingFailureCode.StatusUnavailable,
                "Local release status is unavailable for verified staging.",
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
            return VerifiedReleaseStagingReport.Failure(
                VerifiedReleaseStagingFailureCode.StatusMismatch,
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
            VerifiedReleaseStagingFailureCode.UnsafeDeploymentLayout,
            "The deployment root is unsafe for verified staging.");
        ValidateExistingDirectory(
            plan.ReleaseRootPath,
            privateDirectory: false,
            VerifiedReleaseStagingFailureCode.UnsafeDeploymentLayout,
            "The release root is unsafe for verified staging.");
    }

    private static void EnsureTargetAbsent(string targetPath)
    {
        if (PathEntryExists(targetPath))
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.TargetAlreadyPresent,
                "The target release path already exists and staging will not overwrite it.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static string PrepareStagingRoot(string deploymentRootPath)
    {
        string stagingRoot = Path.GetFullPath(
            Path.Combine(deploymentRootPath, StagingDirectoryName));
        if (!PathEquals(Path.GetDirectoryName(stagingRoot), deploymentRootPath))
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.UnsafeStagingRoot,
                "The private staging root is not a direct deployment child.");
        }

        if (!PathEntryExists(stagingRoot))
        {
            Directory.CreateDirectory(stagingRoot);
            File.SetUnixFileMode(stagingRoot, PrivateWritableDirectoryMode);
        }
        ValidateExistingDirectory(
            stagingRoot,
            privateDirectory: true,
            VerifiedReleaseStagingFailureCode.UnsafeStagingRoot,
            "The private staging root is unsafe.");
        return stagingRoot;
    }

    [SupportedOSPlatform("linux")]
    private static string CreatePrivateStagingDirectory(
        string stagingRoot,
        string targetReleaseIdentity)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            string candidate = Path.GetFullPath(
                Path.Combine(
                    stagingRoot,
                    $"{targetReleaseIdentity}.{Guid.NewGuid():N}"));
            if (!PathEquals(Path.GetDirectoryName(candidate), stagingRoot) ||
                PathEntryExists(candidate))
            {
                continue;
            }

            Directory.CreateDirectory(candidate);
            File.SetUnixFileMode(candidate, PrivateWritableDirectoryMode);
            ValidateExistingDirectory(
                candidate,
                privateDirectory: true,
                VerifiedReleaseStagingFailureCode.UnsafeStagingRoot,
                "The private staging transaction directory is unsafe.");
            return Path.TrimEndingDirectorySeparator(candidate);
        }

        throw Failure(
            VerifiedReleaseStagingFailureCode.UnsafeStagingRoot,
            "A unique private staging transaction directory could not be created.");
    }

    [SupportedOSPlatform("linux")]
    private static IReadOnlyDictionary<string, SourceFile> ReadExactBundleLayout(
        VerifiedReleaseInstallationPlan plan)
    {
        string rootPath = plan.BundleDirectory;
        ValidateBundleRootStillSafe(rootPath);

        HashSet<string> expectedPaths = new(StringComparer.Ordinal)
        {
            LocalOfflineReleaseBundleVerificationService.ManifestFileName
        };
        foreach (VerifiedReleaseInstallationPackagePlan package in plan.Packages)
        {
            expectedPaths.Add(package.SourceRelativePath);
        }

        Dictionary<string, SourceFile> files = new(StringComparer.Ordinal);
        Stack<DirectoryInfo> pending = new();
        pending.Push(new DirectoryInfo(rootPath));
        int directoryCount = 0;
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            ValidateImmutableSourceDirectory(directory);
            if (++directoryCount > MaximumDirectoryCount)
            {
                throw Failure(
                    VerifiedReleaseStagingFailureCode.UnsafeBundle,
                    "The verified bundle directory structure exceeds its staging bound.");
            }

            FileSystemInfo[] entries = directory.GetFileSystemInfos();
            if (!PathEquals(directory.FullName, rootPath) && entries.Length == 0)
            {
                throw Failure(
                    VerifiedReleaseStagingFailureCode.UnsafeBundle,
                    "The verified bundle contains an empty directory.");
            }

            foreach (FileSystemInfo entry in entries)
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        VerifiedReleaseStagingFailureCode.UnsafeBundle,
                        "The verified bundle contains a symbolic link or reparse point.");
                }

                if (entry is DirectoryInfo child)
                {
                    string relativeDirectory = RelativePath(rootPath, child.FullName);
                    if (!ReleasePackagePath.IsSafe(relativeDirectory))
                    {
                        throw Failure(
                            VerifiedReleaseStagingFailureCode.UnsafeBundle,
                            "The verified bundle contains an unsafe directory path.");
                    }
                    pending.Push(child);
                    continue;
                }

                if (entry is not FileInfo file ||
                    (file.Attributes & FileAttributes.Directory) != 0)
                {
                    throw Failure(
                        VerifiedReleaseStagingFailureCode.UnsafeBundle,
                        "The verified bundle contains a non-regular entry.");
                }

                string relativePath = RelativePath(rootPath, file.FullName);
                if (!expectedPaths.Contains(relativePath) ||
                    !files.TryAdd(relativePath, ValidateImmutableSourceFile(file)))
                {
                    throw Failure(
                        VerifiedReleaseStagingFailureCode.UnsafeBundle,
                        "The verified bundle contents no longer match the verified plan.");
                }
            }
        }

        if (!expectedPaths.SetEquals(files.Keys))
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.UnsafeBundle,
                "The verified bundle is missing planned manifest or package files.");
        }
        return files;
    }

    private static List<ExpectedFile> CreateExpectedFiles(
        VerifiedReleaseInstallationPlan plan)
    {
        List<ExpectedFile> expected =
        [
            new ExpectedFile(
                LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                plan.ManifestLength,
                plan.ManifestSha256.ToArray())
        ];
        expected.AddRange(
            plan.Packages.Select(package =>
                new ExpectedFile(
                    package.SourceRelativePath,
                    package.Length,
                    package.Sha256.ToArray())));
        return expected
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    [SupportedOSPlatform("linux")]
    private static string CreateDestinationPath(
        string stagingPath,
        string relativePath)
    {
        string destinationPath = Path.GetFullPath(
            Path.Combine(
                stagingPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = stagingPath + Path.DirectorySeparatorChar;
        if (!destinationPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.InvalidPlan,
                "A planned staging destination leaves the private staging tree.");
        }

        string parent = Path.GetDirectoryName(destinationPath) ??
            throw Failure(
                VerifiedReleaseStagingFailureCode.InvalidPlan,
                "A planned staging file has no parent directory.");
        EnsurePrivateDestinationDirectories(stagingPath, parent);
        return destinationPath;
    }

    [SupportedOSPlatform("linux")]
    private static void EnsurePrivateDestinationDirectories(
        string stagingPath,
        string parentPath)
    {
        string relative = Path.GetRelativePath(stagingPath, parentPath);
        if (relative == ".")
        {
            return;
        }

        string current = stagingPath;
        foreach (string segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw Failure(
                    VerifiedReleaseStagingFailureCode.InvalidPlan,
                    "A planned staging directory contains a relative segment.");
            }
            current = Path.Combine(current, segment);
            if (!PathEntryExists(current))
            {
                Directory.CreateDirectory(current);
                File.SetUnixFileMode(current, PrivateWritableDirectoryMode);
            }
            ValidateExistingDirectory(
                current,
                privateDirectory: true,
                VerifiedReleaseStagingFailureCode.UnsafeStagingRoot,
                "A private staging subdirectory is unsafe.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<long> CopyVerifiedFileAsync(
        SourceFile source,
        string destinationPath,
        long expectedLength,
        ReadOnlyMemory<byte> expectedSha256,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 1 || expectedSha256.Length != 32 ||
            source.Length != expectedLength)
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.IntegrityMismatch,
                "A verified source file no longer matches its planned length.");
        }

        using FileStream input = OpenSource(source.Path);
        if (input.Length != source.Length)
        {
            throw Changed();
        }
        await using FileStream output = new(
            destinationPath,
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
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }
                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
                hash.AppendData(buffer, 0, read);
                copied = checked(copied + read);
                if (copied > expectedLength)
                {
                    throw Changed();
                }
            }
            await output.FlushAsync(cancellationToken);
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
        if (!CryptographicOperations.FixedTimeEquals(
                digest,
                expectedSha256.Span))
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.IntegrityMismatch,
                "A verified source file digest changed before staging completed.");
        }

        ValidateSourceUnchanged(source, input.Length);
        File.SetUnixFileMode(destinationPath, PrivateImmutableFileMode);
        return copied;
    }

    [SupportedOSPlatform("linux")]
    private static void FreezeStagingTree(string stagingPath)
    {
        try
        {
            PrivateStagingTree tree = CollectPrivateStagingTree(
                stagingPath,
                VerifiedReleaseStagingFailureCode.StagingFreezeFailed,
                "The private staging tree became unsafe before it was frozen.");
            foreach (string directory in tree.Directories
                         .OrderByDescending(path => path.Length))
            {
                File.SetUnixFileMode(
                    directory,
                    PrivateImmutableDirectoryMode);
            }
            File.SetUnixFileMode(
                stagingPath,
                PrivateImmutableDirectoryMode);
        }
        catch (StagingException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.StagingFreezeFailed,
                "The private staging tree could not be frozen immutable.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static PrivateStagingTree CollectPrivateStagingTree(
        string stagingPath,
        VerifiedReleaseStagingFailureCode failureCode,
        string message)
    {
        DirectoryInfo root = new(stagingPath);
        ValidateExistingDirectory(
            stagingPath,
            privateDirectory: true,
            failureCode,
            message);

        List<string> directories = [];
        List<string> files = [];
        Stack<DirectoryInfo> pending = new();
        pending.Push(root);
        int directoryCount = 0;
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            ValidateExistingDirectory(
                directory.FullName,
                privateDirectory: true,
                failureCode,
                message);
            if (++directoryCount > MaximumDirectoryCount)
            {
                throw Failure(failureCode, message);
            }

            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(failureCode, message);
                }
                if (entry is DirectoryInfo child)
                {
                    directories.Add(child.FullName);
                    pending.Push(child);
                    continue;
                }
                if (entry is not FileInfo file ||
                    !file.Exists ||
                    (file.Attributes & FileAttributes.Directory) != 0)
                {
                    throw Failure(failureCode, message);
                }

                UnixFileMode mode = File.GetUnixFileMode(file.FullName);
                if ((mode &
                        (UnixFileMode.GroupRead |
                         UnixFileMode.GroupWrite |
                         UnixFileMode.GroupExecute |
                         UnixFileMode.OtherRead |
                         UnixFileMode.OtherWrite |
                         UnixFileMode.OtherExecute)) != 0)
                {
                    throw Failure(failureCode, message);
                }
                files.Add(file.FullName);
            }
        }
        return new PrivateStagingTree(directories, files);
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateFrozenStagingTree(
        string stagingPath,
        IReadOnlyList<ExpectedFile> expectedFiles)
    {
        DirectoryInfo root = new(stagingPath);
        ValidateImmutableStagedDirectory(root);

        Dictionary<string, FileInfo> files = new(StringComparer.Ordinal);
        Stack<DirectoryInfo> pending = new();
        pending.Push(root);
        int directoryCount = 0;
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            ValidateImmutableStagedDirectory(directory);
            if (++directoryCount > MaximumDirectoryCount)
            {
                throw Failure(
                    VerifiedReleaseStagingFailureCode.StagingFreezeFailed,
                    "The frozen staging tree exceeds its directory bound.");
            }
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        VerifiedReleaseStagingFailureCode.StagingFreezeFailed,
                        "The frozen staging tree contains an unsafe link.");
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
                        VerifiedReleaseStagingFailureCode.StagingFreezeFailed,
                        "The frozen staging tree contains a non-regular entry.");
                }
                string relative = RelativePath(stagingPath, file.FullName);
                files.Add(relative, file);
            }
        }

        if (files.Count != expectedFiles.Count)
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.StagingFreezeFailed,
                "The frozen staging tree does not contain the exact planned files.");
        }
        foreach (ExpectedFile expected in expectedFiles)
        {
            if (!files.TryGetValue(expected.RelativePath, out FileInfo? file))
            {
                throw Failure(
                    VerifiedReleaseStagingFailureCode.StagingFreezeFailed,
                    "The frozen staging tree is missing a planned file.");
            }
            ValidateImmutableStagedFile(file, expected);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutableStagedFile(
        FileInfo file,
        ExpectedFile expected)
    {
        file.Refresh();
        if (!file.Exists || file.Length != expected.Length ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.LinkTarget is not null ||
            (File.GetUnixFileMode(file.FullName) & AnyWritableUnixModes) != 0)
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.StagingFreezeFailed,
                "A frozen staged file is unsafe or has the wrong length.");
        }

        byte[] digest = HashFile(file.FullName, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(
                digest,
                expected.Sha256))
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.IntegrityMismatch,
                "A frozen staged file does not match its verified digest.");
        }
    }

    private static byte[] HashFile(string path, long expectedLength)
    {
        using FileStream stream = OpenSource(path);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long bytesRead = 0;
        try
        {
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                bytesRead = checked(bytesRead + read);
                if (bytesRead > expectedLength)
                {
                    throw Failure(
                        VerifiedReleaseStagingFailureCode.IntegrityMismatch,
                        "A staged file exceeds its verified length.");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (bytesRead != expectedLength)
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.IntegrityMismatch,
                "A staged file does not match its verified length.");
        }
        return hash.GetHashAndReset();
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateBundleRootStillSafe(string rootPath)
    {
        ValidateImmutableSourceDirectory(new DirectoryInfo(rootPath));
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutableSourceDirectory(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            (File.GetUnixFileMode(directory.FullName) & AnyWritableUnixModes) != 0)
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.UnsafeBundle,
                "Verified staging requires immutable regular source directories.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static SourceFile ValidateImmutableSourceFile(FileInfo file)
    {
        file.Refresh();
        if (!file.Exists || file.Length < 1 ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.LinkTarget is not null ||
            (File.GetUnixFileMode(file.FullName) & AnyWritableUnixModes) != 0)
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.UnsafeBundle,
                "Verified staging requires immutable regular source files.");
        }
        return new SourceFile(
            file.FullName,
            file.Length,
            file.LastWriteTimeUtc);
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateSourceUnchanged(
        SourceFile source,
        long streamLength)
    {
        SourceFile after = ValidateImmutableSourceFile(new FileInfo(source.Path));
        if (streamLength != source.Length ||
            after.Length != source.Length ||
            after.LastWriteTimeUtc != source.LastWriteTimeUtc)
        {
            throw Changed();
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateExistingDirectory(
        string path,
        bool privateDirectory,
        VerifiedReleaseStagingFailureCode failureCode,
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
        if ((mode & ForbiddenWritableUnixModes) != 0 ||
            privateDirectory &&
            (mode &
                (UnixFileMode.GroupRead |
                 UnixFileMode.GroupExecute |
                 UnixFileMode.OtherRead |
                 UnixFileMode.OtherExecute)) != 0)
        {
            throw Failure(failureCode, message);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutableStagedDirectory(
        DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            (File.GetUnixFileMode(directory.FullName) & AnyWritableUnixModes) != 0)
        {
            throw Failure(
                VerifiedReleaseStagingFailureCode.StagingFreezeFailed,
                "The private staging tree is not immutable.");
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
        foreach ((string relativePath, SourceFile source) in first)
        {
            if (!second.TryGetValue(relativePath, out SourceFile? candidate) ||
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

    [SupportedOSPlatform("linux")]
    private static bool TryCleanup(string stagingPath)
    {
        if (string.IsNullOrEmpty(stagingPath) || !PathEntryExists(stagingPath))
        {
            return true;
        }

        try
        {
            PrivateStagingTree tree = CollectPrivateStagingTree(
                stagingPath,
                VerifiedReleaseStagingFailureCode.CleanupFailed,
                "The private staging tree is unsafe to clean up.");
            DirectoryInfo root = new(stagingPath);
            root.Refresh();
            if (!root.Exists || root.LinkTarget is not null ||
                (root.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            File.SetUnixFileMode(
                stagingPath,
                PrivateWritableDirectoryMode);
            foreach (string directoryPath in tree.Directories)
            {
                DirectoryInfo directory = new(directoryPath);
                directory.Refresh();
                if (!directory.Exists || directory.LinkTarget is not null ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
                File.SetUnixFileMode(
                    directoryPath,
                    PrivateWritableDirectoryMode);
            }

            foreach (string filePath in tree.Files)
            {
                FileInfo file = new(filePath);
                file.Refresh();
                if (!file.Exists || file.LinkTarget is not null ||
                    (file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
                File.SetUnixFileMode(filePath, PrivateWritableFileMode);
                File.Delete(filePath);
            }
            foreach (string directoryPath in tree.Directories
                         .OrderByDescending(path => path.Length))
            {
                DirectoryInfo directory = new(directoryPath);
                directory.Refresh();
                if (!directory.Exists || directory.LinkTarget is not null ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
                Directory.Delete(directoryPath, recursive: false);
            }

            root.Refresh();
            if (!root.Exists || root.LinkTarget is not null ||
                (root.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            Directory.Delete(stagingPath, recursive: false);
            return !PathEntryExists(stagingPath);
        }
        catch (StagingException)
        {
            return false;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static FileStream OpenSource(string path) =>
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

    private static bool PathEntryExists(string path)
    {
        FileSystemInfo info = new DirectoryInfo(path);
        info.Refresh();
        return Directory.Exists(path) ||
            File.Exists(path) ||
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

    private static bool PathEquals(string? left, string right) =>
        string.Equals(
            left,
            right,
            StringComparison.Ordinal);

    private static Func<CancellationToken, Task<ReleaseStatusReadResult>>
        CreateStatusReader(ReleaseInstallationStatusReader statusReader)
    {
        ArgumentNullException.ThrowIfNull(statusReader);
        return statusReader.ReadAsync;
    }

    private static StagingException Changed() =>
        Failure(
            VerifiedReleaseStagingFailureCode.SourceChanged,
            "A verified source file changed while it was staged.");

    private static StagingException Failure(
        VerifiedReleaseStagingFailureCode failureCode,
        string message) =>
        new(failureCode, message);

    private sealed record SourceFile(
        string Path,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed record ExpectedFile(
        string RelativePath,
        long Length,
        byte[] Sha256);

    private sealed record PrivateStagingTree(
        IReadOnlyList<string> Directories,
        IReadOnlyList<string> Files);

    private sealed class StagingException(
        VerifiedReleaseStagingFailureCode failureCode,
        string message) : Exception(message)
    {
        internal VerifiedReleaseStagingFailureCode FailureCode { get; } =
            failureCode;
    }
}
