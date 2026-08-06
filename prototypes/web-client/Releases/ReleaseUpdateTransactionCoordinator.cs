using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public sealed class ReleaseUpdateTransactionSettings
{
    public const string SectionName = "ReleaseUpdateTransaction";
    public const int DefaultLeaseDrainSeconds = 30;
    public const int MaximumLeaseDrainSeconds = 300;

    public bool ExecutionEnabled { get; init; }
    public int LeaseDrainSeconds { get; init; } = DefaultLeaseDrainSeconds;
}

public enum ReleaseUpdateTransactionPhase
{
    None = 0,
    Preparing = 1,
    Prepared = 2,
    AwaitingApproval = 3,
    ClosingLeaseAdmission = 4,
    SafetyValidated = 5,
    ServicesStopped = 6,
    PointerSwitched = 7,
    ServicesStarted = 8,
    HealthVerified = 9,
    Completed = 10,
    RollingBack = 11,
    RolledBack = 12,
    RestartPending = 13,
    Failed = 14,
    ReconciliationRequired = 15
}

public enum ReleaseUpdateTransactionFailureCode
{
    None = 0,
    ExecutionDisabled = 1,
    UnsupportedPlatform = 2,
    InvalidRequest = 3,
    TransactionAlreadyActive = 4,
    TransactionNotFound = 5,
    TransactionPhaseInvalid = 6,
    PreflightFailed = 7,
    InstallationPlanFailed = 8,
    StagingFailed = 9,
    ExtractionFailed = 10,
    PublicationPlanFailed = 11,
    PublicationFailed = 12,
    ActivationPlanFailed = 13,
    BackupPlanFailed = 14,
    BackupFailed = 15,
    MigrationPlanFailed = 16,
    MigrationRunnerSelectionFailed = 17,
    MigrationRunnerProbeFailed = 18,
    MigrationFailed = 19,
    ServiceControlPlanFailed = 20,
    HealthPlanFailed = 21,
    RollbackPlanFailed = 22,
    ApprovalFailed = 23,
    LeaseAdmissionFailed = 24,
    LeaseDrainTimedOut = 25,
    EvidenceCollectionFailed = 26,
    SafetyReadinessFailed = 27,
    PreSwitchServiceControlFailed = 28,
    CurrentPointerSwitchFailed = 29,
    PostSwitchServiceControlFailed = 30,
    HealthVerificationFailed = 31,
    FinalReadinessFailed = 32,
    AutomaticRollbackFailed = 33,
    ManualRollbackFailed = 34,
    JournalWriteFailed = 35,
    HostRestartRequestFailed = 36,
    ReconciliationRequired = 37
}

public sealed record ReleaseUpdateInstallRequest(
    string BundleDirectory,
    string InstalledReleaseIdentity,
    string InstalledVersion,
    int ConfigurationSchemaVersion,
    int ProtocolVersion);

public sealed record ReleaseUpdateTransactionReport(
    bool Succeeded,
    ReleaseUpdateTransactionFailureCode FailureCode,
    string Message,
    string TransactionId,
    ReleaseUpdateTransactionPhase Phase,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    string TargetVersion,
    int PackageCount,
    int FileCount,
    long PublishedBytes,
    bool InactiveReleasePublished,
    bool ConfigurationBackupReady,
    bool MigrationReady,
    bool OperatorApproved,
    bool TxLeaseAdmissionClosed,
    bool CurrentPointerChanged,
    bool ServiceControlCompleted,
    bool HealthVerified,
    bool RollbackReady,
    bool RollbackPerformed,
    bool RestartPending,
    bool ReconciliationRequired,
    bool ActivationCompleted)
{
    internal static ReleaseUpdateTransactionReport Create(
        PreparedReleaseUpdateTransaction? transaction,
        bool succeeded,
        ReleaseUpdateTransactionFailureCode failureCode,
        string message,
        ReleaseUpdateTransactionPhase? phase = null,
        bool operatorApproved = false,
        bool admissionClosed = false,
        bool pointerChanged = false,
        bool serviceControlCompleted = false,
        bool healthVerified = false,
        bool rollbackPerformed = false,
        bool restartPending = false,
        bool reconciliationRequired = false) =>
        new(
            succeeded,
            failureCode,
            message,
            transaction?.TransactionId ?? string.Empty,
            phase ?? transaction?.Phase ?? ReleaseUpdateTransactionPhase.None,
            transaction?.Activation.SetupRevision,
            transaction?.Activation.InstalledReleaseIdentity ?? string.Empty,
            transaction?.Activation.TargetReleaseIdentity ?? string.Empty,
            transaction?.Activation.TargetVersion ?? string.Empty,
            transaction?.Activation.PackageCount ?? 0,
            transaction?.Publication.FileCount ?? 0,
            transaction?.Publication.PublishedBytes ?? 0,
            transaction?.Publication.Succeeded ?? false,
            transaction?.Backup.Succeeded ?? false,
            transaction?.MigrationExecution.Succeeded ?? false,
            operatorApproved,
            admissionClosed,
            pointerChanged,
            serviceControlCompleted,
            healthVerified,
            transaction?.RollbackPlan.Succeeded == true &&
                !transaction.ServiceControlPlan.HostRestartRequired,
            rollbackPerformed,
            restartPending,
            reconciliationRequired,
            succeeded &&
                (phase ?? transaction?.Phase) ==
                    ReleaseUpdateTransactionPhase.Completed);
}

public sealed record ReleaseUpdateTransactionDiagnostics(
    bool Registered,
    bool ExecutionEnabled,
    int LeaseDrainSeconds,
    bool OfflinePreflightRegistered,
    bool VerifiedStagingRegistered,
    bool VerifiedExtractionRegistered,
    bool AtomicInactivePublicationRegistered,
    bool ActivationPlanAdaptationRegistered,
    bool ConfigurationBackupExecutionRegistered,
    bool MigrationExecutionRegistered,
    bool TxLeaseAdmissionClosureRegistered,
    bool RadioAuthoritativeSafetyEvidenceRegistered,
    bool ServiceControlExecutionRegistered,
    bool AtomicCurrentPointerSwitchRegistered,
    bool HealthVerificationRegistered,
    bool HostRestartExecutionRegistered,
    bool AutomaticRollbackRegistered,
    bool ManualRollbackRegistered,
    bool AuthenticatedApprovalRegistered,
    bool DurableJournalRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool RadioCommandRegistered,
    bool TxCallerRegistered);

internal sealed record ReleaseUpdateJournalDocument(
    int SchemaVersion,
    string TransactionId,
    ReleaseUpdateTransactionPhase Phase,
    long SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    string TargetVersion,
    DateTimeOffset UpdatedAt,
    bool CurrentPointerChanged,
    bool RollbackPerformed,
    bool RestartPending,
    bool ReconciliationRequired);

internal sealed class ReleaseUpdateTransactionJournal
{
    internal const int SchemaVersion = 1;
    private const string DirectoryName = "release-transactions";
    private const string FileName = "active.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string m_root;
    private readonly string m_path;

