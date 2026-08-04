using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationMigrationExecutionFailureCode
{
    None = 0,
    UnsupportedPlatform = 1,
    RunnerInvocationNotEligible = 2,
    RunnerInvocationUnavailable = 3,
    RunnerInvocationMismatch = 4,
    StatusUnavailable = 5,
    StatusMismatch = 6,
    MigrationAlreadyPresent = 7,
    StagingAlreadyPresent = 8,
    ReconciliationRequired = 9,
    UnsafeMigrationLayout = 10,
    BackupSourceInvalid = 11,
    BackupSourceChanged = 12,
    StagedCopyFailed = 13,
    RunnerArtifactChanged = 14,
    RunnerStartFailed = 15,
    RunnerTimedOut = 16,
    RunnerOutputTooLarge = 17,
    RunnerProcessFailed = 18,
    RunnerResponseInvalid = 19,
    RunnerExecutionRejected = 20,
    MigrationManifestFailed = 21,
    StagedTreeInvalid = 22,
    AtomicPublishFailed = 23,
    PublishedStateRequiresReconciliation = 24,
    CleanupFailed = 25
}

public sealed record VerifiedReleaseActivationMigrationExecutionReport(
    bool Succeeded,
    VerifiedReleaseActivationMigrationExecutionFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    ReleaseMigrationKind? MigrationKind,
    int? FromConfigurationSchemaVersion,
    int? ToConfigurationSchemaVersion,
    bool MigrationRequired,
    bool NoOpMigrationResolved,
    bool ExactRunnerInvocationBound,
    bool ReleaseStatusStable,
    bool ImmutableBackupValidated,
    bool PrivateStagingCreated,
    bool StagedCopyCompleted,
    bool RunnerArtifactRevalidated,
    bool RunnerInvoked,
    bool MigrationExecutionPerformed,
    bool MigrationManifestWritten,
    bool StagingTreeImmutable,
    bool AtomicPublicationCompleted,
    bool PublishedTreeValidated,
    int DirectoryCount,
    int FileCount,
    long MigrationBytes,
    bool MigrationReady,
    bool ReconciliationRequired,
    bool CurrentPointerChanged,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationMigrationExecution? Execution { get; init; }

    internal static VerifiedReleaseActivationMigrationExecutionReport Failure(
        VerifiedReleaseActivationMigrationExecutionFailureCode failureCode,
        string message,
        VerifiedReleaseActivationMigrationRunnerInvocationReport? invocation = null,
        bool exactInvocationBound = false,
        bool releaseStatusStable = false,
        bool immutableBackupValidated = false,
        bool privateStagingCreated = false,
        bool stagedCopyCompleted = false,
        bool runnerArtifactRevalidated = false,
        bool runnerInvoked = false,
        bool migrationExecutionPerformed = false,
        bool migrationManifestWritten = false,
        bool stagingTreeImmutable = false,
        bool atomicPublicationCompleted = false,
        bool publishedTreeValidated = false,
        int directoryCount = 0,
        int fileCount = 0,
        long migrationBytes = 0,
        bool reconciliationRequired = false) =>
        new(
            false,
            failureCode,
            message,
            invocation?.SetupRevision,
            invocation?.InstalledReleaseIdentity ?? string.Empty,
            invocation?.TargetReleaseIdentity ?? string.Empty,
            invocation?.MigrationKind,
            invocation?.FromConfigurationSchemaVersion,
            invocation?.ToConfigurationSchemaVersion,
            invocation?.MigrationRequired ?? false,
            NoOpMigrationResolved: false,
            exactInvocationBound,
            releaseStatusStable,
            immutableBackupValidated,
            privateStagingCreated,
            stagedCopyCompleted,
            runnerArtifactRevalidated,
            runnerInvoked,
            migrationExecutionPerformed,
            migrationManifestWritten,
            stagingTreeImmutable,
            atomicPublicationCompleted,
            publishedTreeValidated,
            directoryCount,
            fileCount,
            migrationBytes,
            MigrationReady: false,
            reconciliationRequired,
            CurrentPointerChanged: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationMigrationExecutionReport Success(
        VerifiedReleaseActivationMigrationExecution execution) =>
        new(
            true,
            VerifiedReleaseActivationMigrationExecutionFailureCode.None,
            execution.Plan.MigrationRequired
                ? "The exact staged-copy migration was executed, frozen, and atomically published without changing current or authorizing activation."
                : "The exact signed no-migration declaration was retained as ready without filesystem or process mutation.",
            execution.Plan.ActivationPlan.SetupRevision,
            execution.Plan.ActivationPlan.InstalledReleaseIdentity,
            execution.Plan.ActivationPlan.TargetReleaseIdentity,
            execution.Plan.MigrationKind,
            execution.Plan.FromConfigurationSchemaVersion,
            execution.Plan.ToConfigurationSchemaVersion,
            execution.Plan.MigrationRequired,
            NoOpMigrationResolved: !execution.Plan.MigrationRequired,
            ExactRunnerInvocationBound: true,
            ReleaseStatusStable: true,
            ImmutableBackupValidated: execution.Plan.MigrationRequired,
            PrivateStagingCreated: execution.Plan.MigrationRequired,
            StagedCopyCompleted: execution.Plan.MigrationRequired,
            RunnerArtifactRevalidated: execution.Plan.MigrationRequired,
            RunnerInvoked: execution.Plan.MigrationRequired,
            MigrationExecutionPerformed: execution.Plan.MigrationRequired,
            MigrationManifestWritten: execution.Plan.MigrationRequired,
            StagingTreeImmutable: execution.Plan.MigrationRequired,
            AtomicPublicationCompleted: execution.Plan.MigrationRequired,
            PublishedTreeValidated: execution.Plan.MigrationRequired,
            execution.DirectoryCount,
            execution.FileCount,
            execution.MigrationBytes,
            MigrationReady: true,
            ReconciliationRequired: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            Execution = execution
        };
}

public sealed record VerifiedReleaseActivationMigrationExecutionDiagnostics(
    bool Registered,
    bool RunnerInvocationInputRegistered,
    bool ExactRunnerInvocationBindingRegistered,
    bool NoOpResolutionRegistered,
    bool ReleaseStatusDoubleReadRegistered,
    bool ImmutableBackupManifestValidationRegistered,
    bool BoundedSourceTraversalRegistered,
    bool SymbolicLinkRejectionRegistered,
    bool PrivateStagingRegistered,
    bool StagedCopyRegistered,
    bool ImmediateRunnerArtifactRevalidationRegistered,
    bool DirectRunnerExecutionRegistered,
    bool ShellInvocationRegistered,
    bool ClearedEnvironmentRegistered,
    bool BoundedJsonProtocolRegistered,
    bool HardTimeoutRegistered,
    bool ProcessTreeTerminationRegistered,
    bool MigrationManifestWriteRegistered,
    bool DurableFlushRegistered,
    bool ImmutableFreezeRegistered,
    bool AtomicDirectoryPublishRegistered,
    bool PublishedTreeValidationRegistered,
    bool CleanupRegistered,
    bool ExactMigrationEvidenceRegistered,
    bool ExistingMigrationOverwriteRegistered,
    bool CurrentPointerMutationRegistered,
    bool ActivationAuthorityRegistered,
    bool OperationalCallerRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool HttpCallerRegistered,
    bool WebSocketCallerRegistered,
    bool HostedServiceCallerRegistered,
    bool TimerCallerRegistered,
    bool AetherRemoteCallerRegistered,
    bool ServiceControlCallerRegistered,
    bool HealthProbeCallerRegistered,
    bool RollbackCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record VerifiedReleaseActivationMigrationExecutionStateDiagnostics(
    bool MigrationReady,
    bool ExactActivationPlanBound,
    bool MigrationRequired,
    int DirectoryCount,
    int FileCount,
    long MigrationBytes,
    bool ManifestPresent,
    bool PublishedTreeImmutable,
    bool ReconciliationRequired,
    bool CurrentPointerChanged,
    bool ActivationAuthorized);

internal sealed record VerifiedReleaseActivationMigrationObservation(
    bool MigrationReady,
    bool MigrationRequired,
    int DirectoryCount,
    int FileCount,
    long MigrationBytes,
    DateTimeOffset? CompletedAt,
    bool ReconciliationRequired);

internal sealed class VerifiedReleaseActivationMigrationExecution
{
    internal VerifiedReleaseActivationMigrationExecution(
        VerifiedReleaseActivationMigrationPlan plan,
        ReleaseMigrationTrustedRunner? runner,
        int directoryCount,
        int fileCount,
        long migrationBytes,
        byte[] manifestSha256,
        DateTimeOffset completedAt)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Runner = runner;
        DirectoryCount = directoryCount;
        FileCount = fileCount;
        MigrationBytes = migrationBytes;
        ManifestSha256 = (manifestSha256 ??
            throw new ArgumentNullException(nameof(manifestSha256))).ToArray();
        CompletedAt = completedAt;
    }

    internal VerifiedReleaseActivationMigrationPlan Plan { get; }
    internal ReleaseMigrationTrustedRunner? Runner { get; }
    internal int DirectoryCount { get; }
    internal int FileCount { get; }
    internal long MigrationBytes { get; }
    internal byte[] ManifestSha256 { get; }
    internal DateTimeOffset CompletedAt { get; }
}

internal enum VerifiedReleaseActivationMigrationManifestEntryKind
{
    Directory = 1,
    File = 2
}

internal sealed record VerifiedReleaseActivationMigrationManifestEntry(
    VerifiedReleaseActivationConfigurationBackupSourceKind Source,
    VerifiedReleaseActivationMigrationManifestEntryKind Kind,
    string Path,
    long? Length,
    string Sha256);

internal sealed record VerifiedReleaseActivationMigrationManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    long SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    string MigrationIdentity,
    int FromConfigurationSchemaVersion,
    int ToConfigurationSchemaVersion,
    string RunnerIdentity,
    int RunnerProtocolVersion,
    string BackupManifestSha256,
    int DirectoryCount,
    int FileCount,
    long MigrationBytes,
    IReadOnlyList<VerifiedReleaseActivationMigrationManifestEntry> Entries);

