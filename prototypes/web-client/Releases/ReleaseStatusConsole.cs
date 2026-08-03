using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum ReleaseStatusFailureCode
{
    None = 0,
    SetupStateMissing = 1,
    SetupStateInvalid = 2,
    SetupPathsIncomplete = 3,
    SetupPathsMismatch = 4,
    UnsafeReleaseDirectory = 5,
    UnsafeReleaseEntry = 6,
    ReleaseInventoryTooLarge = 7,
    UnsafeCurrentPointer = 8,
    StatusReadFailed = 9
}

public sealed record ReleaseStatusReadResult(
    bool Succeeded,
    ReleaseStatusFailureCode FailureCode,
    string Message,
    int? SetupSchemaVersion,
    long? SetupRevision,
    bool SetupComplete,
    InstallationSetupLockMode? SetupLockMode,
    InstallationSetupStep? LastCompletedStep,
    InstallationUpdateChannel? UpdateChannel,
    string PinnedReleaseIdentity,
    bool InstallTransmitSupport,
    bool ReleaseDirectoryPresent,
    int AvailableReleaseCount,
    IReadOnlyList<string> AvailableReleaseIdentities,
    bool CurrentPointerPresent,
    string ActiveReleaseIdentity,
    bool RollbackCandidateKnown)
{
    internal static ReleaseStatusReadResult Failure(
        ReleaseStatusFailureCode failureCode,
        string message,
        InstallationSetupState? state = null) =>
        new(
            false,
            failureCode,
            message,
            state?.SchemaVersion,
            state?.Revision,
            state?.Lock.Mode == InstallationSetupLockMode.Complete,
            state?.Lock.Mode,
            state?.LastCompletedStep,
            state?.UpdateChannel,
            state?.PinnedRelease ?? string.Empty,
            state?.InstallTransmitSupport ?? false,
            ReleaseDirectoryPresent: false,
            AvailableReleaseCount: 0,
            AvailableReleaseIdentities: [],
            CurrentPointerPresent: false,
            ActiveReleaseIdentity: string.Empty,
            RollbackCandidateKnown: false);

    internal static ReleaseStatusReadResult Success(
        InstallationSetupState state,
        bool releaseDirectoryPresent,
        IReadOnlyList<string> availableReleaseIdentities,
        bool currentPointerPresent,
        string activeReleaseIdentity) =>
        new(
            true,
            ReleaseStatusFailureCode.None,
            "The local release status was read successfully.",
            state.SchemaVersion,
            state.Revision,
            state.Lock.Mode == InstallationSetupLockMode.Complete,
            state.Lock.Mode,
            state.LastCompletedStep,
            state.UpdateChannel,
            state.PinnedRelease,
            state.InstallTransmitSupport,
            releaseDirectoryPresent,
            availableReleaseIdentities.Count,
            availableReleaseIdentities,
            currentPointerPresent,
            activeReleaseIdentity,
            RollbackCandidateKnown: false);
}

