using System.Buffers;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationConfigurationBackupFailureCode
{
    None = 0,
    UnsupportedPlatform = 1,
    BackupPlanNotEligible = 2,
    BackupPlanUnavailable = 3,
    BackupPlanMismatch = 4,
    StatusUnavailable = 5,
    StatusMismatch = 6,
    UnsafeBackupLayout = 7,
    BackupAlreadyPresent = 8,
    StagingAlreadyPresent = 9,
    UnsafeSourceLayout = 10,
    SourceChangedDuringBackup = 11,
    BackupWriteFailed = 12,
    CleanupFailed = 13,
    AtomicPublishFailed = 14,
    PublishedStateRequiresReconciliation = 15
}

public sealed record VerifiedReleaseActivationConfigurationBackupReport(
    bool Succeeded,
    VerifiedReleaseActivationConfigurationBackupFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int SourceDirectoryCount,
    int DirectoryCount,
    int FileCount,
    long BackupBytes,
    bool SourceSnapshotStable,
    bool ManifestWritten,
    bool StagingTreeImmutable,
    bool AtomicPublicationCompleted,
    bool PublishedTreeValidated,
    bool ExistingBackupOverwritten,
    bool ConfigurationBackupReady,
    bool CurrentPointerChanged,
    bool ActivationPerformed,
    bool ReconciliationRequired)
{
    internal VerifiedReleaseActivationConfigurationBackup? Backup { get; init; }

    internal static VerifiedReleaseActivationConfigurationBackupReport Failure(
        VerifiedReleaseActivationConfigurationBackupFailureCode failureCode,
        string message,
        VerifiedReleaseActivationConfigurationBackupPlan? plan = null,
        int directoryCount = 0,
        int fileCount = 0,
        long backupBytes = 0,
        bool sourceSnapshotStable = false,
        bool manifestWritten = false,
        bool stagingTreeImmutable = false,
        bool atomicPublicationCompleted = false,
        bool publishedTreeValidated = false,
        bool reconciliationRequired = false) =>
        new(
            false,
            failureCode,
            message,
            plan?.ActivationPlan.SetupRevision,
            plan?.ActivationPlan.InstalledReleaseIdentity ?? string.Empty,
            plan?.ActivationPlan.TargetReleaseIdentity ?? string.Empty,
            plan?.Sources.Count ?? 0,
            directoryCount,
            fileCount,
            backupBytes,
            sourceSnapshotStable,
            manifestWritten,
            stagingTreeImmutable,
            atomicPublicationCompleted,
            publishedTreeValidated,
            ExistingBackupOverwritten: false,
            ConfigurationBackupReady: false,
            CurrentPointerChanged: false,
            ActivationPerformed: false,
            reconciliationRequired);

    internal static VerifiedReleaseActivationConfigurationBackupReport Success(
        VerifiedReleaseActivationConfigurationBackup backup) =>
        new(
            true,
            VerifiedReleaseActivationConfigurationBackupFailureCode.None,
            "The exact-plan configuration backup was atomically published and validated without changing current or activating the target release.",
            backup.Plan.ActivationPlan.SetupRevision,
            backup.Plan.ActivationPlan.InstalledReleaseIdentity,
            backup.Plan.ActivationPlan.TargetReleaseIdentity,
            backup.Plan.Sources.Count,
            backup.DirectoryCount,
            backup.FileCount,
            backup.BackupBytes,
            SourceSnapshotStable: true,
            ManifestWritten: true,
            StagingTreeImmutable: true,
            AtomicPublicationCompleted: true,
            PublishedTreeValidated: true,
            ExistingBackupOverwritten: false,
            ConfigurationBackupReady: true,
            CurrentPointerChanged: false,
            ActivationPerformed: false,
            ReconciliationRequired: false)
        {
            Backup = backup
        };
}

public sealed record VerifiedReleaseActivationConfigurationBackupDiagnostics(
    bool Registered,
    bool ExactBackupPlanInputRegistered,
    bool ReleaseStatusDoubleReadRegistered,
    bool BoundedSourceTraversalRegistered,
    bool SymbolicLinkRejectionRegistered,
    bool SourceDigestValidationRegistered,
    bool PrivateStagingRegistered,
    bool ManifestWriteRegistered,
    bool DurableFlushRegistered,
    bool ImmutableFreezeRegistered,
    bool AtomicDirectoryPublishRegistered,
    bool PublishedTreeValidationRegistered,
    bool CleanupRegistered,
    bool ExactPlanEvidenceRegistered,
    bool ExistingBackupOverwriteRegistered,
    bool CurrentPointerMutationRegistered,
    bool ActivationExecutionRegistered,
    bool MigrationExecutionRegistered,
    bool ServiceControlRegistered,
    bool HealthProbeCallerRegistered,
    bool RollbackExecutionRegistered,
    bool OperationalCallerRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool HttpCallerRegistered,
    bool WebSocketCallerRegistered,
    bool HostedServiceCallerRegistered,
    bool TimerCallerRegistered,
    bool AetherRemoteCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record VerifiedReleaseActivationConfigurationBackupStateDiagnostics(
    bool ConfigurationBackupReady,
    bool ExactActivationPlanBound,
    int SourceDirectoryCount,
    int DirectoryCount,
    int FileCount,
    long BackupBytes,
    bool ManifestPresent,
    bool PublishedTreeImmutable,
    bool ReconciliationRequired,
    bool CurrentPointerChanged,
    bool ActivationAuthorized);

internal sealed record VerifiedReleaseActivationConfigurationBackupObservation(
    bool ConfigurationBackupReady,
    int SourceDirectoryCount,
    int DirectoryCount,
    int FileCount,
    long BackupBytes,
    DateTimeOffset? CompletedAt,
    bool ReconciliationRequired);

internal sealed class VerifiedReleaseActivationConfigurationBackup
{
    internal VerifiedReleaseActivationConfigurationBackup(
        VerifiedReleaseActivationConfigurationBackupPlan plan,
        int directoryCount,
        int fileCount,
        long backupBytes,
        byte[] manifestSha256,
        DateTimeOffset completedAt)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        DirectoryCount = directoryCount;
        FileCount = fileCount;
        BackupBytes = backupBytes;
        ManifestSha256 = manifestSha256.ToArray();
        CompletedAt = completedAt;
    }

    internal VerifiedReleaseActivationConfigurationBackupPlan Plan { get; }
    internal int DirectoryCount { get; }
    internal int FileCount { get; }
    internal long BackupBytes { get; }
    internal byte[] ManifestSha256 { get; }
    internal DateTimeOffset CompletedAt { get; }
}

internal enum VerifiedReleaseActivationConfigurationBackupManifestEntryKind
{
    Directory = 1,
    File = 2
}

internal sealed record VerifiedReleaseActivationConfigurationBackupManifestEntry(
    VerifiedReleaseActivationConfigurationBackupSourceKind Source,
    VerifiedReleaseActivationConfigurationBackupManifestEntryKind Kind,
    string Path,
    long? Length,
    int UnixMode,
    uint UserId,
    uint GroupId,
    string Sha256);

internal sealed record VerifiedReleaseActivationConfigurationBackupManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    long SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int SourceDirectoryCount,
    int DirectoryCount,
    int FileCount,
    long SourceBytes,
    IReadOnlyList<VerifiedReleaseActivationConfigurationBackupManifestEntry>
        Entries);

public sealed class VerifiedReleaseActivationConfigurationBackupService
{
    internal const int MaximumDirectoryCount = 512;
    internal const int MaximumFileCount = 4096;
    internal const long MaximumFileLength = 128L * 1024 * 1024;
    internal const long MaximumSourceBytes = 1024L * 1024 * 1024;
    internal const int MaximumRelativePathLength = 512;
    internal const int MaximumManifestBytes = 4 * 1024 * 1024;