    internal ReleaseUpdateTransactionJournal(InstallationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InstallationPaths.Validate(paths);
        m_root = Path.GetFullPath(Path.Combine(paths.StateDirectory, DirectoryName));
        m_path = Path.GetFullPath(Path.Combine(m_root, FileName));
        if (!string.Equals(
                Path.GetDirectoryName(m_root),
                Path.GetFullPath(paths.StateDirectory),
                StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(m_path), m_root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The release transaction journal escaped the installation state directory.");
        }
    }

    [SupportedOSPlatform("linux")]
    internal async Task WriteAsync(
        PreparedReleaseUpdateTransaction transaction,
        bool pointerChanged,
        bool rollbackPerformed,
        bool restartPending,
        bool reconciliationRequired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Release transaction journaling requires Linux.");
        }

        Directory.CreateDirectory(m_root);
        File.SetUnixFileMode(
            m_root,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        string temporary = Path.Combine(m_root, $".{FileName}.{Guid.NewGuid():N}");
        ReleaseUpdateJournalDocument document = new(
            SchemaVersion,
            transaction.TransactionId,
            transaction.Phase,
            transaction.Activation.SetupRevision ?? 0,
            transaction.Activation.InstalledReleaseIdentity,
            transaction.Activation.TargetReleaseIdentity,
            transaction.Activation.TargetVersion,
            DateTimeOffset.UtcNow,
            pointerChanged,
            rollbackPerformed,
            restartPending,
            reconciliationRequired);
        await WriteDocumentAsync(document, temporary, cancellationToken);
    }

    [SupportedOSPlatform("linux")]
    internal void ValidatePostBootPending(HostRestartMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ReleaseUpdateJournalDocument document = Read() ??
            throw new InvalidDataException(
                "The host-restart transaction journal is missing.");
        if (!MatchesMarker(document, marker) ||
            document.Phase != ReleaseUpdateTransactionPhase.RestartPending ||
            !document.RestartPending ||
            document.ReconciliationRequired)
        {
            throw new InvalidDataException(
                "The host-restart marker does not match the exact pending transaction journal.");
        }
    }

    [SupportedOSPlatform("linux")]
    internal async Task UpdatePostBootAsync(
        HostRestartMarker marker,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ReleaseUpdateJournalDocument document = Read() ??
            throw new InvalidDataException(
                "The host-restart transaction journal is missing.");
        bool identityMatches = MatchesMarker(document, marker);
        bool alreadyTerminal = identityMatches &&
            (succeeded
                ? document.Phase == ReleaseUpdateTransactionPhase.Completed &&
                  !document.RestartPending &&
                  !document.ReconciliationRequired
                : document.Phase ==
                    ReleaseUpdateTransactionPhase.ReconciliationRequired &&
                  !document.RestartPending &&
                  document.ReconciliationRequired);
        if (alreadyTerminal)
        {
            return;
        }
        if (!identityMatches ||
            document.Phase != ReleaseUpdateTransactionPhase.RestartPending ||
            !document.RestartPending ||
            document.ReconciliationRequired)
        {
            throw new InvalidDataException(
                "The host-restart marker does not match the exact pending transaction journal.");
        }

        ReleaseUpdateJournalDocument updated = document with
        {
            Phase = succeeded
                ? ReleaseUpdateTransactionPhase.Completed
                : ReleaseUpdateTransactionPhase.ReconciliationRequired,
            UpdatedAt = DateTimeOffset.UtcNow,
            RestartPending = false,
            ReconciliationRequired = !succeeded
        };
        string temporary = Path.Combine(
            m_root,
            $".{FileName}.{Guid.NewGuid():N}");
        await WriteDocumentAsync(updated, temporary, cancellationToken);
    }

    private static bool MatchesMarker(
        ReleaseUpdateJournalDocument document,
        HostRestartMarker marker) =>
        string.Equals(
            document.TransactionId,
            marker.TransactionId,
            StringComparison.Ordinal) &&
        document.SetupRevision == marker.SetupRevision &&
        string.Equals(
            document.InstalledReleaseIdentity,
            marker.InstalledReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            document.TargetReleaseIdentity,
            marker.TargetReleaseIdentity,
            StringComparison.Ordinal) &&
        document.CurrentPointerChanged &&
        !document.RollbackPerformed;

    [SupportedOSPlatform("linux")]
    internal ReleaseUpdateJournalDocument? Read()
    {
        if (!File.Exists(m_path))
        {
            return null;
        }
        FileInfo file = new(m_path);
        file.Refresh();
        UnixFileMode mode = File.GetUnixFileMode(m_path);
        if (!file.Exists ||
            file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.Length is < 2 or > 32 * 1024 ||
            (mode & (UnixFileMode.UserWrite |
                     UnixFileMode.UserExecute |
                     UnixFileMode.GroupRead |
                     UnixFileMode.GroupWrite |
                     UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead |
                     UnixFileMode.OtherWrite |
                     UnixFileMode.OtherExecute)) != 0)
        {
            throw new InvalidDataException(
                "The release transaction journal is unsafe.");
        }
        byte[] bytes = File.ReadAllBytes(m_path);
        ReleaseUpdateJournalDocument? document =
            JsonSerializer.Deserialize<ReleaseUpdateJournalDocument>(
                bytes,
                JsonOptions);
        if (document is null ||
            document.SchemaVersion != SchemaVersion ||
            document.TransactionId.Length != 32 ||
            document.TransactionId.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')) ||
            document.SetupRevision < 1 ||
            string.IsNullOrEmpty(document.InstalledReleaseIdentity) ||
            string.IsNullOrEmpty(document.TargetReleaseIdentity) ||
            string.IsNullOrEmpty(document.TargetVersion) ||
            document.UpdatedAt < DateTimeOffset.UnixEpoch)
        {
            throw new InvalidDataException(
                "The release transaction journal is invalid.");
        }
        return document;
    }

    [SupportedOSPlatform("linux")]
    private async Task WriteDocumentAsync(
        ReleaseUpdateJournalDocument document,
        string temporary,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        try
        {
            await using (FileStream stream = new(
                temporary,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough
                }))
            {
                File.SetUnixFileMode(
                    temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, m_path, overwrite: true);
            File.SetUnixFileMode(m_path, UnixFileMode.UserRead);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // An unsafe or failed journal cleanup is surfaced by the caller.
                }
            }
        }
    }

    [SupportedOSPlatform("linux")]
    internal void Clear()
    {
        if (File.Exists(m_path))
        {
            File.SetUnixFileMode(
                m_path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(m_path);
        }
    }
}

internal sealed class PreparedReleaseUpdateTransaction
{
    internal required string TransactionId { get; init; }
    internal required ReleaseUpdateInstallRequest Request { get; init; }
    internal required VerifiedReleaseExtractedPublicationReport Publication { get; init; }
    internal required VerifiedReleaseActivationPlanCompositionResult Activation { get; init; }
    internal required VerifiedReleaseActivationConfigurationBackupReport Backup { get; init; }
    internal required VerifiedReleaseActivationMigrationExecutionReport MigrationExecution { get; init; }
    internal required VerifiedReleaseActivationServiceControlPlanReport ServiceControlPlan { get; init; }
    internal required VerifiedReleaseActivationHealthVerificationPlanReport HealthPlan { get; init; }
    internal required VerifiedReleaseActivationRollbackPlanReport RollbackPlan { get; init; }
    internal required DateTimeOffset PreparedAt { get; init; }
    internal ReleaseUpdateTransactionPhase Phase { get; set; }
    internal VerifiedReleaseActivationOperatorApprovalReport? Approval { get; set; }
    internal VerifiedReleaseActivationLeaseQuiescenceReport? Quiescence { get; set; }
    internal VerifiedReleaseActivationCurrentPointerSwitchReport? PointerSwitch { get; set; }
    internal VerifiedReleaseActivationServiceControlExecutionReport? PostSwitchServiceControl { get; set; }
    internal VerifiedReleaseActivationHealthVerificationReport? Health { get; set; }
}