internal sealed record ReleaseMigrationRunnerExecutionRequest(
    int ProtocolVersion,
    string Type,
    string RequestId,
    long SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    string RunnerIdentity,
    string MigrationIdentity,
    int FromConfigurationSchemaVersion,
    int ToConfigurationSchemaVersion,
    string MigrationRootPath,
    string ConfigurationPath,
    string StatePath,
    string SecretsPath,
    bool MigrationExecutionRequested,
    bool SourceBackupPathsProvided,
    bool CurrentPointerMutationAuthorized,
    bool ActivationAuthorized);

internal sealed record ReleaseMigrationRunnerExecutionResponse(
    int ProtocolVersion,
    string Type,
    string RequestId,
    string RunnerIdentity,
    string MigrationIdentity,
    int FromConfigurationSchemaVersion,
    int ToConfigurationSchemaVersion,
    bool ExecutionAccepted,
    bool MigrationExecutionPerformed,
    bool StagedCopyMutationPerformed,
    bool SourceBackupPathsReceived,
    bool CurrentPointerChanged,
    bool ActivationPerformed,
    bool ServiceControlPerformed,
    bool RadioCommandPerformed,
    bool TxCommandPerformed);

public sealed class VerifiedReleaseActivationMigrationExecutionService
{
    internal const int MaximumDirectoryCount =
        VerifiedReleaseActivationConfigurationBackupService.MaximumDirectoryCount;
    internal const int MaximumFileCount =
        VerifiedReleaseActivationConfigurationBackupService.MaximumFileCount;
    internal const long MaximumFileLength =
        VerifiedReleaseActivationConfigurationBackupService.MaximumFileLength;
    internal const long MaximumMigrationBytes =
        VerifiedReleaseActivationConfigurationBackupService.MaximumSourceBytes;
    internal const int MaximumRelativePathLength =
        VerifiedReleaseActivationConfigurationBackupService.MaximumRelativePathLength;
    internal const int MaximumManifestBytes =
        VerifiedReleaseActivationConfigurationBackupService.MaximumManifestBytes;
    internal const int MaximumRequestCharacters = 16 * 1024;
    internal const int MaximumStandardOutputCharacters = 16 * 1024;
    internal const int MaximumStandardErrorCharacters = 8 * 1024;
    internal const int CurrentExecutionProtocolVersion = 1;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private const int BufferSize = 128 * 1024;
    private const int ManifestSchemaVersion = 1;
    private const string ExecutionRequestType =
        "aethersdr.release-migration.execute.v1";
    private const string ExecutionResponseType =
        "aethersdr.release-migration.execute-result.v1";
    private const UnixFileMode PrivateWritableDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;
    private const UnixFileMode PrivateImmutableDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateWritableFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateImmutableFileMode = UnixFileMode.UserRead;
    private const UnixFileMode GroupOrOtherModes =
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly JsonSerializerOptions StrictProtocolJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;
    private readonly Action<string, string> m_directoryMove;
    private readonly TimeProvider m_timeProvider;
    private readonly TimeSpan m_timeout;
    private readonly SemaphoreSlim m_executionGate = new(1, 1);
    private readonly object m_stateGate = new();
    private VerifiedReleaseActivationMigrationExecution? m_completed;
    private bool m_reconciliationRequired;

    public VerifiedReleaseActivationMigrationExecutionService(
        ReleaseInstallationStatusReader statusReader)
        : this(
            statusReader is null
                ? throw new ArgumentNullException(nameof(statusReader))
                : statusReader.ReadAsync,
            Directory.Move,
            TimeProvider.System,
            DefaultTimeout)
    {
    }