public sealed record ReleaseStatusConsoleDiagnostics(
    bool Registered,
    bool SetupStateReadRegistered,
    bool ReleaseInventoryReadRegistered,
    bool CurrentPointerReadRegistered,
    bool NetworkDownloadRegistered,
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

public sealed record ReleaseStatusConsoleReport(
    int ReportVersion,
    string Command,
    bool Succeeded,
    int ExitCode,
    ReleaseStatusFailureCode FailureCode,
    string Message,
    int? SetupSchemaVersion,
    long? SetupRevision,
    bool SetupComplete,
    InstallationSetupLockMode? SetupLockMode,
    InstallationSetupStep? LastCompletedStep,
    InstallationUpdateChannel? UpdateChannel,
    string PinnedReleaseIdentity,
    bool InstallTransmitSupport,
    bool ReleaseDirectoryPresent,
    int AvailableReleaseCount,
    IReadOnlyList<string> AvailableReleaseIdentities,
    bool CurrentPointerPresent,
    string ActiveReleaseIdentity,
    bool RollbackCandidateKnown);

/// <summary>
/// Reads only the persisted installation setup state, the direct immutable
/// release-directory inventory, and the sibling current symbolic link. It never
/// follows an unvalidated pointer and has no download, extraction, staging,
/// installation, activation, rollback, migration, service, Admin, browser,
/// radio, watchdog, command, lease, or transmit operation.
/// </summary>
public sealed class ReleaseInstallationStatusReader
{
    internal const int MaximumReleaseCount = 64;

    private const UnixFileMode ForbiddenWritableUnixModes =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private readonly InstallationSetupStore m_setupStore;
    private readonly InstallationPaths m_paths;

    public ReleaseInstallationStatusReader(
        InstallationSetupStore setupStore,
        InstallationPaths paths)
    {
        m_setupStore = setupStore ??
            throw new ArgumentNullException(nameof(setupStore));
        m_paths = paths ?? throw new ArgumentNullException(nameof(paths));
        InstallationPaths.Validate(m_paths);
    }

    public async Task<ReleaseStatusReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        InstallationSetupState state;
        try
        {
            state = await m_setupStore.LoadAsync(cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ReleaseStatusReadResult.Failure(
                ReleaseStatusFailureCode.SetupStateMissing,
                "Installation setup state is not available.");
        }
        catch (InvalidOperationException)
        {
            return ReleaseStatusReadResult.Failure(
                ReleaseStatusFailureCode.SetupStateInvalid,
                "Installation setup state is invalid.");
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException or PathTooLongException)
        {
            return ReleaseStatusReadResult.Failure(
                ReleaseStatusFailureCode.StatusReadFailed,
                "Installation setup state could not be read.");
        }

        if (state.LastCompletedStep < InstallationSetupStep.Paths ||
            state.Paths is null)
        {
            return ReleaseStatusReadResult.Failure(
                ReleaseStatusFailureCode.SetupPathsIncomplete,
                "Installation paths have not been completed.",
                state);
        }
        if (!Equals(state.Paths, m_paths))
        {
            return ReleaseStatusReadResult.Failure(
                ReleaseStatusFailureCode.SetupPathsMismatch,
                "Resolved installation paths do not match persisted setup state.",
                state);
        }

        try
        {
            return ReadReleaseLayout(state);
        }
        catch (StatusReadException exception)
        {
            return ReleaseStatusReadResult.Failure(
                exception.FailureCode,
                exception.Message,
                state);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return ReleaseStatusReadResult.Failure(
                ReleaseStatusFailureCode.StatusReadFailed,
                "The local release layout could not be read.",
                state);
        }
    }

    private ReleaseStatusReadResult ReadReleaseLayout(
        InstallationSetupState state)
    {
        string releaseRootPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(m_paths.ReleaseDirectory));
        DirectoryInfo releaseRoot = new(releaseRootPath);
        releaseRoot.Refresh();

        List<string> identities = [];
        if (releaseRoot.Exists)
        {
            ValidateRegularDirectory(
                releaseRoot,
                ReleaseStatusFailureCode.UnsafeReleaseDirectory,
                "The configured release directory is unsafe.");

            FileSystemInfo[] entries = releaseRoot.GetFileSystemInfos();
            if (entries.Length > MaximumReleaseCount)
            {
                throw Failure(
                    ReleaseStatusFailureCode.ReleaseInventoryTooLarge,
                    "The local release inventory exceeds its bounded size.");
            }

            foreach (FileSystemInfo entry in entries)
            {
                entry.Refresh();
                if (entry is not DirectoryInfo releaseDirectory ||
                    (entry.Attributes & FileAttributes.Directory) == 0)
                {
                    throw Failure(
                        ReleaseStatusFailureCode.UnsafeReleaseEntry,
                        "The local release inventory contains a non-directory entry.");
                }
                ValidateRegularDirectory(
                    releaseDirectory,
                    ReleaseStatusFailureCode.UnsafeReleaseEntry,
                    "The local release inventory contains an unsafe directory.");

                string identity = RequireCanonicalReleaseIdentity(
                    entry.Name,
                    ReleaseStatusFailureCode.UnsafeReleaseEntry,
                    "The local release inventory contains a non-canonical identity.");
                identities.Add(identity);
            }
            identities.Sort(StringComparer.Ordinal);
        }

        string deploymentRoot = Path.GetDirectoryName(releaseRootPath) ??
            throw Failure(
                ReleaseStatusFailureCode.UnsafeReleaseDirectory,
                "The configured release directory has no deployment root.");
        string currentPath = Path.Combine(deploymentRoot, "current");
        DirectoryInfo current = new(currentPath);
        current.Refresh();
        string? linkTarget = current.LinkTarget;
        bool currentEntryExists =
            Directory.Exists(currentPath) || File.Exists(currentPath);

        if (!currentEntryExists && linkTarget is null)
        {
            return ReleaseStatusReadResult.Success(
                state,
                releaseRoot.Exists,
                identities,
                currentPointerPresent: false,
                activeReleaseIdentity: string.Empty);
        }
        if (linkTarget is null)
        {
            throw Failure(
                ReleaseStatusFailureCode.UnsafeCurrentPointer,
                "The current release entry is not a symbolic link.");
        }

        string targetPath = ResolveCanonicalLinkTarget(
            deploymentRoot,
            linkTarget);
        string? targetParent = Path.GetDirectoryName(targetPath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(targetParent, releaseRootPath, comparison))
        {
            throw Failure(
                ReleaseStatusFailureCode.UnsafeCurrentPointer,
                "The current release pointer leaves the configured release directory.");
        }

        string activeIdentity = RequireCanonicalReleaseIdentity(
            Path.GetFileName(targetPath),
            ReleaseStatusFailureCode.UnsafeCurrentPointer,
            "The current release pointer does not name a canonical release.");
        if (!identities.Contains(activeIdentity, StringComparer.Ordinal))
        {
            throw Failure(
                ReleaseStatusFailureCode.UnsafeCurrentPointer,
                "The current release pointer does not name an inventoried release.");
        }

        DirectoryInfo activeDirectory = new(targetPath);
        ValidateRegularDirectory(
            activeDirectory,
            ReleaseStatusFailureCode.UnsafeCurrentPointer,
            "The current release pointer target is unsafe.");

        return ReleaseStatusReadResult.Success(
            state,
            releaseRoot.Exists,
            identities,
            currentPointerPresent: true,
            activeIdentity);
    }

    private static string ResolveCanonicalLinkTarget(
        string deploymentRoot,
        string linkTarget)
    {
        if (string.IsNullOrWhiteSpace(linkTarget) ||
            !string.Equals(linkTarget, linkTarget.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                ReleaseStatusFailureCode.UnsafeCurrentPointer,
                "The current release pointer target is malformed.");
        }

        bool absolute = Path.IsPathFullyQualified(linkTarget);
        string resolved = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                absolute
                    ? linkTarget
                    : Path.Combine(deploymentRoot, linkTarget)));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string canonical = absolute
            ? resolved
            : Path.GetRelativePath(deploymentRoot, resolved);
        if (!string.Equals(linkTarget, canonical, comparison))
        {
            throw Failure(
                ReleaseStatusFailureCode.UnsafeCurrentPointer,
                "The current release pointer target is not canonical.");
        }
        return resolved;
    }

    private static string RequireCanonicalReleaseIdentity(
        string value,
        ReleaseStatusFailureCode failureCode,
        string message)
    {
        try
        {
            string identity = InstallationReleaseIdentity.Parse(value);
            if (string.Equals(identity, value, StringComparison.Ordinal))
            {
                return identity;
            }
        }
        catch (InvalidOperationException)
        {
        }
        throw Failure(failureCode, message);
    }

    private static void ValidateRegularDirectory(
        DirectoryInfo directory,
        ReleaseStatusFailureCode failureCode,
        string message)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null)
        {
            throw Failure(failureCode, message);
        }
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(directory.FullName) &
                ForbiddenWritableUnixModes) != 0)
        {
            throw Failure(failureCode, message);
        }
    }

    private static StatusReadException Failure(
        ReleaseStatusFailureCode failureCode,
        string message) =>
        new(failureCode, message);

    private sealed class StatusReadException(
        ReleaseStatusFailureCode failureCode,
        string message) : Exception(message)
    {
        public ReleaseStatusFailureCode FailureCode { get; } = failureCode;
    }
}

