using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum GitHubReleaseBundleDownloadFailureCode
{
    None = 0,
    UnsupportedPlatform = 1,
    InvalidInstallationPaths = 2,
    UnsafeStateDirectory = 3,
    DownloadRootUnavailable = 4,
    SourceRejected = 5,
    ExistingBundleUnsafe = 6,
    AtomicPublishFailed = 7,
    PersistenceRequiresReconciliation = 8,
    CleanupFailed = 9
}

public sealed record GitHubReleaseBundleDownloadDiagnostics(
    bool Registered,
    bool GitHubSourceRegistered,
    bool NetworkReadRegistered,
    bool LocalSignedVerificationRegistered,
    bool InstallationPathBindingRegistered,
    bool PrivateDownloadRootRegistered,
    bool SameParentTemporaryBundleRegistered,
    bool AtomicDirectoryPublishRegistered,
    bool ExistingBundleVerificationRegistered,
    bool PersistentDownloadRegistered,
    bool ArchiveExtractionRegistered,
    bool StagingRegistered,
    bool InstallationRegistered,
    bool ActivationRegistered,
    bool RollbackRegistered,
    bool MigrationRegistered,
    bool ServiceControlRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record GitHubReleaseBundleDownloadConsoleDiagnostics(
    bool Registered,
    bool DownloadServiceRegistered,
    bool CliCallerRegistered,
    bool PersistentDownloadRegistered,
    bool ArchiveExtractionRegistered,
    bool StagingRegistered,
    bool InstallationRegistered,
    bool ActivationRegistered,
    bool RollbackRegistered,
    bool MigrationRegistered,
    bool ServiceControlRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record GitHubReleaseBundleDownloadReport(
    int ReportVersion,
    string Command,
    bool Succeeded,
    int ExitCode,
    GitHubReleaseBundleDownloadFailureCode FailureCode,
    GitHubReleaseBundleFailureCode SourceFailureCode,
    string Message,
    int ExaminedReleaseCount,
    int DownloadedAssetCount,
    long DownloadedBytes,
    bool AlreadyPresent,
    bool BundlePersisted,
    bool ReconciliationRequired,
    ReleaseManifestVerificationReport? Verification);

internal sealed record GitHubReleaseBundleDownloadResult(
    GitHubReleaseBundleDownloadFailureCode FailureCode,
    GitHubReleaseBundleFailureCode SourceFailureCode,
    string Message,
    int ExaminedReleaseCount,
    int DownloadedAssetCount,
    long DownloadedBytes,
    bool AlreadyPresent,
    bool BundlePersisted,
    bool ReconciliationRequired,
    ReleaseManifestVerificationReport? Verification)
{
    internal bool Succeeded =>
        FailureCode == GitHubReleaseBundleDownloadFailureCode.None;

    internal static GitHubReleaseBundleDownloadResult Success(
        GitHubReleaseBundleCheckResult source,
        bool alreadyPresent) =>
        new(
            GitHubReleaseBundleDownloadFailureCode.None,
            GitHubReleaseBundleFailureCode.None,
            alreadyPresent
                ? "The exact verified GitHub release bundle is already present in the persistent download inventory."
                : "The exact verified GitHub release bundle was atomically persisted without extraction or installation.",
            source.ExaminedReleaseCount,
            source.DownloadedAssetCount,
            source.DownloadedBytes,
            alreadyPresent,
            BundlePersisted: !alreadyPresent,
            ReconciliationRequired: false,
            source.Verification);

    internal static GitHubReleaseBundleDownloadResult Failure(
        GitHubReleaseBundleDownloadFailureCode failureCode,
        string message,
        GitHubReleaseBundleCheckResult? source = null,
        bool reconciliationRequired = false) =>
        new(
            failureCode,
            source?.FailureCode ?? GitHubReleaseBundleFailureCode.None,
            message,
            source?.ExaminedReleaseCount ?? 0,
            source?.DownloadedAssetCount ?? 0,
            source?.DownloadedBytes ?? 0,
            AlreadyPresent: false,
            BundlePersisted: false,
            reconciliationRequired,
            source?.Verification);
}

/// <summary>
/// Persists one fully downloaded and signed-verifier-approved GitHub release
/// bundle under the installation state directory. The source bundle is created
/// in the same parent directory and atomically renamed to one absent canonical
/// target. Existing targets are accepted only when they independently pass the
/// same signed-bundle verifier. This boundary performs no extraction, staging,
/// installation, current-pointer mutation, activation, rollback, migration,
/// service control, Admin/browser action, radio/watchdog command, lease change,
/// keying, or transmit operation.
/// </summary>
public sealed class GitHubReleaseBundleDownloadService
{
    private const UnixFileMode ForbiddenSharedWritableUnixModes =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode PrivateWritableDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;

    private readonly GitHubReleaseBundleSource m_source;
    private readonly LocalOfflineReleaseBundleVerificationService
        m_bundleVerificationService;
    private readonly InstallationPaths m_paths;
    private readonly Action<string, string> m_directoryMove;

    public GitHubReleaseBundleDownloadService(
        GitHubReleaseBundleSource source,
        LocalOfflineReleaseBundleVerificationService bundleVerificationService,
        InstallationPaths paths)
        : this(source, bundleVerificationService, paths, Directory.Move)
    {
    }

    internal GitHubReleaseBundleDownloadService(
        GitHubReleaseBundleSource source,
        LocalOfflineReleaseBundleVerificationService bundleVerificationService,
        InstallationPaths paths,
        Action<string, string> directoryMove)
    {
        m_source = source ?? throw new ArgumentNullException(nameof(source));
        m_bundleVerificationService = bundleVerificationService ??
            throw new ArgumentNullException(nameof(bundleVerificationService));
        m_paths = paths ?? throw new ArgumentNullException(nameof(paths));
        m_directoryMove = directoryMove ??
            throw new ArgumentNullException(nameof(directoryMove));
        Snapshot = new GitHubReleaseBundleDownloadDiagnostics(
            Registered: true,
            GitHubSourceRegistered: true,
            NetworkReadRegistered: true,
            LocalSignedVerificationRegistered: true,
            InstallationPathBindingRegistered: true,
            PrivateDownloadRootRegistered: true,
            SameParentTemporaryBundleRegistered: true,
            AtomicDirectoryPublishRegistered: true,
            ExistingBundleVerificationRegistered: true,
            PersistentDownloadRegistered: true,
            ArchiveExtractionRegistered: false,
            StagingRegistered: false,
            InstallationRegistered: false,
            ActivationRegistered: false,
            RollbackRegistered: false,
            MigrationRegistered: false,
            ServiceControlRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public GitHubReleaseBundleDownloadDiagnostics Snapshot { get; }

    internal async Task<GitHubReleaseBundleDownloadResult> DownloadAsync(
        ReleaseManifestVerificationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsLinux())
        {
            return GitHubReleaseBundleDownloadResult.Failure(
                GitHubReleaseBundleDownloadFailureCode.UnsupportedPlatform,
                "Persistent GitHub release download requires a supported Linux runtime.");
        }
        if (!m_source.Snapshot.Enabled)
        {
            return GitHubReleaseBundleDownloadResult.Failure(
                GitHubReleaseBundleDownloadFailureCode.SourceRejected,
                "The GitHub release source is disabled.",
                GitHubReleaseBundleCheckResult.Failure(
                    GitHubReleaseBundleFailureCode.SourceDisabled,
                    "GitHub release bundle checking is disabled."));
        }
        if (!m_bundleVerificationService.LocalVerificationAvailable)
        {
            return GitHubReleaseBundleDownloadResult.Failure(
                GitHubReleaseBundleDownloadFailureCode.SourceRejected,
                "Signed release verification trust is unavailable.",
                GitHubReleaseBundleCheckResult.Failure(
                    GitHubReleaseBundleFailureCode.VerificationTrustUnavailable,
                    "GitHub release assets cannot be read because signed release verification trust is unavailable."));
        }

        string downloadRoot;
        try
        {
            InstallationPaths.Validate(m_paths);
            downloadRoot = EnsureDownloadRoot(m_paths);
        }
        catch (DownloadPersistenceException exception)
        {
            return GitHubReleaseBundleDownloadResult.Failure(
                exception.FailureCode,
                exception.Message);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return GitHubReleaseBundleDownloadResult.Failure(
                GitHubReleaseBundleDownloadFailureCode.InvalidInstallationPaths,
                "The persistent release download paths could not be validated safely.");
        }

        GitHubReleaseBundleAcquisition acquisition =
            await m_source.AcquireVerifiedBundleAsync(
                context,
                cancellationToken).ConfigureAwait(false);
        if (!acquisition.Succeeded)
        {
            return GitHubReleaseBundleDownloadResult.Failure(
                GitHubReleaseBundleDownloadFailureCode.SourceRejected,
                "The GitHub release source did not produce a verified bundle for persistence.",
                acquisition.Result);
        }

        string sourcePath = acquisition.BundleDirectory;
        string targetPath;
        try
        {
            targetPath = CreateTargetPath(
                downloadRoot,
                acquisition.Result.Verification ??
                    throw Failure(
                        GitHubReleaseBundleDownloadFailureCode.SourceRejected,
                        "The verified GitHub release acquisition omitted its trusted release summary."),
                context.Architecture);
        }
        catch (DownloadPersistenceException exception)
        {
            return CleanupBeforeFailure(
                sourcePath,
                exception.FailureCode,
                exception.Message,
                acquisition.Result);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return CleanupBeforeFailure(
                sourcePath,
                GitHubReleaseBundleDownloadFailureCode.InvalidInstallationPaths,
                "The persistent release download target could not be derived safely.",
                acquisition.Result);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _ = GitHubReleaseBundleSource.TryDeleteAcquiredBundle(sourcePath);
            cancellationToken.ThrowIfCancellationRequested();
        }
        try
        {
            if (PathEntryExists(targetPath))
            {
                if (!IsExactVerifiedBundle(
                        targetPath,
                        context,
                        acquisition.Result.Verification!))
                {
                    return CleanupBeforeFailure(
                        sourcePath,
                        GitHubReleaseBundleDownloadFailureCode.ExistingBundleUnsafe,
                        "The persistent download target already exists but is not the exact verified release bundle.",
                        acquisition.Result);
                }
                if (!GitHubReleaseBundleSource.TryDeleteAcquiredBundle(sourcePath))
                {
                    return GitHubReleaseBundleDownloadResult.Failure(
                        GitHubReleaseBundleDownloadFailureCode.CleanupFailed,
                        "The duplicate temporary verified bundle could not be removed safely.",
                        acquisition.Result);
                }
                return GitHubReleaseBundleDownloadResult.Success(
                    acquisition.Result,
                    alreadyPresent: true);
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or CryptographicException or ArgumentException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return CleanupBeforeFailure(
                sourcePath,
                GitHubReleaseBundleDownloadFailureCode.ExistingBundleUnsafe,
                "The persistent download target could not be inspected safely.",
                acquisition.Result);
        }

        bool moveReturned = false;
        try
        {
            m_directoryMove(sourcePath, targetPath);
            moveReturned = true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            try
            {
                return ResolveMoveFailure(
                    sourcePath,
                    targetPath,
                    context,
                    acquisition.Result);
            }
            catch (Exception reconciliationException)
                when (reconciliationException is IOException or
                    UnauthorizedAccessException or SecurityException or
                    CryptographicException or ArgumentException or
                    NotSupportedException or PathTooLongException or
                    OverflowException)
            {
                return GitHubReleaseBundleDownloadResult.Failure(
                    GitHubReleaseBundleDownloadFailureCode.PersistenceRequiresReconciliation,
                    "The persistent release download rename outcome could not be inspected safely and requires local reconciliation.",
                    acquisition.Result,
                    reconciliationRequired: true);
            }
        }

        if (!moveReturned)
        {
            throw new InvalidOperationException(
                "The persistent release bundle rename did not return or throw.");
        }
        try
        {
            if (PathEntryExists(sourcePath) || !PathEntryExists(targetPath))
            {
                return GitHubReleaseBundleDownloadResult.Failure(
                    GitHubReleaseBundleDownloadFailureCode.PersistenceRequiresReconciliation,
                    "The atomic persistent-download paths are ambiguous and require local reconciliation.",
                    acquisition.Result,
                    reconciliationRequired: true);
            }
            if (!IsExactVerifiedBundle(
                    targetPath,
                    context,
                    acquisition.Result.Verification!))
            {
                return GitHubReleaseBundleDownloadResult.Failure(
                    GitHubReleaseBundleDownloadFailureCode.PersistenceRequiresReconciliation,
                    "The atomically persisted bundle no longer passes exact signed verification and requires local reconciliation.",
                    acquisition.Result,
                    reconciliationRequired: true);
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or CryptographicException or ArgumentException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return GitHubReleaseBundleDownloadResult.Failure(
                GitHubReleaseBundleDownloadFailureCode.PersistenceRequiresReconciliation,
                "The atomically persisted bundle could not be inspected safely and requires local reconciliation.",
                acquisition.Result,
                reconciliationRequired: true);
        }

        return GitHubReleaseBundleDownloadResult.Success(
            acquisition.Result,
            alreadyPresent: false);
    }

    private GitHubReleaseBundleDownloadResult ResolveMoveFailure(
        string sourcePath,
        string targetPath,
        ReleaseManifestVerificationContext context,
        GitHubReleaseBundleCheckResult source)
    {
        bool sourcePresent = PathEntryExists(sourcePath);
        bool targetPresent = PathEntryExists(targetPath);

        if (sourcePresent && targetPresent)
        {
            bool exactTarget = IsExactVerifiedBundle(
                targetPath,
                context,
                source.Verification!);
            if (!GitHubReleaseBundleSource.TryDeleteAcquiredBundle(sourcePath))
            {
                return GitHubReleaseBundleDownloadResult.Failure(
                    GitHubReleaseBundleDownloadFailureCode.CleanupFailed,
                    "The temporary verified bundle could not be removed after a concurrent target appeared.",
                    source);
            }
            return exactTarget
                ? GitHubReleaseBundleDownloadResult.Success(
                    source,
                    alreadyPresent: true)
                : GitHubReleaseBundleDownloadResult.Failure(
                    GitHubReleaseBundleDownloadFailureCode.ExistingBundleUnsafe,
                    "A concurrent persistent download target appeared but is not the exact verified release bundle.",
                    source);
        }

        if (sourcePresent && !targetPresent)
        {
            return CleanupBeforeFailure(
                sourcePath,
                GitHubReleaseBundleDownloadFailureCode.AtomicPublishFailed,
                "The verified bundle could not be atomically persisted into the download inventory.",
                source);
        }

        if (!sourcePresent && targetPresent &&
            IsExactVerifiedBundle(targetPath, context, source.Verification!))
        {
            return GitHubReleaseBundleDownloadResult.Success(
                source,
                alreadyPresent: false);
        }

        return GitHubReleaseBundleDownloadResult.Failure(
            GitHubReleaseBundleDownloadFailureCode.PersistenceRequiresReconciliation,
            "The persistent release download rename outcome is ambiguous and requires local reconciliation.",
            source,
            reconciliationRequired: true);
    }

    private GitHubReleaseBundleDownloadResult CleanupBeforeFailure(
        string sourcePath,
        GitHubReleaseBundleDownloadFailureCode failureCode,
        string message,
        GitHubReleaseBundleCheckResult source)
    {
        if (!GitHubReleaseBundleSource.TryDeleteAcquiredBundle(sourcePath))
        {
            return GitHubReleaseBundleDownloadResult.Failure(
                GitHubReleaseBundleDownloadFailureCode.CleanupFailed,
                "The temporary verified GitHub release bundle could not be removed safely.",
                source);
        }
        return GitHubReleaseBundleDownloadResult.Failure(
            failureCode,
            message,
            source);
    }

    private bool IsExactVerifiedBundle(
        string path,
        ReleaseManifestVerificationContext context,
        ReleaseManifestVerificationReport expected)
    {
        LocalOfflineReleaseBundleVerificationReport verification =
            m_bundleVerificationService.VerifyDirectory(path, context);
        return verification.Succeeded &&
            verification.Verification is not null &&
            string.Equals(
                verification.Verification.ReleaseIdentity,
                expected.ReleaseIdentity,
                StringComparison.Ordinal) &&
            string.Equals(
                verification.Verification.Version,
                expected.Version,
                StringComparison.Ordinal) &&
            verification.Verification.Architecture == expected.Architecture &&
            verification.Verification.Channel == expected.Channel;
    }

    [SupportedOSPlatform("linux")]
    private static string EnsureDownloadRoot(InstallationPaths paths)
    {
        string stateDirectory = CanonicalPath(paths.StateDirectory);
        ValidateDirectory(
            stateDirectory,
            GitHubReleaseBundleDownloadFailureCode.UnsafeStateDirectory,
            "The configured installation state directory is unavailable or unsafe.");

        string downloadRoot = CanonicalPath(paths.ReleaseDownloadDirectory);
        string expected = Path.Combine(stateDirectory, "release-downloads");
        if (!string.Equals(downloadRoot, expected, StringComparison.Ordinal))
        {
            throw Failure(
                GitHubReleaseBundleDownloadFailureCode.InvalidInstallationPaths,
                "The persistent release download root must be the exact installation-state child.");
        }

        if (!PathEntryExists(downloadRoot))
        {
            Directory.CreateDirectory(downloadRoot);
            File.SetUnixFileMode(downloadRoot, PrivateWritableDirectoryMode);
        }
        ValidateDirectory(
            downloadRoot,
            GitHubReleaseBundleDownloadFailureCode.DownloadRootUnavailable,
            "The persistent release download root is unavailable or unsafe.");
        return downloadRoot;
    }

    private static string CreateTargetPath(
        string downloadRoot,
        ReleaseManifestVerificationReport verification,
        ReleaseManifestArchitecture architecture)
    {
        string releaseIdentity = InstallationReleaseIdentity.Parse(
            verification.ReleaseIdentity);
        string architectureToken = architecture switch
        {
            ReleaseManifestArchitecture.LinuxX64 => "linux-x64",
            ReleaseManifestArchitecture.LinuxArm64 => "linux-arm64",
            _ => throw Failure(
                GitHubReleaseBundleDownloadFailureCode.SourceRejected,
                "Persistent GitHub release download supports only linux-x64 and linux-arm64.")
        };
        string name = $"{releaseIdentity}-{architectureToken}";
        string target = CanonicalPath(Path.Combine(downloadRoot, name));
        if (!string.Equals(
                Path.GetDirectoryName(target),
                downloadRoot,
                StringComparison.Ordinal))
        {
            throw Failure(
                GitHubReleaseBundleDownloadFailureCode.InvalidInstallationPaths,
                "The persistent release download target escaped its configured root.");
        }
        return target;
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateDirectory(
        string path,
        GitHubReleaseBundleDownloadFailureCode failureCode,
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

    private static string CanonicalPath(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(path, fullPath, StringComparison.Ordinal))
        {
            throw Failure(
                GitHubReleaseBundleDownloadFailureCode.InvalidInstallationPaths,
                "Persistent release download requires canonical absolute installation paths.");
        }
        return fullPath;
    }

    private static bool PathEntryExists(string path) =>
        Directory.Exists(path) || File.Exists(path) ||
        new DirectoryInfo(path).LinkTarget is not null ||
        new FileInfo(path).LinkTarget is not null;

    private static DownloadPersistenceException Failure(
        GitHubReleaseBundleDownloadFailureCode failureCode,
        string message) =>
        new(failureCode, message);

    private sealed class DownloadPersistenceException(
        GitHubReleaseBundleDownloadFailureCode failureCode,
        string message) : Exception(message)
    {
        internal GitHubReleaseBundleDownloadFailureCode FailureCode { get; } =
            failureCode;
    }
}

/// <summary>
/// CLI-only adapter for the persistent verified GitHub download boundary. It
/// emits a path-redacted report and returns before any web/setup host, service,
/// radio, watchdog, or route composition.
/// </summary>
public sealed class GitHubReleaseBundleDownloadConsole
{
    public const int SuccessExitCode = 0;
    public const int DownloadFailedExitCode = 2;

    private const int CurrentReportVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly GitHubReleaseBundleDownloadService m_service;
    private readonly Func<ReleaseManifestArchitecture> m_architectureResolver;

    public GitHubReleaseBundleDownloadConsole(
        GitHubReleaseBundleDownloadService service)
        : this(service, ResolveCurrentArchitecture)
    {
    }

    internal GitHubReleaseBundleDownloadConsole(
        GitHubReleaseBundleDownloadService service,
        Func<ReleaseManifestArchitecture> architectureResolver)
    {
        m_service = service ?? throw new ArgumentNullException(nameof(service));
        m_architectureResolver = architectureResolver ??
            throw new ArgumentNullException(nameof(architectureResolver));
        Snapshot = new GitHubReleaseBundleDownloadConsoleDiagnostics(
            Registered: true,
            DownloadServiceRegistered: true,
            CliCallerRegistered: true,
            PersistentDownloadRegistered: true,
            ArchiveExtractionRegistered: false,
            StagingRegistered: false,
            InstallationRegistered: false,
            ActivationRegistered: false,
            RollbackRegistered: false,
            MigrationRegistered: false,
            ServiceControlRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public GitHubReleaseBundleDownloadConsoleDiagnostics Snapshot { get; }

    public async Task<int> ExecuteAsync(
        ReleaseUpdateConsoleCommandLine commandLine,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        if (commandLine.Command !=
            ReleaseUpdateConsoleCommandKind.DownloadGitHubRelease)
        {
            throw new InvalidOperationException(
                "The GitHub release download console requires its exact command.");
        }

        ReleaseManifestVerificationContext context = new(
            m_architectureResolver(),
            commandLine.UpdateChannel ??
                throw new InvalidOperationException(
                    "The GitHub release download requires an update channel."),
            commandLine.PinnedReleaseIdentity,
            commandLine.InstalledVersion,
            commandLine.ConfigurationSchemaVersion ??
                throw new InvalidOperationException(
                    "The GitHub release download requires a configuration schema version."),
            commandLine.ProtocolVersion ??
                throw new InvalidOperationException(
                    "The GitHub release download requires a protocol version."));

        GitHubReleaseBundleDownloadResult result =
            await m_service.DownloadAsync(context, cancellationToken)
                .ConfigureAwait(false);
        int exitCode = result.Succeeded
            ? SuccessExitCode
            : DownloadFailedExitCode;
        GitHubReleaseBundleDownloadReport report = new(
            CurrentReportVersion,
            "downloadGitHubRelease",
            result.Succeeded,
            exitCode,
            result.FailureCode,
            result.SourceFailureCode,
            result.Message,
            result.ExaminedReleaseCount,
            result.DownloadedAssetCount,
            result.DownloadedBytes,
            result.AlreadyPresent,
            result.BundlePersisted,
            result.ReconciliationRequired,
            result.Verification);
        await output.WriteLineAsync(
            JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return exitCode;
    }

    private static ReleaseManifestArchitecture ResolveCurrentArchitecture() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => ReleaseManifestArchitecture.LinuxX64,
            Architecture.Arm64 => ReleaseManifestArchitecture.LinuxArm64,
            _ => ReleaseManifestArchitecture.Unknown
        };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}
