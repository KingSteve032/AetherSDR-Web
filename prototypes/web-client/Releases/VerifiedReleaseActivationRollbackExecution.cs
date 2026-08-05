using System.Buffers;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public sealed class ReleaseActivationRollbackSettings
{
    public const string SectionName = "ReleaseActivationRollback";

    public bool ExecutionEnabled { get; init; }
    public string ExpectedStationId { get; init; } = string.Empty;
}

public enum VerifiedReleaseActivationRollbackTriggerKind
{
    PostSwitchServiceControlFailure = 1,
    PostSwitchHealthFailure = 2
}

public enum VerifiedReleaseActivationRollbackExecutionFailureCode
{
    None = 0,
    ExecutionDisabled = 1,
    UnsupportedPlatform = 2,
    RollbackPlanNotEligible = 3,
    RollbackPlanUnavailable = 4,
    RollbackPlanMismatch = 5,
    CurrentPointerSwitchUnavailable = 6,
    FailureTriggerInvalid = 7,
    StatusUnavailable = 8,
    StatusMismatch = 9,
    SetupUnavailable = 10,
    SetupMismatch = 11,
    UnsupportedTopology = 12,
    StationIdentityMismatch = 13,
    ImmutableBackupInvalid = 14,
    RestoreLayoutUnsafe = 15,
    RestoreStagingFailed = 16,
    RemoteServiceControlUnavailable = 17,
    TargetServiceStopFailed = 18,
    LiveRootRestoreFailed = 19,
    CurrentPointerRollbackFailed = 20,
    InstalledServiceStartFailed = 21,
    InstalledHealthVerificationFailed = 22,
    DisplacedTreeCleanupFailed = 23,
    ObservationDrift = 24,
    RollbackAlreadyCompleted = 25,
    ReconciliationRequired = 26
}