/// <summary>
/// Read-only release status CLI. It emits one redacted report and owns no
/// network, write, extraction, staging, installation, activation, rollback,
/// migration, service, Admin, browser, radio, watchdog, command, lease, or TX
/// method.
/// </summary>
public sealed class ReleaseStatusConsole
{
    public const int SuccessExitCode = 0;
    public const int StatusFailedExitCode = 2;

    private const int CurrentReportVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ReleaseInstallationStatusReader m_reader;

    public ReleaseStatusConsole(ReleaseInstallationStatusReader reader)
    {
        m_reader = reader ?? throw new ArgumentNullException(nameof(reader));
        Snapshot = new ReleaseStatusConsoleDiagnostics(
            Registered: true,
            SetupStateReadRegistered: true,
            ReleaseInventoryReadRegistered: true,
            CurrentPointerReadRegistered: true,
            NetworkDownloadRegistered: false,
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

    public ReleaseStatusConsoleDiagnostics Snapshot { get; }

    public async Task<int> ExecuteAsync(
        ReleaseUpdateConsoleCommandLine commandLine,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(output);
        if (commandLine.Command != ReleaseUpdateConsoleCommandKind.Status)
        {
            throw new InvalidOperationException(
                "The release status console requires its exact command.");
        }

        ReleaseStatusReadResult status =
            await m_reader.ReadAsync(cancellationToken);
        int exitCode = status.Succeeded
            ? SuccessExitCode
            : StatusFailedExitCode;
        ReleaseStatusConsoleReport report = new(
            CurrentReportVersion,
            "release-status",
            status.Succeeded,
            exitCode,
            status.FailureCode,
            status.Message,
            status.SetupSchemaVersion,
            status.SetupRevision,
            status.SetupComplete,
            status.SetupLockMode,
            status.LastCompletedStep,
            status.UpdateChannel,
            status.PinnedReleaseIdentity,
            status.InstallTransmitSupport,
            status.ReleaseDirectoryPresent,
            status.AvailableReleaseCount,
            status.AvailableReleaseIdentities,
            status.CurrentPointerPresent,
            status.ActiveReleaseIdentity,
            status.RollbackCandidateKnown);
        await output.WriteLineAsync(
            JsonSerializer.Serialize(report, JsonOptions));
        return exitCode;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.Strict
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}