/// <summary>
/// One bounded operational update coordinator. It composes the existing signed
/// verification, immutable staging/extraction/publication, activation planning,
/// backup, migration, service control, current-pointer, health, and rollback
/// boundaries without reconstructing their internal authority tokens. It never
/// issues a radio command, force-releases a TX lease, arms a watchdog, keys a
/// transmitter, or treats browser state as safety evidence.
/// </summary>
public sealed class ReleaseUpdateTransactionCoordinator
{
    private readonly OfflineReleaseInstallPreflightPlanner m_preflight;
    private readonly VerifiedReleaseInstallationPlanComposer m_installationPlan;
    private readonly VerifiedReleaseStagingService m_staging;
    private readonly VerifiedReleaseArchiveExtractionService m_extraction;
    private readonly VerifiedReleaseExtractedPublicationPlanComposer m_publicationPlan;
    private readonly VerifiedReleaseExtractedPublicationService m_publication;
    private readonly VerifiedReleaseActivationPlanComposer m_activationPlan;
    private readonly VerifiedReleaseActivationConfigurationBackupPlanner m_backupPlan;
    private readonly VerifiedReleaseActivationConfigurationBackupService m_backup;
    private readonly VerifiedReleaseActivationMigrationPlanComposer m_migrationPlan;
    private readonly VerifiedReleaseActivationMigrationRunnerSelector m_runnerSelector;
    private readonly VerifiedReleaseActivationMigrationRunnerInvocationService m_runnerProbe;
    private readonly VerifiedReleaseActivationMigrationExecutionService m_migration;
    private readonly VerifiedReleaseActivationServiceControlPlanComposer m_serviceControlPlan;
    private readonly VerifiedReleaseActivationHealthVerificationPlanComposer m_healthPlan;
    private readonly VerifiedReleaseActivationRollbackPlanComposer m_rollbackPlan;
    private readonly VerifiedReleaseActivationOperatorApprovalAuthority m_approval;
    private readonly VerifiedReleaseActivationLeaseQuiescenceBoundary m_quiescence;
    private readonly VerifiedReleaseActivationEvidenceCollector m_evidence;
    private readonly VerifiedReleaseActivationReadinessEvaluator m_readiness;
    private readonly VerifiedReleaseActivationServiceControlExecutionService m_serviceControl;
    private readonly VerifiedReleaseActivationCurrentPointerSwitchService m_pointerSwitch;
    private readonly VerifiedReleaseActivationHealthVerificationService m_health;
    private readonly VerifiedReleaseActivationHostRestartTransport m_hostRestart;
    private readonly VerifiedReleaseActivationRollbackExecutionService m_rollback;
    private readonly InstallationPaths m_paths;
    private readonly ReleaseUpdateTransactionSettings m_settings;
    private readonly ReleaseUpdateTransactionJournal m_journal;
    private readonly TimeProvider m_timeProvider;
    private readonly SemaphoreSlim m_gate = new(1, 1);
    private PreparedReleaseUpdateTransaction? m_active;
    private ReleaseUpdateTransactionReport? m_recoveredStatus;