public sealed record VerifiedReleaseActivationRollbackExecutionReport(
    bool Succeeded,
    VerifiedReleaseActivationRollbackExecutionFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    VerifiedReleaseActivationRollbackTriggerKind TriggerKind,
    bool ExecutionEnabled,
    bool ExecutionAvailable,
    bool ExactRollbackPlanBound,
    bool ExactActivationPlanBound,
    bool ExactPointerSwitchEvidenceBound,
    bool ExactFailureTriggerBound,
    bool TargetReleaseActiveBeforeRollback,
    bool InstalledReleaseActiveAfterRollback,
    bool SetupStable,
    bool TopologyBound,
    bool ImmutableOriginalBackupRevalidated,
    int RestoreSourceCount,
    int RestoreDirectoryCount,
    int RestoreFileCount,
    long RestoreBytes,
    int PlannedStopActionCount,
    int ExecutedStopActionCount,
    int TopologyNoOpStopActionCount,
    int RestoredLiveRootCount,
    bool AtomicCurrentPointerRollbackCompleted,
    int PlannedStartActionCount,
    int ExecutedStartActionCount,
    int TopologyNoOpStartActionCount,
    int HealthTargetCount,
    int VerifiedHealthTargetCount,
    int UnitActivityCheckCount,
    int LoopbackHttpCheckCount,
    int FreshBrokerLinkCheckCount,
    int DisplacedTreeCleanupCount,
    bool ReverseMigrationRunnerUsed,
    bool ProcessInvocationPerformed,
    bool SystemdCommandPerformed,
    bool ShellUsed,
    bool NetworkRequestPerformed,
    bool CurrentPointerChanged,
    bool ConfigurationRestored,
    bool ServicesRestored,
    bool InstalledHealthVerified,
    bool RollbackPerformed,
    bool RollbackReady,
    bool ReconciliationRequired,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationRollbackEvidence? Evidence { get; init; }

    internal static VerifiedReleaseActivationRollbackExecutionReport Failure(
        VerifiedReleaseActivationRollbackExecutionFailureCode failureCode,
        string message,
        ReleaseActivationRollbackSettings settings,
        VerifiedReleaseActivationRollbackTriggerKind triggerKind,
        VerifiedReleaseActivationRollbackPlanReport? planReport = null,
        RollbackExecutionTally? tally = null,
        bool exactPlanBound = false,
        bool exactPointerBound = false,
        bool exactTriggerBound = false,
        bool targetActive = false,
        bool installedActive = false,
        bool setupStable = false,
        bool topologyBound = false,
        bool backupRevalidated = false,
        bool pointerChanged = false,
        bool configurationRestored = false,
        bool servicesRestored = false,
        bool healthVerified = false,
        bool rollbackPerformed = false,
        bool reconciliationRequired = false) =>
        new(
            false,
            failureCode,
            message,
            planReport?.SetupRevision,
            planReport?.InstalledReleaseIdentity ?? string.Empty,
            planReport?.TargetReleaseIdentity ?? string.Empty,
            triggerKind,
            settings.ExecutionEnabled,
            settings.ExecutionEnabled && OperatingSystem.IsLinux(),
            exactPlanBound,
            exactPlanBound,
            exactPointerBound,
            exactTriggerBound,
            targetActive,
            installedActive,
            setupStable,
            topologyBound,
            backupRevalidated,
            tally?.RestoreSourceCount ?? 0,
            tally?.RestoreDirectoryCount ?? 0,
            tally?.RestoreFileCount ?? 0,
            tally?.RestoreBytes ?? 0,
            tally?.PlannedStopActionCount ?? 0,
            tally?.ExecutedStopActionCount ?? 0,
            tally?.TopologyNoOpStopActionCount ?? 0,
            tally?.RestoredLiveRootCount ?? 0,
            pointerChanged,
            tally?.PlannedStartActionCount ?? 0,
            tally?.ExecutedStartActionCount ?? 0,
            tally?.TopologyNoOpStartActionCount ?? 0,
            tally?.HealthTargetCount ?? 0,
            tally?.VerifiedHealthTargetCount ?? 0,
            tally?.UnitActivityCheckCount ?? 0,
            tally?.LoopbackHttpCheckCount ?? 0,
            tally?.FreshBrokerLinkCheckCount ?? 0,
            tally?.DisplacedTreeCleanupCount ?? 0,
            ReverseMigrationRunnerUsed: false,
            ProcessInvocationPerformed:
                (tally?.ServiceProcessInvocationCount ?? 0) > 0 ||
                (tally?.UnitActivityAttemptCount ?? 0) > 0,
            SystemdCommandPerformed:
                (tally?.ServiceProcessInvocationCount ?? 0) > 0 ||
                (tally?.UnitActivityAttemptCount ?? 0) > 0,
            ShellUsed: false,
            NetworkRequestPerformed:
                (tally?.LoopbackHttpAttemptCount ?? 0) > 0,
            pointerChanged,
            configurationRestored,
            servicesRestored,
            healthVerified,
            rollbackPerformed,
            RollbackReady: false,
            reconciliationRequired,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationRollbackExecutionReport Success(
        ReleaseActivationRollbackSettings settings,
        VerifiedReleaseActivationRollbackPlanReport planReport,
        VerifiedReleaseActivationRollbackTriggerKind triggerKind,
        RollbackExecutionTally tally,
        VerifiedReleaseActivationRollbackEvidence evidence) =>
        new(
            true,
            VerifiedReleaseActivationRollbackExecutionFailureCode.None,
            "The exact failed post-switch activation transaction was rolled back to the verified installed release and immutable original configuration without reverse-running migration code or authorizing activation.",
            evidence.Plan.ActivationPlan.SetupRevision,
            evidence.Plan.ActivationPlan.InstalledReleaseIdentity,
            evidence.Plan.ActivationPlan.TargetReleaseIdentity,
            triggerKind,
            settings.ExecutionEnabled,
            ExecutionAvailable: true,
            ExactRollbackPlanBound: true,
            ExactActivationPlanBound: true,
            ExactPointerSwitchEvidenceBound: true,
            ExactFailureTriggerBound: true,
            TargetReleaseActiveBeforeRollback: true,
            InstalledReleaseActiveAfterRollback: true,
            SetupStable: true,
            TopologyBound: true,
            ImmutableOriginalBackupRevalidated: true,
            tally.RestoreSourceCount,
            tally.RestoreDirectoryCount,
            tally.RestoreFileCount,
            tally.RestoreBytes,
            tally.PlannedStopActionCount,
            tally.ExecutedStopActionCount,
            tally.TopologyNoOpStopActionCount,
            tally.RestoredLiveRootCount,
            AtomicCurrentPointerRollbackCompleted: true,
            tally.PlannedStartActionCount,
            tally.ExecutedStartActionCount,
            tally.TopologyNoOpStartActionCount,
            tally.HealthTargetCount,
            tally.VerifiedHealthTargetCount,
            tally.UnitActivityCheckCount,
            tally.LoopbackHttpCheckCount,
            tally.FreshBrokerLinkCheckCount,
            tally.DisplacedTreeCleanupCount,
            ReverseMigrationRunnerUsed: false,
            ProcessInvocationPerformed:
                tally.ServiceProcessInvocationCount > 0 ||
                tally.UnitActivityAttemptCount > 0,
            SystemdCommandPerformed:
                tally.ServiceProcessInvocationCount > 0 ||
                tally.UnitActivityAttemptCount > 0,
            ShellUsed: false,
            NetworkRequestPerformed: tally.LoopbackHttpAttemptCount > 0,
            CurrentPointerChanged: true,
            ConfigurationRestored: true,
            ServicesRestored: true,
            InstalledHealthVerified: true,
            RollbackPerformed: true,
            RollbackReady: true,
            ReconciliationRequired: false,
            ActivationAuthorized: false)
        {
            Evidence = evidence
        };
}

public sealed record VerifiedReleaseActivationRollbackExecutionDiagnostics(
    bool Registered,
    bool ConfigurationRegistered,
    bool ExecutionEnabled,
    bool ExecutionAvailable,
    bool ExpectedStationIdentityConfigured,
    bool ExactRollbackPlanInputRegistered,
    bool ExactRollbackPlanBindingRegistered,
    bool ExactActivationPlanBindingRegistered,
    bool ExactCurrentPointerSwitchEvidenceInputRegistered,
    bool PostSwitchServiceFailureTriggerRegistered,
    bool PostSwitchHealthFailureTriggerRegistered,
    bool ReleaseStatusDoubleReadRegistered,
    bool SetupStateDoubleReadRegistered,
    bool TopologyBindingRegistered,
    bool ImmutableOriginalBackupRevalidationRegistered,
    bool OriginalUnixModeRestoreRegistered,
    bool ReverseMigrationRunnerRegistered,
    bool ThreeSourceRestoreRegistered,
    bool SameParentRestoreStagingRegistered,
    bool DisplacedLiveTreeRegistered,
    bool TargetServiceStopRegistered,
    bool DirectProcessRegistered,
    bool ShellRegistered,
    bool ClearedEnvironmentRegistered,
    bool UserUnitScopeRegistered,
    bool SystemUnitScopeRegistered,
    bool BoundedOutputRegistered,
    bool HardTimeoutRegistered,
    bool ProcessTreeTerminationRegistered,
    bool AtomicDirectoryReplacementRegistered,
    bool AtomicCurrentPointerRollbackRegistered,
    bool InstalledServiceStartRegistered,
    bool InstalledHealthVerificationRegistered,
    bool LoopbackOnlyHttpRegistered,
    bool ProxyBypassRegistered,
    bool RedirectRejectionRegistered,
    bool BoundedHttpBodyRegistered,
    bool FreshBrokerSnapshotRegistered,
    bool ExactStationIdentityRegistered,
    bool BoundedDeadlineRegistered,
    bool DisplacedTreeCleanupRegistered,
    bool ExactPlanEvidenceRegistered,
    bool PartialFailureReconciliationRegistered,
    bool AutomaticRetryRegistered,
    bool HostRestartRegistered,
    bool RemoteServiceControlRegistered,
    bool ActivationAuthorityRegistered,
    bool OperationalCallerRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool HttpCallerRegistered,
    bool WebSocketCallerRegistered,
    bool HostedServiceCallerRegistered,
    bool TimerCallerRegistered,
    bool AetherRemoteCommandCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record VerifiedReleaseActivationRollbackExecutionStateDiagnostics(
    bool RollbackReady,
    bool ExactRollbackPlanBound,
    bool ExactActivationPlanBound,
    bool ExactPointerSwitchEvidenceBound,
    bool ExactFailureTriggerBound,
    bool ImmutableOriginalBackupValidated,
    int RestoreSourceCount,
    int RestoreDirectoryCount,
    int RestoreFileCount,
    long RestoreBytes,
    int ExecutedStopActionCount,
    int TopologyNoOpStopActionCount,
    int RestoredLiveRootCount,
    bool CurrentPointerRolledBack,
    int ExecutedStartActionCount,
    int TopologyNoOpStartActionCount,
    int VerifiedHealthTargetCount,
    int DisplacedTreeCleanupCount,
    bool InstalledReleaseActive,
    bool SetupStable,
    bool TopologyStable,
    bool ConfigurationRestored,
    bool ServicesRestored,
    bool InstalledHealthVerified,
    bool RollbackPerformed,
    bool ReconciliationRequired,
    bool ActivationAuthorized);

internal sealed record VerifiedReleaseActivationRollbackObservation(
    bool RollbackReady,
    bool RollbackPerformed,
    DateTimeOffset? CompletedAt,
    bool ReconciliationRequired);

internal sealed class VerifiedReleaseActivationRollbackEvidence
{
    internal VerifiedReleaseActivationRollbackEvidence(
        VerifiedReleaseActivationRollbackPlan plan,
        VerifiedReleaseActivationCurrentPointerSwitchEvidence pointerEvidence,
        VerifiedReleaseActivationRollbackTriggerKind triggerKind,
        InstallationTopologyKind topology,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        RollbackExecutionTally tally)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        PointerEvidence = pointerEvidence ??
            throw new ArgumentNullException(nameof(pointerEvidence));
        if (!ReferenceEquals(pointerEvidence.ServiceControlPlan, plan.ServiceControlPlan) ||
            startedAt == default || completedAt < startedAt)
        {
            throw new InvalidOperationException(
                "Rollback evidence is not bound to the exact failed activation transaction.");
        }
        TriggerKind = triggerKind;
        Topology = topology;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Tally = tally.Clone();
    }

    internal VerifiedReleaseActivationRollbackPlan Plan { get; }
    internal VerifiedReleaseActivationCurrentPointerSwitchEvidence PointerEvidence { get; }
    internal VerifiedReleaseActivationRollbackTriggerKind TriggerKind { get; }
    internal InstallationTopologyKind Topology { get; }
    internal DateTimeOffset StartedAt { get; }
    internal DateTimeOffset CompletedAt { get; }
    internal RollbackExecutionTally Tally { get; }
}

internal sealed record RollbackBoundAction(
    VerifiedReleaseActivationServiceControlAction Action,
    bool TopologyNoOp);

internal sealed class RollbackExecutionTally
{
    internal int RestoreSourceCount { get; set; }
    internal int RestoreDirectoryCount { get; set; }
    internal int RestoreFileCount { get; set; }
    internal long RestoreBytes { get; set; }
    internal int PlannedStopActionCount { get; set; }
    internal int ExecutedStopActionCount { get; set; }
    internal int TopologyNoOpStopActionCount { get; set; }
    internal int PlannedStartActionCount { get; set; }
    internal int ExecutedStartActionCount { get; set; }
    internal int TopologyNoOpStartActionCount { get; set; }
    internal int ServiceProcessInvocationCount { get; set; }
    internal int RestoredLiveRootCount { get; set; }
    internal int HealthTargetCount { get; set; }
    internal int VerifiedHealthTargetCount { get; set; }
    internal int UnitActivityAttemptCount { get; set; }
    internal int UnitActivityCheckCount { get; set; }
    internal int LoopbackHttpAttemptCount { get; set; }
    internal int LoopbackHttpCheckCount { get; set; }
    internal int FreshBrokerLinkAttemptCount { get; set; }
    internal int FreshBrokerLinkCheckCount { get; set; }
    internal int DisplacedTreeCleanupCount { get; set; }

    internal RollbackExecutionTally Clone() => (RollbackExecutionTally)MemberwiseClone();
}

/// <summary>
/// Disabled-by-default, callerless Linux rollback boundary for one exact failed
/// post-switch activation transaction. Reverse migration is never invoked. Any
/// partial or unknown mutation requires reconciliation and is never retried.
/// No operational caller, activation authority, radio, watchdog, command, lease,
/// TX action, keying, or live RF operation is added.
/// </summary>
public sealed class VerifiedReleaseActivationRollbackExecutionService
{
    internal static readonly TimeSpan UnitControlTimeout =
        VerifiedReleaseActivationServiceControlExecutionService.UnitControlTimeout;
    internal static readonly TimeSpan ProbeAttemptTimeout =
        VerifiedReleaseActivationHealthVerificationService.ProbeAttemptTimeout;
    internal static readonly TimeSpan PollInterval =
        VerifiedReleaseActivationHealthVerificationService.PollInterval;
    internal const int MaximumAttemptsPerTarget =
        VerifiedReleaseActivationHealthVerificationService.MaximumAttemptsPerTarget;

    private const int BufferSize = 128 * 1024;
    private const UnixFileMode PrivateWritableDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateWritableFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>> m_statusReader;
    private readonly Func<CancellationToken, Task<InstallationSetupState>> m_setupReader;
    private readonly Func<RemoteStationAdministrationSnapshot> m_remoteStationSnapshotReader;
    private readonly IVerifiedReleaseActivationServiceControlRuntime m_serviceRuntime;
    private readonly IVerifiedReleaseActivationHealthProbeRuntime m_healthRuntime;
    private readonly IVerifiedReleaseActivationCurrentPointerRuntime m_pointerRuntime;
    private readonly Action<string, string> m_directoryMove;
    private readonly Func<string, bool> m_treeDelete;
    private readonly ReleaseActivationRollbackSettings m_settings;
    private readonly TimeProvider m_timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> m_delay;
    private readonly SemaphoreSlim m_executionGate = new(1, 1);
    private readonly object m_stateGate = new();
    private VerifiedReleaseActivationRollbackEvidence? m_completed;
    private VerifiedReleaseActivationRollbackPlan? m_reconciliationPlan;
    private VerifiedReleaseActivationCurrentPointerSwitchEvidence? m_reconciliationPointerEvidence;
    private VerifiedReleaseActivationRollbackTriggerKind? m_reconciliationTrigger;
    private RollbackExecutionTally? m_reconciliationTally;

    public VerifiedReleaseActivationRollbackExecutionService(
        ReleaseInstallationStatusReader statusReader,
        InstallationSetupStore setupStore,
        RemoteStationCatalogService remoteStations,
        IOptions<ReleaseActivationRollbackSettings> settings)
        : this(
            statusReader is null ? throw new ArgumentNullException(nameof(statusReader)) : statusReader.ReadAsync,
            setupStore is null ? throw new ArgumentNullException(nameof(setupStore)) : setupStore.LoadAsync,
            remoteStations is null ? throw new ArgumentNullException(nameof(remoteStations)) : remoteStations.GetAdministrationSnapshot,
            new LinuxVerifiedReleaseActivationServiceControlRuntime(),
            new LinuxVerifiedReleaseActivationHealthProbeRuntime(),
            new LinuxVerifiedReleaseActivationCurrentPointerRuntime(),
            Directory.Move,
            settings?.Value ?? throw new ArgumentNullException(nameof(settings)),
            TimeProvider.System,
            treeDelete: TryDeleteTreeOnCurrentPlatform)
    {
    }

    internal VerifiedReleaseActivationRollbackExecutionService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Func<CancellationToken, Task<InstallationSetupState>> setupReader,
        Func<RemoteStationAdministrationSnapshot> remoteStationSnapshotReader,
        IVerifiedReleaseActivationServiceControlRuntime serviceRuntime,
        IVerifiedReleaseActivationHealthProbeRuntime healthRuntime,
        IVerifiedReleaseActivationCurrentPointerRuntime pointerRuntime,
        Action<string, string> directoryMove,
        ReleaseActivationRollbackSettings settings,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, bool>? treeDelete = null)
    {
        m_statusReader = statusReader ?? throw new ArgumentNullException(nameof(statusReader));
        m_setupReader = setupReader ?? throw new ArgumentNullException(nameof(setupReader));
        m_remoteStationSnapshotReader = remoteStationSnapshotReader ?? throw new ArgumentNullException(nameof(remoteStationSnapshotReader));
        m_serviceRuntime = serviceRuntime ?? throw new ArgumentNullException(nameof(serviceRuntime));
        m_healthRuntime = healthRuntime ?? throw new ArgumentNullException(nameof(healthRuntime));
        m_pointerRuntime = pointerRuntime ?? throw new ArgumentNullException(nameof(pointerRuntime));
        m_directoryMove = directoryMove ?? throw new ArgumentNullException(nameof(directoryMove));
        m_treeDelete = treeDelete ?? TryDeleteTreeOnCurrentPlatform;
        m_settings = ValidateSettings(settings);
        m_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        m_delay = delay ?? ((duration, token) => Task.Delay(duration, token));

        Snapshot = new VerifiedReleaseActivationRollbackExecutionDiagnostics(
            Registered: true,
            ConfigurationRegistered: true,
            ExecutionEnabled: m_settings.ExecutionEnabled,
            ExecutionAvailable:
                m_settings.ExecutionEnabled && OperatingSystem.IsLinux(),
            ExpectedStationIdentityConfigured:
                !string.IsNullOrEmpty(m_settings.ExpectedStationId),
            ExactRollbackPlanInputRegistered: true,
            ExactRollbackPlanBindingRegistered: true,
            ExactActivationPlanBindingRegistered: true,
            ExactCurrentPointerSwitchEvidenceInputRegistered: true,
            PostSwitchServiceFailureTriggerRegistered: true,
            PostSwitchHealthFailureTriggerRegistered: true,
            ReleaseStatusDoubleReadRegistered: true,
            SetupStateDoubleReadRegistered: true,
            TopologyBindingRegistered: true,
            ImmutableOriginalBackupRevalidationRegistered: true,
            OriginalUnixModeRestoreRegistered: true,
            ReverseMigrationRunnerRegistered: false,
            ThreeSourceRestoreRegistered: true,
            SameParentRestoreStagingRegistered: true,
            DisplacedLiveTreeRegistered: true,
            TargetServiceStopRegistered: true,
            DirectProcessRegistered: true,
            ShellRegistered: false,
            ClearedEnvironmentRegistered: true,
            UserUnitScopeRegistered: true,
            SystemUnitScopeRegistered: true,
            BoundedOutputRegistered: true,
            HardTimeoutRegistered: true,
            ProcessTreeTerminationRegistered: true,
            AtomicDirectoryReplacementRegistered: true,
            AtomicCurrentPointerRollbackRegistered: true,
            InstalledServiceStartRegistered: true,
            InstalledHealthVerificationRegistered: true,
            LoopbackOnlyHttpRegistered: true,
            ProxyBypassRegistered: true,
            RedirectRejectionRegistered: true,
            BoundedHttpBodyRegistered: true,
            FreshBrokerSnapshotRegistered: true,
            ExactStationIdentityRegistered: true,
            BoundedDeadlineRegistered: true,
            DisplacedTreeCleanupRegistered: true,
            ExactPlanEvidenceRegistered: true,
            PartialFailureReconciliationRegistered: true,
            AutomaticRetryRegistered: false,
            HostRestartRegistered: false,
            RemoteServiceControlRegistered: false,
            ActivationAuthorityRegistered: false,
            OperationalCallerRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            HttpCallerRegistered: false,
            WebSocketCallerRegistered: false,
            HostedServiceCallerRegistered: false,
            TimerCallerRegistered: false,
            AetherRemoteCommandCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationRollbackExecutionDiagnostics Snapshot { get; }

    public VerifiedReleaseActivationRollbackExecutionStateDiagnostics State
    {
        get
        {
            lock (m_stateGate)
            {
                VerifiedReleaseActivationRollbackEvidence? completed = m_completed;
                RollbackExecutionTally? tally = completed?.Tally ?? m_reconciliationTally;
                bool reconciliation = m_reconciliationPlan is not null;
                return new VerifiedReleaseActivationRollbackExecutionStateDiagnostics(
                    completed is not null,
                    completed is not null || reconciliation,
                    completed is not null || reconciliation,
                    completed is not null || m_reconciliationPointerEvidence is not null,
                    completed is not null || m_reconciliationTrigger is not null,
                    completed is not null || (tally?.RestoreSourceCount ?? 0) > 0,
                    tally?.RestoreSourceCount ?? 0,
                    tally?.RestoreDirectoryCount ?? 0,
                    tally?.RestoreFileCount ?? 0,
                    tally?.RestoreBytes ?? 0,
                    tally?.ExecutedStopActionCount ?? 0,
                    tally?.TopologyNoOpStopActionCount ?? 0,
                    tally?.RestoredLiveRootCount ?? 0,
                    completed is not null,
                    tally?.ExecutedStartActionCount ?? 0,
                    tally?.TopologyNoOpStartActionCount ?? 0,
                    tally?.VerifiedHealthTargetCount ?? 0,
                    tally?.DisplacedTreeCleanupCount ?? 0,
                    completed is not null,
                    completed is not null,
                    completed is not null,
                    completed is not null || (tally?.RestoredLiveRootCount ?? 0) > 0,
                    completed is not null,
                    completed is not null,
                    completed is not null || (tally?.RestoredLiveRootCount ?? 0) > 0,
                    reconciliation,
                    ActivationAuthorized: false);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    internal Task<VerifiedReleaseActivationRollbackExecutionReport>
        ExecuteAfterPostSwitchServiceFailureAsync(
            VerifiedReleaseActivationRollbackPlanReport planReport,
            VerifiedReleaseActivationCurrentPointerSwitchReport pointerReport,
            VerifiedReleaseActivationServiceControlExecutionReport failureReport,
            CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            planReport,
            pointerReport,
            VerifiedReleaseActivationRollbackTriggerKind.PostSwitchServiceControlFailure,
            failureReport,
            healthFailureReport: null,
            cancellationToken);

    [SupportedOSPlatform("linux")]
    internal Task<VerifiedReleaseActivationRollbackExecutionReport>
        ExecuteAfterHealthFailureAsync(
            VerifiedReleaseActivationRollbackPlanReport planReport,
            VerifiedReleaseActivationCurrentPointerSwitchReport pointerReport,
            VerifiedReleaseActivationHealthVerificationReport failureReport,
            CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            planReport,
            pointerReport,
            VerifiedReleaseActivationRollbackTriggerKind.PostSwitchHealthFailure,
            serviceFailureReport: null,
            failureReport,
            cancellationToken);

    internal VerifiedReleaseActivationRollbackObservation Observe(
        VerifiedReleaseActivationPlan activationPlan)
    {
        ArgumentNullException.ThrowIfNull(activationPlan);
        lock (m_stateGate)
        {
            bool exactCompleted = m_completed is not null &&
                ReferenceEquals(m_completed.Plan.ActivationPlan, activationPlan);
            bool exactReconciliation = m_reconciliationPlan is not null &&
                ReferenceEquals(m_reconciliationPlan.ActivationPlan, activationPlan);
            return new VerifiedReleaseActivationRollbackObservation(
                exactCompleted,
                exactCompleted,
                exactCompleted ? m_completed!.CompletedAt : null,
                exactReconciliation);
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task<VerifiedReleaseActivationRollbackExecutionReport> ExecuteAsync(
        VerifiedReleaseActivationRollbackPlanReport planReport,
        VerifiedReleaseActivationCurrentPointerSwitchReport pointerReport,
        VerifiedReleaseActivationRollbackTriggerKind triggerKind,
        VerifiedReleaseActivationServiceControlExecutionReport? serviceFailureReport,
        VerifiedReleaseActivationHealthVerificationReport? healthFailureReport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planReport);
        ArgumentNullException.ThrowIfNull(pointerReport);
        cancellationToken.ThrowIfCancellationRequested();
        RollbackExecutionTally tally = new();
        HashSet<string> createdRestoreStagingPaths = new(StringComparer.Ordinal);

        if (!m_settings.ExecutionEnabled)
        {
            return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                VerifiedReleaseActivationRollbackExecutionFailureCode.ExecutionDisabled,
                "Release rollback execution is disabled.",
                m_settings,
                triggerKind,
                planReport);
        }
        if (!OperatingSystem.IsLinux())
        {
            return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                VerifiedReleaseActivationRollbackExecutionFailureCode.UnsupportedPlatform,
                "Release rollback execution requires Linux.",
                m_settings,
                triggerKind,
                planReport);
        }

        VerifiedReleaseActivationRollbackPlan? plan =
            VerifiedReleaseActivationRollbackPlanComposer.ValidateReport(planReport);
        if (plan is null)
        {
            return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                planReport.Plan is null
                    ? VerifiedReleaseActivationRollbackExecutionFailureCode.RollbackPlanUnavailable
                    : VerifiedReleaseActivationRollbackExecutionFailureCode.RollbackPlanNotEligible,
                "A successful exact non-executing rollback plan is required.",
                m_settings,
                triggerKind,
                planReport);
        }
        if (!ValidateRollbackPlanShape(planReport, plan))
        {
            return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                VerifiedReleaseActivationRollbackExecutionFailureCode.RollbackPlanMismatch,
                "The rollback plan no longer matches its exact activation artifacts.",
                m_settings,
                triggerKind,
                planReport);
        }
        if (!VerifiedReleaseActivationCurrentPointerSwitchService.ValidateEvidenceReport(
                pointerReport,
                plan.ServiceControlPlan))
        {
            return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                VerifiedReleaseActivationRollbackExecutionFailureCode.CurrentPointerSwitchUnavailable,
                "The exact successful forward current-pointer switch token is required.",
                m_settings,
                triggerKind,
                planReport,
                exactPlanBound: true);
        }
        VerifiedReleaseActivationCurrentPointerSwitchEvidence pointerEvidence =
            pointerReport.Evidence!;
        if (!ValidateFailureTrigger(
                plan,
                triggerKind,
                serviceFailureReport,
                healthFailureReport))
        {
            return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                VerifiedReleaseActivationRollbackExecutionFailureCode.FailureTriggerInvalid,
                "An exact eligible failed post-switch service or health transaction is required.",
                m_settings,
                triggerKind,
                planReport,
                exactPlanBound: true,
                exactPointerBound: true);
        }

        await m_executionGate.WaitAsync(cancellationToken);
        try
        {
            lock (m_stateGate)
            {
                if (m_reconciliationPlan is not null)
                {
                    return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                        VerifiedReleaseActivationRollbackExecutionFailureCode.ReconciliationRequired,
                        "A previous rollback attempt requires local reconciliation before another attempt.",
                        m_settings,
                        triggerKind,
                        planReport,
                        m_reconciliationTally,
                        exactPlanBound: true,
                        exactPointerBound: true,
                        exactTriggerBound: true,
                        reconciliationRequired: true);
                }
                if (m_completed is not null)
                {
                    return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                        VerifiedReleaseActivationRollbackExecutionFailureCode.RollbackAlreadyCompleted,
                        "Rollback is already retained for this service lifetime.",
                        m_settings,
                        triggerKind,
                        planReport,
                        exactPlanBound: true,
                        exactPointerBound: true,
                        exactTriggerBound: true);
                }
            }

            DateTimeOffset startedAt = m_timeProvider.GetUtcNow();
            ReleaseStatusReadResult beforeStatus;
            InstallationSetupState beforeSetup;
            try
            {
                beforeStatus = await m_statusReader(cancellationToken);
                beforeSetup = await m_setupReader(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsObservationException(exception))
            {
                return FailureBeforeMutation(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.StatusUnavailable,
                    "Release or setup status could not be read before rollback.",
                    planReport,
                    triggerKind,
                    tally,
                    exactPointerBound: true,
                    exactTriggerBound: true);
            }

            if (!MatchesExpectedStatus(beforeStatus, plan.ActivationPlan, targetActive: true))
            {
                return FailureBeforeMutation(
                    beforeStatus.Succeeded
                        ? VerifiedReleaseActivationRollbackExecutionFailureCode.StatusMismatch
                        : VerifiedReleaseActivationRollbackExecutionFailureCode.StatusUnavailable,
                    "The exact target release is not the stable active release before rollback.",
                    planReport,
                    triggerKind,
                    tally,
                    exactPointerBound: true,
                    exactTriggerBound: true);
            }
            if (!TryBindSetup(
                    beforeSetup,
                    plan.ActivationPlan,
                    out string canonicalGatewayAuthority,
                    out InstallationTopologyProfile topology))
            {
                return FailureBeforeMutation(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.SetupMismatch,
                    "Completed setup no longer matches the exact rollback plan.",
                    planReport,
                    triggerKind,
                    tally,
                    exactPointerBound: true,
                    exactTriggerBound: true,
                    targetActive: true);
            }
            if (!TryResolveSupportedTopology(topology, out bool remoteAgentRequired))
            {
                return FailureBeforeMutation(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.UnsupportedTopology,
                    "The completed setup topology requires a remote rollback transport that is not registered.",
                    planReport,
                    triggerKind,
                    tally,
                    exactPointerBound: true,
                    exactTriggerBound: true,
                    targetActive: true,
                    topologyBound: true);
            }
            bool stationIdentityConfigured = !string.IsNullOrEmpty(m_settings.ExpectedStationId);
            if (stationIdentityConfigured != remoteAgentRequired)
            {
                return FailureBeforeMutation(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.StationIdentityMismatch,
                    remoteAgentRequired
                        ? "The completed setup topology requires one exact remote station identity."
                        : "The completed setup topology must not configure a remote station identity.",
                    planReport,
                    triggerKind,
                    tally,
                    exactPointerBound: true,
                    exactTriggerBound: true,
                    targetActive: true,
                    topologyBound: true);
            }

            VerifiedReleaseActivationConfigurationBackupManifest manifest;
            try
            {
                manifest = await VerifiedReleaseActivationConfigurationBackupService
                    .RevalidatePublishedBackupAsync(plan.ConfigurationBackup, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or
                    UnauthorizedAccessException or SecurityException or
                    InvalidOperationException or ArgumentException or
                    NotSupportedException or OverflowException)
            {
                return FailureBeforeMutation(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.ImmutableBackupInvalid,
                    "The immutable original activation backup could not be revalidated.",
                    planReport,
                    triggerKind,
                    tally,
                    exactPointerBound: true,
                    exactTriggerBound: true,
                    targetActive: true,
                    topologyBound: true);
            }

            try
            {
                PrepareRestoreLayout(plan);
                await StageRestoreTreesAsync(
                    plan,
                    manifest,
                    tally,
                    createdRestoreStagingPaths,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!TryCleanupRestoreStaging(plan, createdRestoreStagingPaths))
                {
                    MarkReconciliation(plan, pointerEvidence, triggerKind, tally);
                }
                throw;
            }
            catch (Exception exception) when (IsFileMutationException(exception))
            {
                bool cleaned = TryCleanupRestoreStaging(
                    plan,
                    createdRestoreStagingPaths);
                if (!cleaned)
                {
                    MarkReconciliation(plan, pointerEvidence, triggerKind, tally);
                }
                return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                    cleaned
                        ? VerifiedReleaseActivationRollbackExecutionFailureCode.RestoreStagingFailed
                        : VerifiedReleaseActivationRollbackExecutionFailureCode.ReconciliationRequired,
                    cleaned
                        ? "The immutable original backup could not be staged for rollback."
                        : "Failed rollback staging could not be removed and requires reconciliation.",
                    m_settings,
                    triggerKind,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    exactPointerBound: true,
                    exactTriggerBound: true,
                    targetActive: true,
                    topologyBound: true,
                    backupRevalidated: true,
                    reconciliationRequired: !cleaned);
            }

            ReleaseStatusReadResult stagedStatus;
            InstallationSetupState stagedSetup;
            try
            {
                stagedStatus = await m_statusReader(cancellationToken);
                stagedSetup = await m_setupReader(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!TryCleanupRestoreStaging(plan, createdRestoreStagingPaths))
                {
                    MarkReconciliation(plan, pointerEvidence, triggerKind, tally);
                }
                throw;
            }
            catch (Exception exception) when (IsObservationException(exception))
            {
                return FailureAfterStagingCleanup(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.StatusUnavailable,
                    "Release or setup status could not be re-read after rollback staging.",
                    planReport,
                    plan,
                    pointerEvidence,
                    triggerKind,
                    tally,
                    createdRestoreStagingPaths);
            }
            if (!MatchesExpectedStatus(stagedStatus, plan.ActivationPlan, targetActive: true) ||
                !EquivalentStatus(beforeStatus, stagedStatus) ||
                !EquivalentSetup(beforeSetup, stagedSetup))
            {
                return FailureAfterStagingCleanup(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.ObservationDrift,
                    "Release or setup state changed while rollback restore trees were staged.",
                    planReport,
                    plan,
                    pointerEvidence,
                    triggerKind,
                    tally,
                    createdRestoreStagingPaths);
            }

            if (!TryClassifyActions(plan.ServiceControlPlan.StopActions, topology, out RollbackBoundAction[] stopActions) ||
                !TryClassifyActions(plan.ServiceControlPlan.StartActions, topology, out RollbackBoundAction[] startActions))
            {
                return FailureAfterStagingCleanup(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.RemoteServiceControlUnavailable,
                    "The rollback plan requires a remote service-control action that is not registered.",
                    planReport,
                    plan,
                    pointerEvidence,
                    triggerKind,
                    tally,
                    createdRestoreStagingPaths);
            }

            tally.PlannedStopActionCount = stopActions.Length;
            tally.PlannedStartActionCount = startActions.Length;
            try
            {
                if (!await ExecuteServiceActionsAsync(stopActions, stopPhase: true, tally, cancellationToken))
                {
                    bool mutationStarted = tally.ServiceProcessInvocationCount > 0;
                    if (mutationStarted)
                    {
                        MarkReconciliation(plan, pointerEvidence, triggerKind, tally);
                    }
                    bool stagingCleaned = mutationStarted ||
                        TryCleanupRestoreStaging(plan, createdRestoreStagingPaths);
                    if (!stagingCleaned)
                    {
                        MarkReconciliation(plan, pointerEvidence, triggerKind, tally);
                    }
                    return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                        mutationStarted || !stagingCleaned
                            ? VerifiedReleaseActivationRollbackExecutionFailureCode.ReconciliationRequired
                            : VerifiedReleaseActivationRollbackExecutionFailureCode.TargetServiceStopFailed,
                        mutationStarted
                            ? "A target-service stop had an unknown or failed outcome and requires reconciliation."
                            : stagingCleaned
                                ? "A target-service stop could not begin."
                                : "Rollback staging could not be removed after the target-service stop failed to begin and requires reconciliation.",
                        m_settings,
                        triggerKind,
                        planReport,
                        tally,
                        exactPlanBound: true,
                        exactPointerBound: true,
                        exactTriggerBound: true,
                        targetActive: true,
                        topologyBound: true,
                        backupRevalidated: true,
                        reconciliationRequired: mutationStarted || !stagingCleaned);
                }
            }
            catch (OperationCanceledException)
            {
                if (tally.ServiceProcessInvocationCount > 0)
                {
                    MarkReconciliation(plan, pointerEvidence, triggerKind, tally);
                }
                else if (!TryCleanupRestoreStaging(
                             plan,
                             createdRestoreStagingPaths))
                {
                    MarkReconciliation(plan, pointerEvidence, triggerKind, tally);
                }
                throw;
            }

            bool configurationRestored = false;
            bool pointerChanged = false;
            bool servicesRestored = false;
            bool healthVerified = false;
            bool rollbackPerformed = false;
            try
            {
                RestoreLiveRoots(plan, tally);
                configurationRestored = true;
                RollbackCurrentPointer(plan);
                pointerChanged = true;
                rollbackPerformed = true;

                ReleaseStatusReadResult switchedStatus = await m_statusReader(cancellationToken);
                InstallationSetupState switchedSetup = await m_setupReader(cancellationToken);
                if (!MatchesExpectedStatus(switchedStatus, plan.ActivationPlan, targetActive: false) ||
                    !EquivalentStatusExceptActive(stagedStatus, switchedStatus) ||
                    !EquivalentSetup(beforeSetup, switchedSetup))
                {
                    throw new RollbackMutationException(
                        VerifiedReleaseActivationRollbackExecutionFailureCode.ObservationDrift,
                        "Release or setup state did not reflect the exact installed release after pointer rollback.");
                }

                if (!await ExecuteServiceActionsAsync(startActions, stopPhase: false, tally, cancellationToken))
                {
                    throw new RollbackMutationException(
                        VerifiedReleaseActivationRollbackExecutionFailureCode.InstalledServiceStartFailed,
                        "An installed-service start had an unknown or failed outcome.");
                }
                servicesRestored = true;

                await VerifyInstalledHealthAsync(
                    plan,
                    canonicalGatewayAuthority,
                    remoteAgentRequired,
                    startedAt,
                    tally,
                    cancellationToken);
                healthVerified = true;

                ReleaseStatusReadResult afterStatus = await m_statusReader(cancellationToken);
                InstallationSetupState afterSetup = await m_setupReader(cancellationToken);
                if (!MatchesExpectedStatus(afterStatus, plan.ActivationPlan, targetActive: false) ||
                    !EquivalentStatus(switchedStatus, afterStatus) ||
                    !EquivalentSetup(beforeSetup, afterSetup) ||
                    !TryBindSetup(afterSetup, plan.ActivationPlan, out string afterAuthority, out InstallationTopologyProfile afterTopology) ||
                    !Equals(topology, afterTopology) ||
                    !string.Equals(canonicalGatewayAuthority, afterAuthority, StringComparison.Ordinal))
                {
                    throw new RollbackMutationException(
                        VerifiedReleaseActivationRollbackExecutionFailureCode.ObservationDrift,
                        "Release, setup, or topology state changed during installed-release verification.");
                }

                foreach (VerifiedReleaseActivationRollbackRestoreSource source in plan.RestoreSources)
                {
                    if (!m_treeDelete(source.DisplacedLivePath))
                    {
                        throw new RollbackMutationException(
                            VerifiedReleaseActivationRollbackExecutionFailureCode.DisplacedTreeCleanupFailed,
                            "A displaced failed live tree could not be securely removed.");
                    }
                    tally.DisplacedTreeCleanupCount++;
                }

                DateTimeOffset completedAt = m_timeProvider.GetUtcNow();
                if (completedAt < startedAt)
                {
                    throw new RollbackMutationException(
                        VerifiedReleaseActivationRollbackExecutionFailureCode.ObservationDrift,
                        "The rollback observation clock moved backwards.");
                }
                VerifiedReleaseActivationRollbackEvidence evidence = new(
                    plan,
                    pointerEvidence,
                    triggerKind,
                    topology.Kind,
                    startedAt,
                    completedAt,
                    tally);
                lock (m_stateGate)
                {
                    m_completed = evidence;
                }
                return VerifiedReleaseActivationRollbackExecutionReport.Success(
                    m_settings,
                    planReport,
                    triggerKind,
                    tally,
                    evidence);
            }
            catch (OperationCanceledException)
            {
                MarkReconciliation(plan, pointerEvidence, triggerKind, tally);
                throw;
            }
            catch (Exception exception) when (
                exception is RollbackMutationException or IOException or
                    UnauthorizedAccessException or SecurityException or
                    InvalidOperationException or ArgumentException or
                    NotSupportedException or OverflowException)
            {
                MarkReconciliation(plan, pointerEvidence, triggerKind, tally);
                VerifiedReleaseActivationRollbackExecutionFailureCode code =
                    exception is RollbackMutationException rollbackException
                        ? rollbackException.FailureCode
                        : pointerChanged
                            ? VerifiedReleaseActivationRollbackExecutionFailureCode.ReconciliationRequired
                            : configurationRestored
                                ? VerifiedReleaseActivationRollbackExecutionFailureCode.CurrentPointerRollbackFailed
                                : VerifiedReleaseActivationRollbackExecutionFailureCode.LiveRootRestoreFailed;
                return VerifiedReleaseActivationRollbackExecutionReport.Failure(
                    code,
                    exception is RollbackMutationException known
                        ? known.Message
                        : "Rollback mutation had an unknown or failed outcome and requires reconciliation.",
                    m_settings,
                    triggerKind,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    exactPointerBound: true,
                    exactTriggerBound: true,
                    targetActive: true,
                    installedActive: healthVerified,
                    setupStable: healthVerified,
                    topologyBound: true,
                    backupRevalidated: true,
                    pointerChanged: pointerChanged,
                    configurationRestored: configurationRestored,
                    servicesRestored: servicesRestored,
                    healthVerified: healthVerified,
                    rollbackPerformed: rollbackPerformed,
                    reconciliationRequired: true);
            }
        }
        finally
        {
            m_executionGate.Release();
        }
    }

    private VerifiedReleaseActivationRollbackExecutionReport FailureBeforeMutation(
        VerifiedReleaseActivationRollbackExecutionFailureCode failureCode,
        string message,
        VerifiedReleaseActivationRollbackPlanReport planReport,
        VerifiedReleaseActivationRollbackTriggerKind triggerKind,
        RollbackExecutionTally tally,
        bool exactPointerBound,
        bool exactTriggerBound,
        bool targetActive = false,
        bool topologyBound = false,
        bool backupRevalidated = false) =>
        VerifiedReleaseActivationRollbackExecutionReport.Failure(
            failureCode,
            message,
            m_settings,
            triggerKind,
            planReport,
            tally,
            exactPlanBound: true,
            exactPointerBound,
            exactTriggerBound,
            targetActive,
            topologyBound: topologyBound,
            backupRevalidated: backupRevalidated);

    [SupportedOSPlatform("linux")]
    private VerifiedReleaseActivationRollbackExecutionReport
        FailureAfterStagingCleanup(
            VerifiedReleaseActivationRollbackExecutionFailureCode failureCode,
            string message,
            VerifiedReleaseActivationRollbackPlanReport planReport,
            VerifiedReleaseActivationRollbackPlan plan,
            VerifiedReleaseActivationCurrentPointerSwitchEvidence pointerEvidence,
            VerifiedReleaseActivationRollbackTriggerKind triggerKind,
            RollbackExecutionTally tally,
            IReadOnlySet<string> createdRestoreStagingPaths)
    {
        bool cleaned = TryCleanupRestoreStaging(
            plan,
            createdRestoreStagingPaths);
        if (!cleaned)
        {
            MarkReconciliation(plan, pointerEvidence, triggerKind, tally);
        }
        return VerifiedReleaseActivationRollbackExecutionReport.Failure(
            cleaned
                ? failureCode
                : VerifiedReleaseActivationRollbackExecutionFailureCode
                    .ReconciliationRequired,
            cleaned
                ? message
                : "Rollback staging could not be removed and requires reconciliation.",
            m_settings,
            triggerKind,
            planReport,
            tally,
            exactPlanBound: true,
            exactPointerBound: true,
            exactTriggerBound: true,
            targetActive: true,
            topologyBound: true,
            backupRevalidated: true,
            reconciliationRequired: !cleaned);
    }

    private static bool ValidateRollbackPlanShape(
        VerifiedReleaseActivationRollbackPlanReport report,
        VerifiedReleaseActivationRollbackPlan plan) =>
        report.RestoreSourceCount == plan.RestoreSources.Count &&
        report.StopActionCount == plan.ServiceControlPlan.StopActions.Count &&
        report.StartActionCount == plan.ServiceControlPlan.StartActions.Count &&
        report.HealthTargetCount == plan.HealthPlan.Targets.Count &&
        ReferenceEquals(plan.ConfigurationBackup.Plan.ActivationPlan, plan.ActivationPlan) &&
        ReferenceEquals(plan.MigrationPlan.ActivationPlan, plan.ActivationPlan) &&
        ReferenceEquals(plan.MigrationPlan.ConfigurationBackup, plan.ConfigurationBackup) &&
        ReferenceEquals(plan.ServiceControlPlan.ActivationPlan, plan.ActivationPlan) &&
        ReferenceEquals(plan.HealthPlan.ServiceControlPlan, plan.ServiceControlPlan) &&
        !plan.ActivationPlan.RestartHost &&
        !plan.ServiceControlPlan.HostRestartRequired &&
        plan.ServiceControlPlan.HostRestartActions.Count == 0 &&
        plan.RestoreSources.Count == 3 &&
        !plan.ReverseMigrationRunnerRequired;

    private static bool ValidateFailureTrigger(
        VerifiedReleaseActivationRollbackPlan plan,
        VerifiedReleaseActivationRollbackTriggerKind triggerKind,
        VerifiedReleaseActivationServiceControlExecutionReport? serviceFailureReport,
        VerifiedReleaseActivationHealthVerificationReport? healthFailureReport) =>
        triggerKind switch
        {
            VerifiedReleaseActivationRollbackTriggerKind.PostSwitchServiceControlFailure =>
                healthFailureReport is null && serviceFailureReport is not null &&
                ValidateServiceFailure(plan, serviceFailureReport),
            VerifiedReleaseActivationRollbackTriggerKind.PostSwitchHealthFailure =>
                serviceFailureReport is null && healthFailureReport is not null &&
                ValidateHealthFailure(plan, healthFailureReport),
            _ => false
        };

    private static bool ValidateServiceFailure(
        VerifiedReleaseActivationRollbackPlan plan,
        VerifiedReleaseActivationServiceControlExecutionReport report)
    {
        VerifiedReleaseActivationServiceControlExecutionFailureCode[] eligible =
        [
            VerifiedReleaseActivationServiceControlExecutionFailureCode.ReleaseStatusUnavailable,
            VerifiedReleaseActivationServiceControlExecutionFailureCode.ReleaseStatusMismatch,
            VerifiedReleaseActivationServiceControlExecutionFailureCode.SetupUnavailable,
            VerifiedReleaseActivationServiceControlExecutionFailureCode.SetupMismatch,
            VerifiedReleaseActivationServiceControlExecutionFailureCode.RemoteServiceControlUnavailable,
            VerifiedReleaseActivationServiceControlExecutionFailureCode.UnitControlFailed,
            VerifiedReleaseActivationServiceControlExecutionFailureCode.ObservationDrift,
            VerifiedReleaseActivationServiceControlExecutionFailureCode.ReconciliationRequired
        ];
        return !report.Succeeded &&
            eligible.Contains(report.FailureCode) &&
            report.Phase == VerifiedReleaseActivationServiceControlExecutionPhase.PostSwitchStart &&
            report.SetupRevision == plan.ActivationPlan.SetupRevision &&
            string.Equals(report.InstalledReleaseIdentity, plan.ActivationPlan.InstalledReleaseIdentity, StringComparison.Ordinal) &&
            string.Equals(report.TargetReleaseIdentity, plan.ActivationPlan.TargetReleaseIdentity, StringComparison.Ordinal) &&
            report.ExactServiceControlPlanBound &&
            report.ExactActivationPlanBound &&
            ReferenceEquals(report.FailedPlan, plan.ServiceControlPlan) &&
            report.PlannedActionCount == plan.ServiceControlPlan.StartActions.Count &&
            plan.ServiceControlPlan.ServiceControlRequired &&
            plan.ServiceControlPlan.StartActions.Count > 0 &&
            report.PreSwitchStopComplete &&
            !report.PostSwitchStartComplete &&
            !report.ServiceControlReady &&
            !report.HostRestartPerformed &&
            !report.ActivationAuthorized;
    }

    private static bool ValidateHealthFailure(
        VerifiedReleaseActivationRollbackPlan plan,
        VerifiedReleaseActivationHealthVerificationReport report)
    {
        VerifiedReleaseActivationHealthVerificationFailureCode[] eligible =
        [
            VerifiedReleaseActivationHealthVerificationFailureCode.StatusUnavailable,
            VerifiedReleaseActivationHealthVerificationFailureCode.StatusMismatch,
            VerifiedReleaseActivationHealthVerificationFailureCode.SetupUnavailable,
            VerifiedReleaseActivationHealthVerificationFailureCode.SetupMismatch,
            VerifiedReleaseActivationHealthVerificationFailureCode.UnitActivityUnavailable,
            VerifiedReleaseActivationHealthVerificationFailureCode.LoopbackHealthUnavailable,
            VerifiedReleaseActivationHealthVerificationFailureCode.BrokerLinkUnavailable,
            VerifiedReleaseActivationHealthVerificationFailureCode.ObservationDrift,
            VerifiedReleaseActivationHealthVerificationFailureCode.UnsupportedTopology,
            VerifiedReleaseActivationHealthVerificationFailureCode.StationIdentityMismatch
        ];
        return !report.Succeeded &&
            eligible.Contains(report.FailureCode) &&
            report.SetupRevision == plan.ActivationPlan.SetupRevision &&
            string.Equals(report.InstalledReleaseIdentity, plan.ActivationPlan.InstalledReleaseIdentity, StringComparison.Ordinal) &&
            string.Equals(report.TargetReleaseIdentity, plan.ActivationPlan.TargetReleaseIdentity, StringComparison.Ordinal) &&
            report.HealthTargetCount == plan.HealthPlan.Targets.Count &&
            report.ExactHealthPlanBound &&
            report.ExactActivationPlanBound &&
            ReferenceEquals(report.FailedPlan, plan.HealthPlan) &&
            !report.HealthEvidenceProduced &&
            !report.ServiceHealthReady &&
            !report.ActivationAuthorized;
    }

    private static void PrepareRestoreLayout(VerifiedReleaseActivationRollbackPlan plan)
    {
        foreach (VerifiedReleaseActivationRollbackRestoreSource source in plan.RestoreSources)
        {
            EnsureRealDirectory(source.ImmutableBackupPath);
            EnsureRealDirectory(source.LiveDestinationPath);
            ValidateLiveTreeForDisplacement(source.LiveDestinationPath);
            EnsureAbsent(source.RestoreStagingPath);
            EnsureAbsent(source.DisplacedLivePath);
            string parent = CanonicalDirectory(Path.GetDirectoryName(source.LiveDestinationPath) ?? string.Empty);
            if (!PathEquals(Path.GetDirectoryName(source.RestoreStagingPath), parent) ||
                !PathEquals(Path.GetDirectoryName(source.DisplacedLivePath), parent))
            {
                throw new InvalidOperationException(
                    "A rollback restore path escaped the live root parent.");
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task StageRestoreTreesAsync(
        VerifiedReleaseActivationRollbackPlan plan,
        VerifiedReleaseActivationConfigurationBackupManifest manifest,
        RollbackExecutionTally tally,
        ISet<string> createdRestoreStagingPaths,
        CancellationToken cancellationToken)
    {
        tally.RestoreSourceCount = plan.RestoreSources.Count;
        foreach (VerifiedReleaseActivationRollbackRestoreSource source in plan.RestoreSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifiedReleaseActivationConfigurationBackupManifestEntry[] entries =
                manifest.Entries
                    .Where(entry => entry.Source == source.Kind)
                    .OrderBy(entry => entry.Kind)
                    .ThenBy(entry => RelativeDepth(entry.Path))
                    .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                    .ToArray();
            if (entries.Length == 0 || entries.Count(entry => entry.Path == ".") != 1)
            {
                throw new InvalidDataException(
                    "The immutable original backup is missing a restore source.");
            }

            EnsureAbsent(source.RestoreStagingPath);
            Directory.CreateDirectory(source.RestoreStagingPath);
            createdRestoreStagingPaths.Add(source.RestoreStagingPath);
            File.SetUnixFileMode(source.RestoreStagingPath, PrivateWritableDirectoryMode);
            foreach (VerifiedReleaseActivationConfigurationBackupManifestEntry entry in entries.Where(
                         entry => entry.Kind == VerifiedReleaseActivationConfigurationBackupManifestEntryKind.Directory &&
                             entry.Path != "."))
            {
                string destination = SafeDescendant(source.RestoreStagingPath, entry.Path);
                Directory.CreateDirectory(destination);
                File.SetUnixFileMode(destination, PrivateWritableDirectoryMode);
            }

            foreach (VerifiedReleaseActivationConfigurationBackupManifestEntry entry in entries.Where(
                         entry => entry.Kind == VerifiedReleaseActivationConfigurationBackupManifestEntryKind.File))
            {
                string inputPath = SafeDescendant(source.ImmutableBackupPath, entry.Path);
                string outputPath = SafeDescendant(source.RestoreStagingPath, entry.Path);
                await CopyAndValidateFileAsync(inputPath, outputPath, entry, cancellationToken);
                tally.RestoreFileCount++;
                tally.RestoreBytes = checked(tally.RestoreBytes + entry.Length!.Value);
            }

            foreach (VerifiedReleaseActivationConfigurationBackupManifestEntry entry in entries.Where(
                         entry => entry.Kind == VerifiedReleaseActivationConfigurationBackupManifestEntryKind.Directory)
                     .OrderByDescending(entry => RelativeDepth(entry.Path)))
            {
                string destination = entry.Path == "."
                    ? source.RestoreStagingPath
                    : SafeDescendant(source.RestoreStagingPath, entry.Path);
                File.SetUnixFileMode(destination, (UnixFileMode)entry.UnixMode);
                tally.RestoreDirectoryCount++;
            }
            await ValidateRestoreTreeAsync(source, entries, cancellationToken);
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task CopyAndValidateFileAsync(
        string inputPath,
        string outputPath,
        VerifiedReleaseActivationConfigurationBackupManifestEntry entry,
        CancellationToken cancellationToken)
    {
        FileInfo input = new(inputPath);
        input.Refresh();
        if (!input.Exists || input.LinkTarget is not null ||
            (input.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device | FileAttributes.Offline)) != 0 ||
            input.Length != entry.Length || PathEntryExists(outputPath))
        {
            throw new InvalidDataException(
                "An immutable backup file is unsafe or changed.");
        }

        FileStreamOptions outputOptions = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            UnixCreateMode = PrivateWritableFileMode
        };
        await using FileStream source = new(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(outputPath, outputOptions);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long copied = 0;
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                copied = checked(copied + read);
                if (copied > entry.Length)
                {
                    throw new InvalidDataException(
                        "An immutable backup file exceeded its retained length.");
                }
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (copied != entry.Length)
            {
                throw new InvalidDataException(
                    "An immutable backup file length changed while being copied.");
            }
            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        byte[] digest = hash.GetHashAndReset();
        if (!string.Equals(Convert.ToHexString(digest).ToLowerInvariant(), entry.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "An immutable backup file digest changed while being restored.");
        }
        File.SetUnixFileMode(outputPath, (UnixFileMode)entry.UnixMode);
    }

    [SupportedOSPlatform("linux")]
    private static async Task ValidateRestoreTreeAsync(
        VerifiedReleaseActivationRollbackRestoreSource source,
        IReadOnlyList<VerifiedReleaseActivationConfigurationBackupManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        Dictionary<string, VerifiedReleaseActivationConfigurationBackupManifestEntry> expected =
            entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        HashSet<string> observed = new(StringComparer.Ordinal);
        Stack<(DirectoryInfo Directory, string Relative)> pending = new();
        pending.Push((new DirectoryInfo(source.RestoreStagingPath), "."));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (DirectoryInfo directory, string relative) = pending.Pop();
            directory.Refresh();
            if (!directory.Exists || directory.LinkTarget is not null ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !expected.TryGetValue(relative, out var directoryEntry) ||
                directoryEntry.Kind != VerifiedReleaseActivationConfigurationBackupManifestEntryKind.Directory ||
                File.GetUnixFileMode(directory.FullName) != (UnixFileMode)directoryEntry.UnixMode ||
                !observed.Add(relative))
            {
                throw new InvalidDataException(
                    "A staged rollback directory does not match its immutable manifest.");
            }
            foreach (FileSystemInfo child in directory.GetFileSystemInfos())
            {
                child.Refresh();
                if (child.LinkTarget is not null ||
                    (child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "A staged rollback tree contains a linked entry.");
                }
                string childRelative = relative == "." ? child.Name : $"{relative}/{child.Name}";
                if (child is DirectoryInfo childDirectory)
                {
                    pending.Push((childDirectory, childRelative));
                    continue;
                }
                if (child is not FileInfo file ||
                    !expected.TryGetValue(childRelative, out var fileEntry) ||
                    fileEntry.Kind != VerifiedReleaseActivationConfigurationBackupManifestEntryKind.File ||
                    file.Length != fileEntry.Length ||
                    File.GetUnixFileMode(file.FullName) != (UnixFileMode)fileEntry.UnixMode ||
                    !observed.Add(childRelative))
                {
                    throw new InvalidDataException(
                        "A staged rollback file does not match its immutable manifest.");
                }
                await using FileStream stream = new(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
                if (!string.Equals(Convert.ToHexString(digest).ToLowerInvariant(), fileEntry.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A staged rollback file digest does not match its immutable manifest.");
                }
            }
        }
        if (observed.Count != expected.Count)
        {
            throw new InvalidDataException(
                "A staged rollback tree is incomplete.");
        }
    }

    private async Task<bool> ExecuteServiceActionsAsync(
        IReadOnlyList<RollbackBoundAction> actions,
        bool stopPhase,
        RollbackExecutionTally tally,
        CancellationToken cancellationToken)
    {
        foreach (RollbackBoundAction bound in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bound.TopologyNoOp)
            {
                if (stopPhase)
                {
                    tally.TopologyNoOpStopActionCount++;
                }
                else
                {
                    tally.TopologyNoOpStartActionCount++;
                }
                continue;
            }

            tally.ServiceProcessInvocationCount++;
            ServiceControlAttemptResult result = await m_serviceRuntime.ControlUnitAsync(
                bound.Action,
                UnitControlTimeout,
                cancellationToken);
            if (!result.Succeeded || !result.ProcessStarted || !result.OutcomeKnown)
            {
                return false;
            }
            if (stopPhase)
            {
                tally.ExecutedStopActionCount++;
            }
            else
            {
                tally.ExecutedStartActionCount++;
            }
        }
        return true;
    }

    private void RestoreLiveRoots(
        VerifiedReleaseActivationRollbackPlan plan,
        RollbackExecutionTally tally)
    {
        foreach (VerifiedReleaseActivationRollbackRestoreSource source in plan.RestoreSources)
        {
            EnsureRealDirectory(source.LiveDestinationPath);
            EnsureRealDirectory(source.RestoreStagingPath);
            EnsureAbsent(source.DisplacedLivePath);
            m_directoryMove(source.LiveDestinationPath, source.DisplacedLivePath);
            m_directoryMove(source.RestoreStagingPath, source.LiveDestinationPath);
            if (!Directory.Exists(source.LiveDestinationPath) ||
                Directory.Exists(source.RestoreStagingPath) ||
                !Directory.Exists(source.DisplacedLivePath))
            {
                throw new RollbackMutationException(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.LiveRootRestoreFailed,
                    "A live configuration root had an ambiguous atomic replacement outcome.");
            }
            tally.RestoredLiveRootCount++;
        }
    }

    private void RollbackCurrentPointer(VerifiedReleaseActivationRollbackPlan plan)
    {
        CurrentPointerRuntimeSnapshot before = m_pointerRuntime.Read(plan.ActivationPlan.CurrentPointerPath);
        if (!before.EntryPresent || !before.IsSymbolicLink ||
            !string.Equals(before.LinkTarget, plan.ExpectedCurrentLinkTarget, StringComparison.Ordinal))
        {
            throw new RollbackMutationException(
                VerifiedReleaseActivationRollbackExecutionFailureCode.CurrentPointerRollbackFailed,
                "The current pointer no longer matches the exact target release.");
        }
        if (m_pointerRuntime.Read(plan.TemporaryCurrentPointerPath).EntryPresent)
        {
            throw new RollbackMutationException(
                VerifiedReleaseActivationRollbackExecutionFailureCode.CurrentPointerRollbackFailed,
                "The planned rollback pointer staging identity already exists.");
        }

        bool created = false;
        try
        {
            m_pointerRuntime.CreateSymbolicLink(plan.TemporaryCurrentPointerPath, plan.RollbackCurrentLinkTarget);
            created = true;
            CurrentPointerRuntimeSnapshot staged = m_pointerRuntime.Read(plan.TemporaryCurrentPointerPath);
            if (!staged.EntryPresent || !staged.IsSymbolicLink ||
                !string.Equals(staged.LinkTarget, plan.RollbackCurrentLinkTarget, StringComparison.Ordinal))
            {
                throw new RollbackMutationException(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.CurrentPointerRollbackFailed,
                    "The rollback pointer staging link does not match the exact installed release.");
            }
            m_pointerRuntime.ReplaceAtomically(plan.TemporaryCurrentPointerPath, plan.ActivationPlan.CurrentPointerPath);
            CurrentPointerRuntimeSnapshot after = m_pointerRuntime.Read(plan.ActivationPlan.CurrentPointerPath);
            CurrentPointerRuntimeSnapshot consumed = m_pointerRuntime.Read(plan.TemporaryCurrentPointerPath);
            if (!after.EntryPresent || !after.IsSymbolicLink ||
                !string.Equals(after.LinkTarget, plan.RollbackCurrentLinkTarget, StringComparison.Ordinal) ||
                consumed.EntryPresent)
            {
                throw new RollbackMutationException(
                    VerifiedReleaseActivationRollbackExecutionFailureCode.CurrentPointerRollbackFailed,
                    "The rollback pointer had an ambiguous atomic replacement outcome.");
            }
        }
        catch
        {
            if (created)
            {
                try
                {
                    if (m_pointerRuntime.Read(plan.TemporaryCurrentPointerPath).EntryPresent)
                    {
                        m_pointerRuntime.DeleteTemporary(plan.TemporaryCurrentPointerPath);
                    }
                }
                catch
                {
                }
            }
            throw;
        }
    }

    private async Task VerifyInstalledHealthAsync(
        VerifiedReleaseActivationRollbackPlan plan,
        string canonicalGatewayAuthority,
        bool remoteAgentRequired,
        DateTimeOffset startedAt,
        RollbackExecutionTally tally,
        CancellationToken cancellationToken)
    {
        tally.HealthTargetCount = plan.HealthPlan.Targets.Count;
        foreach (VerifiedReleaseActivationHealthVerificationTarget target in
                 plan.HealthPlan.Targets)
        {
            DateTimeOffset deadline = m_timeProvider.GetUtcNow().AddMilliseconds(
                target.DeadlineMilliseconds);
            if (target.ServiceRole ==
                VerifiedReleaseActivationServiceRole.AetherRemoteAgent)
            {
                if (remoteAgentRequired)
                {
                    bool ready = await WaitForAttemptAsync(
                        deadline,
                        _ =>
                        {
                            tally.FreshBrokerLinkAttemptCount++;
                            return Task.FromResult(ObserveFreshBrokerLink(startedAt));
                        },
                        cancellationToken);
                    if (!ready)
                    {
                        throw new RollbackMutationException(
                            VerifiedReleaseActivationRollbackExecutionFailureCode
                                .InstalledHealthVerificationFailed,
                            "The exact remote station agent did not establish a fresh broker link after rollback.");
                    }
                    tally.FreshBrokerLinkCheckCount++;
                }
                tally.VerifiedHealthTargetCount++;
                continue;
            }

            bool unitActive = await WaitForAttemptAsync(
                deadline,
                timeout =>
                {
                    tally.UnitActivityAttemptCount++;
                    return m_healthRuntime.CheckUnitActiveAsync(
                        target.UnitIdentity,
                        timeout,
                        cancellationToken);
                },
                cancellationToken);
            if (!unitActive)
            {
                throw new RollbackMutationException(
                    VerifiedReleaseActivationRollbackExecutionFailureCode
                        .InstalledHealthVerificationFailed,
                    "A rollback service unit did not become active within its bounded deadline.");
            }
            tally.UnitActivityCheckCount++;

            bool contractReady = await WaitForAttemptAsync(
                deadline,
                timeout =>
                {
                    tally.LoopbackHttpAttemptCount++;
                    return m_healthRuntime.CheckLoopbackHealthAsync(
                        target,
                        canonicalGatewayAuthority,
                        timeout,
                        cancellationToken);
                },
                cancellationToken);
            if (!contractReady)
            {
                throw new RollbackMutationException(
                    VerifiedReleaseActivationRollbackExecutionFailureCode
                        .InstalledHealthVerificationFailed,
                    "A rollback loopback health contract did not become ready within its bounded deadline.");
            }
            tally.LoopbackHttpCheckCount++;
            tally.VerifiedHealthTargetCount++;
        }
    }

    private async Task<bool> WaitForAttemptAsync(
        DateTimeOffset deadline,
        Func<TimeSpan, Task<HealthProbeAttemptResult>> attempt,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < MaximumAttemptsPerTarget; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            TimeSpan remaining = deadline - now;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }
            TimeSpan timeout = remaining < ProbeAttemptTimeout
                ? remaining
                : ProbeAttemptTimeout;
            HealthProbeAttemptResult result = await attempt(timeout);
            if (result.Succeeded)
            {
                return true;
            }
            if (!result.Retryable)
            {
                return false;
            }
            now = m_timeProvider.GetUtcNow();
            remaining = deadline - now;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }
            TimeSpan delay = remaining < PollInterval
                ? remaining
                : PollInterval;
            await m_delay(delay, cancellationToken);
        }
        return false;
    }

    private HealthProbeAttemptResult ObserveFreshBrokerLink(DateTimeOffset startedAt)
    {
        RemoteStationAdministrationSnapshot snapshot;
        try
        {
            snapshot = m_remoteStationSnapshotReader();
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or OverflowException)
        {
            return HealthProbeAttemptResult.Retry(
                "The broker station snapshot is unavailable.");
        }
        if (!snapshot.Enabled || !snapshot.BrokerReachable ||
            snapshot.RefreshedAt is null || snapshot.RefreshedAt < startedAt)
        {
            return HealthProbeAttemptResult.Retry(
                "The broker station snapshot is not fresh.");
        }
        RemoteStationAdministrationEntry[] matches = snapshot.Stations
            .Where(station => string.Equals(
                station.StationId,
                m_settings.ExpectedStationId,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            return HealthProbeAttemptResult.Retry(
                "The expected station link is not uniquely present.");
        }
        RemoteStationAdministrationEntry station = matches[0];
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        return string.Equals(station.State, "online", StringComparison.Ordinal) &&
            station.LastSeen >= startedAt &&
            station.LastSeen <= now.AddSeconds(5) &&
            station.ConnectedAt <= station.LastSeen &&
            station.HeartbeatSequence >= 1 &&
            station.InventorySequence >= 1
            ? HealthProbeAttemptResult.Success()
            : HealthProbeAttemptResult.Retry(
                "The expected station link is not freshly online.");
    }

    private static ReleaseActivationRollbackSettings ValidateSettings(
        ReleaseActivationRollbackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string stationId = settings.ExpectedStationId ?? string.Empty;
        if (!settings.ExecutionEnabled)
        {
            if (stationId.Length != 0)
            {
                throw new InvalidOperationException(
                    "Disabled release rollback must not configure a station identity.");
            }
            return new ReleaseActivationRollbackSettings();
        }
        if (stationId.Length != 0 && !IsCanonicalStationId(stationId))
        {
            throw new InvalidOperationException(
                "An enabled release rollback station identity must be canonical when configured.");
        }
        return new ReleaseActivationRollbackSettings
        {
            ExecutionEnabled = true,
            ExpectedStationId = stationId
        };
    }

    private static bool IsCanonicalStationId(string value) =>
        value.Length is >= 1 and <= 64 &&
        IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool TryClassifyActions(
        IReadOnlyList<VerifiedReleaseActivationServiceControlAction> actions,
        InstallationTopologyProfile topology,
        out RollbackBoundAction[] boundActions)
    {
        List<RollbackBoundAction> result = [];
        foreach (VerifiedReleaseActivationServiceControlAction action in actions)
        {
            VerifiedReleaseActivationServiceRole role = action.ServiceRole!.Value;
            bool local = role switch
            {
                VerifiedReleaseActivationServiceRole.GatewayWeb =>
                    topology.GatewayRunsHere,
                VerifiedReleaseActivationServiceRole.Broker =>
                    topology.BrokerRunsHere,
                VerifiedReleaseActivationServiceRole.AetherRemoteAgent =>
                    topology.AgentRunsHere,
                VerifiedReleaseActivationServiceRole.StationEngine =>
                    topology.StationEngineRunsHere,
                _ => false
            };
            if (local)
            {
                result.Add(new RollbackBoundAction(action, TopologyNoOp: false));
                continue;
            }
            if (role == VerifiedReleaseActivationServiceRole.AetherRemoteAgent &&
                !topology.AcceptsRemoteStations)
            {
                result.Add(new RollbackBoundAction(action, TopologyNoOp: true));
                continue;
            }
            boundActions = [];
            return false;
        }
        boundActions = result.ToArray();
        return true;
    }

    private static bool TryResolveSupportedTopology(
        InstallationTopologyProfile topology,
        out bool remoteAgentRequired)
    {
        remoteAgentRequired = false;
        if (!topology.GatewayRunsHere || !topology.BrokerRunsHere ||
            !topology.StationEngineRunsHere || topology.AgentRunsHere)
        {
            return false;
        }
        remoteAgentRequired = topology.AcceptsRemoteStations;
        return topology.Kind is
            InstallationTopologyKind.PersonalSingleStation or
            InstallationTopologyKind.LocalStationGateway or
            InstallationTopologyKind.HybridGateway;
    }

    private static bool TryBindSetup(
        InstallationSetupState state,
        VerifiedReleaseActivationPlan activation,
        out string canonicalGatewayAuthority,
        out InstallationTopologyProfile topology)
    {
        canonicalGatewayAuthority = string.Empty;
        topology = null!;
        try
        {
            InstallationSetupStateValidator.Validate(state);
            if (state.Revision != activation.SetupRevision ||
                state.Lock.Mode != InstallationSetupLockMode.Complete ||
                state.LastCompletedStep != InstallationSetupStep.Administrator ||
                state.Topology is null || state.Paths is null ||
                state.UpdateChannel != activation.UpdateChannel ||
                !string.Equals(
                    state.PinnedRelease,
                    activation.PinnedReleaseIdentity,
                    StringComparison.Ordinal) ||
                state.InstallTransmitSupport != activation.InstallTransmitSupport ||
                !PathEquals(
                    state.Paths.ReleaseDirectory,
                    activation.ReleaseRootPath))
            {
                return false;
            }
            CanonicalPublicUrl publicUrl =
                CanonicalPublicUrl.Parse(state.CanonicalPublicUrl);
            canonicalGatewayAuthority = publicUrl.Uri.Authority;
            topology = InstallationTopologyProfile.For(state.Topology.Value);
            return !string.IsNullOrEmpty(canonicalGatewayAuthority);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool MatchesExpectedStatus(
        ReleaseStatusReadResult status,
        VerifiedReleaseActivationPlan activation,
        bool targetActive)
    {
        if (!status.Succeeded ||
            status.FailureCode != ReleaseStatusFailureCode.None ||
            status.SetupSchemaVersion is null or < 1 ||
            status.SetupRevision != activation.SetupRevision ||
            !status.SetupComplete ||
            status.SetupLockMode != InstallationSetupLockMode.Complete ||
            status.LastCompletedStep != InstallationSetupStep.Administrator ||
            status.UpdateChannel != activation.UpdateChannel ||
            !string.Equals(
                status.PinnedReleaseIdentity,
                activation.PinnedReleaseIdentity,
                StringComparison.Ordinal) ||
            status.InstallTransmitSupport != activation.InstallTransmitSupport ||
            !status.ReleaseDirectoryPresent ||
            status.AvailableReleaseIdentities is null ||
            status.AvailableReleaseCount != status.AvailableReleaseIdentities.Count ||
            status.AvailableReleaseCount is < 2 or >
                ReleaseInstallationStatusReader.MaximumReleaseCount ||
            !status.CurrentPointerPresent ||
            status.AvailableReleaseIdentities
                .Distinct(StringComparer.Ordinal).Count() !=
                status.AvailableReleaseIdentities.Count)
        {
            return false;
        }
        string expectedActive = targetActive
            ? activation.TargetReleaseIdentity
            : activation.InstalledReleaseIdentity;
        return string.Equals(
                status.ActiveReleaseIdentity,
                expectedActive,
                StringComparison.Ordinal) &&
            status.AvailableReleaseIdentities.Contains(
                activation.InstalledReleaseIdentity,
                StringComparer.Ordinal) &&
            status.AvailableReleaseIdentities.Contains(
                activation.TargetReleaseIdentity,
                StringComparer.Ordinal);
    }

    private static bool EquivalentStatus(
        ReleaseStatusReadResult first,
        ReleaseStatusReadResult second) =>
        first.Succeeded == second.Succeeded &&
        first.FailureCode == second.FailureCode &&
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
        first.AvailableReleaseCount == second.AvailableReleaseCount &&
        first.AvailableReleaseIdentities is not null &&
        second.AvailableReleaseIdentities is not null &&
        first.AvailableReleaseIdentities.SequenceEqual(
            second.AvailableReleaseIdentities,
            StringComparer.Ordinal) &&
        first.CurrentPointerPresent == second.CurrentPointerPresent &&
        string.Equals(
            first.ActiveReleaseIdentity,
            second.ActiveReleaseIdentity,
            StringComparison.Ordinal) &&
        first.RollbackCandidateKnown == second.RollbackCandidateKnown;

    private static bool EquivalentStatusExceptActive(
        ReleaseStatusReadResult first,
        ReleaseStatusReadResult second) =>
        first.Succeeded == second.Succeeded &&
        first.FailureCode == second.FailureCode &&
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
        first.AvailableReleaseCount == second.AvailableReleaseCount &&
        first.AvailableReleaseIdentities is not null &&
        second.AvailableReleaseIdentities is not null &&
        first.AvailableReleaseIdentities.SequenceEqual(
            second.AvailableReleaseIdentities,
            StringComparer.Ordinal) &&
        first.CurrentPointerPresent && second.CurrentPointerPresent &&
        first.RollbackCandidateKnown == second.RollbackCandidateKnown;

    private static bool EquivalentSetup(
        InstallationSetupState first,
        InstallationSetupState second) =>
        first.SchemaVersion == second.SchemaVersion &&
        first.Revision == second.Revision &&
        first.UpdatedAt == second.UpdatedAt &&
        first.LastCompletedStep == second.LastCompletedStep &&
        Equals(first.Lock, second.Lock) &&
        first.Topology == second.Topology &&
        string.Equals(
            first.CanonicalPublicUrl,
            second.CanonicalPublicUrl,
            StringComparison.Ordinal) &&
        Equals(first.Paths, second.Paths) &&
        first.UpdateChannel == second.UpdateChannel &&
        string.Equals(
            first.PinnedRelease,
            second.PinnedRelease,
            StringComparison.Ordinal) &&
        first.InstallTransmitSupport == second.InstallTransmitSupport;

    private static void EnsureRealDirectory(string path)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists || directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !PathEquals(directory.FullName, path))
        {
            throw new InvalidOperationException(
                "A rollback directory is missing, linked, or non-canonical.");
        }
    }

    private static void EnsureAbsent(string path)
    {
        if (PathEntryExists(path))
        {
            throw new InvalidOperationException(
                "A rollback staging or displaced identity already exists.");
        }
    }

    private static void ValidateLiveTreeForDisplacement(string root)
    {
        int directories = 0;
        int files = 0;
        long bytes = 0;
        Stack<DirectoryInfo> pending = new();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            directory.Refresh();
            if (!directory.Exists || directory.LinkTarget is not null ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                ++directories >
                    VerifiedReleaseActivationConfigurationBackupService
                        .MaximumDirectoryCount)
            {
                throw new InvalidOperationException(
                    "A live rollback root contains an unsafe directory.");
            }
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entry.Refresh();
                if (entry.LinkTarget is not null ||
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "A live rollback root contains a linked entry.");
                }
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                    continue;
                }
                if (entry is not FileInfo file ||
                    (file.Attributes &
                        (FileAttributes.Directory |
                         FileAttributes.Device |
                         FileAttributes.Offline)) != 0 ||
                    ++files >
                        VerifiedReleaseActivationConfigurationBackupService
                            .MaximumFileCount ||
                    file.Length < 0 ||
                    file.Length >
                        VerifiedReleaseActivationConfigurationBackupService
                            .MaximumFileLength)
                {
                    throw new InvalidOperationException(
                        "A live rollback root contains an unsafe file.");
                }
                bytes = checked(bytes + file.Length);
                if (bytes >
                    VerifiedReleaseActivationConfigurationBackupService
                        .MaximumSourceBytes)
                {
                    throw new InvalidOperationException(
                        "A live rollback root exceeds its bounded byte limit.");
                }
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private bool TryCleanupRestoreStaging(
        VerifiedReleaseActivationRollbackPlan plan,
        IReadOnlySet<string> createdRestoreStagingPaths)
    {
        bool success = true;
        foreach (VerifiedReleaseActivationRollbackRestoreSource source in
                 plan.RestoreSources)
        {
            if (createdRestoreStagingPaths.Contains(source.RestoreStagingPath))
            {
                success &= m_treeDelete(source.RestoreStagingPath);
            }
            else if (PathEntryExists(source.RestoreStagingPath))
            {
                success = false;
            }
            if (PathEntryExists(source.DisplacedLivePath))
            {
                success = false;
            }
        }
        return success;
    }

    private static bool TryDeleteTreeOnCurrentPlatform(string path) =>
        OperatingSystem.IsLinux() && TryDeleteTree(path);

    [SupportedOSPlatform("linux")]
    private static bool TryDeleteTree(string path)
    {
        try
        {
            if (!PathEntryExists(path))
            {
                return true;
            }
            DirectoryInfo root = new(path);
            root.Refresh();
            if (!root.Exists || root.LinkTarget is not null ||
                (root.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            Stack<DirectoryInfo> pending = new();
            pending.Push(root);
            int directoryCount = 0;
            int fileCount = 0;
            while (pending.Count > 0)
            {
                DirectoryInfo directory = pending.Pop();
                directory.Refresh();
                if (directory.LinkTarget is not null ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    ++directoryCount >
                        VerifiedReleaseActivationConfigurationBackupService
                            .MaximumDirectoryCount)
                {
                    return false;
                }
                File.SetUnixFileMode(
                    directory.FullName,
                    PrivateWritableDirectoryMode);
                foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
                {
                    entry.Refresh();
                    if (entry.LinkTarget is not null ||
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child);
                    }
                    else if (entry is FileInfo file)
                    {
                        if (++fileCount >
                            VerifiedReleaseActivationConfigurationBackupService
                                .MaximumFileCount)
                        {
                            return false;
                        }
                        File.SetUnixFileMode(file.FullName, PrivateWritableFileMode);
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            Directory.Delete(path, recursive: true);
            return !PathEntryExists(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or InvalidOperationException or
                ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private void MarkReconciliation(
        VerifiedReleaseActivationRollbackPlan plan,
        VerifiedReleaseActivationCurrentPointerSwitchEvidence pointerEvidence,
        VerifiedReleaseActivationRollbackTriggerKind triggerKind,
        RollbackExecutionTally tally)
    {
        lock (m_stateGate)
        {
            m_reconciliationPlan = plan;
            m_reconciliationPointerEvidence = pointerEvidence;
            m_reconciliationTrigger = triggerKind;
            m_reconciliationTally = tally.Clone();
        }
    }

    private static string SafeDescendant(string root, string relativePath)
    {
        if (relativePath == ".")
        {
            return CanonicalDirectory(root);
        }
        string[] segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Length is < 1 or > 32 ||
            segments.Any(segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".." ||
                segment.Length > 255 || segment.Contains('/') ||
                segment.Contains('\\') || segment.Any(char.IsControl)))
        {
            throw new InvalidDataException(
                "A rollback relative path is unsafe.");
        }
        string canonicalRoot = CanonicalDirectory(root);
        string candidate = Path.GetFullPath(
            segments.Aggregate(canonicalRoot, Path.Combine));
        string prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A rollback path escaped its validated root.");
        }
        return candidate;
    }

    private static int RelativeDepth(string value) =>
        value == "." ? 0 : value.Count(character => character == '/') + 1;

    private static string CanonicalDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
        {
            throw new InvalidOperationException(
                "A canonical absolute rollback directory is required.");
        }
        string result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!string.Equals(result, value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Rollback directories must already be canonical.");
        }
        return result;
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal);
    }

    private static bool PathEntryExists(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        info.Refresh();
        return info.Exists || info.LinkTarget is not null || Directory.Exists(path);
    }

    private static bool IsObservationException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException or
            FileNotFoundException or PathTooLongException or SecurityException;

    private static bool IsFileMutationException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or
            ArgumentException or NotSupportedException or PathTooLongException or
            OverflowException or SecurityException;

    private sealed class RollbackMutationException : Exception
    {
        internal RollbackMutationException(
            VerifiedReleaseActivationRollbackExecutionFailureCode failureCode,
            string message)
            : base(message)
        {
            FailureCode = failureCode;
        }

        internal VerifiedReleaseActivationRollbackExecutionFailureCode FailureCode
        {
            get;
        }
    }
}