    private const int BufferSize = 128 * 1024;
    internal const int ManifestSchemaVersion = 3;
    private const UnixFileMode ForbiddenSharedWritableUnixModes =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode ForbiddenSecretSharedUnixModes =
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;
    private const UnixFileMode PrivateWritableDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;
    private const UnixFileMode PrivateImmutableDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateWritableFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateImmutableFileMode = UnixFileMode.UserRead;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;
    private readonly Action<string, string> m_directoryMove;
    private readonly TimeProvider m_timeProvider;
    private readonly SemaphoreSlim m_executionGate = new(1, 1);
    private readonly object m_stateGate = new();
    private VerifiedReleaseActivationConfigurationBackup? m_completed;
    private bool m_reconciliationRequired;

    public VerifiedReleaseActivationConfigurationBackupService(
        ReleaseInstallationStatusReader statusReader)
        : this(
            statusReader is null
                ? throw new ArgumentNullException(nameof(statusReader))
                : statusReader.ReadAsync,
            Directory.Move,
            TimeProvider.System)
    {
    }

    internal VerifiedReleaseActivationConfigurationBackupService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Action<string, string>? directoryMove = null,
        TimeProvider? timeProvider = null)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_directoryMove = directoryMove ?? Directory.Move;
        m_timeProvider = timeProvider ?? TimeProvider.System;
        Snapshot = new VerifiedReleaseActivationConfigurationBackupDiagnostics(
            Registered: true,
            ExactBackupPlanInputRegistered: true,
            ReleaseStatusDoubleReadRegistered: true,
            BoundedSourceTraversalRegistered: true,
            SymbolicLinkRejectionRegistered: true,
            SourceDigestValidationRegistered: true,
            PrivateStagingRegistered: true,
            ManifestWriteRegistered: true,
            DurableFlushRegistered: true,
            ImmutableFreezeRegistered: true,
            AtomicDirectoryPublishRegistered: true,
            PublishedTreeValidationRegistered: true,
            CleanupRegistered: true,
            ExactPlanEvidenceRegistered: true,
            ExistingBackupOverwriteRegistered: false,
            CurrentPointerMutationRegistered: false,
            ActivationExecutionRegistered: false,
            MigrationExecutionRegistered: false,
            ServiceControlRegistered: false,
            HealthProbeCallerRegistered: false,
            RollbackExecutionRegistered: false,
            OperationalCallerRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            HttpCallerRegistered: false,
            WebSocketCallerRegistered: false,
            HostedServiceCallerRegistered: false,
            TimerCallerRegistered: false,
            AetherRemoteCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationConfigurationBackupDiagnostics Snapshot
    {
        get;
    }

    public VerifiedReleaseActivationConfigurationBackupStateDiagnostics State
    {
        get
        {
            lock (m_stateGate)
            {
                VerifiedReleaseActivationConfigurationBackup? completed =
                    m_completed;
                return new VerifiedReleaseActivationConfigurationBackupStateDiagnostics(
                    ConfigurationBackupReady:
                        completed is not null && !m_reconciliationRequired,
                    ExactActivationPlanBound: completed is not null,
                    SourceDirectoryCount: completed?.Plan.Sources.Count ?? 0,
                    DirectoryCount: completed?.DirectoryCount ?? 0,
                    FileCount: completed?.FileCount ?? 0,
                    BackupBytes: completed?.BackupBytes ?? 0,
                    ManifestPresent: completed is not null,
                    PublishedTreeImmutable: completed is not null,
                    ReconciliationRequired: m_reconciliationRequired,
                    CurrentPointerChanged: false,
                    ActivationAuthorized: false);
            }
        }
    }
    [SupportedOSPlatform("linux")]
    internal async Task<VerifiedReleaseActivationConfigurationBackupReport>
        ExecuteAsync(
            VerifiedReleaseActivationConfigurationBackupPlanReport planReport,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planReport);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsLinux())
        {
            return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsupportedPlatform,
                "Exact-plan configuration backup execution requires a supported Linux runtime.");
        }

        VerifiedReleaseActivationConfigurationBackupPlan? plan =
            ValidatePlanReport(planReport);
        if (plan is null)
        {
            return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                planReport.Plan is null
                    ? VerifiedReleaseActivationConfigurationBackupFailureCode
                        .BackupPlanUnavailable
                    : VerifiedReleaseActivationConfigurationBackupFailureCode
                        .BackupPlanNotEligible,
                "A successful exact-plan configuration-backup plan is required.");
        }
        if (!ValidatePlanShape(planReport, plan))
        {
            return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .BackupPlanMismatch,
                "The backup plan no longer matches its public summary or exact activation plan.",
                plan);
        }

        await m_executionGate.WaitAsync(cancellationToken);
        try
        {
            lock (m_stateGate)
            {
                if (m_reconciliationRequired)
                {
                    return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .PublishedStateRequiresReconciliation,
                        "A previous backup publication requires local reconciliation before another attempt.",
                        plan,
                        reconciliationRequired: true);
                }
                if (m_completed is not null)
                {
                    return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .BackupAlreadyPresent,
                        "A configuration backup is already retained for this service lifetime and will not be overwritten.",
                        plan);
                }
            }

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
                return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                    VerifiedReleaseActivationConfigurationBackupFailureCode
                        .StatusUnavailable,
                    "Release status could not be read before configuration backup.",
                    plan);
            }

            if (!MatchesStatus(beforeStatus, plan.ActivationPlan))
            {
                return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                    beforeStatus.Succeeded
                        ? VerifiedReleaseActivationConfigurationBackupFailureCode
                            .StatusMismatch
                        : VerifiedReleaseActivationConfigurationBackupFailureCode
                            .StatusUnavailable,
                    "Completed setup, inactive release inventory, or current no longer matches the exact activation plan.",
                    plan);
            }

            bool stagingCreated = false;
            bool atomicPublicationCompleted = false;
            int directoryCount = 0;
            int fileCount = 0;
            long backupBytes = 0;
            bool sourceSnapshotStable = false;
            bool manifestWritten = false;
            bool stagingTreeImmutable = false;
            try
            {
                ValidateBackupLayout(plan);
                PreparePrivateBackupParents(plan);
                EnsureAbsent(plan.PublishedPath, published: true);
                EnsureAbsent(plan.StagingPath, published: false);

                SourceSnapshot beforeSnapshot =
                    await CaptureSourceSnapshotAsync(
                        plan,
                        includeHashes: false,
                        cancellationToken);
                directoryCount = beforeSnapshot.Directories.Count;
                fileCount = beforeSnapshot.Files.Count;

                Directory.CreateDirectory(plan.StagingPath);
                File.SetUnixFileMode(
                    plan.StagingPath,
                    PrivateWritableDirectoryMode);
                ValidatePrivateDirectory(plan.StagingPath);
                stagingCreated = true;

                IReadOnlyList<CopiedFile> copied =
                    await CopySourceSnapshotAsync(
                        beforeSnapshot,
                        cancellationToken);
                SourceSnapshot afterSnapshot =
                    await CaptureSourceSnapshotAsync(
                        plan,
                        includeHashes: true,
                        cancellationToken);
                if (!EquivalentSnapshots(beforeSnapshot, afterSnapshot, copied))
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .SourceChangedDuringBackup,
                        "Configuration, state, or secret content changed while the backup was being copied.");
                }
                sourceSnapshotStable = true;

                BackupManifestArtifact manifest =
                    CreateManifest(plan, afterSnapshot);
                await WriteManifestAsync(
                    plan,
                    manifest,
                    cancellationToken);
                manifestWritten = true;
                backupBytes = checked(
                    afterSnapshot.SourceBytes + manifest.Bytes.Length);

                FreezeBackupTree(plan.StagingPath);
                await ValidateImmutableBackupTreeAsync(
                    plan.StagingPath,
                    manifest,
                    cancellationToken);
                stagingTreeImmutable = true;

                ReleaseStatusReadResult afterCopyStatus =
                    await m_statusReader(cancellationToken);
                if (!EquivalentStatus(beforeStatus, afterCopyStatus) ||
                    !MatchesStatus(afterCopyStatus, plan.ActivationPlan))
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .StatusMismatch,
                        "Release status changed while the exact-plan configuration backup was prepared.");
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
                        return VerifiedReleaseActivationConfigurationBackupReport
                            .Failure(
                                VerifiedReleaseActivationConfigurationBackupFailureCode
                                    .PublishedStateRequiresReconciliation,
                                "The atomic backup publication outcome is ambiguous and requires local reconciliation.",
                                plan,
                                directoryCount,
                                fileCount,
                                backupBytes,
                                sourceSnapshotStable,
                                manifestWritten,
                                stagingTreeImmutable,
                                atomicPublicationCompleted: publishedPresent,
                                publishedTreeValidated: false,
                                reconciliationRequired: true);
                    }
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .AtomicPublishFailed,
                        "The immutable configuration backup could not be atomically published.");
                }

                if (PathEntryExists(plan.StagingPath) ||
                    !PathEntryExists(plan.PublishedPath))
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .PublishedStateRequiresReconciliation,
                        "Atomic publication did not leave one consumed staging tree and one published backup.");
                }

                await ValidateImmutableBackupTreeAsync(
                    plan.PublishedPath,
                    manifest,
                    CancellationToken.None);
                ReleaseStatusReadResult finalStatus =
                    await m_statusReader(CancellationToken.None);
                if (!EquivalentStatus(beforeStatus, finalStatus) ||
                    !MatchesStatus(finalStatus, plan.ActivationPlan))
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .PublishedStateRequiresReconciliation,
                        "Release status changed unexpectedly after atomic backup publication.");
                }

                VerifiedReleaseActivationConfigurationBackup completed = new(
                    plan,
                    directoryCount,
                    fileCount,
                    backupBytes,
                    manifest.Sha256,
                    manifest.Manifest.CreatedAt);
                lock (m_stateGate)
                {
                    m_completed = completed;
                }
                return VerifiedReleaseActivationConfigurationBackupReport.Success(
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
                return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                    VerifiedReleaseActivationConfigurationBackupFailureCode
                        .CleanupFailed,
                    "Cancelled configuration backup could not remove its private staging tree.",
                    plan,
                    directoryCount,
                    fileCount,
                    backupBytes,
                    sourceSnapshotStable,
                    manifestWritten,
                    stagingTreeImmutable,
                    atomicPublicationCompleted,
                    publishedTreeValidated: false,
                    reconciliationRequired: true);
            }
            catch (BackupException exception)
            {
                if (atomicPublicationCompleted ||
                    (stagingCreated && PathEntryExists(plan.PublishedPath)))
                {
                    MarkReconciliationRequired();
                    TryFreezeRoot(plan.PublishedPath);
                    return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .PublishedStateRequiresReconciliation,
                        exception.Message,
                        plan,
                        directoryCount,
                        fileCount,
                        backupBytes,
                        sourceSnapshotStable,
                        manifestWritten,
                        stagingTreeImmutable,
                        atomicPublicationCompleted,
                        publishedTreeValidated: false,
                        reconciliationRequired: true);
                }
                if (!stagingCreated || TryCleanup(plan.StagingPath))
                {
                    return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                        exception.FailureCode,
                        exception.Message,
                        plan,
                        directoryCount,
                        fileCount,
                        backupBytes,
                        sourceSnapshotStable,
                        manifestWritten,
                        stagingTreeImmutable);
                }
                MarkReconciliationRequired();
                return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                    VerifiedReleaseActivationConfigurationBackupFailureCode
                        .CleanupFailed,
                    "Failed configuration backup also could not remove its private staging tree.",
                    plan,
                    directoryCount,
                    fileCount,
                    backupBytes,
                    sourceSnapshotStable,
                    manifestWritten,
                    stagingTreeImmutable,
                    reconciliationRequired: true);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    SecurityException or CryptographicException or ArgumentException or
                    InvalidOperationException or NotSupportedException or
                    PathTooLongException or OverflowException or JsonException)
            {
                if (atomicPublicationCompleted ||
                    (stagingCreated && PathEntryExists(plan.PublishedPath)))
                {
                    MarkReconciliationRequired();
                    TryFreezeRoot(plan.PublishedPath);
                    return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .PublishedStateRequiresReconciliation,
                        "The published configuration backup could not be fully reconciled after its atomic rename.",
                        plan,
                        directoryCount,
                        fileCount,
                        backupBytes,
                        sourceSnapshotStable,
                        manifestWritten,
                        stagingTreeImmutable,
                        atomicPublicationCompleted,
                        publishedTreeValidated: false,
                        reconciliationRequired: true);
                }
                if (!stagingCreated || TryCleanup(plan.StagingPath))
                {
                    return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .BackupWriteFailed,
                        "The exact-plan configuration backup could not be written and validated.",
                        plan,
                        directoryCount,
                        fileCount,
                        backupBytes,
                        sourceSnapshotStable,
                        manifestWritten,
                        stagingTreeImmutable);
                }
                MarkReconciliationRequired();
                return VerifiedReleaseActivationConfigurationBackupReport.Failure(
                    VerifiedReleaseActivationConfigurationBackupFailureCode
                        .CleanupFailed,
                    "Failed configuration backup also could not remove its private staging tree.",
                    plan,
                    directoryCount,
                    fileCount,
                    backupBytes,
                    sourceSnapshotStable,
                    manifestWritten,
                    stagingTreeImmutable,
                    reconciliationRequired: true);
            }
        }
        finally
        {
            m_executionGate.Release();
        }
    }

    internal VerifiedReleaseActivationConfigurationBackupObservation Observe(
        VerifiedReleaseActivationPlan activationPlan)
    {
        ArgumentNullException.ThrowIfNull(activationPlan);
        lock (m_stateGate)
        {
            VerifiedReleaseActivationConfigurationBackup? completed = m_completed;
            bool exact = completed is not null &&
                ReferenceEquals(
                    completed.Plan.ActivationPlan,
                    activationPlan);
            return new VerifiedReleaseActivationConfigurationBackupObservation(
                ConfigurationBackupReady:
                    exact && !m_reconciliationRequired,
                SourceDirectoryCount:
                    exact ? completed!.Plan.Sources.Count : 0,
                DirectoryCount: exact ? completed!.DirectoryCount : 0,
                FileCount: exact ? completed!.FileCount : 0,
                BackupBytes: exact ? completed!.BackupBytes : 0,
                CompletedAt: exact ? completed!.CompletedAt : null,
                ReconciliationRequired: m_reconciliationRequired);
        }
    }

    [SupportedOSPlatform("linux")]
    internal static async Task<
        VerifiedReleaseActivationConfigurationBackupManifest>
        RevalidatePublishedBackupAsync(
            VerifiedReleaseActivationConfigurationBackup backup,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Immutable activation-backup validation requires Linux.");
        }

        VerifiedReleaseActivationConfigurationBackupPlan plan = backup.Plan;
        FileInfo manifestFile = new(plan.ManifestPath);
        manifestFile.Refresh();
        if (!manifestFile.Exists ||
            (manifestFile.Attributes &
                (FileAttributes.ReparsePoint |
                 FileAttributes.Directory |
                 FileAttributes.Device |
                 FileAttributes.Offline)) != 0 ||
            manifestFile.LinkTarget is not null ||
            manifestFile.Length is < 1 or > MaximumManifestBytes ||
            File.GetUnixFileMode(manifestFile.FullName) !=
                PrivateImmutableFileMode)
        {
            throw new InvalidDataException(
                "The immutable activation-backup manifest is unavailable or unsafe.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(
            manifestFile.FullName,
            cancellationToken);
        manifestFile.Refresh();
        if (manifestFile.Length != bytes.Length ||
            File.GetUnixFileMode(manifestFile.FullName) !=
                PrivateImmutableFileMode)
        {
            throw new InvalidDataException(
                "The immutable activation-backup manifest changed while being read.");
        }
        byte[] digest = SHA256.HashData(bytes);
        if (backup.ManifestSha256.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(
                digest,
                backup.ManifestSha256))
        {
            throw new InvalidDataException(
                "The immutable activation-backup manifest digest changed.");
        }

        VerifiedReleaseActivationConfigurationBackupManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<
                    VerifiedReleaseActivationConfigurationBackupManifest>(
                    bytes,
                    ManifestJsonOptions) ??
                throw new InvalidDataException(
                    "The immutable activation-backup manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The immutable activation-backup manifest is malformed.",
                exception);
        }

        ValidateRetainedManifest(backup, manifest, bytes.Length);
        BackupManifestArtifact artifact = new(manifest, bytes, digest);
        try
        {
            await ValidateImmutableBackupTreeAsync(
                plan.PublishedPath,
                artifact,
                cancellationToken);
        }
        catch (BackupException exception)
        {
            throw new InvalidDataException(
                "The immutable activation-backup tree does not match its retained manifest.",
                exception);
        }
        return manifest;
    }

    private static void ValidateRetainedManifest(
        VerifiedReleaseActivationConfigurationBackup backup,
        VerifiedReleaseActivationConfigurationBackupManifest manifest,
        int manifestLength)
    {
        VerifiedReleaseActivationConfigurationBackupPlan plan = backup.Plan;
        VerifiedReleaseActivationPlan activation = plan.ActivationPlan;
        if (manifest.SchemaVersion != ManifestSchemaVersion ||
            manifest.CreatedAt != backup.CompletedAt ||
            manifest.SetupRevision != activation.SetupRevision ||
            !string.Equals(
                manifest.InstalledReleaseIdentity,
                activation.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.TargetReleaseIdentity,
                activation.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            manifest.SourceDirectoryCount != plan.Sources.Count ||
            manifest.DirectoryCount != backup.DirectoryCount ||
            manifest.FileCount != backup.FileCount ||
            manifestLength is < 1 or > MaximumManifestBytes ||
            checked(manifest.SourceBytes + manifestLength) != backup.BackupBytes ||
            manifest.Entries is null ||
            manifest.Entries.Count !=
                manifest.DirectoryCount + manifest.FileCount)
        {
            throw new InvalidDataException(
                "The immutable activation-backup manifest does not match its retained exact-plan artifact.");
        }

        HashSet<(VerifiedReleaseActivationConfigurationBackupSourceKind, string)>
            identities = [];
        int directoryCount = 0;
        int fileCount = 0;
        long sourceBytes = 0;
        foreach (VerifiedReleaseActivationConfigurationBackupManifestEntry entry in
                 manifest.Entries)
        {
            if (!Enum.IsDefined(entry.Source) ||
                !Enum.IsDefined(entry.Kind) ||
                !IsSafeManifestPath(entry.Path) ||
                !identities.Add((entry.Source, entry.Path)))
            {
                throw new InvalidDataException(
                    "The immutable activation-backup manifest contains an unsafe or duplicated entry.");
            }

            UnixFileMode mode = (UnixFileMode)entry.UnixMode;
            bool secret = entry.Source ==
                VerifiedReleaseActivationConfigurationBackupSourceKind.Secret;
            if (entry.Kind ==
                VerifiedReleaseActivationConfigurationBackupManifestEntryKind
                    .Directory)
            {
                directoryCount++;
                if (entry.Length is not null ||
                    !string.IsNullOrEmpty(entry.Sha256) ||
                    !IsSafeOriginalDirectoryMode(mode, secret))
                {
                    throw new InvalidDataException(
                        "The immutable activation-backup manifest contains invalid directory metadata.");
                }
                continue;
            }

            fileCount++;
            if (entry.Length is null or < 0 or > MaximumFileLength ||
                !IsLowerHexSha256(entry.Sha256) ||
                !IsSafeOriginalFileMode(mode, secret))
            {
                throw new InvalidDataException(
                    "The immutable activation-backup manifest contains invalid file metadata.");
            }
            sourceBytes = checked(sourceBytes + entry.Length.Value);
            if (sourceBytes > MaximumSourceBytes)
            {
                throw new InvalidDataException(
                    "The immutable activation-backup manifest exceeds its byte bound.");
            }
        }

        if (directoryCount != manifest.DirectoryCount ||
            fileCount != manifest.FileCount ||
            sourceBytes != manifest.SourceBytes ||
            plan.Sources.Any(source =>
                !identities.Contains((source.Kind, "."))))
        {
            throw new InvalidDataException(
                "The immutable activation-backup manifest is incomplete.");
        }
    }

    private static bool IsSafeManifestPath(string value)
    {
        if (value == ".")
        {
            return true;
        }
        return !string.IsNullOrEmpty(value) &&
            value.Length <= MaximumRelativePathLength &&
            value.Split('/', StringSplitOptions.None) is { Length: > 0 and <= 32 }
                segments &&
            segments.All(ValidSegment);
    }

    private static bool IsSafeOriginalDirectoryMode(
        UnixFileMode mode,
        bool secret) =>
        HasOnlyOrdinaryPermissionBits(mode) &&
        (mode & ForbiddenSharedWritableUnixModes) == 0 &&
        (mode & UnixFileMode.UserRead) != 0 &&
        (mode & UnixFileMode.UserExecute) != 0 &&
        (!secret || (mode & ForbiddenSecretSharedUnixModes) == 0);

    private static bool IsSafeOriginalFileMode(
        UnixFileMode mode,
        bool secret) =>
        HasOnlyOrdinaryPermissionBits(mode) &&
        (mode & ForbiddenSharedWritableUnixModes) == 0 &&
        (mode & UnixFileMode.UserRead) != 0 &&
        (!secret || (mode & ForbiddenSecretSharedUnixModes) == 0);

    private static bool HasOnlyOrdinaryPermissionBits(UnixFileMode mode)
    {
        const UnixFileMode ordinary =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherWrite |
            UnixFileMode.OtherExecute;
        return (mode & ~ordinary) == 0;
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static VerifiedReleaseActivationConfigurationBackupPlan?
        ValidatePlanReport(
            VerifiedReleaseActivationConfigurationBackupPlanReport report) =>
        report.Succeeded &&
        report.FailureCode ==
            VerifiedReleaseActivationConfigurationBackupPlanFailureCode.None &&
        report.SetupRevision is > 0 &&
        report.SourceDirectoryCount == 3 &&
        report.ConfigurationDirectoryIncluded &&
        report.StateDirectoryIncluded &&
        report.SecretDirectoryIncluded &&
        report.BackupRootSeparated &&
        report.ExactActivationPlanBound &&
        report.BackupManifestRequired &&
        report.AtomicPublicationRequired &&
        !report.SourceReadPerformed &&
        !report.BackupWritePerformed &&
        !report.ExistingBackupOverwritten &&
        !report.ConfigurationBackupReady &&
        !report.CurrentPointerChanged &&
        !report.ActivationAuthorized
            ? report.Plan
            : null;

    private static bool ValidatePlanShape(
        VerifiedReleaseActivationConfigurationBackupPlanReport report,
        VerifiedReleaseActivationConfigurationBackupPlan plan)
    {
        VerifiedReleaseActivationPlan activation = plan.ActivationPlan;
        if (report.SetupRevision != activation.SetupRevision ||
            !string.Equals(
                report.InstalledReleaseIdentity,
                activation.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.TargetReleaseIdentity,
                activation.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            plan.Sources.Count != 3 ||
            plan.ExistingBackupOverwriteAllowed ||
            !plan.AtomicPublicationRequired ||
            activation.SetupRevision < 1 ||
            !activation.ConfigurationBackupRequired ||
            !activation.AtomicCurrentPointerSwitchRequired ||
            !activation.ServiceHealthVerificationRequired ||
            !activation.AutomaticRollbackRequired ||
            !activation.OperatorApprovalRequired ||
            !IsCanonicalAbsolutePath(plan.BackupRootPath) ||
            !IsCanonicalAbsolutePath(plan.StagingPath) ||
            !IsCanonicalAbsolutePath(plan.PublishedPath) ||
            !IsCanonicalAbsolutePath(plan.ManifestPath))
        {
            return false;
        }

        string? revisionRoot = Path.GetDirectoryName(plan.PublishedPath);
        string? activationRoot = revisionRoot is null
            ? null
            : Path.GetDirectoryName(revisionRoot);
        if (revisionRoot is null ||
            activationRoot is null ||
            !PathEquals(
                Path.GetDirectoryName(activationRoot),
                plan.BackupRootPath) ||
            !string.Equals(
                Path.GetFileName(activationRoot),
                "activation",
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFileName(revisionRoot),
                $"setup-{activation.SetupRevision}",
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFileName(plan.PublishedPath),
                $"{activation.InstalledReleaseIdentity}-to-" +
                activation.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            !PathEquals(
                Path.GetDirectoryName(plan.StagingPath),
                revisionRoot) ||
            !string.Equals(
                Path.GetFileName(plan.StagingPath),
                $".{Path.GetFileName(plan.PublishedPath)}.staging",
                StringComparison.Ordinal) ||
            !PathEquals(
                Path.GetDirectoryName(plan.ManifestPath),
                plan.PublishedPath) ||
            !string.Equals(
                Path.GetFileName(plan.ManifestPath),
                "backup-manifest.json",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (plan.Sources.Count is < 2 or > 3 ||
            plan.Sources[0].Kind !=
                VerifiedReleaseActivationConfigurationBackupSourceKind.Configuration ||
            plan.Sources[1].Kind !=
                VerifiedReleaseActivationConfigurationBackupSourceKind.State ||
            (plan.Sources.Count == 3 &&
             plan.Sources[2].Kind !=
                VerifiedReleaseActivationConfigurationBackupSourceKind.Secret))
        {
            return false;
        }
        HashSet<string> sourcePaths = new(PathComparer);
        HashSet<string> stagedPaths = new(PathComparer);
        for (int index = 0; index < plan.Sources.Count; index++)
        {
            VerifiedReleaseActivationConfigurationBackupSourcePlan source =
                plan.Sources[index];
            string expectedName = source.Kind switch
            {
                VerifiedReleaseActivationConfigurationBackupSourceKind.Configuration =>
                    "configuration",
                VerifiedReleaseActivationConfigurationBackupSourceKind.State => "state",
                VerifiedReleaseActivationConfigurationBackupSourceKind.Secret => "secrets",
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(expectedName) ||
                !IsCanonicalAbsolutePath(source.SourcePath) ||
                !IsCanonicalAbsolutePath(source.StagedPath) ||
                !sourcePaths.Add(source.SourcePath) ||
                !stagedPaths.Add(source.StagedPath) ||
                !PathEquals(
                    Path.GetDirectoryName(source.StagedPath),
                    plan.StagingPath) ||
                !string.Equals(
                    Path.GetFileName(source.StagedPath),
                    expectedName,
                    StringComparison.Ordinal) ||
                PathsOverlap(source.SourcePath, plan.BackupRootPath))
            {
                return false;
            }
        }
        return true;
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateBackupLayout(
        VerifiedReleaseActivationConfigurationBackupPlan plan)
    {
        ValidatePrivateDirectory(plan.BackupRootPath);
        if (PathsOverlap(
                plan.BackupRootPath,
                plan.ActivationPlan.DeploymentRootPath) ||
            plan.Sources.Any(source =>
                PathsOverlap(source.SourcePath, plan.BackupRootPath)))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeBackupLayout,
                "The backup root overlaps deployment or a protected source root.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void PreparePrivateBackupParents(
        VerifiedReleaseActivationConfigurationBackupPlan plan)
    {
        string revisionRoot =
            Path.GetDirectoryName(plan.PublishedPath) ??
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeBackupLayout,
                "The published backup requires a revision parent.");
        string activationRoot =
            Path.GetDirectoryName(revisionRoot) ??
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeBackupLayout,
                "The backup revision requires an activation parent.");
        EnsurePrivateChild(plan.BackupRootPath, activationRoot);
        EnsurePrivateChild(activationRoot, revisionRoot);
    }

    [SupportedOSPlatform("linux")]
    private static void EnsurePrivateChild(string parent, string child)
    {
        if (!PathEquals(Path.GetDirectoryName(child), parent))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeBackupLayout,
                "A private backup directory escaped its validated parent.");
        }
        if (!PathEntryExists(child))
        {
            Directory.CreateDirectory(child);
            File.SetUnixFileMode(child, PrivateWritableDirectoryMode);
        }
        ValidatePrivateDirectory(child);
    }

    private static void EnsureAbsent(string path, bool published)
    {
        if (!PathEntryExists(path))
        {
            return;
        }
        throw Failure(
            published
                ? VerifiedReleaseActivationConfigurationBackupFailureCode
                    .BackupAlreadyPresent
                : VerifiedReleaseActivationConfigurationBackupFailureCode
                    .StagingAlreadyPresent,
            published
                ? "The exact backup identity already exists and will not be overwritten."
                : "The planned private staging identity already exists and will not be reused or removed.");
    }

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
    [SupportedOSPlatform("linux")]
    private static async Task<SourceSnapshot> CaptureSourceSnapshotAsync(
        VerifiedReleaseActivationConfigurationBackupPlan plan,
        bool includeHashes,
        CancellationToken cancellationToken)
    {
        List<SourceDirectory> directories = [];
        List<SourceFile> files = [];
        long sourceBytes = 0;

        foreach (VerifiedReleaseActivationConfigurationBackupSourcePlan source in
                 plan.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo root = new(source.SourcePath);
            Stack<(DirectoryInfo Directory, string RelativePath)> pending = new();
            pending.Push((root, "."));
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (DirectoryInfo directory, string relativePath) = pending.Pop();
                ValidateSourceDirectory(directory, source.Kind);
                if (directories.Count >= MaximumDirectoryCount)
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .UnsafeSourceLayout,
                        "Configuration backup source directories exceed the bounded traversal limit.");
                }
                string stagedPath = relativePath == "."
                    ? source.StagedPath
                    : SafeDescendant(source.StagedPath, relativePath);
                directories.Add(
                    new SourceDirectory(
                        source.Kind,
                        relativePath,
                        directory.FullName,
                        stagedPath,
                        directory.LastWriteTimeUtc,
                        File.GetUnixFileMode(directory.FullName),
                        LinuxFileOwnership.Read(directory.FullName)));

                FileSystemInfo[] entries = directory
                    .GetFileSystemInfos()
                    .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                    .ToArray();
                foreach (FileSystemInfo entry in entries.Reverse())
                {
                    entry.Refresh();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        entry.LinkTarget is not null)
                    {
                        throw Failure(
                            VerifiedReleaseActivationConfigurationBackupFailureCode
                                .UnsafeSourceLayout,
                            "Configuration backup sources may not contain symbolic links or reparse points.");
                    }

                    string childRelative = CombineRelative(
                        relativePath,
                        entry.Name);
                    if (entry is DirectoryInfo childDirectory)
                    {
                        pending.Push((childDirectory, childRelative));
                        continue;
                    }
                    if (entry is not FileInfo file)
                    {
                        throw Failure(
                            VerifiedReleaseActivationConfigurationBackupFailureCode
                                .UnsafeSourceLayout,
                            "Configuration backup sources contain an unsupported filesystem entry.");
                    }

                    ValidateSourceFile(file, source.Kind);
                    if (files.Count >= MaximumFileCount)
                    {
                        throw Failure(
                            VerifiedReleaseActivationConfigurationBackupFailureCode
                                .UnsafeSourceLayout,
                            "Configuration backup source files exceed the bounded file limit.");
                    }
                    sourceBytes = checked(sourceBytes + file.Length);
                    if (sourceBytes > MaximumSourceBytes)
                    {
                        throw Failure(
                            VerifiedReleaseActivationConfigurationBackupFailureCode
                                .UnsafeSourceLayout,
                            "Configuration backup sources exceed the bounded byte limit.");
                    }

                    byte[] sha256 = includeHashes
                        ? await HashStableSourceFileAsync(
                            file,
                            source.Kind,
                            cancellationToken)
                        : [];
                    files.Add(
                        new SourceFile(
                            source.Kind,
                            childRelative,
                            file.FullName,
                            SafeDescendant(source.StagedPath, childRelative),
                            file.Length,
                            file.LastWriteTimeUtc,
                            File.GetUnixFileMode(file.FullName),
                            LinuxFileOwnership.Read(file.FullName),
                            sha256));
                }
            }
        }

        SourceDirectory[] frozenDirectories = directories
            .OrderBy(directory => directory.Kind)
            .ThenBy(directory => directory.RelativePath, StringComparer.Ordinal)
            .ToArray();
        SourceFile[] frozenFiles = files
            .OrderBy(file => file.Kind)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        return new SourceSnapshot(
            Array.AsReadOnly(frozenDirectories),
            Array.AsReadOnly(frozenFiles),
            sourceBytes);
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateSourceDirectory(
        DirectoryInfo directory,
        VerifiedReleaseActivationConfigurationBackupSourceKind kind)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            !IsCanonicalAbsolutePath(directory.FullName))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeSourceLayout,
                "A configuration backup source directory is missing, linked, or non-canonical.");
        }
        UnixFileMode mode = File.GetUnixFileMode(directory.FullName);
        if ((mode & ForbiddenSharedWritableUnixModes) != 0 ||
            (mode & UnixFileMode.UserRead) == 0 ||
            (mode & UnixFileMode.UserExecute) == 0 ||
            (kind ==
                    VerifiedReleaseActivationConfigurationBackupSourceKind.Secret &&
                (mode & ForbiddenSecretSharedUnixModes) != 0))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeSourceLayout,
                "A configuration backup source directory has unsafe permissions.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateSourceFile(
        FileInfo file,
        VerifiedReleaseActivationConfigurationBackupSourceKind kind)
    {
        file.Refresh();
        if (!file.Exists ||
            (file.Attributes &
                (FileAttributes.ReparsePoint |
                 FileAttributes.Directory |
                 FileAttributes.Device |
                 FileAttributes.Offline)) != 0 ||
            file.LinkTarget is not null ||
            !IsCanonicalAbsolutePath(file.FullName) ||
            file.Length < 0 ||
            file.Length > MaximumFileLength)
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeSourceLayout,
                "A configuration backup source file is unsafe or exceeds its bounded length.");
        }
        UnixFileMode mode = File.GetUnixFileMode(file.FullName);
        if ((mode & ForbiddenSharedWritableUnixModes) != 0 ||
            (mode & UnixFileMode.UserRead) == 0 ||
            (kind ==
                    VerifiedReleaseActivationConfigurationBackupSourceKind.Secret &&
                (mode & ForbiddenSecretSharedUnixModes) != 0))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeSourceLayout,
                "A configuration backup source file has unsafe permissions.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<byte[]> HashStableSourceFileAsync(
        FileInfo file,
        VerifiedReleaseActivationConfigurationBackupSourceKind kind,
        CancellationToken cancellationToken)
    {
        long length = file.Length;
        DateTime lastWrite = file.LastWriteTimeUtc;
        UnixFileMode mode = File.GetUnixFileMode(file.FullName);
        await using FileStream stream = new(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
        file.Refresh();
        ValidateSourceFile(file, kind);
        if (file.Length != length ||
            file.LastWriteTimeUtc != lastWrite ||
            File.GetUnixFileMode(file.FullName) != mode)
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .SourceChangedDuringBackup,
                "A configuration backup source file changed while it was being hashed.");
        }
        return digest;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<IReadOnlyList<CopiedFile>> CopySourceSnapshotAsync(
        SourceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (SourceDirectory directory in snapshot.Directories
                     .OrderBy(directory => RelativeDepth(directory.RelativePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory.StagedPath);
            File.SetUnixFileMode(
                directory.StagedPath,
                PrivateWritableDirectoryMode);
            ValidatePrivateDirectory(directory.StagedPath);
        }

        List<CopiedFile> copied = [];
        foreach (SourceFile file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            copied.Add(await CopySourceFileAsync(file, cancellationToken));
        }
        return copied.AsReadOnly();
    }

    [SupportedOSPlatform("linux")]
    private static async Task<CopiedFile> CopySourceFileAsync(
        SourceFile source,
        CancellationToken cancellationToken)
    {
        FileInfo sourceInfo = new(source.SourcePath);
        ValidateSourceFile(sourceInfo, source.Kind);
        if (sourceInfo.Length != source.Length ||
            sourceInfo.LastWriteTimeUtc != source.LastWriteTimeUtc ||
            File.GetUnixFileMode(source.SourcePath) != source.Mode ||
            PathEntryExists(source.StagedPath))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .SourceChangedDuringBackup,
                "A configuration backup source changed before it could be copied.");
        }

        FileStreamOptions destinationOptions = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            UnixCreateMode = PrivateWritableFileMode
        };
        await using FileStream input = new(
            source.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(source.StagedPath, destinationOptions);
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long copied = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                copied = checked(copied + read);
                if (copied > source.Length)
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .SourceChangedDuringBackup,
                        "A configuration backup source grew while it was being copied.");
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }
            if (copied != source.Length)
            {
                throw Failure(
                    VerifiedReleaseActivationConfigurationBackupFailureCode
                        .SourceChangedDuringBackup,
                    "A configuration backup source length changed while it was being copied.");
            }
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        File.SetUnixFileMode(source.StagedPath, PrivateWritableFileMode);
        sourceInfo.Refresh();
        ValidateSourceFile(sourceInfo, source.Kind);
        if (sourceInfo.Length != source.Length ||
            sourceInfo.LastWriteTimeUtc != source.LastWriteTimeUtc ||
            File.GetUnixFileMode(source.SourcePath) != source.Mode)
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .SourceChangedDuringBackup,
                "A configuration backup source changed while it was being copied.");
        }
        return new CopiedFile(
            source.Kind,
            source.RelativePath,
            source.Length,
            hash.GetHashAndReset());
    }

    private static bool EquivalentSnapshots(
        SourceSnapshot before,
        SourceSnapshot after,
        IReadOnlyList<CopiedFile> copied)
    {
        if (before.Directories.Count != after.Directories.Count ||
            before.Files.Count != after.Files.Count ||
            before.SourceBytes != after.SourceBytes ||
            copied.Count != after.Files.Count)
        {
            return false;
        }

        for (int index = 0; index < before.Directories.Count; index++)
        {
            SourceDirectory left = before.Directories[index];
            SourceDirectory right = after.Directories[index];
            if (left.Kind != right.Kind ||
                !string.Equals(
                    left.RelativePath,
                    right.RelativePath,
                    StringComparison.Ordinal) ||
                !PathEquals(left.SourcePath, right.SourcePath) ||
                left.LastWriteTimeUtc != right.LastWriteTimeUtc ||
                left.Mode != right.Mode ||
                left.Ownership != right.Ownership)
            {
                return false;
            }
        }

        Dictionary<(VerifiedReleaseActivationConfigurationBackupSourceKind, string),
            CopiedFile> copiedByPath = copied.ToDictionary(
                file => (file.Kind, file.RelativePath));
        for (int index = 0; index < before.Files.Count; index++)
        {
            SourceFile left = before.Files[index];
            SourceFile right = after.Files[index];
            if (left.Kind != right.Kind ||
                !string.Equals(
                    left.RelativePath,
                    right.RelativePath,
                    StringComparison.Ordinal) ||
                !PathEquals(left.SourcePath, right.SourcePath) ||
                left.Length != right.Length ||
                left.LastWriteTimeUtc != right.LastWriteTimeUtc ||
                left.Mode != right.Mode ||
                left.Ownership != right.Ownership ||
                right.Sha256.Length != 32 ||
                !copiedByPath.TryGetValue(
                    (right.Kind, right.RelativePath),
                    out CopiedFile? copiedFile) ||
                copiedFile.Length != right.Length ||
                !copiedFile.Sha256.SequenceEqual(right.Sha256))
            {
                return false;
            }
        }
        return true;
    }
    private BackupManifestArtifact CreateManifest(
        VerifiedReleaseActivationConfigurationBackupPlan plan,
        SourceSnapshot snapshot)
    {
        DateTimeOffset createdAt = m_timeProvider.GetUtcNow();
        List<VerifiedReleaseActivationConfigurationBackupManifestEntry> entries =
            [];
        entries.AddRange(snapshot.Directories.Select(directory =>
            new VerifiedReleaseActivationConfigurationBackupManifestEntry(
                directory.Kind,
                VerifiedReleaseActivationConfigurationBackupManifestEntryKind
                    .Directory,
                directory.RelativePath,
                Length: null,
                UnixMode: (int)directory.Mode,
                directory.Ownership.UserId,
                directory.Ownership.GroupId,
                Sha256: string.Empty)));
        entries.AddRange(snapshot.Files.Select(file =>
            new VerifiedReleaseActivationConfigurationBackupManifestEntry(
                file.Kind,
                VerifiedReleaseActivationConfigurationBackupManifestEntryKind.File,
                file.RelativePath,
                file.Length,
                (int)file.Mode,
                file.Ownership.UserId,
                file.Ownership.GroupId,
                Convert.ToHexString(file.Sha256).ToLowerInvariant())));
        VerifiedReleaseActivationConfigurationBackupManifest manifest = new(
            ManifestSchemaVersion,
            createdAt,
            plan.ActivationPlan.SetupRevision,
            plan.ActivationPlan.InstalledReleaseIdentity,
            plan.ActivationPlan.TargetReleaseIdentity,
            plan.Sources.Count,
            snapshot.Directories.Count,
            snapshot.Files.Count,
            snapshot.SourceBytes,
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
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .BackupWriteFailed,
                "The configuration backup manifest exceeded its bounded size.");
        }
        return new BackupManifestArtifact(
            manifest,
            bytes,
            SHA256.HashData(bytes));
    }

    [SupportedOSPlatform("linux")]
    private static async Task WriteManifestAsync(
        VerifiedReleaseActivationConfigurationBackupPlan plan,
        BackupManifestArtifact manifest,
        CancellationToken cancellationToken)
    {
        string stagingManifestPath = Path.GetFullPath(
            Path.Combine(plan.StagingPath, "backup-manifest.json"));
        if (!PathEquals(
                Path.GetDirectoryName(stagingManifestPath),
                plan.StagingPath) ||
            PathEntryExists(stagingManifestPath))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .BackupWriteFailed,
                "The private backup manifest path is unsafe or already exists.");
        }

        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            UnixCreateMode = PrivateWritableFileMode
        };
        await using (FileStream stream = new(stagingManifestPath, options))
        {
            await stream.WriteAsync(manifest.Bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.SetUnixFileMode(stagingManifestPath, PrivateWritableFileMode);
    }

    [SupportedOSPlatform("linux")]
    private static void FreezeBackupTree(string root)
    {
        List<string> directories = [];
        Stack<DirectoryInfo> pending = new();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            ValidatePrivateWritableDirectory(directory.FullName);
            directories.Add(directory.FullName);
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .BackupWriteFailed,
                        "The private backup staging tree contains a linked entry.");
                }
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo file)
                {
                    File.SetUnixFileMode(
                        file.FullName,
                        PrivateImmutableFileMode);
                }
                else
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .BackupWriteFailed,
                        "The private backup staging tree contains an unsupported entry.");
                }
            }
        }
        foreach (string directory in directories
                     .OrderByDescending(RelativeDepth))
        {
            File.SetUnixFileMode(
                directory,
                PrivateImmutableDirectoryMode);
        }
    }
    [SupportedOSPlatform("linux")]
    private static async Task ValidateImmutableBackupTreeAsync(
        string root,
        BackupManifestArtifact manifest,
        CancellationToken cancellationToken)
    {
        ValidateImmutableDirectory(root);
        Dictionary<string,
            VerifiedReleaseActivationConfigurationBackupManifestEntry>
            expected = CreateExpectedTree(manifest);
        HashSet<string> observed = new(StringComparer.Ordinal);
        Stack<DirectoryInfo> pending = new();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = pending.Pop();
            ValidateImmutableDirectory(directory.FullName);
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .PublishedStateRequiresReconciliation,
                        "The immutable backup tree contains a symbolic link or reparse point.");
                }
                string relative = NormalizeRelative(
                    Path.GetRelativePath(root, entry.FullName));
                if (!observed.Add(relative) ||
                    !expected.TryGetValue(
                        relative,
                        out VerifiedReleaseActivationConfigurationBackupManifestEntry?
                            expectedEntry))
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .PublishedStateRequiresReconciliation,
                        "The immutable backup tree contains an unexpected or duplicated entry.");
                }

                if (entry is DirectoryInfo child)
                {
                    if (expectedEntry.Kind !=
                        VerifiedReleaseActivationConfigurationBackupManifestEntryKind
                            .Directory)
                    {
                        throw Failure(
                            VerifiedReleaseActivationConfigurationBackupFailureCode
                                .PublishedStateRequiresReconciliation,
                            "The immutable backup tree entry type does not match its manifest.");
                    }
                    pending.Push(child);
                    continue;
                }
                if (entry is not FileInfo file ||
                    expectedEntry.Kind !=
                        VerifiedReleaseActivationConfigurationBackupManifestEntryKind
                            .File)
                {
                    throw Failure(
                        VerifiedReleaseActivationConfigurationBackupFailureCode
                            .PublishedStateRequiresReconciliation,
                        "The immutable backup tree entry type does not match its manifest.");
                }
                await ValidateImmutableFileAsync(
                    file,
                    expectedEntry,
                    cancellationToken);
            }
        }

        if (observed.Count != expected.Count)
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .PublishedStateRequiresReconciliation,
                "The immutable backup tree is incomplete.");
        }
    }

    private static Dictionary<string,
        VerifiedReleaseActivationConfigurationBackupManifestEntry>
        CreateExpectedTree(BackupManifestArtifact manifest)
    {
        Dictionary<string,
            VerifiedReleaseActivationConfigurationBackupManifestEntry>
            expected = new(StringComparer.Ordinal)
            {
                ["backup-manifest.json"] =
                    new VerifiedReleaseActivationConfigurationBackupManifestEntry(
                        VerifiedReleaseActivationConfigurationBackupSourceKind
                            .Configuration,
                        VerifiedReleaseActivationConfigurationBackupManifestEntryKind
                            .File,
                        "backup-manifest.json",
                        manifest.Bytes.Length,
                        UnixMode: 0,
                        UserId: 0,
                        GroupId: 0,
                        Convert.ToHexString(manifest.Sha256).ToLowerInvariant())
            };
        foreach (VerifiedReleaseActivationConfigurationBackupManifestEntry entry in
                 manifest.Manifest.Entries)
        {
            string prefix = SourceFolderName(entry.Source);
            string relative = entry.Path == "."
                ? prefix
                : $"{prefix}/{entry.Path}";
            expected.Add(relative, entry);
        }
        return expected;
    }

    [SupportedOSPlatform("linux")]
    private static async Task ValidateImmutableFileAsync(
        FileInfo file,
        VerifiedReleaseActivationConfigurationBackupManifestEntry expected,
        CancellationToken cancellationToken)
    {
        if (File.GetUnixFileMode(file.FullName) !=
                PrivateImmutableFileMode ||
            file.Length != expected.Length)
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .PublishedStateRequiresReconciliation,
                "The immutable backup file mode or length does not match its manifest.");
        }
        await using FileStream stream = new(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!string.Equals(
                Convert.ToHexString(digest).ToLowerInvariant(),
                expected.Sha256,
                StringComparison.Ordinal))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .PublishedStateRequiresReconciliation,
                "The immutable backup file digest does not match its manifest.");
        }
    }
    [SupportedOSPlatform("linux")]
    private static void ValidatePrivateDirectory(string path)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            !IsCanonicalAbsolutePath(directory.FullName) ||
            File.GetUnixFileMode(path) != PrivateWritableDirectoryMode)
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeBackupLayout,
                "A private backup directory is missing, linked, non-canonical, or not mode 0700.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidatePrivateWritableDirectory(string path)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            File.GetUnixFileMode(path) != PrivateWritableDirectoryMode)
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .BackupWriteFailed,
                "The private backup staging directory is unsafe.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateImmutableDirectory(string path)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null ||
            File.GetUnixFileMode(path) != PrivateImmutableDirectoryMode)
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .PublishedStateRequiresReconciliation,
                "The immutable backup directory is missing, linked, or writable.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static bool TryCleanup(string stagingPath)
    {
        try
        {
            if (!PathEntryExists(stagingPath))
            {
                return true;
            }
            DirectoryInfo root = new(stagingPath);
            root.Refresh();
            if (!root.Exists ||
                (root.Attributes & FileAttributes.ReparsePoint) != 0 ||
                root.LinkTarget is not null)
            {
                return false;
            }

            Stack<DirectoryInfo> pending = new();
            pending.Push(root);
            while (pending.Count > 0)
            {
                DirectoryInfo directory = pending.Pop();
                File.SetUnixFileMode(
                    directory.FullName,
                    PrivateWritableDirectoryMode);
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
                        pending.Push(child);
                    }
                    else if (entry is FileInfo file)
                    {
                        File.SetUnixFileMode(
                            file.FullName,
                            PrivateWritableFileMode);
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            Directory.Delete(stagingPath, recursive: true);
            return !PathEntryExists(stagingPath);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static bool TryFreezeRoot(string path)
    {
        try
        {
            if (!PathEntryExists(path))
            {
                return true;
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

    private void MarkReconciliationRequired()
    {
        lock (m_stateGate)
        {
            m_reconciliationRequired = true;
        }
    }

    private static BackupException Failure(
        VerifiedReleaseActivationConfigurationBackupFailureCode failureCode,
        string message) =>
        new(failureCode, message);

    private static string SourceFolderName(
        VerifiedReleaseActivationConfigurationBackupSourceKind kind) =>
        kind switch
        {
            VerifiedReleaseActivationConfigurationBackupSourceKind.Configuration =>
                "configuration",
            VerifiedReleaseActivationConfigurationBackupSourceKind.State => "state",
            VerifiedReleaseActivationConfigurationBackupSourceKind.Secret =>
                "secrets",
            _ => throw new InvalidOperationException(
                "Unsupported configuration backup source kind.")
        };

    private static string CombineRelative(string parent, string child)
    {
        if (!ValidSegment(child))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeSourceLayout,
                "A configuration backup source name is unsafe.");
        }
        string relative = parent == "." ? child : $"{parent}/{child}";
        if (relative.Length > MaximumRelativePathLength)
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeSourceLayout,
                "A configuration backup source path exceeds its bounded length.");
        }
        return relative;
    }

    private static bool ValidSegment(string value) =>
        !string.IsNullOrEmpty(value) &&
        value is not "." and not ".." &&
        value.Length <= 255 &&
        !value.Contains('/') &&
        !value.Contains('\\') &&
        value.All(character => !char.IsControl(character));

    private static string SafeDescendant(string root, string relativePath)
    {
        string[] segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Length > 32 ||
            segments.Any(segment => !ValidSegment(segment)))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeSourceLayout,
                "A configuration backup relative path is unsafe.");
        }
        string candidate = Path.GetFullPath(
            segments.Aggregate(root, Path.Combine));
        string prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison))
        {
            throw Failure(
                VerifiedReleaseActivationConfigurationBackupFailureCode
                    .UnsafeSourceLayout,
                "A configuration backup path escaped its private staging root.");
        }
        return candidate;
    }

    private static string NormalizeRelative(string value)
    {
        string normalized = value.Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            normalized = normalized.Replace(Path.AltDirectorySeparatorChar, '/');
        }
        return normalized;
    }

    private static int RelativeDepth(string relativePath) =>
        relativePath == "."
            ? 0
            : relativePath.Count(character => character == '/') + 1;

    private static bool PathEntryExists(string path) =>
        File.Exists(path) ||
        Directory.Exists(path) ||
        new FileInfo(path).LinkTarget is not null ||
        new DirectoryInfo(path).LinkTarget is not null;

    private static bool IsCanonicalAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }
        try
        {
            string canonical = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
            return string.Equals(path, canonical, PathComparison) &&
                !PathEquals(path, Path.GetPathRoot(path));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathsOverlap(string left, string right) =>
        IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left);

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        if (PathEquals(candidate, parent))
        {
            return true;
        }
        string prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                PathComparison);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    private sealed record SourceDirectory(
        VerifiedReleaseActivationConfigurationBackupSourceKind Kind,
        string RelativePath,
        string SourcePath,
        string StagedPath,
        DateTime LastWriteTimeUtc,
        UnixFileMode Mode,
        LinuxFileOwnership Ownership);

    private sealed record SourceFile(
        VerifiedReleaseActivationConfigurationBackupSourceKind Kind,
        string RelativePath,
        string SourcePath,
        string StagedPath,
        long Length,
        DateTime LastWriteTimeUtc,
        UnixFileMode Mode,
        LinuxFileOwnership Ownership,
        byte[] Sha256);

    private sealed record CopiedFile(
        VerifiedReleaseActivationConfigurationBackupSourceKind Kind,
        string RelativePath,
        long Length,
        byte[] Sha256);

    private sealed record SourceSnapshot(
        ReadOnlyCollection<SourceDirectory> Directories,
        ReadOnlyCollection<SourceFile> Files,
        long SourceBytes);

    private sealed record BackupManifestArtifact(
        VerifiedReleaseActivationConfigurationBackupManifest Manifest,
        byte[] Bytes,
        byte[] Sha256);

    private sealed class BackupException(
        VerifiedReleaseActivationConfigurationBackupFailureCode failureCode,
        string message)
        : Exception(message)
    {
        internal VerifiedReleaseActivationConfigurationBackupFailureCode
            FailureCode
        { get; } = failureCode;
    }
}