    public ReleaseUpdateTransactionCoordinator(
        OfflineReleaseInstallPreflightPlanner preflight,
        VerifiedReleaseInstallationPlanComposer installationPlan,
        VerifiedReleaseStagingService staging,
        VerifiedReleaseArchiveExtractionService extraction,
        VerifiedReleaseExtractedPublicationPlanComposer publicationPlan,
        VerifiedReleaseExtractedPublicationService publication,
        VerifiedReleaseActivationPlanComposer activationPlan,
        VerifiedReleaseActivationConfigurationBackupPlanner backupPlan,
        VerifiedReleaseActivationConfigurationBackupService backup,
        VerifiedReleaseActivationMigrationPlanComposer migrationPlan,
        VerifiedReleaseActivationMigrationRunnerSelector runnerSelector,
        VerifiedReleaseActivationMigrationRunnerInvocationService runnerProbe,
        VerifiedReleaseActivationMigrationExecutionService migration,
        VerifiedReleaseActivationServiceControlPlanComposer serviceControlPlan,
        VerifiedReleaseActivationHealthVerificationPlanComposer healthPlan,
        VerifiedReleaseActivationRollbackPlanComposer rollbackPlan,
        VerifiedReleaseActivationOperatorApprovalAuthority approval,
        VerifiedReleaseActivationLeaseQuiescenceBoundary quiescence,
        VerifiedReleaseActivationEvidenceCollector evidence,
        VerifiedReleaseActivationReadinessEvaluator readiness,
        VerifiedReleaseActivationServiceControlExecutionService serviceControl,
        VerifiedReleaseActivationCurrentPointerSwitchService pointerSwitch,
        VerifiedReleaseActivationHealthVerificationService health,
        VerifiedReleaseActivationHostRestartTransport hostRestart,
        VerifiedReleaseActivationRollbackExecutionService rollback,
        InstallationPaths paths,
        IOptions<ReleaseUpdateTransactionSettings> settings,
        TimeProvider? timeProvider = null)
    {
        m_preflight = preflight;
        m_installationPlan = installationPlan;
        m_staging = staging;
        m_extraction = extraction;
        m_publicationPlan = publicationPlan;
        m_publication = publication;
        m_activationPlan = activationPlan;
        m_backupPlan = backupPlan;
        m_backup = backup;
        m_migrationPlan = migrationPlan;
        m_runnerSelector = runnerSelector;
        m_runnerProbe = runnerProbe;
        m_migration = migration;
        m_serviceControlPlan = serviceControlPlan;
        m_healthPlan = healthPlan;
        m_rollbackPlan = rollbackPlan;
        m_approval = approval;
        m_quiescence = quiescence;
        m_evidence = evidence;
        m_readiness = readiness;
        m_serviceControl = serviceControl;
        m_pointerSwitch = pointerSwitch;
        m_health = health;
        m_hostRestart = hostRestart;
        m_rollback = rollback;
        m_paths = paths ?? throw new ArgumentNullException(nameof(paths));
        InstallationPaths.Validate(m_paths);
        m_settings = ValidateSettings(settings?.Value ??
            throw new ArgumentNullException(nameof(settings)));
        m_journal = new ReleaseUpdateTransactionJournal(m_paths);
        m_timeProvider = timeProvider ?? TimeProvider.System;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                ReleaseUpdateJournalDocument? recovered = m_journal.Read();
                if (recovered is not null)
                {
                    m_recoveredStatus = RecoveredStatus(recovered);
                }
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or JsonException or
                    NotSupportedException)
            {
                m_recoveredStatus = new ReleaseUpdateTransactionReport(
                    false,
                    ReleaseUpdateTransactionFailureCode.ReconciliationRequired,
                    "The durable release transaction journal is unsafe or unreadable; local reconciliation is required.",
                    TransactionId: string.Empty,
                    ReleaseUpdateTransactionPhase.ReconciliationRequired,
                    SetupRevision: null,
                    InstalledReleaseIdentity: string.Empty,
                    TargetReleaseIdentity: string.Empty,
                    TargetVersion: string.Empty,
                    PackageCount: 0,
                    FileCount: 0,
                    PublishedBytes: 0,
                    InactiveReleasePublished: false,
                    ConfigurationBackupReady: false,
                    MigrationReady: false,
                    OperatorApproved: false,
                    TxLeaseAdmissionClosed: false,
                    CurrentPointerChanged: false,
                    ServiceControlCompleted: false,
                    HealthVerified: false,
                    RollbackReady: false,
                    RollbackPerformed: false,
                    RestartPending: false,
                    ReconciliationRequired: true,
                    ActivationCompleted: false);
            }
        }

        Snapshot = new ReleaseUpdateTransactionDiagnostics(
            Registered: true,
            m_settings.ExecutionEnabled,
            m_settings.LeaseDrainSeconds,
            OfflinePreflightRegistered: true,
            VerifiedStagingRegistered: true,
            VerifiedExtractionRegistered: true,
            AtomicInactivePublicationRegistered: true,
            ActivationPlanAdaptationRegistered: true,
            ConfigurationBackupExecutionRegistered: true,
            MigrationExecutionRegistered: true,
            TxLeaseAdmissionClosureRegistered: true,
            RadioAuthoritativeSafetyEvidenceRegistered: true,
            ServiceControlExecutionRegistered: true,
            AtomicCurrentPointerSwitchRegistered: true,
            HealthVerificationRegistered: true,
            HostRestartExecutionRegistered: true,
            AutomaticRollbackRegistered: true,
            ManualRollbackRegistered: true,
            AuthenticatedApprovalRegistered: true,
            DurableJournalRegistered: true,
            CliCallerRegistered: true,
            AdminCallerRegistered: true,
            BrowserCallerRegistered: true,
            RadioCommandRegistered: false,
            TxCallerRegistered: false);
    }

    public ReleaseUpdateTransactionDiagnostics Snapshot { get; }

    public ReleaseUpdateTransactionReport Status()
    {
        PreparedReleaseUpdateTransaction? active = m_active;
        if (active is null && OperatingSystem.IsLinux())
        {
            try
            {
                ReleaseUpdateJournalDocument? recovered = m_journal.Read();
                m_recoveredStatus = recovered is null
                    ? null
                    : RecoveredStatus(recovered);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or JsonException or
                    NotSupportedException)
            {
                m_recoveredStatus = new ReleaseUpdateTransactionReport(
                    false,
                    ReleaseUpdateTransactionFailureCode.ReconciliationRequired,
                    "The durable release transaction journal is unsafe or unreadable; local reconciliation is required.",
                    TransactionId: string.Empty,
                    ReleaseUpdateTransactionPhase.ReconciliationRequired,
                    SetupRevision: null,
                    InstalledReleaseIdentity: string.Empty,
                    TargetReleaseIdentity: string.Empty,
                    TargetVersion: string.Empty,
                    PackageCount: 0,
                    FileCount: 0,
                    PublishedBytes: 0,
                    InactiveReleasePublished: false,
                    ConfigurationBackupReady: false,
                    MigrationReady: false,
                    OperatorApproved: false,
                    TxLeaseAdmissionClosed: false,
                    CurrentPointerChanged: false,
                    ServiceControlCompleted: false,
                    HealthVerified: false,
                    RollbackReady: false,
                    RollbackPerformed: false,
                    RestartPending: false,
                    ReconciliationRequired: true,
                    ActivationCompleted: false);
            }
        }
        return active is null
            ? m_recoveredStatus ?? ReleaseUpdateTransactionReport.Create(
                null,
                succeeded: true,
                ReleaseUpdateTransactionFailureCode.None,
                "No release update transaction is active.")
            : ReleaseUpdateTransactionReport.Create(
                active,
                active.Phase is ReleaseUpdateTransactionPhase.Prepared or
                    ReleaseUpdateTransactionPhase.AwaitingApproval or
                    ReleaseUpdateTransactionPhase.Completed or
                    ReleaseUpdateTransactionPhase.RolledBack,
                ReleaseUpdateTransactionFailureCode.None,
                "The current release update transaction status was read.",
                active.Phase,
                operatorApproved: active.Approval?.Succeeded ?? false,
                admissionClosed: active.Quiescence?.AdmissionClosed ?? false,
                pointerChanged: active.PointerSwitch?.CurrentPointerChanged ?? false,
                serviceControlCompleted:
                    active.PostSwitchServiceControl?.ServiceControlReady ?? false,
                healthVerified: active.Health?.ServiceHealthReady ?? false,
                rollbackPerformed: active.Phase == ReleaseUpdateTransactionPhase.RolledBack,
                restartPending: active.Phase == ReleaseUpdateTransactionPhase.RestartPending,
                reconciliationRequired:
                    active.Phase == ReleaseUpdateTransactionPhase.ReconciliationRequired);
    }

    [SupportedOSPlatform("linux")]
    internal async Task<ReleaseUpdateTransactionReport> PrepareOfflineAsync(
        ReleaseUpdateInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!m_settings.ExecutionEnabled)
        {
            return Failure(null, ReleaseUpdateTransactionFailureCode.ExecutionDisabled,
                "Release update transaction execution is disabled.");
        }
        if (!OperatingSystem.IsLinux())
        {
            return Failure(null, ReleaseUpdateTransactionFailureCode.UnsupportedPlatform,
                "Release update transactions require Linux.");
        }
        if (!ValidateRequest(request))
        {
            return Failure(null, ReleaseUpdateTransactionFailureCode.InvalidRequest,
                "The release update request is incomplete or non-canonical.");
        }

        await m_gate.WaitAsync(cancellationToken);
        try
        {
            if (m_recoveredStatus?.ReconciliationRequired == true ||
                m_recoveredStatus?.RestartPending == true)
            {
                return m_recoveredStatus;
            }
            if (m_active is not null &&
                m_active.Phase is not ReleaseUpdateTransactionPhase.Completed and
                    not ReleaseUpdateTransactionPhase.RolledBack and
                    not ReleaseUpdateTransactionPhase.Failed)
            {
                return Failure(
                    m_active,
                    ReleaseUpdateTransactionFailureCode.TransactionAlreadyActive,
                    "A release update transaction is already active.");
            }

            m_recoveredStatus = null;
            OfflineReleaseInstallPreflightCommandLine command = new(
                OfflineReleaseInstallPreflightCommandKind.Preflight,
                request.BundleDirectory,
                request.InstalledReleaseIdentity,
                request.InstalledVersion,
                request.ConfigurationSchemaVersion,
                request.ProtocolVersion,
                ApplicationArguments: []);
            OfflineReleaseInstallPreflightResult preflight =
                await m_preflight.CreateAsync(command, cancellationToken);
            if (!preflight.Succeeded)
            {
                return Failure(null, ReleaseUpdateTransactionFailureCode.PreflightFailed,
                    preflight.Message);
            }

            VerifiedReleaseInstallationPlanCompositionResult installation =
                m_installationPlan.Compose(preflight, m_paths);
            if (!installation.Succeeded)
            {
                return Failure(null,
                    ReleaseUpdateTransactionFailureCode.InstallationPlanFailed,
                    installation.Message);
            }
            VerifiedReleaseInstallationPlan? exactInstallationPlan = installation.Plan;
            if (exactInstallationPlan is null)
            {
                return Failure(null,
                    ReleaseUpdateTransactionFailureCode.InstallationPlanFailed,
                    "The successful installation plan did not retain its exact internal token.");
            }
            VerifiedReleaseStagingReport staging =
                await m_staging.StageAsync(exactInstallationPlan, cancellationToken);
            if (!staging.Succeeded)
            {
                return Failure(null, ReleaseUpdateTransactionFailureCode.StagingFailed,
                    staging.Message,
                    staging.CleanupRequired);
            }
            VerifiedReleaseArchiveExtractionReport extraction =
                await m_extraction.ExtractAsync(staging, cancellationToken);
            if (!extraction.Succeeded)
            {
                return Failure(null, ReleaseUpdateTransactionFailureCode.ExtractionFailed,
                    extraction.Message,
                    extraction.CleanupRequired);
            }
            VerifiedReleaseExtractedPublicationPlanCompositionResult publicationPlan =
                m_publicationPlan.Compose(extraction);
            if (!publicationPlan.Succeeded)
            {
                return Failure(null,
                    ReleaseUpdateTransactionFailureCode.PublicationPlanFailed,
                    publicationPlan.Message);
            }
            VerifiedReleaseExtractedPublicationReport publication =
                await m_publication.PublishAsync(publicationPlan, cancellationToken);
            if (!publication.Succeeded)
            {
                return Failure(null, ReleaseUpdateTransactionFailureCode.PublicationFailed,
                    publication.Message,
                    publication.ReconciliationRequired);
            }
            VerifiedReleaseActivationPlanCompositionResult activation =
                m_activationPlan.Compose(publication);
            if (!activation.Succeeded)
            {
                return Failure(null,
                    ReleaseUpdateTransactionFailureCode.ActivationPlanFailed,
                    activation.Message);
            }
            VerifiedReleaseActivationConfigurationBackupPlanReport backupPlan =
                m_backupPlan.Compose(activation);
            if (!backupPlan.Succeeded)
            {
                return Failure(null,
                    ReleaseUpdateTransactionFailureCode.BackupPlanFailed,
                    backupPlan.Message);
            }
            VerifiedReleaseActivationConfigurationBackupReport backup =
                await m_backup.ExecuteAsync(backupPlan, cancellationToken);
            if (!backup.Succeeded)
            {
                return Failure(null, ReleaseUpdateTransactionFailureCode.BackupFailed,
                    backup.Message,
                    backup.ReconciliationRequired);
            }
            VerifiedReleaseActivationMigrationPlanReport migrationPlan =
                m_migrationPlan.Compose(activation, backup);
            if (!migrationPlan.Succeeded)
            {
                return Failure(null,
                    ReleaseUpdateTransactionFailureCode.MigrationPlanFailed,
                    migrationPlan.Message);
            }
            VerifiedReleaseActivationMigrationRunnerSelectionReport selection =
                m_runnerSelector.Select(migrationPlan);
            if (!selection.Succeeded)
            {
                return Failure(null,
                    ReleaseUpdateTransactionFailureCode
                        .MigrationRunnerSelectionFailed,
                    selection.Message);
            }
            VerifiedReleaseActivationMigrationRunnerInvocationReport invocation =
                await m_runnerProbe.InvokeAsync(selection, cancellationToken);
            if (!invocation.Succeeded)
            {
                return Failure(null,
                    ReleaseUpdateTransactionFailureCode.MigrationRunnerProbeFailed,
                    invocation.Message);
            }
            VerifiedReleaseActivationMigrationExecutionReport migration =
                await m_migration.ExecuteAsync(invocation, cancellationToken);
            if (!migration.Succeeded)
            {
                return Failure(null, ReleaseUpdateTransactionFailureCode.MigrationFailed,
                    migration.Message,
                    migration.ReconciliationRequired);
            }
            VerifiedReleaseActivationServiceControlPlanReport servicePlan =
                m_serviceControlPlan.Compose(activation);
            if (!servicePlan.Succeeded)
            {
                return Failure(null,
                    ReleaseUpdateTransactionFailureCode.ServiceControlPlanFailed,
                    servicePlan.Message);
            }
            VerifiedReleaseActivationHealthVerificationPlanReport healthPlan =
                m_healthPlan.Compose(servicePlan);
            if (!healthPlan.Succeeded)
            {
                return Failure(null, ReleaseUpdateTransactionFailureCode.HealthPlanFailed,
                    healthPlan.Message);
            }
            VerifiedReleaseActivationRollbackPlanReport rollbackPlan =
                m_rollbackPlan.Compose(
                    activation,
                    backup,
                    migrationPlan,
                    servicePlan,
                    healthPlan);
            if (!rollbackPlan.Succeeded)
            {
                return Failure(null,
                    ReleaseUpdateTransactionFailureCode.RollbackPlanFailed,
                    rollbackPlan.Message);
            }

            PreparedReleaseUpdateTransaction transaction = new()
            {
                TransactionId = Guid.NewGuid().ToString("N"),
                Request = request,
                Publication = publication,
                Activation = activation,
                Backup = backup,
                MigrationExecution = migration,
                ServiceControlPlan = servicePlan,
                HealthPlan = healthPlan,
                RollbackPlan = rollbackPlan,
                PreparedAt = m_timeProvider.GetUtcNow(),
                Phase = ReleaseUpdateTransactionPhase.Prepared
            };
            m_active = transaction;
            if (!await TryJournalAsync(transaction, cancellationToken))
            {
                transaction.Phase = ReleaseUpdateTransactionPhase.Failed;
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.JournalWriteFailed,
                    "The prepared transaction could not be durably journaled.");
            }
            transaction.Phase = ReleaseUpdateTransactionPhase.AwaitingApproval;
            await TryJournalAsync(transaction, CancellationToken.None);
            return ReleaseUpdateTransactionReport.Create(
                transaction,
                succeeded: true,
                ReleaseUpdateTransactionFailureCode.None,
                "The signed release is published inactive with immutable backup, migration, health, service-control, and rollback plans ready; fresh operator approval is required before activation.",
                transaction.Phase);
        }
        finally
        {
            m_gate.Release();
        }
    }

    [SupportedOSPlatform("linux")]
    internal async Task<ReleaseUpdateTransactionReport> ApproveAndActivateAsync(
        string transactionId,
        VerifiedReleaseActivationOperatorAuthenticationEvidence authentication,
        CancellationToken cancellationToken = default)
    {
        if (!m_settings.ExecutionEnabled)
        {
            return Failure(m_active, ReleaseUpdateTransactionFailureCode.ExecutionDisabled,
                "Release update transaction execution is disabled.");
        }
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            PreparedReleaseUpdateTransaction? transaction = m_active;
            if (transaction is null ||
                !string.Equals(transaction.TransactionId, transactionId,
                    StringComparison.Ordinal))
            {
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.TransactionNotFound,
                    "The exact prepared release update transaction was not found.");
            }
            if (transaction.Phase != ReleaseUpdateTransactionPhase.AwaitingApproval)
            {
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.TransactionPhaseInvalid,
                    "The release update transaction is not awaiting approval.");
            }

            VerifiedReleaseActivationOperatorApprovalReport approval =
                m_approval.Approve(transaction.Activation, authentication);
            if (!approval.Succeeded)
            {
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.ApprovalFailed,
                    approval.Message);
            }
            transaction.Approval = approval;
            transaction.Phase = ReleaseUpdateTransactionPhase.ClosingLeaseAdmission;
            await TryJournalAsync(transaction, CancellationToken.None);

            VerifiedReleaseActivationLeaseQuiescenceReport quiescence =
                m_quiescence.Compose(transaction.Activation);
            quiescence = m_quiescence.CloseAdmission(quiescence);
            if (!quiescence.Succeeded || !quiescence.AdmissionClosed)
            {
                RevokeApproval(approval);
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.LeaseAdmissionFailed,
                    quiescence.Message);
            }
            transaction.Quiescence = quiescence;
            DateTimeOffset drainDeadline = m_timeProvider.GetUtcNow() +
                TimeSpan.FromSeconds(m_settings.LeaseDrainSeconds);
            while (!quiescence.DrainSatisfied &&
                m_timeProvider.GetUtcNow() < drainDeadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                quiescence = m_quiescence.EvaluateDrain(quiescence);
                if (!quiescence.Succeeded)
                {
                    break;
                }
                transaction.Quiescence = quiescence;
            }
            if (!quiescence.Succeeded || !quiescence.DrainSatisfied)
            {
                ReleaseAdmission(transaction);
                RevokeApproval(approval);
                transaction.Phase = ReleaseUpdateTransactionPhase.AwaitingApproval;
                await TryJournalAsync(transaction, CancellationToken.None);
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.LeaseDrainTimedOut,
                    "TX-lease admission was closed, but existing leases did not drain within the bounded window.");
            }

            VerifiedReleaseActivationEvidenceCollectionReport evidence =
                await m_evidence.CollectAsync(transaction.Activation, cancellationToken);
            if (!evidence.Succeeded || evidence.Collection is null)
            {
                ReleaseAdmission(transaction);
                RevokeApproval(approval);
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.EvidenceCollectionFailed,
                    evidence.Message);
            }
            VerifiedReleaseActivationReadinessEvidence preSwitchEvidence =
                evidence.Collection.Evidence with
                {
                    ServiceControlReady = true,
                    HealthVerificationReady = false,
                    RollbackReady = true,
                    OperatorApproved = true
                };
            VerifiedReleaseActivationReadinessReport preSwitchReadiness =
                m_readiness.Evaluate(transaction.Activation, preSwitchEvidence);
            if (!IsExpectedPreSwitchReadiness(preSwitchReadiness))
            {
                ReleaseAdmission(transaction);
                RevokeApproval(approval);
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.SafetyReadinessFailed,
                    preSwitchReadiness.Message);
            }
            transaction.Phase = ReleaseUpdateTransactionPhase.SafetyValidated;
            await TryJournalAsync(transaction, CancellationToken.None);

            if (!transaction.ServiceControlPlan.HostRestartRequired)
            {
                VerifiedReleaseActivationServiceControlExecutionReport preSwitch =
                    await m_serviceControl.ExecutePreSwitchStopAsync(
                        transaction.ServiceControlPlan,
                        cancellationToken);
                if (!preSwitch.Succeeded)
                {
                    ReleaseAdmission(transaction);
                    RevokeApproval(approval);
                    return Failure(transaction,
                        ReleaseUpdateTransactionFailureCode
                            .PreSwitchServiceControlFailed,
                        preSwitch.Message,
                        preSwitch.ReconciliationRequired);
                }
                transaction.Phase = ReleaseUpdateTransactionPhase.ServicesStopped;
                await TryJournalAsync(transaction, CancellationToken.None);
            }

            VerifiedReleaseActivationCurrentPointerSwitchReport pointer =
                await m_pointerSwitch.ExecuteAsync(
                    transaction.ServiceControlPlan,
                    cancellationToken);
            if (!pointer.Succeeded)
            {
                ReleaseAdmission(transaction);
                RevokeApproval(approval);
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.CurrentPointerSwitchFailed,
                    pointer.Message,
                    pointer.ReconciliationRequired);
            }
            transaction.PointerSwitch = pointer;
            transaction.Phase = ReleaseUpdateTransactionPhase.PointerSwitched;
            await TryJournalAsync(transaction, CancellationToken.None);

            if (transaction.ServiceControlPlan.HostRestartRequired)
            {
                ReleaseAdmission(transaction);
                RevokeApproval(approval);
                transaction.Phase = ReleaseUpdateTransactionPhase.RestartPending;
                if (!await TryJournalAsync(transaction, CancellationToken.None))
                {
                    transaction.Phase =
                        ReleaseUpdateTransactionPhase.ReconciliationRequired;
                    await TryJournalAsync(transaction, CancellationToken.None);
                    return Failure(
                        transaction,
                        ReleaseUpdateTransactionFailureCode.JournalWriteFailed,
                        "The exact host-restart transaction could not enter its durable pending phase.",
                        reconciliationRequired: true);
                }

                VerifiedReleaseActivationHostRestartReport restart =
                    await m_hostRestart.RequestAsync(
                        transaction.TransactionId,
                        transaction.ServiceControlPlan,
                        pointer,
                        cancellationToken);
                if (!restart.Succeeded || !restart.HostRestartRequested)
                {
                    transaction.Phase =
                        ReleaseUpdateTransactionPhase.ReconciliationRequired;
                    await TryJournalAsync(transaction, CancellationToken.None);
                    return Failure(
                        transaction,
                        ReleaseUpdateTransactionFailureCode
                            .HostRestartRequestFailed,
                        restart.Message,
                        reconciliationRequired: true);
                }

                return ReleaseUpdateTransactionReport.Create(
                    transaction,
                    succeeded: true,
                    ReleaseUpdateTransactionFailureCode.None,
                    "The exact host restart was requested; activation remains pending until the transaction-bound post-boot continuation records health or reconciliation.",
                    transaction.Phase,
                    operatorApproved: false,
                    admissionClosed: false,
                    pointerChanged: true,
                    serviceControlCompleted: false,
                    healthVerified: false,
                    restartPending: true);
            }

            VerifiedReleaseActivationServiceControlExecutionReport postSwitch =
                await m_serviceControl.ExecutePostSwitchStartAsync(
                    transaction.ServiceControlPlan,
                    pointer,
                    cancellationToken);
            transaction.PostSwitchServiceControl = postSwitch;
            if (!postSwitch.Succeeded)
            {
                ReleaseUpdateTransactionReport rollback = await AutomaticRollbackAsync(
                    transaction,
                    postSwitch,
                    healthFailure: null,
                    cancellationToken);
                return rollback.Succeeded
                    ? Failure(transaction,
                        ReleaseUpdateTransactionFailureCode
                            .PostSwitchServiceControlFailed,
                        "Post-switch service control failed and the exact automatic rollback completed.")
                    : rollback;
            }
            transaction.Phase = ReleaseUpdateTransactionPhase.ServicesStarted;
            await TryJournalAsync(transaction, CancellationToken.None);

            VerifiedReleaseActivationHealthVerificationReport health =
                await m_health.ExecuteAsync(transaction.HealthPlan, cancellationToken);
            transaction.Health = health;
            if (!health.Succeeded)
            {
                ReleaseUpdateTransactionReport rollback = await AutomaticRollbackAsync(
                    transaction,
                    serviceFailure: null,
                    health,
                    cancellationToken);
                return rollback.Succeeded
                    ? Failure(transaction,
                        ReleaseUpdateTransactionFailureCode.HealthVerificationFailed,
                        "Post-switch health verification failed and the exact automatic rollback completed.")
                    : rollback;
            }
            transaction.Phase = ReleaseUpdateTransactionPhase.HealthVerified;

            VerifiedReleaseActivationEvidenceCollectionReport finalEvidence =
                await m_evidence.CollectAsync(
                    transaction.Activation,
                    CancellationToken.None);
            if (!finalEvidence.Succeeded || finalEvidence.Collection is null)
            {
                ReleaseUpdateTransactionReport rollback = await AutomaticRollbackAsync(
                    transaction,
                    serviceFailure: null,
                    CreateSyntheticHealthFailure(transaction, health),
                    CancellationToken.None);
                return rollback.Succeeded
                    ? Failure(transaction,
                        ReleaseUpdateTransactionFailureCode.FinalReadinessFailed,
                        "Final activation evidence could not be collected and rollback completed.")
                    : rollback;
            }
            VerifiedReleaseActivationReadinessEvidence finalReadinessEvidence =
                finalEvidence.Collection.Evidence with
                {
                    RollbackReady = true,
                    OperatorApproved = true
                };
            VerifiedReleaseActivationReadinessReport finalReadiness =
                m_readiness.Evaluate(transaction.Activation, finalReadinessEvidence);
            if (!finalReadiness.Succeeded)
            {
                ReleaseUpdateTransactionReport rollback = await AutomaticRollbackAsync(
                    transaction,
                    serviceFailure: null,
                    CreateSyntheticHealthFailure(transaction, health),
                    CancellationToken.None);
                return rollback.Succeeded
                    ? Failure(transaction,
                        ReleaseUpdateTransactionFailureCode.FinalReadinessFailed,
                        "Final activation readiness was not proven and rollback completed.")
                    : rollback;
            }

            transaction.Phase = ReleaseUpdateTransactionPhase.Completed;
            ReleaseAdmission(transaction);
            RevokeApproval(approval);
            if (!await TryJournalAsync(transaction, CancellationToken.None))
            {
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.JournalWriteFailed,
                    "Activation completed, but its final transaction journal could not be written.",
                    reconciliationRequired: true);
            }
            return ReleaseUpdateTransactionReport.Create(
                transaction,
                succeeded: true,
                ReleaseUpdateTransactionFailureCode.None,
                "The exact signed release transaction completed with health verified and TX-lease admission restored.",
                transaction.Phase,
                operatorApproved: true,
                admissionClosed: false,
                pointerChanged: true,
                serviceControlCompleted: true,
                healthVerified: true);
        }
        finally
        {
            m_gate.Release();
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("linux")]
    internal async Task<ReleaseUpdateTransactionReport> ApproveAndRollbackAsync(
        string transactionId,
        VerifiedReleaseActivationOperatorAuthenticationEvidence authentication,
        CancellationToken cancellationToken = default)
    {
        if (!m_settings.ExecutionEnabled)
        {
            return Failure(m_active, ReleaseUpdateTransactionFailureCode.ExecutionDisabled,
                "Release update transaction execution is disabled.");
        }
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            PreparedReleaseUpdateTransaction? transaction = m_active;
            if (transaction is null ||
                !string.Equals(transaction.TransactionId, transactionId,
                    StringComparison.Ordinal))
            {
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.TransactionNotFound,
                    "The exact completed release update transaction was not found.");
            }
            if (transaction.Phase != ReleaseUpdateTransactionPhase.Completed ||
                transaction.PointerSwitch is null ||
                transaction.Health is null ||
                !transaction.Health.Succeeded)
            {
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.TransactionPhaseInvalid,
                    "Manual rollback requires one exact completed and health-verified activation transaction.");
            }

            VerifiedReleaseActivationOperatorApprovalReport approval =
                m_approval.Approve(transaction.Activation, authentication);
            if (!approval.Succeeded)
            {
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.ApprovalFailed,
                    approval.Message);
            }
            transaction.Approval = approval;

            VerifiedReleaseActivationLeaseQuiescenceReport quiescence =
                m_quiescence.Compose(transaction.Activation);
            quiescence = m_quiescence.CloseAdmission(quiescence);
            transaction.Quiescence = quiescence;
            if (!quiescence.Succeeded || !quiescence.AdmissionClosed)
            {
                RevokeApproval(approval);
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.LeaseAdmissionFailed,
                    quiescence.Message);
            }
            DateTimeOffset drainDeadline = m_timeProvider.GetUtcNow() +
                TimeSpan.FromSeconds(m_settings.LeaseDrainSeconds);
            while (!quiescence.DrainSatisfied &&
                m_timeProvider.GetUtcNow() < drainDeadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                quiescence = m_quiescence.EvaluateDrain(quiescence);
                transaction.Quiescence = quiescence;
                if (!quiescence.Succeeded)
                {
                    break;
                }
            }
            if (!quiescence.Succeeded || !quiescence.DrainSatisfied)
            {
                ReleaseAdmission(transaction);
                RevokeApproval(approval);
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.LeaseDrainTimedOut,
                    "TX leases did not drain within the bounded manual rollback window.");
            }

            VerifiedReleaseActivationEvidenceCollectionReport evidence =
                await m_evidence.CollectAsync(transaction.Activation, cancellationToken);
            if (!evidence.Succeeded || evidence.Collection is null)
            {
                ReleaseAdmission(transaction);
                RevokeApproval(approval);
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.EvidenceCollectionFailed,
                    evidence.Message);
            }
            VerifiedReleaseActivationReadinessReport readiness =
                m_readiness.Evaluate(
                    transaction.Activation,
                    evidence.Collection.Evidence with
                    {
                        RollbackReady = true,
                        OperatorApproved = true
                    });
            if (!readiness.Succeeded)
            {
                ReleaseAdmission(transaction);
                RevokeApproval(approval);
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.SafetyReadinessFailed,
                    readiness.Message);
            }

            transaction.Phase = ReleaseUpdateTransactionPhase.RollingBack;
            await TryJournalAsync(transaction, CancellationToken.None);
            VerifiedReleaseActivationRollbackExecutionReport rollback =
                await m_rollback.ExecuteOperatorRequestedAsync(
                    transaction.RollbackPlan,
                    transaction.PointerSwitch,
                    approval,
                    cancellationToken);
            ReleaseAdmission(transaction);
            RevokeApproval(approval);
            if (!rollback.Succeeded)
            {
                transaction.Phase = rollback.ReconciliationRequired
                    ? ReleaseUpdateTransactionPhase.ReconciliationRequired
                    : ReleaseUpdateTransactionPhase.Failed;
                await TryJournalAsync(transaction, CancellationToken.None);
                return Failure(transaction,
                    ReleaseUpdateTransactionFailureCode.ManualRollbackFailed,
                    rollback.Message,
                    rollback.ReconciliationRequired);
            }

            transaction.Phase = ReleaseUpdateTransactionPhase.RolledBack;
            await TryJournalAsync(transaction, CancellationToken.None);
            return ReleaseUpdateTransactionReport.Create(
                transaction,
                succeeded: true,
                ReleaseUpdateTransactionFailureCode.None,
                "Freshly approved manual rollback restored the exact previous release, configuration, services, and health.",
                transaction.Phase,
                pointerChanged: true,
                serviceControlCompleted: true,
                healthVerified: true,
                rollbackPerformed: true);
        }
        finally
        {
            m_gate.Release();
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task<ReleaseUpdateTransactionReport> AutomaticRollbackAsync(
        PreparedReleaseUpdateTransaction transaction,
        VerifiedReleaseActivationServiceControlExecutionReport? serviceFailure,
        VerifiedReleaseActivationHealthVerificationReport? healthFailure,
        CancellationToken cancellationToken)
    {
        transaction.Phase = ReleaseUpdateTransactionPhase.RollingBack;
        await TryJournalAsync(transaction, CancellationToken.None);
        VerifiedReleaseActivationRollbackExecutionReport rollback =
            serviceFailure is not null
                ? await m_rollback.ExecuteAfterPostSwitchServiceFailureAsync(
                    transaction.RollbackPlan,
                    transaction.PointerSwitch!,
                    serviceFailure,
                    cancellationToken)
                : await m_rollback.ExecuteAfterHealthFailureAsync(
                    transaction.RollbackPlan,
                    transaction.PointerSwitch!,
                    healthFailure!,
                    cancellationToken);
        ReleaseAdmission(transaction);
        if (transaction.Approval is not null)
        {
            RevokeApproval(transaction.Approval);
        }
        if (!rollback.Succeeded)
        {
            transaction.Phase = rollback.ReconciliationRequired
                ? ReleaseUpdateTransactionPhase.ReconciliationRequired
                : ReleaseUpdateTransactionPhase.Failed;
            await TryJournalAsync(transaction, CancellationToken.None);
            return Failure(transaction,
                ReleaseUpdateTransactionFailureCode.AutomaticRollbackFailed,
                rollback.Message,
                rollback.ReconciliationRequired);
        }
        transaction.Phase = ReleaseUpdateTransactionPhase.RolledBack;
        await TryJournalAsync(transaction, CancellationToken.None);
        return ReleaseUpdateTransactionReport.Create(
            transaction,
            succeeded: true,
            ReleaseUpdateTransactionFailureCode.None,
            "The exact automatic rollback restored the previous release, configuration, services, and health.",
            transaction.Phase,
            pointerChanged: true,
            serviceControlCompleted: true,
            healthVerified: true,
            rollbackPerformed: true);
    }

    private static bool IsExpectedPreSwitchReadiness(
        VerifiedReleaseActivationReadinessReport report) =>
        !report.Succeeded &&
        report.FailureCode ==
            VerifiedReleaseActivationReadinessFailureCode
                .HealthVerificationNotReady &&
        report.ReleaseStatusStable &&
        report.TxLeaseAdmissionClosed &&
        report.ActiveTxLeaseCount == 0 &&
        report.AllSessionsConnected &&
        report.AllRadiosFreshIdle &&
        report.AllSessionSafetyDisarmed &&
        report.AllWatchdogsDisarmed &&
        report.ConfigurationBackupReady &&
        report.MigrationReady &&
        report.ServiceControlReady;

    private static VerifiedReleaseActivationHealthVerificationReport
        CreateSyntheticHealthFailure(
            PreparedReleaseUpdateTransaction transaction,
            VerifiedReleaseActivationHealthVerificationReport completed)
    {
        VerifiedReleaseActivationHealthVerificationPlan plan =
            transaction.HealthPlan.Plan ??
            throw new InvalidOperationException(
                "The exact health plan is unavailable for rollback triggering.");
        return completed with
        {
            Succeeded = false,
            FailureCode =
                VerifiedReleaseActivationHealthVerificationFailureCode
                    .ObservationDrift,
            Message = "Final activation evidence drifted after health verification.",
            HealthEvidenceProduced = false,
            ServiceHealthReady = false,
            ActivationAuthorized = false,
            FailedPlan = plan
        };
    }

    private void ReleaseAdmission(PreparedReleaseUpdateTransaction transaction)
    {
        if (transaction.Quiescence is not null &&
            transaction.Quiescence.AdmissionClosed)
        {
            transaction.Quiescence =
                m_quiescence.ReleaseAdmission(transaction.Quiescence);
        }
    }

    private void RevokeApproval(
        VerifiedReleaseActivationOperatorApprovalReport report)
    {
        if (report.Approval is not null)
        {
            _ = m_approval.Revoke(report.Approval);
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task<bool> TryJournalAsync(
        PreparedReleaseUpdateTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await m_journal.WriteAsync(
                transaction,
                pointerChanged: transaction.PointerSwitch?.CurrentPointerChanged ?? false,
                rollbackPerformed:
                    transaction.Phase == ReleaseUpdateTransactionPhase.RolledBack,
                restartPending:
                    transaction.Phase == ReleaseUpdateTransactionPhase.RestartPending,
                reconciliationRequired:
                    transaction.Phase ==
                        ReleaseUpdateTransactionPhase.ReconciliationRequired,
                cancellationToken);
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or NotSupportedException or
                JsonException)
        {
            return false;
        }
    }

    private static ReleaseUpdateTransactionReport Failure(
        PreparedReleaseUpdateTransaction? transaction,
        ReleaseUpdateTransactionFailureCode failureCode,
        string message,
        bool reconciliationRequired = false)
    {
        if (transaction is not null && reconciliationRequired)
        {
            transaction.Phase = ReleaseUpdateTransactionPhase.ReconciliationRequired;
        }
        return ReleaseUpdateTransactionReport.Create(
            transaction,
            succeeded: false,
            reconciliationRequired
                ? ReleaseUpdateTransactionFailureCode.ReconciliationRequired
                : failureCode,
            message,
            transaction?.Phase,
            operatorApproved: transaction?.Approval?.Succeeded ?? false,
            admissionClosed: transaction?.Quiescence?.AdmissionClosed ?? false,
            pointerChanged: transaction?.PointerSwitch?.CurrentPointerChanged ?? false,
            serviceControlCompleted:
                transaction?.PostSwitchServiceControl?.ServiceControlReady ?? false,
            healthVerified: transaction?.Health?.ServiceHealthReady ?? false,
            rollbackPerformed:
                transaction?.Phase == ReleaseUpdateTransactionPhase.RolledBack,
            restartPending:
                transaction?.Phase == ReleaseUpdateTransactionPhase.RestartPending,
            reconciliationRequired: reconciliationRequired);
    }

    internal static ReleaseUpdateTransactionReport RecoveredStatus(
        ReleaseUpdateJournalDocument document)
    {
        bool completed = document.Phase == ReleaseUpdateTransactionPhase.Completed;
        bool rolledBack = document.Phase == ReleaseUpdateTransactionPhase.RolledBack;
        bool restartPending =
            document.Phase == ReleaseUpdateTransactionPhase.RestartPending &&
            document.RestartPending &&
            !document.ReconciliationRequired;
        bool terminal = completed || rolledBack;
        bool safeStatus = terminal || restartPending;
        return new ReleaseUpdateTransactionReport(
            Succeeded: safeStatus,
            safeStatus
                ? ReleaseUpdateTransactionFailureCode.None
                : ReleaseUpdateTransactionFailureCode.ReconciliationRequired,
            terminal
                ? "A terminal release transaction summary was recovered after updater restart. Exact rollback authority was intentionally not reconstructed; a new transaction may be prepared."
                : restartPending
                    ? "The exact host-restart transaction remains pending post-boot health verification; no approval or rollback authority was reconstructed."
                    : "A non-terminal release transaction was recovered after updater restart; exact in-memory authority cannot be reconstructed and local reconciliation is required.",
            document.TransactionId,
            safeStatus
                ? document.Phase
                : ReleaseUpdateTransactionPhase.ReconciliationRequired,
            document.SetupRevision,
            document.InstalledReleaseIdentity,
            document.TargetReleaseIdentity,
            document.TargetVersion,
            PackageCount: 0,
            FileCount: 0,
            PublishedBytes: 0,
            InactiveReleasePublished: document.CurrentPointerChanged,
            ConfigurationBackupReady: safeStatus,
            MigrationReady: safeStatus,
            OperatorApproved: false,
            TxLeaseAdmissionClosed: false,
            CurrentPointerChanged: document.CurrentPointerChanged,
            ServiceControlCompleted: terminal,
            HealthVerified: terminal,
            RollbackReady: false,
            RollbackPerformed: rolledBack || document.RollbackPerformed,
            RestartPending: restartPending,
            ReconciliationRequired:
                !safeStatus || document.ReconciliationRequired,
            ActivationCompleted: completed);
    }

    private static bool ValidateRequest(ReleaseUpdateInstallRequest request)
    {
        try
        {
            return Path.IsPathFullyQualified(request.BundleDirectory) &&
                string.Equals(
                    Path.GetFullPath(request.BundleDirectory),
                    request.BundleDirectory,
                    StringComparison.Ordinal) &&
                string.Equals(
                    InstallationReleaseIdentity.Parse(
                        request.InstalledReleaseIdentity),
                    request.InstalledReleaseIdentity,
                    StringComparison.Ordinal) &&
                ReleaseSemanticVersion.TryParse(request.InstalledVersion, out _) &&
                request.ConfigurationSchemaVersion > 0 &&
                request.ProtocolVersion > 0;
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static ReleaseUpdateTransactionSettings ValidateSettings(
        ReleaseUpdateTransactionSettings settings)
    {
        if (settings.LeaseDrainSeconds is < 1 or >
            ReleaseUpdateTransactionSettings.MaximumLeaseDrainSeconds)
        {
            throw new InvalidOperationException(
                "Release update lease-drain duration is outside its bounded range.");
        }
        return new ReleaseUpdateTransactionSettings
        {
            ExecutionEnabled = settings.ExecutionEnabled,
            LeaseDrainSeconds = settings.LeaseDrainSeconds
        };
    }
}