    internal VerifiedReleaseActivationMigrationExecutionService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Action<string, string>? directoryMove = null,
        TimeProvider? timeProvider = null,
        TimeSpan? timeout = null)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_directoryMove = directoryMove ?? Directory.Move;
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_timeout = timeout ?? DefaultTimeout;
        if (m_timeout <= TimeSpan.Zero || m_timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Snapshot = new VerifiedReleaseActivationMigrationExecutionDiagnostics(
            Registered: true,
            RunnerInvocationInputRegistered: true,
            ExactRunnerInvocationBindingRegistered: true,
            NoOpResolutionRegistered: true,
            ReleaseStatusDoubleReadRegistered: true,
            ImmutableBackupManifestValidationRegistered: true,
            BoundedSourceTraversalRegistered: true,
            SymbolicLinkRejectionRegistered: true,
            PrivateStagingRegistered: true,
            StagedCopyRegistered: true,
            ImmediateRunnerArtifactRevalidationRegistered: true,
            DirectRunnerExecutionRegistered: true,
            ShellInvocationRegistered: false,
            ClearedEnvironmentRegistered: true,
            BoundedJsonProtocolRegistered: true,
            HardTimeoutRegistered: true,
            ProcessTreeTerminationRegistered: true,
            MigrationManifestWriteRegistered: true,
            DurableFlushRegistered: true,
            ImmutableFreezeRegistered: true,
            AtomicDirectoryPublishRegistered: true,
            PublishedTreeValidationRegistered: true,
            CleanupRegistered: true,
            ExactMigrationEvidenceRegistered: true,
            ExistingMigrationOverwriteRegistered: false,
            CurrentPointerMutationRegistered: false,
            ActivationAuthorityRegistered: false,
            OperationalCallerRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            HttpCallerRegistered: false,
            WebSocketCallerRegistered: false,
            HostedServiceCallerRegistered: false,
            TimerCallerRegistered: false,
            AetherRemoteCallerRegistered: false,
            ServiceControlCallerRegistered: false,
            HealthProbeCallerRegistered: false,
            RollbackCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationMigrationExecutionDiagnostics Snapshot { get; }

    public VerifiedReleaseActivationMigrationExecutionStateDiagnostics State
    {
        get
        {
            lock (m_stateGate)
            {
                VerifiedReleaseActivationMigrationExecution? completed = m_completed;
                return new VerifiedReleaseActivationMigrationExecutionStateDiagnostics(
                    MigrationReady: completed is not null && !m_reconciliationRequired,
                    ExactActivationPlanBound: completed is not null,
                    MigrationRequired: completed?.Plan.MigrationRequired ?? false,
                    DirectoryCount: completed?.DirectoryCount ?? 0,
                    FileCount: completed?.FileCount ?? 0,
                    MigrationBytes: completed?.MigrationBytes ?? 0,
                    ManifestPresent:
                        completed is not null && completed.Plan.MigrationRequired,
                    PublishedTreeImmutable:
                        completed is not null && completed.Plan.MigrationRequired,
                    ReconciliationRequired: m_reconciliationRequired,
                    CurrentPointerChanged: false,
                    ActivationAuthorized: false);
            }
        }
    }

    internal VerifiedReleaseActivationMigrationObservation Observe(
        VerifiedReleaseActivationPlan activationPlan)
    {
        ArgumentNullException.ThrowIfNull(activationPlan);
        lock (m_stateGate)
        {
            VerifiedReleaseActivationMigrationExecution? completed = m_completed;
            bool exact = completed is not null &&
                ReferenceEquals(completed.Plan.ActivationPlan, activationPlan);
            return new VerifiedReleaseActivationMigrationObservation(
                MigrationReady: exact && !m_reconciliationRequired,
                MigrationRequired: activationPlan.MigrationRequired,
                DirectoryCount: exact ? completed!.DirectoryCount : 0,
                FileCount: exact ? completed!.FileCount : 0,
                MigrationBytes: exact ? completed!.MigrationBytes : 0,
                CompletedAt: exact ? completed!.CompletedAt : null,
                ReconciliationRequired: m_reconciliationRequired);
        }
    }

    [SupportedOSPlatform("linux")]
    internal async Task<VerifiedReleaseActivationMigrationExecutionReport>
        ExecuteAsync(
            VerifiedReleaseActivationMigrationRunnerInvocationReport invocationReport,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocationReport);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            return VerifiedReleaseActivationMigrationExecutionReport.Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .UnsupportedPlatform,
                "Exact staged-copy migration execution requires a supported Linux runtime.",
                invocationReport);
        }

        VerifiedReleaseActivationMigrationRunnerInvocation? invocation =
            ValidateInvocationReport(invocationReport);
        if (invocation is null)
        {
            return VerifiedReleaseActivationMigrationExecutionReport.Failure(
                invocationReport.Invocation is null
                    ? VerifiedReleaseActivationMigrationExecutionFailureCode
                        .RunnerInvocationUnavailable
                    : VerifiedReleaseActivationMigrationExecutionFailureCode
                        .RunnerInvocationNotEligible,
                "A successful exact probe-only migration-runner invocation is required.",
                invocationReport);
        }
        if (!MatchesInvocationReport(invocationReport, invocation))
        {
            return VerifiedReleaseActivationMigrationExecutionReport.Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .RunnerInvocationMismatch,
                "The probe-only invocation no longer matches its exact internal runner selection.",
                invocationReport);
        }

        VerifiedReleaseActivationMigrationPlan plan = invocation.Selection.Plan;
        await m_executionGate.WaitAsync(cancellationToken);
        try
        {
            lock (m_stateGate)
            {
                if (m_reconciliationRequired)
                {
                    return VerifiedReleaseActivationMigrationExecutionReport.Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .ReconciliationRequired,
                        "A previous migration publication requires local reconciliation before another attempt.",
                        invocationReport,
                        exactInvocationBound: true,
                        reconciliationRequired: true);
                }
                if (m_completed is not null)
                {
                    return VerifiedReleaseActivationMigrationExecutionReport.Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .MigrationAlreadyPresent,
                        "Migration evidence is already retained for this service lifetime and will not be overwritten.",
                        invocationReport,
                        exactInvocationBound: true);
                }
            }

            if (!plan.MigrationRequired)
            {
                VerifiedReleaseActivationMigrationExecution noOp = new(
                    plan,
                    runner: null,
                    directoryCount: 0,
                    fileCount: 0,
                    migrationBytes: 0,
                    manifestSha256: [],
                    completedAt: m_timeProvider.GetUtcNow());
                lock (m_stateGate)
                {
                    m_completed = noOp;
                }
                return VerifiedReleaseActivationMigrationExecutionReport.Success(noOp);
            }

            ReleaseMigrationTrustedRunner runner = invocation.Selection.Runner!;
            ReleaseStatusReadResult beforeStatus;
            try
            {
                beforeStatus = await m_statusReader(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    InvalidOperationException or ArgumentException or
                    NotSupportedException or OverflowException)
            {
                return VerifiedReleaseActivationMigrationExecutionReport.Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .StatusUnavailable,
                    "Release status could not be read before staged-copy migration.",
                    invocationReport,
                    exactInvocationBound: true);
            }
            if (!MatchesStatus(beforeStatus, plan.ActivationPlan))
            {
                return VerifiedReleaseActivationMigrationExecutionReport.Failure(
                    beforeStatus.Succeeded
                        ? VerifiedReleaseActivationMigrationExecutionFailureCode
                            .StatusMismatch
                        : VerifiedReleaseActivationMigrationExecutionFailureCode
                            .StatusUnavailable,
                    "Completed setup, release inventory, or current no longer matches the exact activation plan.",
                    invocationReport,
                    exactInvocationBound: true);
            }

            bool stagingCreated = false;
            bool atomicPublicationCompleted = false;
            bool immutableBackupValidated = false;
            bool stagedCopyCompleted = false;
            bool runnerArtifactRevalidated = false;
            bool runnerInvoked = false;
            bool migrationExecuted = false;
            bool manifestWritten = false;
            bool stagingTreeImmutable = false;
            bool publishedTreeValidated = false;
            int directoryCount = 0;
            int fileCount = 0;
            long migrationBytes = 0;
            try
            {
                ValidateMigrationLayout(plan);
                PreparePrivateMigrationParent(plan);
                EnsureAbsent(plan.PublishedPath, published: true);
                EnsureAbsent(plan.StagingPath, published: false);

                BackupSnapshot backup = await ValidateBackupAsync(
                    plan,
                    cancellationToken);
                immutableBackupValidated = true;

                Directory.CreateDirectory(plan.StagingPath);
                File.SetUnixFileMode(
                    plan.StagingPath,
                    PrivateWritableDirectoryMode);
                ValidatePrivateWritableDirectory(plan.StagingPath);
                stagingCreated = true;

                await CopyBackupAsync(plan, backup, cancellationToken);
                BackupSnapshot afterCopy = await ValidateBackupAsync(
                    plan,
                    cancellationToken);
                if (!EquivalentBackupSnapshots(backup, afterCopy))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .BackupSourceChanged,
                        "The immutable configuration backup changed while its staged copy was created.");
                }
                stagedCopyCompleted = true;

                if (!VerifiedReleaseActivationMigrationRunnerInvocationService
                        .RevalidateRunnerArtifact(runner))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .RunnerArtifactChanged,
                        "The exact migration runner changed after probe validation.");
                }
                runnerArtifactRevalidated = true;

                RunnerExecutionResult runnerResult = await ExecuteRunnerAsync(
                    plan,
                    runner,
                    cancellationToken);
                runnerInvoked = runnerResult.RunnerInvoked;
                migrationExecuted = runnerResult.MigrationExecuted;
                if (!runnerResult.Succeeded)
                {
                    throw Failure(runnerResult.FailureCode, runnerResult.Message);
                }

                MigrationSnapshot migrated = await CaptureMigrationSnapshotAsync(
                    plan,
                    includeHashes: true,
                    cancellationToken);
                directoryCount = migrated.Directories.Count;
                fileCount = migrated.Files.Count;
                MigrationManifestArtifact manifest = CreateManifest(
                    plan,
                    runner,
                    migrated);
                await WriteManifestAsync(plan, manifest, cancellationToken);
                manifestWritten = true;
                migrationBytes = checked(
                    migrated.ContentBytes + manifest.Bytes.Length);

                FreezeTree(plan.StagingPath);
                await ValidateImmutableMigrationTreeAsync(
                    plan.StagingPath,
                    manifest,
                    cancellationToken);
                stagingTreeImmutable = true;

                ReleaseStatusReadResult afterStatus =
                    await m_statusReader(cancellationToken);
                if (!EquivalentStatus(beforeStatus, afterStatus) ||
                    !MatchesStatus(afterStatus, plan.ActivationPlan))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .StatusMismatch,
                        "Release status changed while the staged-copy migration was prepared.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    m_directoryMove(plan.StagingPath, plan.PublishedPath);
                    atomicPublicationCompleted = true;
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException or
                        SecurityException or ArgumentException or
                        NotSupportedException)
                {
                    bool stagingPresent = PathEntryExists(plan.StagingPath);
                    bool publishedPresent = PathEntryExists(plan.PublishedPath);
                    if (publishedPresent || !stagingPresent)
                    {
                        MarkReconciliationRequired();
                        TryFreezeRoot(
                            publishedPresent
                                ? plan.PublishedPath
                                : plan.StagingPath);
                        return VerifiedReleaseActivationMigrationExecutionReport
                            .Failure(
                                VerifiedReleaseActivationMigrationExecutionFailureCode
                                    .PublishedStateRequiresReconciliation,
                                "The atomic migration publication outcome is ambiguous and requires local reconciliation.",
                                invocationReport,
                                exactInvocationBound: true,
                                releaseStatusStable: true,
                                immutableBackupValidated,
                                privateStagingCreated: stagingCreated,
                                stagedCopyCompleted,
                                runnerArtifactRevalidated,
                                runnerInvoked,
                                migrationExecutionPerformed: migrationExecuted,
                                migrationManifestWritten: manifestWritten,
                                stagingTreeImmutable,
                                atomicPublicationCompleted: publishedPresent,
                                publishedTreeValidated: false,
                                directoryCount,
                                fileCount,
                                migrationBytes,
                                reconciliationRequired: true);
                    }
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .AtomicPublishFailed,
                        "The immutable migration tree could not be atomically published.");
                }

                if (PathEntryExists(plan.StagingPath) ||
                    !PathEntryExists(plan.PublishedPath))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .PublishedStateRequiresReconciliation,
                        "Atomic publication did not leave one consumed staging tree and one published migration.");
                }

                await ValidateImmutableMigrationTreeAsync(
                    plan.PublishedPath,
                    manifest,
                    CancellationToken.None);
                publishedTreeValidated = true;
                ReleaseStatusReadResult finalStatus =
                    await m_statusReader(CancellationToken.None);
                if (!EquivalentStatus(beforeStatus, finalStatus) ||
                    !MatchesStatus(finalStatus, plan.ActivationPlan))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .PublishedStateRequiresReconciliation,
                        "Release status changed unexpectedly after migration publication.");
                }

                VerifiedReleaseActivationMigrationExecution completed = new(
                    plan,
                    runner,
                    directoryCount,
                    fileCount,
                    migrationBytes,
                    manifest.Sha256,
                    manifest.Manifest.CreatedAt);
                lock (m_stateGate)
                {
                    m_completed = completed;
                }
                return VerifiedReleaseActivationMigrationExecutionReport.Success(
                    completed);
            }
            catch (OperationCanceledException)
            {
                if (!atomicPublicationCompleted &&
                    (!stagingCreated || TryCleanup(plan.StagingPath)))
                {
                    throw;
                }
                MarkReconciliationRequired();
                return FailureReport(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .CleanupFailed,
                    "Cancelled migration execution could not remove its private staging tree.",
                    invocationReport,
                    stagingCreated,
                    immutableBackupValidated,
                    stagedCopyCompleted,
                    runnerArtifactRevalidated,
                    runnerInvoked,
                    migrationExecuted,
                    manifestWritten,
                    stagingTreeImmutable,
                    atomicPublicationCompleted,
                    publishedTreeValidated,
                    directoryCount,
                    fileCount,
                    migrationBytes,
                    reconciliationRequired: true);
            }
            catch (MigrationExecutionException exception)
            {
                if (atomicPublicationCompleted ||
                    (stagingCreated && PathEntryExists(plan.PublishedPath)))
                {
                    MarkReconciliationRequired();
                    TryFreezeRoot(plan.PublishedPath);
                    return FailureReport(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .PublishedStateRequiresReconciliation,
                        exception.Message,
                        invocationReport,
                        stagingCreated,
                        immutableBackupValidated,
                        stagedCopyCompleted,
                        runnerArtifactRevalidated,
                        runnerInvoked,
                        migrationExecuted,
                        manifestWritten,
                        stagingTreeImmutable,
                        atomicPublicationCompleted,
                        publishedTreeValidated,
                        directoryCount,
                        fileCount,
                        migrationBytes,
                        reconciliationRequired: true);
                }
                if (!stagingCreated || TryCleanup(plan.StagingPath))
                {
                    return FailureReport(
                        exception.FailureCode,
                        exception.Message,
                        invocationReport,
                        stagingCreated,
                        immutableBackupValidated,
                        stagedCopyCompleted,
                        runnerArtifactRevalidated,
                        runnerInvoked,
                        migrationExecuted,
                        manifestWritten,
                        stagingTreeImmutable,
                        atomicPublicationCompleted,
                        publishedTreeValidated,
                        directoryCount,
                        fileCount,
                        migrationBytes,
                        reconciliationRequired: false);
                }
                MarkReconciliationRequired();
                return FailureReport(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .CleanupFailed,
                    "Failed migration execution could not remove its private staging tree.",
                    invocationReport,
                    stagingCreated,
                    immutableBackupValidated,
                    stagedCopyCompleted,
                    runnerArtifactRevalidated,
                    runnerInvoked,
                    migrationExecuted,
                    manifestWritten,
                    stagingTreeImmutable,
                    atomicPublicationCompleted,
                    publishedTreeValidated,
                    directoryCount,
                    fileCount,
                    migrationBytes,
                    reconciliationRequired: true);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    SecurityException or CryptographicException or JsonException or
                    ArgumentException or NotSupportedException or OverflowException or
                    InvalidOperationException)
            {
                if (!atomicPublicationCompleted &&
                    (!stagingCreated || TryCleanup(plan.StagingPath)))
                {
                    return FailureReport(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .StagedTreeInvalid,
                        "Staged-copy migration failed closed before immutable publication.",
                        invocationReport,
                        stagingCreated,
                        immutableBackupValidated,
                        stagedCopyCompleted,
                        runnerArtifactRevalidated,
                        runnerInvoked,
                        migrationExecuted,
                        manifestWritten,
                        stagingTreeImmutable,
                        atomicPublicationCompleted,
                        publishedTreeValidated,
                        directoryCount,
                        fileCount,
                        migrationBytes,
                        reconciliationRequired: false);
                }
                MarkReconciliationRequired();
                TryFreezeRoot(
                    PathEntryExists(plan.PublishedPath)
                        ? plan.PublishedPath
                        : plan.StagingPath);
                return FailureReport(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .PublishedStateRequiresReconciliation,
                    "Migration state became ambiguous and requires local reconciliation.",
                    invocationReport,
                    stagingCreated,
                    immutableBackupValidated,
                    stagedCopyCompleted,
                    runnerArtifactRevalidated,
                    runnerInvoked,
                    migrationExecuted,
                    manifestWritten,
                    stagingTreeImmutable,
                    atomicPublicationCompleted,
                    publishedTreeValidated,
                    directoryCount,
                    fileCount,
                    migrationBytes,
                    reconciliationRequired: true);
            }
        }
        finally
        {
            _ = m_executionGate.Release();
        }
    }

    private static VerifiedReleaseActivationMigrationExecutionReport FailureReport(
        VerifiedReleaseActivationMigrationExecutionFailureCode failureCode,
        string message,
        VerifiedReleaseActivationMigrationRunnerInvocationReport invocation,
        bool stagingCreated,
        bool immutableBackupValidated,
        bool stagedCopyCompleted,
        bool runnerArtifactRevalidated,
        bool runnerInvoked,
        bool migrationExecuted,
        bool manifestWritten,
        bool stagingTreeImmutable,
        bool atomicPublicationCompleted,
        bool publishedTreeValidated,
        int directoryCount,
        int fileCount,
        long migrationBytes,
        bool reconciliationRequired) =>
        VerifiedReleaseActivationMigrationExecutionReport.Failure(
            failureCode,
            message,
            invocation,
            exactInvocationBound: true,
            releaseStatusStable: true,
            immutableBackupValidated,
            privateStagingCreated: stagingCreated,
            stagedCopyCompleted,
            runnerArtifactRevalidated,
            runnerInvoked,
            migrationExecutionPerformed: migrationExecuted,
            migrationManifestWritten: manifestWritten,
            stagingTreeImmutable,
            atomicPublicationCompleted,
            publishedTreeValidated,
            directoryCount,
            fileCount,
            migrationBytes,
            reconciliationRequired);

    private static VerifiedReleaseActivationMigrationRunnerInvocation?
        ValidateInvocationReport(
            VerifiedReleaseActivationMigrationRunnerInvocationReport report)
    {
        if (!report.Succeeded ||
            report.FailureCode !=
                VerifiedReleaseActivationMigrationRunnerInvocationFailureCode.None ||
            report.SetupRevision is not > 0 ||
            string.IsNullOrEmpty(report.InstalledReleaseIdentity) ||
            string.IsNullOrEmpty(report.TargetReleaseIdentity) ||
            report.MigrationKind is not (ReleaseMigrationKind.None or
                ReleaseMigrationKind.Required) ||
            !report.ExactRunnerSelectionBound ||
            !report.ShellInvocationDisabled ||
            !report.EnvironmentCleared ||
            report.MigrationSourcePathProvided ||
            report.MigrationSourceReadPerformed ||
            report.FileWritePerformed ||
            report.DirectoryMutationPerformed ||
            report.MigrationExecutionPerformed ||
            report.CurrentPointerChanged ||
            report.ActivationAuthorized)
        {
            return null;
        }
        return report.Invocation;
    }

    private static bool MatchesInvocationReport(
        VerifiedReleaseActivationMigrationRunnerInvocationReport report,
        VerifiedReleaseActivationMigrationRunnerInvocation invocation)
    {
        VerifiedReleaseActivationMigrationRunnerSelection selection =
            invocation.Selection;
        VerifiedReleaseActivationMigrationPlan plan = selection.Plan;
        if (report.SetupRevision != plan.ActivationPlan.SetupRevision ||
            !string.Equals(
                report.InstalledReleaseIdentity,
                plan.ActivationPlan.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.TargetReleaseIdentity,
                plan.ActivationPlan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            report.MigrationKind != plan.MigrationKind ||
            report.FromConfigurationSchemaVersion !=
                plan.FromConfigurationSchemaVersion ||
            report.ToConfigurationSchemaVersion !=
                plan.ToConfigurationSchemaVersion ||
            report.MigrationRequired != plan.MigrationRequired ||
            report.NoOpMigrationResolved == plan.MigrationRequired ||
            report.MigrationReady != !plan.MigrationRequired)
        {
            return false;
        }
        if (!plan.MigrationRequired)
        {
            return !invocation.RunnerInvoked &&
                !invocation.ArtifactRevalidated &&
                !report.ProbeRequestSent &&
                !report.RunnerInvoked &&
                !report.ProbeResponseAccepted &&
                !report.RunnerArtifactRevalidated &&
                report.RunnerProtocolVersion is null &&
                selection.Runner is null &&
                selection.Mapping is null;
        }
        return invocation.RunnerInvoked &&
            invocation.ArtifactRevalidated &&
            report.ProbeRequestSent &&
            report.RunnerInvoked &&
            report.ProbeResponseAccepted &&
            report.RunnerArtifactRevalidated &&
            report.RunnerProtocolVersion ==
                ReleaseMigrationRunnerTrustRegistry.CurrentRunnerProtocolVersion &&
            selection.Runner is not null &&
            selection.Mapping is not null &&
            selection.Runner.RunnerProtocolVersion ==
                report.RunnerProtocolVersion &&
            string.Equals(
                selection.Mapping.MigrationIdentity,
                plan.MigrationIdentity,
                StringComparison.Ordinal) &&
            selection.Mapping.FromConfigurationSchemaVersion ==
                plan.FromConfigurationSchemaVersion &&
            selection.Mapping.ToConfigurationSchemaVersion ==
                plan.ToConfigurationSchemaVersion;
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateMigrationLayout(
        VerifiedReleaseActivationMigrationPlan plan)
    {
        if (!plan.MigrationRequired ||
            plan.Sources.Count != 3 ||
            plan.ConfigurationBackup.ManifestSha256.Length != 32 ||
            !Path.IsPathFullyQualified(plan.MigrationRootPath) ||
            !Path.IsPathFullyQualified(plan.StagingPath) ||
            !Path.IsPathFullyQualified(plan.PublishedPath) ||
            !Path.IsPathFullyQualified(plan.ManifestPath) ||
            !PathEquals(
                Path.GetDirectoryName(plan.StagingPath),
                plan.MigrationRootPath) ||
            !PathEquals(
                Path.GetDirectoryName(plan.PublishedPath),
                plan.MigrationRootPath) ||
            !PathEquals(
                Path.GetDirectoryName(plan.ManifestPath),
                plan.PublishedPath) ||
            !string.Equals(
                Path.GetFileName(plan.ManifestPath),
                "migration-manifest.json",
                StringComparison.Ordinal) ||
            PathEquals(plan.StagingPath, plan.PublishedPath) ||
            plan.Sources.Select(source => source.Kind).Distinct().Count() != 3 ||
            plan.Sources.Select(source => source.SourcePath)
                .Distinct(PathComparer).Count() != 3 ||
            plan.Sources.Select(source => source.StagedPath)
                .Distinct(PathComparer).Count() != 3)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .UnsafeMigrationLayout,
                "The exact migration layout is incomplete, duplicated, or noncanonical.");
        }

        foreach (VerifiedReleaseActivationMigrationSourcePlan source in plan.Sources)
        {
            if (!Path.IsPathFullyQualified(source.SourcePath) ||
                !Path.IsPathFullyQualified(source.StagedPath) ||
                !IsSameOrDescendant(
                    source.SourcePath,
                    plan.ConfigurationBackup.Plan.PublishedPath) ||
                !IsSameOrDescendant(source.StagedPath, plan.StagingPath) ||
                !PathEquals(
                    Path.GetDirectoryName(source.StagedPath),
                    plan.StagingPath))
            {
                throw Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .UnsafeMigrationLayout,
                    "A migration source or staged destination escaped its exact transaction root.");
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static void PreparePrivateMigrationParent(
        VerifiedReleaseActivationMigrationPlan plan)
    {
        string revisionRoot = Path.GetDirectoryName(plan.MigrationRootPath) ??
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .UnsafeMigrationLayout,
                "The migration root has no exact setup-revision parent.");
        if (!Directory.Exists(revisionRoot))
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .UnsafeMigrationLayout,
                "The exact setup-revision backup root is unavailable.");
        }
        ValidateNonLinkDirectory(revisionRoot);
        if (!PathEntryExists(plan.MigrationRootPath))
        {
            Directory.CreateDirectory(plan.MigrationRootPath);
            File.SetUnixFileMode(
                plan.MigrationRootPath,
                PrivateWritableDirectoryMode);
        }
        ValidatePrivateWritableDirectory(plan.MigrationRootPath);
    }

    private static void EnsureAbsent(string path, bool published)
    {
        if (!PathEntryExists(path))
        {
            return;
        }
        throw Failure(
            published
                ? VerifiedReleaseActivationMigrationExecutionFailureCode
                    .MigrationAlreadyPresent
                : VerifiedReleaseActivationMigrationExecutionFailureCode
                    .StagingAlreadyPresent,
            published
                ? "The exact migration identity already exists and will not be overwritten."
                : "The planned private migration staging identity already exists and will not be reused or removed.");
    }

    [SupportedOSPlatform("linux")]
    private static async Task<BackupSnapshot> ValidateBackupAsync(
        VerifiedReleaseActivationMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        string manifestPath = plan.ConfigurationBackup.Plan.ManifestPath;
        FileInfo manifestFile = new(manifestPath);
        manifestFile.Refresh();
        ValidateImmutableFile(manifestFile);
        if (manifestFile.Length is < 1 or > MaximumManifestBytes)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .BackupSourceInvalid,
                "The immutable backup manifest exceeded its bounded size.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        byte[] digest = SHA256.HashData(bytes);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    digest,
                    plan.ConfigurationBackup.ManifestSha256))
            {
                throw Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .BackupSourceInvalid,
                    "The immutable backup manifest no longer matches its exact evidence digest.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }

        VerifiedReleaseActivationConfigurationBackupManifest? manifest =
            JsonSerializer.Deserialize<
                VerifiedReleaseActivationConfigurationBackupManifest>(
                bytes,
                ManifestJsonOptions);
        if (manifest is null ||
            manifest.SchemaVersion != 1 ||
            manifest.SetupRevision != plan.ActivationPlan.SetupRevision ||
            !string.Equals(
                manifest.InstalledReleaseIdentity,
                plan.ActivationPlan.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.TargetReleaseIdentity,
                plan.ActivationPlan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            manifest.SourceDirectoryCount != plan.Sources.Count ||
            manifest.DirectoryCount != plan.ConfigurationBackup.DirectoryCount ||
            manifest.FileCount != plan.ConfigurationBackup.FileCount ||
            manifest.SourceBytes < 0 ||
            checked(manifest.SourceBytes + bytes.Length) !=
                plan.ConfigurationBackup.BackupBytes ||
            manifest.Entries is null ||
            manifest.Entries.Count !=
                manifest.DirectoryCount + manifest.FileCount)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .BackupSourceInvalid,
                "The immutable backup manifest does not match the exact activation and backup evidence.");
        }

        Dictionary<VerifiedReleaseActivationConfigurationBackupSourceKind,
            VerifiedReleaseActivationMigrationSourcePlan> sourcePlans =
            plan.Sources.ToDictionary(source => source.Kind);
        List<BackupDirectory> directories = [];
        List<BackupFile> files = [];
        HashSet<string> unique = new(StringComparer.Ordinal);
        foreach (VerifiedReleaseActivationConfigurationBackupManifestEntry entry in
                 manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourcePlans.TryGetValue(entry.Source, out var sourcePlan) ||
                !IsSafeRelativePath(entry.Path))
            {
                throw Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .BackupSourceInvalid,
                    "The immutable backup manifest contains an unsafe source entry.");
            }
            string key = $"{entry.Source}:{entry.Kind}:{entry.Path}";
            if (!unique.Add(key))
            {
                throw Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .BackupSourceInvalid,
                    "The immutable backup manifest contains a duplicate source entry.");
            }

            string sourcePath = ResolveRelative(sourcePlan.SourcePath, entry.Path);
            string stagedPath = ResolveRelative(sourcePlan.StagedPath, entry.Path);
            if (entry.Kind ==
                VerifiedReleaseActivationConfigurationBackupManifestEntryKind
                    .Directory)
            {
                if (entry.Length is not null ||
                    !string.IsNullOrEmpty(entry.Sha256))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .BackupSourceInvalid,
                        "The immutable backup manifest contains malformed directory metadata.");
                }
                DirectoryInfo directory = new(sourcePath);
                directory.Refresh();
                ValidateImmutableDirectory(directory);
                directories.Add(new BackupDirectory(
                    entry.Source,
                    entry.Path,
                    sourcePath,
                    stagedPath));
            }
            else if (entry.Kind ==
                VerifiedReleaseActivationConfigurationBackupManifestEntryKind.File)
            {
                if (entry.Length is not >= 0 or > MaximumFileLength ||
                    !IsCanonicalSha256(entry.Sha256))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .BackupSourceInvalid,
                        "The immutable backup manifest contains malformed file metadata.");
                }
                FileInfo file = new(sourcePath);
                file.Refresh();
                ValidateImmutableFile(file);
                if (file.Length != entry.Length)
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .BackupSourceInvalid,
                        "An immutable backup file no longer matches its manifest length.");
                }
                byte[] fileDigest = await HashFileAsync(
                    sourcePath,
                    cancellationToken);
                string fileDigestHex =
                    Convert.ToHexString(fileDigest).ToLowerInvariant();
                CryptographicOperations.ZeroMemory(fileDigest);
                if (!string.Equals(
                        fileDigestHex,
                        entry.Sha256,
                        StringComparison.Ordinal))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .BackupSourceInvalid,
                        "An immutable backup file no longer matches its manifest digest.");
                }
                files.Add(new BackupFile(
                    entry.Source,
                    entry.Path,
                    sourcePath,
                    stagedPath,
                    file.Length,
                    entry.Sha256));
            }
            else
            {
                throw Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .BackupSourceInvalid,
                    "The immutable backup manifest contains an unsupported entry kind.");
            }
        }

        if (directories.Count != manifest.DirectoryCount ||
            files.Count != manifest.FileCount ||
            directories.Count is < 3 or > MaximumDirectoryCount ||
            files.Count > MaximumFileCount ||
            files.Sum(file => file.Length) != manifest.SourceBytes ||
            !sourcePlans.Keys.All(kind => directories.Any(directory =>
                directory.Kind == kind && directory.RelativePath == ".")))
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .BackupSourceInvalid,
                "The immutable backup tree is incomplete or exceeds migration bounds.");
        }

        ValidateActualBackupTree(sourcePlans, directories, files);
        return new BackupSnapshot(
            manifest,
            bytes,
            directories
                .OrderBy(directory => directory.StagedPath.Count(character =>
                    character == Path.DirectorySeparatorChar))
                .ThenBy(directory => directory.StagedPath, PathComparer)
                .ToArray(),
            files.OrderBy(file => file.StagedPath, PathComparer).ToArray());
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateActualBackupTree(
        IReadOnlyDictionary<
            VerifiedReleaseActivationConfigurationBackupSourceKind,
            VerifiedReleaseActivationMigrationSourcePlan> sourcePlans,
        IReadOnlyList<BackupDirectory> directories,
        IReadOnlyList<BackupFile> files)
    {
        HashSet<string> expectedDirectories =
            directories.Select(directory => directory.SourcePath)
                .ToHashSet(PathComparer);
        HashSet<string> expectedFiles =
            files.Select(file => file.SourcePath).ToHashSet(PathComparer);
        foreach (VerifiedReleaseActivationMigrationSourcePlan source in
                 sourcePlans.Values)
        {
            foreach (string path in Directory.EnumerateDirectories(
                         source.SourcePath,
                         "*",
                         SearchOption.AllDirectories))
            {
                if (!expectedDirectories.Contains(Path.GetFullPath(path)))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .BackupSourceInvalid,
                        "The immutable backup contains a directory absent from its manifest.");
                }
            }
            foreach (string path in Directory.EnumerateFiles(
                         source.SourcePath,
                         "*",
                         SearchOption.AllDirectories))
            {
                if (!expectedFiles.Contains(Path.GetFullPath(path)))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .BackupSourceInvalid,
                        "The immutable backup contains a file absent from its manifest.");
                }
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task CopyBackupAsync(
        VerifiedReleaseActivationMigrationPlan plan,
        BackupSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (BackupDirectory directory in snapshot.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory.StagedPath);
            File.SetUnixFileMode(
                directory.StagedPath,
                PrivateWritableDirectoryMode);
            ValidatePrivateWritableDirectory(directory.StagedPath);
        }

        byte[] buffer = new byte[BufferSize];
        try
        {
            foreach (BackupFile file in snapshot.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string parent = Path.GetDirectoryName(file.StagedPath) ??
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .StagedCopyFailed,
                        "A staged migration file has no validated parent.");
                ValidatePrivateWritableDirectory(parent);
                FileStreamOptions options = new()
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                };
                await using FileStream input = new(
                    file.SourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using FileStream output = new(file.StagedPath, options);
                long copied = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    copied = checked(copied + read);
                    if (copied > file.Length)
                    {
                        throw Failure(
                            VerifiedReleaseActivationMigrationExecutionFailureCode
                                .BackupSourceChanged,
                            "An immutable backup file grew while being copied.");
                    }
                    await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
                if (copied != file.Length)
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .BackupSourceChanged,
                        "An immutable backup file changed length while being copied.");
                }
                await output.DisposeAsync();
                await input.DisposeAsync();
                File.SetUnixFileMode(file.StagedPath, PrivateWritableFileMode);
                byte[] digest = await HashFileAsync(
                    file.StagedPath,
                    cancellationToken);
                string digestHex = Convert.ToHexString(digest).ToLowerInvariant();
                CryptographicOperations.ZeroMemory(digest);
                if (!string.Equals(digestHex, file.Sha256, StringComparison.Ordinal))
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .StagedCopyFailed,
                        "A copied migration source file failed digest validation.");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private async Task<RunnerExecutionResult> ExecuteRunnerAsync(
        VerifiedReleaseActivationMigrationPlan plan,
        ReleaseMigrationTrustedRunner runner,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        string requestJson = JsonSerializer.Serialize(
            new ReleaseMigrationRunnerExecutionRequest(
                CurrentExecutionProtocolVersion,
                ExecutionRequestType,
                requestId,
                plan.ActivationPlan.SetupRevision,
                plan.ActivationPlan.InstalledReleaseIdentity,
                plan.ActivationPlan.TargetReleaseIdentity,
                runner.RunnerIdentity,
                plan.MigrationIdentity,
                plan.FromConfigurationSchemaVersion!.Value,
                plan.ToConfigurationSchemaVersion!.Value,
                plan.StagingPath,
                plan.Sources.Single(source => source.Kind ==
                    VerifiedReleaseActivationConfigurationBackupSourceKind
                        .Configuration).StagedPath,
                plan.Sources.Single(source => source.Kind ==
                    VerifiedReleaseActivationConfigurationBackupSourceKind.State)
                    .StagedPath,
                plan.Sources.Single(source => source.Kind ==
                    VerifiedReleaseActivationConfigurationBackupSourceKind.Secret)
                    .StagedPath,
                MigrationExecutionRequested: true,
                SourceBackupPathsProvided: false,
                CurrentPointerMutationAuthorized: false,
                ActivationAuthorized: false),
            StrictProtocolJson);
        if (requestJson.Length is 0 or > MaximumRequestCharacters)
        {
            return RunnerExecutionResult.Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .RunnerInvocationMismatch,
                "The exact runner execution request exceeded its bounded protocol envelope.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = runner.RunnerPath,
            WorkingDirectory = Path.GetDirectoryName(runner.RunnerPath) ??
                throw Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .RunnerStartFailed,
                    "The exact migration runner has no working directory."),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment.Clear();
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["AETHERSDR_MIGRATION_RUNNER_PROTOCOL"] =
            CurrentExecutionProtocolVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture);

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return RunnerExecutionResult.Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .RunnerStartFailed,
                    "The exact migration runner process did not start.");
            }
        }
        catch (Exception exception)
            when (exception is Win32Exception or InvalidOperationException or
                IOException or UnauthorizedAccessException or SecurityException)
        {
            return RunnerExecutionResult.Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .RunnerStartFailed,
                "The exact migration runner process could not be started directly.");
        }

        using CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(m_timeout);
        Task<string> standardOutput = ReadBoundedAsync(
            process.StandardOutput,
            MaximumStandardOutputCharacters,
            operation.Token);
        Task<string> standardError = ReadBoundedAsync(
            process.StandardError,
            MaximumStandardErrorCharacters,
            operation.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(
                requestJson.AsMemory(),
                operation.Token);
            await process.StandardInput.FlushAsync(operation.Token);
            process.StandardInput.Close();

            Task exit = process.WaitForExitAsync(operation.Token);
            Task first = await Task.WhenAny(exit, standardOutput, standardError);
            if (first != exit && first.IsFaulted)
            {
                await TerminateAsync(process);
                await first;
            }
            await exit;
            string output = await standardOutput;
            string errors = await standardError;
            if (process.ExitCode != 0)
            {
                return RunnerExecutionResult.Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .RunnerProcessFailed,
                    "The exact migration runner returned a nonzero exit code.",
                    runnerInvoked: true);
            }
            if (!string.IsNullOrWhiteSpace(errors))
            {
                return RunnerExecutionResult.Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .RunnerResponseInvalid,
                    "The exact migration runner wrote unexpected standard-error output.",
                    runnerInvoked: true);
            }

            ReleaseMigrationRunnerExecutionResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<
                    ReleaseMigrationRunnerExecutionResponse>(
                    output,
                    StrictProtocolJson);
            }
            catch (JsonException)
            {
                response = null;
            }
            if (response is null ||
                !MatchesExecutionResponse(
                    response,
                    requestId,
                    plan,
                    runner))
            {
                return RunnerExecutionResult.Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .RunnerResponseInvalid,
                    "The exact migration runner returned an invalid or mismatched execution response.",
                    runnerInvoked: true);
            }
            if (!response.ExecutionAccepted)
            {
                return RunnerExecutionResult.Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .RunnerExecutionRejected,
                    "The exact migration runner rejected the staged-copy execution request.",
                    runnerInvoked: true);
            }
            return RunnerExecutionResult.Success();
        }
        catch (InvalidDataException)
        {
            await TerminateAsync(process);
            return RunnerExecutionResult.Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .RunnerOutputTooLarge,
                "The exact migration runner exceeded a bounded output channel.",
                runnerInvoked: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateAsync(process);
            return RunnerExecutionResult.Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .RunnerTimedOut,
                "The exact migration runner exceeded its hard execution timeout.",
                runnerInvoked: true);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process);
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or ObjectDisposedException or
                InvalidOperationException)
        {
            await TerminateAsync(process);
            return RunnerExecutionResult.Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .RunnerProcessFailed,
                "The exact migration runner failed before a valid execution response.",
                runnerInvoked: true);
        }
    }

    private static bool MatchesExecutionResponse(
        ReleaseMigrationRunnerExecutionResponse response,
        string requestId,
        VerifiedReleaseActivationMigrationPlan plan,
        ReleaseMigrationTrustedRunner runner) =>
        response.ProtocolVersion == CurrentExecutionProtocolVersion &&
        string.Equals(
            response.Type,
            ExecutionResponseType,
            StringComparison.Ordinal) &&
        string.Equals(response.RequestId, requestId, StringComparison.Ordinal) &&
        string.Equals(
            response.RunnerIdentity,
            runner.RunnerIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            response.MigrationIdentity,
            plan.MigrationIdentity,
            StringComparison.Ordinal) &&
        response.FromConfigurationSchemaVersion ==
            plan.FromConfigurationSchemaVersion &&
        response.ToConfigurationSchemaVersion ==
            plan.ToConfigurationSchemaVersion &&
        response.MigrationExecutionPerformed &&
        response.StagedCopyMutationPerformed &&
        !response.SourceBackupPathsReceived &&
        !response.CurrentPointerChanged &&
        !response.ActivationPerformed &&
        !response.ServiceControlPerformed &&
        !response.RadioCommandPerformed &&
        !response.TxCommandPerformed;

    [SupportedOSPlatform("linux")]
    private static async Task<MigrationSnapshot> CaptureMigrationSnapshotAsync(
        VerifiedReleaseActivationMigrationPlan plan,
        bool includeHashes,
        CancellationToken cancellationToken)
    {
        List<MigrationDirectory> directories = [];
        List<MigrationFile> files = [];
        long contentBytes = 0;
        foreach (VerifiedReleaseActivationMigrationSourcePlan source in plan.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo root = new(source.StagedPath);
            root.Refresh();
            ValidatePrivateMutableDirectory(root);
            directories.Add(new MigrationDirectory(source.Kind, ".", root.FullName));

            foreach (string directoryPath in Directory.EnumerateDirectories(
                         root.FullName,
                         "*",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectoryInfo directory = new(directoryPath);
                directory.Refresh();
                ValidatePrivateMutableDirectory(directory);
                string relative = NormalizeRelative(
                    Path.GetRelativePath(root.FullName, directory.FullName));
                directories.Add(new MigrationDirectory(
                    source.Kind,
                    relative,
                    directory.FullName));
                if (directories.Count > MaximumDirectoryCount)
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .StagedTreeInvalid,
                        "The migrated staged copy exceeded its directory bound.");
                }
            }

            foreach (string filePath in Directory.EnumerateFiles(
                         root.FullName,
                         "*",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo file = new(filePath);
                file.Refresh();
                ValidatePrivateMutableFile(file);
                if (file.Length > MaximumFileLength)
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .StagedTreeInvalid,
                        "A migrated staged file exceeded its length bound.");
                }
                contentBytes = checked(contentBytes + file.Length);
                if (contentBytes > MaximumMigrationBytes)
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .StagedTreeInvalid,
                        "The migrated staged copy exceeded its byte bound.");
                }
                string relative = NormalizeRelative(
                    Path.GetRelativePath(root.FullName, file.FullName));
                byte[] digest = includeHashes
                    ? await HashFileAsync(file.FullName, cancellationToken)
                    : [];
                files.Add(new MigrationFile(
                    source.Kind,
                    relative,
                    file.FullName,
                    file.Length,
                    digest));
                if (files.Count > MaximumFileCount)
                {
                    throw Failure(
                        VerifiedReleaseActivationMigrationExecutionFailureCode
                            .StagedTreeInvalid,
                        "The migrated staged copy exceeded its file-count bound.");
                }
            }
        }

        string manifestStagingPath = Path.Combine(
            plan.StagingPath,
            "migration-manifest.json");
        if (PathEntryExists(manifestStagingPath))
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .StagedTreeInvalid,
                "The migration runner attempted to create the host-owned manifest path.");
        }
        return new MigrationSnapshot(
            directories
                .OrderBy(directory => directory.Kind)
                .ThenBy(directory => directory.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            files
                .OrderBy(file => file.Kind)
                .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            contentBytes);
    }

    private MigrationManifestArtifact CreateManifest(
        VerifiedReleaseActivationMigrationPlan plan,
        ReleaseMigrationTrustedRunner runner,
        MigrationSnapshot snapshot)
    {
        List<VerifiedReleaseActivationMigrationManifestEntry> entries = [];
        entries.AddRange(snapshot.Directories.Select(directory =>
            new VerifiedReleaseActivationMigrationManifestEntry(
                directory.Kind,
                VerifiedReleaseActivationMigrationManifestEntryKind.Directory,
                directory.RelativePath,
                Length: null,
                Sha256: string.Empty)));
        entries.AddRange(snapshot.Files.Select(file =>
            new VerifiedReleaseActivationMigrationManifestEntry(
                file.Kind,
                VerifiedReleaseActivationMigrationManifestEntryKind.File,
                file.RelativePath,
                file.Length,
                Convert.ToHexString(file.Sha256).ToLowerInvariant())));
        VerifiedReleaseActivationMigrationManifest manifest = new(
            ManifestSchemaVersion,
            m_timeProvider.GetUtcNow(),
            plan.ActivationPlan.SetupRevision,
            plan.ActivationPlan.InstalledReleaseIdentity,
            plan.ActivationPlan.TargetReleaseIdentity,
            plan.MigrationIdentity,
            plan.FromConfigurationSchemaVersion!.Value,
            plan.ToConfigurationSchemaVersion!.Value,
            runner.RunnerIdentity,
            runner.RunnerProtocolVersion,
            Convert.ToHexString(plan.ConfigurationBackup.ManifestSha256)
                .ToLowerInvariant(),
            snapshot.Directories.Count,
            snapshot.Files.Count,
            snapshot.ContentBytes,
            entries
                .OrderBy(entry => entry.Source)
                .ThenBy(entry => entry.Kind)
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            ManifestJsonOptions);
        if (bytes.Length is < 1 or > MaximumManifestBytes)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .MigrationManifestFailed,
                "The migration manifest exceeded its bounded size.");
        }
        return new MigrationManifestArtifact(
            manifest,
            bytes,
            SHA256.HashData(bytes));
    }

    [SupportedOSPlatform("linux")]
    private static async Task WriteManifestAsync(
        VerifiedReleaseActivationMigrationPlan plan,
        MigrationManifestArtifact manifest,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(plan.StagingPath, "migration-manifest.json");
        if (!PathEquals(Path.GetDirectoryName(path), plan.StagingPath) ||
            PathEntryExists(path))
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .MigrationManifestFailed,
                "The host-owned migration manifest path is unsafe or already present.");
        }
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        await using FileStream stream = new(path, options);
        await stream.WriteAsync(manifest.Bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        File.SetUnixFileMode(path, PrivateWritableFileMode);
    }

    [SupportedOSPlatform("linux")]
    private static void FreezeTree(string rootPath)
    {
        foreach (string file in Directory.EnumerateFiles(
                     rootPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(file, PrivateImmutableFileMode);
        }
        string[] directories = Directory.EnumerateDirectories(
                rootPath,
                "*",
                SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length)
            .ToArray();
        foreach (string directory in directories)
        {
            File.SetUnixFileMode(directory, PrivateImmutableDirectoryMode);
        }
        File.SetUnixFileMode(rootPath, PrivateImmutableDirectoryMode);
    }

    [SupportedOSPlatform("linux")]
    private static async Task ValidateImmutableMigrationTreeAsync(
        string rootPath,
        MigrationManifestArtifact manifest,
        CancellationToken cancellationToken)
    {
        DirectoryInfo root = new(rootPath);
        root.Refresh();
        ValidateImmutableDirectory(root);
        Dictionary<string, VerifiedReleaseActivationMigrationManifestEntry>
            expected = CreateExpectedMigrationTree(manifest);
        HashSet<string> observed = new(StringComparer.Ordinal);

        foreach (string directoryPath in Directory.EnumerateDirectories(
                     rootPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = new(directoryPath);
            directory.Refresh();
            ValidateImmutableDirectory(directory);
            string relative = NormalizeRelative(
                Path.GetRelativePath(rootPath, directory.FullName));
            if (!expected.TryGetValue(
                    relative,
                    out VerifiedReleaseActivationMigrationManifestEntry? entry) ||
                entry.Kind !=
                    VerifiedReleaseActivationMigrationManifestEntryKind.Directory ||
                !observed.Add(relative))
            {
                throw Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .StagedTreeInvalid,
                    "The immutable migration tree contains an unexpected directory.");
            }
        }

        foreach (string filePath in Directory.EnumerateFiles(
                     rootPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo file = new(filePath);
            file.Refresh();
            ValidateImmutableFile(file);
            string relative = NormalizeRelative(
                Path.GetRelativePath(rootPath, file.FullName));
            if (!expected.TryGetValue(
                    relative,
                    out VerifiedReleaseActivationMigrationManifestEntry? entry) ||
                entry.Kind !=
                    VerifiedReleaseActivationMigrationManifestEntryKind.File ||
                entry.Length != file.Length ||
                !observed.Add(relative))
            {
                throw Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .StagedTreeInvalid,
                    "The immutable migration tree contains an unexpected file.");
            }
            byte[] digest = await HashFileAsync(file.FullName, cancellationToken);
            string digestHex = Convert.ToHexString(digest).ToLowerInvariant();
            CryptographicOperations.ZeroMemory(digest);
            if (!string.Equals(digestHex, entry.Sha256, StringComparison.Ordinal))
            {
                throw Failure(
                    VerifiedReleaseActivationMigrationExecutionFailureCode
                        .StagedTreeInvalid,
                    "An immutable migration file failed digest validation.");
            }
        }

        if (!expected.Keys.All(observed.Contains))
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .StagedTreeInvalid,
                "The immutable migration tree is incomplete.");
        }
    }

    private static Dictionary<string,
        VerifiedReleaseActivationMigrationManifestEntry>
        CreateExpectedMigrationTree(MigrationManifestArtifact manifest)
    {
        Dictionary<string,
            VerifiedReleaseActivationMigrationManifestEntry> expected =
            new(StringComparer.Ordinal)
            {
                ["migration-manifest.json"] =
                    new VerifiedReleaseActivationMigrationManifestEntry(
                        VerifiedReleaseActivationConfigurationBackupSourceKind
                            .Configuration,
                        VerifiedReleaseActivationMigrationManifestEntryKind.File,
                        "migration-manifest.json",
                        manifest.Bytes.Length,
                        Convert.ToHexString(manifest.Sha256).ToLowerInvariant())
            };
        foreach (VerifiedReleaseActivationMigrationManifestEntry entry in
                 manifest.Manifest.Entries)
        {
            string prefix = SourceDirectoryName(entry.Source);
            string relative = entry.Path == "."
                ? prefix
                : $"{prefix}/{entry.Path}";
            expected.Add(relative, entry);
        }
        return expected;
    }

    private static bool EquivalentBackupSnapshots(
        BackupSnapshot left,
        BackupSnapshot right) =>
        left.Manifest.SetupRevision == right.Manifest.SetupRevision &&
        left.Manifest.CreatedAt == right.Manifest.CreatedAt &&
        left.Manifest.SourceBytes == right.Manifest.SourceBytes &&
        left.Manifest.DirectoryCount == right.Manifest.DirectoryCount &&
        left.Manifest.FileCount == right.Manifest.FileCount &&
        left.ManifestBytes.AsSpan().SequenceEqual(right.ManifestBytes) &&
        left.Directories.SequenceEqual(right.Directories) &&
        left.Files.SequenceEqual(right.Files);

    private static bool MatchesStatus(
        ReleaseStatusReadResult status,
        VerifiedReleaseActivationPlan plan) =>
        status.Succeeded &&
        status.FailureCode == ReleaseStatusFailureCode.None &&
        status.SetupSchemaVersion is >= 1 &&
        status.SetupRevision == plan.SetupRevision &&
        status.SetupComplete &&
        status.SetupLockMode == InstallationSetupLockMode.Complete &&
        status.LastCompletedStep == InstallationSetupStep.Administrator &&
        status.UpdateChannel == plan.UpdateChannel &&
        string.Equals(
            status.PinnedReleaseIdentity,
            plan.PinnedReleaseIdentity,
            StringComparison.Ordinal) &&
        status.InstallTransmitSupport == plan.InstallTransmitSupport &&
        status.ReleaseDirectoryPresent &&
        status.AvailableReleaseIdentities is not null &&
        status.AvailableReleaseCount == status.AvailableReleaseIdentities.Count &&
        status.AvailableReleaseIdentities.Contains(
            plan.InstalledReleaseIdentity,
            StringComparer.Ordinal) &&
        status.AvailableReleaseIdentities.Contains(
            plan.TargetReleaseIdentity,
            StringComparer.Ordinal) &&
        status.CurrentPointerPresent &&
        string.Equals(
            status.ActiveReleaseIdentity,
            plan.InstalledReleaseIdentity,
            StringComparison.Ordinal) &&
        !string.Equals(
            status.ActiveReleaseIdentity,
            plan.TargetReleaseIdentity,
            StringComparison.Ordinal);

    private static bool EquivalentStatus(
        ReleaseStatusReadResult left,
        ReleaseStatusReadResult right) =>
        left.Succeeded == right.Succeeded &&
        left.FailureCode == right.FailureCode &&
        left.SetupSchemaVersion == right.SetupSchemaVersion &&
        left.SetupRevision == right.SetupRevision &&
        left.SetupComplete == right.SetupComplete &&
        left.SetupLockMode == right.SetupLockMode &&
        left.LastCompletedStep == right.LastCompletedStep &&
        left.UpdateChannel == right.UpdateChannel &&
        string.Equals(
            left.PinnedReleaseIdentity,
            right.PinnedReleaseIdentity,
            StringComparison.Ordinal) &&
        left.InstallTransmitSupport == right.InstallTransmitSupport &&
        left.ReleaseDirectoryPresent == right.ReleaseDirectoryPresent &&
        left.AvailableReleaseCount == right.AvailableReleaseCount &&
        left.AvailableReleaseIdentities is not null &&
        right.AvailableReleaseIdentities is not null &&
        left.AvailableReleaseIdentities.SequenceEqual(
            right.AvailableReleaseIdentities,
            StringComparer.Ordinal) &&
        left.CurrentPointerPresent == right.CurrentPointerPresent &&
        string.Equals(
            left.ActiveReleaseIdentity,
            right.ActiveReleaseIdentity,
            StringComparison.Ordinal) &&
        left.RollbackCandidateKnown == right.RollbackCandidateKnown;

    private static async Task<byte[]> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[1024];
        StringBuilder output = new();
        while (true)
        {
            int read = await reader.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (read == 0)
            {
                return output.ToString();
            }
            if (output.Length > maximumCharacters - read)
            {
                throw new InvalidDataException(
                    "The migration runner output exceeded its bound.");
            }
            _ = output.Append(buffer, 0, read);
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or Win32Exception or
                NotSupportedException)
        {
        }
        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or TimeoutException or
                Win32Exception)
        {
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateNonLinkDirectory(string path)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .UnsafeMigrationLayout,
                "A migration parent is missing or linked.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidatePrivateWritableDirectory(string path) =>
        ValidatePrivateMutableDirectory(new DirectoryInfo(path));

    [SupportedOSPlatform("linux")]
    private static void ValidatePrivateMutableDirectory(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .StagedTreeInvalid,
                "A staged migration directory is missing or linked.");
        }
        UnixFileMode mode = File.GetUnixFileMode(directory.FullName);
        if ((mode & UnixFileMode.UserRead) == 0 ||
            (mode & UnixFileMode.UserWrite) == 0 ||
            (mode & UnixFileMode.UserExecute) == 0 ||
            (mode & GroupOrOtherModes) != 0)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .StagedTreeInvalid,
                "A staged migration directory does not have private writable permissions.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidatePrivateMutableFile(FileInfo file)
    {
        if (!file.Exists ||
            (file.Attributes & FileAttributes.Directory) != 0 ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.LinkTarget is not null)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .StagedTreeInvalid,
                "A staged migration file is missing, linked, or not regular.");
        }
        UnixFileMode mode = File.GetUnixFileMode(file.FullName);
        if ((mode & UnixFileMode.UserRead) == 0 ||
            (mode & UnixFileMode.UserWrite) == 0 ||
            (mode & GroupOrOtherModes) != 0)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .StagedTreeInvalid,
                "A staged migration file does not have private writable permissions.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutableDirectory(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            File.GetUnixFileMode(directory.FullName) !=
                PrivateImmutableDirectoryMode)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .BackupSourceInvalid,
                "An immutable migration or backup directory is missing, linked, or mutable.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutableFile(FileInfo file)
    {
        if (!file.Exists ||
            (file.Attributes & FileAttributes.Directory) != 0 ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.LinkTarget is not null ||
            File.GetUnixFileMode(file.FullName) != PrivateImmutableFileMode)
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .BackupSourceInvalid,
                "An immutable migration or backup file is missing, linked, or mutable.");
        }
    }

    private static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaximumRelativePathLength ||
            value.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(value))
        {
            return false;
        }
        if (value == ".")
        {
            return true;
        }
        string[] segments = value.Split('/');
        return segments.All(segment =>
            segment.Length > 0 && segment is not "." and not "..");
    }

    private static string ResolveRelative(string root, string relative)
    {
        string path = relative == "."
            ? root
            : Path.GetFullPath(
                Path.Combine(
                    root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrDescendant(path, root))
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .UnsafeMigrationLayout,
                "A relative migration path escaped its validated root.");
        }
        return path;
    }

    private static string NormalizeRelative(string relative)
    {
        string normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
        if (!IsSafeRelativePath(normalized))
        {
            throw Failure(
                VerifiedReleaseActivationMigrationExecutionFailureCode
                    .StagedTreeInvalid,
                "A migrated staged entry has an unsafe relative path.");
        }
        return normalized;
    }

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        string candidateFull = Path.GetFullPath(candidate);
        string rootFull = Path.GetFullPath(root);
        if (PathEquals(candidateFull, rootFull))
        {
            return true;
        }
        string prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(prefix, PathComparison);
    }

    private static bool PathEntryExists(string path) =>
        File.Exists(path) || Directory.Exists(path) ||
        new FileInfo(path).LinkTarget is not null ||
        new DirectoryInfo(path).LinkTarget is not null;

    private static bool PathEquals(string? left, string? right) =>
        !string.IsNullOrEmpty(left) &&
        !string.IsNullOrEmpty(right) &&
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            PathComparison);

    private static string SourceDirectoryName(
        VerifiedReleaseActivationConfigurationBackupSourceKind kind) =>
        kind switch
        {
            VerifiedReleaseActivationConfigurationBackupSourceKind.Configuration =>
                "configuration",
            VerifiedReleaseActivationConfigurationBackupSourceKind.State => "state",
            VerifiedReleaseActivationConfigurationBackupSourceKind.Secret =>
                "secrets",
            _ => throw new InvalidOperationException(
                "Unsupported migration source kind.")
        };

    [SupportedOSPlatform("linux")]
    private static bool TryCleanup(string path)
    {
        try
        {
            if (!PathEntryExists(path))
            {
                return true;
            }
            if (!Path.IsPathFullyQualified(path) ||
                string.IsNullOrEmpty(Path.GetFileName(path)))
            {
                return false;
            }
            foreach (string directory in Directory.EnumerateDirectories(
                         path,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(directory, PrivateWritableDirectoryMode);
            }
            foreach (string file in Directory.EnumerateFiles(
                         path,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(file, PrivateWritableFileMode);
            }
            File.SetUnixFileMode(path, PrivateWritableDirectoryMode);
            Directory.Delete(path, recursive: true);
            return !PathEntryExists(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static void TryFreezeRoot(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                FreezeTree(path);
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
        }
    }

    private void MarkReconciliationRequired()
    {
        lock (m_stateGate)
        {
            m_reconciliationRequired = true;
        }
    }

    private static MigrationExecutionException Failure(
        VerifiedReleaseActivationMigrationExecutionFailureCode failureCode,
        string message) =>
        new(failureCode, message);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed class MigrationExecutionException : Exception
    {
        internal MigrationExecutionException(
            VerifiedReleaseActivationMigrationExecutionFailureCode failureCode,
            string message)
            : base(message)
        {
            FailureCode = failureCode;
        }

        internal VerifiedReleaseActivationMigrationExecutionFailureCode FailureCode
        {
            get;
        }
    }

    private sealed record BackupDirectory(
        VerifiedReleaseActivationConfigurationBackupSourceKind Kind,
        string RelativePath,
        string SourcePath,
        string StagedPath);

    private sealed record BackupFile(
        VerifiedReleaseActivationConfigurationBackupSourceKind Kind,
        string RelativePath,
        string SourcePath,
        string StagedPath,
        long Length,
        string Sha256);

    private sealed record BackupSnapshot(
        VerifiedReleaseActivationConfigurationBackupManifest Manifest,
        byte[] ManifestBytes,
        IReadOnlyList<BackupDirectory> Directories,
        IReadOnlyList<BackupFile> Files);

    private sealed record MigrationDirectory(
        VerifiedReleaseActivationConfigurationBackupSourceKind Kind,
        string RelativePath,
        string FullPath);

    private sealed record MigrationFile(
        VerifiedReleaseActivationConfigurationBackupSourceKind Kind,
        string RelativePath,
        string FullPath,
        long Length,
        byte[] Sha256);

    private sealed record MigrationSnapshot(
        IReadOnlyList<MigrationDirectory> Directories,
        IReadOnlyList<MigrationFile> Files,
        long ContentBytes);

    private sealed record MigrationManifestArtifact(
        VerifiedReleaseActivationMigrationManifest Manifest,
        byte[] Bytes,
        byte[] Sha256);

    private sealed record RunnerExecutionResult(
        bool Succeeded,
        VerifiedReleaseActivationMigrationExecutionFailureCode FailureCode,
        string Message,
        bool RunnerInvoked,
        bool MigrationExecuted)
    {
        internal static RunnerExecutionResult Success() =>
            new(
                true,
                VerifiedReleaseActivationMigrationExecutionFailureCode.None,
                string.Empty,
                RunnerInvoked: true,
                MigrationExecuted: true);

        internal static RunnerExecutionResult Failure(
            VerifiedReleaseActivationMigrationExecutionFailureCode failureCode,
            string message,
            bool runnerInvoked = false) =>
            new(
                false,
                failureCode,
                message,
                runnerInvoked,
                MigrationExecuted: false);
    }
}
