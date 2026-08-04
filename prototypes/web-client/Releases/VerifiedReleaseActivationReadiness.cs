using System.Collections.ObjectModel;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationReadinessFailureCode
{
    None = 0,
    ActivationPlanNotEligible = 1,
    ActivationPlanUnavailable = 2,
    ActivationPlanMismatch = 3,
    EvidenceInvalid = 4,
    ReleaseStatusUnavailable = 5,
    ReleaseStatusMismatch = 6,
    TxLeaseAdmissionOpen = 7,
    ActiveTxLeasesPresent = 8,
    SessionEvidenceUnsafe = 9,
    WatchdogEvidenceUnsafe = 10,
    ConfigurationBackupNotReady = 11,
    MigrationNotReady = 12,
    ServiceControlNotReady = 13,
    HealthVerificationNotReady = 14,
    RollbackNotReady = 15,
    OperatorApprovalMissing = 16
}

public sealed record VerifiedReleaseActivationReadinessReport(
    bool Succeeded,
    VerifiedReleaseActivationReadinessFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int SessionCount,
    int RadioCount,
    int ActiveTxLeaseCount,
    int ArmedWatchdogCount,
    int WatchdogReconciliationCount,
    bool ReleaseStatusStable,
    bool TxLeaseAdmissionClosed,
    bool AllSessionsConnected,
    bool AllRadiosFreshIdle,
    bool AllSessionSafetyDisarmed,
    bool AllWatchdogsDisarmed,
    bool ConfigurationBackupReady,
    bool MigrationReady,
    bool ServiceControlReady,
    bool HealthVerificationReady,
    bool RollbackReady,
    bool OperatorApproved,
    bool CurrentPointerChanged,
    bool ActivationPerformed)
{
    internal VerifiedReleaseActivationReadiness? Readiness { get; init; }

    internal static VerifiedReleaseActivationReadinessReport Failure(
        VerifiedReleaseActivationReadinessFailureCode failureCode,
        string message,
        VerifiedReleaseActivationPlanCompositionResult? planResult = null,
        VerifiedReleaseActivationReadinessEvidence? evidence = null,
        bool releaseStatusStable = false,
        bool allSessionsConnected = false,
        bool allRadiosFreshIdle = false,
        bool allSessionSafetyDisarmed = false,
        bool allWatchdogsDisarmed = false) =>
        new(
            false,
            failureCode,
            message,
            planResult?.SetupRevision,
            planResult?.InstalledReleaseIdentity ?? string.Empty,
            planResult?.TargetReleaseIdentity ?? string.Empty,
            evidence?.Sessions.Count ?? 0,
            evidence?.Sessions
                .Select(session => session.RadioId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() ?? 0,
            evidence?.ActiveTxLeases.Count ?? 0,
            evidence?.Watchdogs.ArmedProcessCount ?? 0,
            evidence?.Watchdogs.ReconciliationRequiredCount ?? 0,
            releaseStatusStable,
            evidence?.TxLeaseAdmissionClosed ?? false,
            allSessionsConnected,
            allRadiosFreshIdle,
            allSessionSafetyDisarmed,
            allWatchdogsDisarmed,
            evidence?.ConfigurationBackupReady ?? false,
            evidence?.MigrationReady ?? false,
            evidence?.ServiceControlReady ?? false,
            evidence?.HealthVerificationReady ?? false,
            evidence?.RollbackReady ?? false,
            evidence?.OperatorApproved ?? false,
            CurrentPointerChanged: false,
            ActivationPerformed: false);

    internal static VerifiedReleaseActivationReadinessReport Success(
        VerifiedReleaseActivationPlanCompositionResult planResult,
        VerifiedReleaseActivationReadiness readiness,
        bool serviceControlReady) =>
        new(
            true,
            VerifiedReleaseActivationReadinessFailureCode.None,
            "Verified release activation readiness was proven without changing current or executing activation work.",
            readiness.Plan.SetupRevision,
            readiness.Plan.InstalledReleaseIdentity,
            readiness.Plan.TargetReleaseIdentity,
            readiness.SessionCount,
            readiness.RadioCount,
            ActiveTxLeaseCount: 0,
            ArmedWatchdogCount: 0,
            WatchdogReconciliationCount: 0,
            ReleaseStatusStable: true,
            TxLeaseAdmissionClosed: true,
            AllSessionsConnected: true,
            AllRadiosFreshIdle: true,
            AllSessionSafetyDisarmed: true,
            AllWatchdogsDisarmed: true,
            ConfigurationBackupReady: true,
            MigrationReady: true,
            ServiceControlReady: serviceControlReady,
            HealthVerificationReady: true,
            RollbackReady: true,
            OperatorApproved: true,
            CurrentPointerChanged: false,
            ActivationPerformed: false)
        {
            Readiness = readiness
        };
}

public sealed record VerifiedReleaseActivationReadinessDiagnostics(
    bool Registered,
    bool ActivationPlanInputRegistered,
    bool ReleaseStatusEvaluationRegistered,
    bool TxLeaseAdmissionEvaluationRegistered,
    bool SessionSafetyEvaluationRegistered,
    bool RadioIdleEvaluationRegistered,
    bool WatchdogEvaluationRegistered,
    bool BackupReadinessEvaluationRegistered,
    bool MigrationReadinessEvaluationRegistered,
    bool ServiceControlReadinessEvaluationRegistered,
    bool HealthVerificationReadinessEvaluationRegistered,
    bool RollbackReadinessEvaluationRegistered,
    bool OperatorApprovalEvaluationRegistered,
    bool FileWriteRegistered,
    bool CurrentPointerMutationRegistered,
    bool ActivationExecutionRegistered,
    bool TxLeaseMutationRegistered,
    bool RadioCommandRegistered,
    bool WatchdogMutationRegistered,
    bool BackupExecutionRegistered,
    bool MigrationExecutionRegistered,
    bool ServiceControlRegistered,
    bool HealthProbeCallerRegistered,
    bool RollbackExecutionRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool HostedServiceCallerRegistered,
    bool TimerCallerRegistered,
    bool AetherRemoteCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed record VerifiedReleaseActivationSessionEvidence(
    string SessionId,
    string RadioId,
    bool Connected,
    bool TxLifecycleRegistered,
    bool LeaseActive,
    string GateState,
    bool GateHasActiveIntent,
    string SafetyState,
    bool SafetyActive,
    bool CommandTransactionActive,
    bool CommandTransactionReconciliationRequired,
    bool IndependentWatchdogArmed,
    string IndependentWatchdogState,
    bool IndependentWatchdogReconciliationRequired,
    RadioTxOccupancySnapshot Occupancy)
{
    internal static VerifiedReleaseActivationSessionEvidence Capture(
        RadioSessionDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        StationTxLifecycleDiagnostics? lifecycle = diagnostics.TxLifecycle;
        StationTxIndependentWatchdogDiagnostics? watchdog =
            lifecycle?.IndependentWatchdog;
        StationTxCommandTransactionCompositionDiagnostics? transaction =
            lifecycle?.StationCommandTransactionComposition;
        return new VerifiedReleaseActivationSessionEvidence(
            diagnostics.SessionId,
            diagnostics.RadioId,
            diagnostics.Connected,
            lifecycle?.Registered ?? false,
            lifecycle?.LeaseActive ?? true,
            lifecycle?.GateState ?? string.Empty,
            lifecycle?.GateHasActiveIntent ?? true,
            lifecycle?.SafetyState ?? string.Empty,
            lifecycle?.SafetyActive ?? true,
            transaction?.Active ?? true,
            transaction?.ReconciliationRequired ?? true,
            watchdog?.Armed ?? true,
            watchdog?.State ?? string.Empty,
            string.Equals(
                watchdog?.State,
                "ReconciliationRequired",
                StringComparison.Ordinal),
            diagnostics.TxOccupancy);
    }
}

internal sealed record VerifiedReleaseActivationReadinessEvidence(
    DateTimeOffset CapturedAt,
    ReleaseStatusReadResult ReleaseStatus,
    bool TxLeaseAdmissionClosed,
    IReadOnlyList<TxLease> ActiveTxLeases,
    IReadOnlyList<VerifiedReleaseActivationSessionEvidence> Sessions,
    StationTxIndependentWatchdogAggregate Watchdogs,
    bool ConfigurationBackupReady,
    bool MigrationReady,
    bool ServiceControlReady,
    bool HealthVerificationReady,
    bool RollbackReady,
    bool OperatorApproved);

internal sealed class VerifiedReleaseActivationReadiness
{
    private readonly ReadOnlyCollection<VerifiedReleaseActivationSessionEvidence>
        m_sessions;

    internal VerifiedReleaseActivationReadiness(
        VerifiedReleaseActivationPlan plan,
        DateTimeOffset capturedAt,
        IReadOnlyList<VerifiedReleaseActivationSessionEvidence> sessions)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        CapturedAt = capturedAt;
        m_sessions = Array.AsReadOnly(
            (sessions ?? throw new ArgumentNullException(nameof(sessions)))
                .ToArray());
    }

    internal VerifiedReleaseActivationPlan Plan { get; }
    internal DateTimeOffset CapturedAt { get; }
    internal IReadOnlyList<VerifiedReleaseActivationSessionEvidence> Sessions =>
        m_sessions;
    internal int SessionCount => m_sessions.Count;
    internal int RadioCount => m_sessions
        .Select(session => session.RadioId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
}

/// <summary>
/// Pure fail-closed evaluation of one verified activation plan against a bounded
/// authoritative evidence snapshot. The evaluator proves stable inactive release
/// status, closed TX-lease admission, zero active leases, fresh idle radio state,
/// disarmed session safety/watchdogs, prepared backup/migration/service/health/
/// rollback prerequisites, and explicit operator approval. It performs no I/O,
/// current-pointer mutation, activation, lease mutation, radio command, watchdog
/// mutation, backup, migration, service control, health probe, rollback, browser,
/// Admin, hosted-service, timer, AetherRemote, command, or transmit action.
/// </summary>
public sealed class VerifiedReleaseActivationReadinessEvaluator
{
    internal const int MaximumSessionCount = 64;
    internal const int MaximumLeaseCount = 64;
    internal static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(1);

    private readonly TimeProvider m_timeProvider;

    public VerifiedReleaseActivationReadinessEvaluator()
        : this(TimeProvider.System)
    {
    }

    internal VerifiedReleaseActivationReadinessEvaluator(
        TimeProvider timeProvider)
    {
        m_timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        Snapshot = new VerifiedReleaseActivationReadinessDiagnostics(
            Registered: true,
            ActivationPlanInputRegistered: true,
            ReleaseStatusEvaluationRegistered: true,
            TxLeaseAdmissionEvaluationRegistered: true,
            SessionSafetyEvaluationRegistered: true,
            RadioIdleEvaluationRegistered: true,
            WatchdogEvaluationRegistered: true,
            BackupReadinessEvaluationRegistered: true,
            MigrationReadinessEvaluationRegistered: true,
            ServiceControlReadinessEvaluationRegistered: true,
            HealthVerificationReadinessEvaluationRegistered: true,
            RollbackReadinessEvaluationRegistered: true,
            OperatorApprovalEvaluationRegistered: true,
            FileWriteRegistered: false,
            CurrentPointerMutationRegistered: false,
            ActivationExecutionRegistered: false,
            TxLeaseMutationRegistered: false,
            RadioCommandRegistered: false,
            WatchdogMutationRegistered: false,
            BackupExecutionRegistered: false,
            MigrationExecutionRegistered: false,
            ServiceControlRegistered: false,
            HealthProbeCallerRegistered: false,
            RollbackExecutionRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            HostedServiceCallerRegistered: false,
            TimerCallerRegistered: false,
            AetherRemoteCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationReadinessDiagnostics Snapshot { get; }

    internal VerifiedReleaseActivationReadinessReport Evaluate(
        VerifiedReleaseActivationPlanCompositionResult planResult,
        VerifiedReleaseActivationReadinessEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(planResult);
        ArgumentNullException.ThrowIfNull(evidence);

        if (!IsEligiblePlanResult(planResult))
        {
            return VerifiedReleaseActivationReadinessReport.Failure(
                VerifiedReleaseActivationReadinessFailureCode
                    .ActivationPlanNotEligible,
                "A successful non-mutating verified activation plan is required.",
                planResult,
                evidence);
        }

        VerifiedReleaseActivationPlan? plan = planResult.Plan;
        if (plan is null)
        {
            return VerifiedReleaseActivationReadinessReport.Failure(
                VerifiedReleaseActivationReadinessFailureCode
                    .ActivationPlanUnavailable,
                "The successful activation plan does not retain its internal verified plan.",
                planResult,
                evidence);
        }
        if (!MatchesPlanResult(planResult, plan))
        {
            return VerifiedReleaseActivationReadinessReport.Failure(
                VerifiedReleaseActivationReadinessFailureCode
                    .ActivationPlanMismatch,
                "Activation plan metadata does not match its public summary.",
                planResult,
                evidence);
        }

        DateTimeOffset now = m_timeProvider.GetUtcNow();
        if (!ValidateEvidenceShape(evidence, now))
        {
            return VerifiedReleaseActivationReadinessReport.Failure(
                VerifiedReleaseActivationReadinessFailureCode.EvidenceInvalid,
                "Activation readiness evidence is incomplete, stale, oversized, or malformed.",
                planResult,
                evidence);
        }

        if (!evidence.ReleaseStatus.Succeeded)
        {
            return VerifiedReleaseActivationReadinessReport.Failure(
                VerifiedReleaseActivationReadinessFailureCode
                    .ReleaseStatusUnavailable,
                "The inactive release status snapshot is unavailable.",
                planResult,
                evidence);
        }
        if (!MatchesReleaseStatus(
                evidence.ReleaseStatus,
                plan,
                evidence.HealthVerificationReady))
        {
            return VerifiedReleaseActivationReadinessReport.Failure(
                VerifiedReleaseActivationReadinessFailureCode
                    .ReleaseStatusMismatch,
                evidence.HealthVerificationReady
                    ? "Release status no longer matches the exact post-switch health-verification phase."
                    : "Release status no longer matches the verified inactive activation plan.",
                planResult,
                evidence);
        }

        if (!evidence.TxLeaseAdmissionClosed)
        {
            return VerifiedReleaseActivationReadinessReport.Failure(
                VerifiedReleaseActivationReadinessFailureCode.TxLeaseAdmissionOpen,
                "TX lease admission must be closed before activation readiness can be proven.",
                planResult,
                evidence,
                releaseStatusStable: true);
        }
        if (evidence.ActiveTxLeases.Count != 0)
        {
            return VerifiedReleaseActivationReadinessReport.Failure(
                VerifiedReleaseActivationReadinessFailureCode
                    .ActiveTxLeasesPresent,
                "All active TX leases must be absent before activation.",
                planResult,
                evidence,
                releaseStatusStable: true);
        }

        SessionEvaluation sessions = EvaluateSessions(evidence.Sessions, now);
        if (!sessions.Succeeded)
        {
            return VerifiedReleaseActivationReadinessReport.Failure(
                VerifiedReleaseActivationReadinessFailureCode
                    .SessionEvidenceUnsafe,
                sessions.Message,
                planResult,
                evidence,
                releaseStatusStable: true,
                sessions.AllConnected,
                sessions.AllFreshIdle,
                sessions.AllSafetyDisarmed);
        }

        bool watchdogsDisarmed =
            ValidateWatchdogs(evidence.Watchdogs, plan, evidence.Sessions);
        if (!watchdogsDisarmed)
        {
            return VerifiedReleaseActivationReadinessReport.Failure(
                VerifiedReleaseActivationReadinessFailureCode
                    .WatchdogEvidenceUnsafe,
                "Independent watchdog evidence is not fully disarmed and reconciliation-free.",
                planResult,
                evidence,
                releaseStatusStable: true,
                allSessionsConnected: true,
                allRadiosFreshIdle: true,
                allSessionSafetyDisarmed: true,
                allWatchdogsDisarmed: false);
        }

        if (!evidence.ConfigurationBackupReady)
        {
            return NotReady(
                VerifiedReleaseActivationReadinessFailureCode
                    .ConfigurationBackupNotReady,
                "A verified configuration backup must be prepared before activation.",
                planResult,
                evidence);
        }
        if (!evidence.MigrationReady)
        {
            return NotReady(
                VerifiedReleaseActivationReadinessFailureCode.MigrationNotReady,
                "The signed migration requirement must be prepared and validated before activation.",
                planResult,
                evidence);
        }
        if (RequiresServiceControl(plan) && !evidence.ServiceControlReady)
        {
            return NotReady(
                VerifiedReleaseActivationReadinessFailureCode
                    .ServiceControlNotReady,
                "Required service or host restart control is not ready.",
                planResult,
                evidence);
        }
        if (!evidence.HealthVerificationReady)
        {
            return NotReady(
                VerifiedReleaseActivationReadinessFailureCode
                    .HealthVerificationNotReady,
                "Post-switch service health verification is not ready.",
                planResult,
                evidence);
        }
        if (!evidence.RollbackReady)
        {
            return NotReady(
                VerifiedReleaseActivationReadinessFailureCode.RollbackNotReady,
                "Automatic rollback to the verified previous release is not ready.",
                planResult,
                evidence);
        }
        if (!evidence.OperatorApproved)
        {
            return NotReady(
                VerifiedReleaseActivationReadinessFailureCode
                    .OperatorApprovalMissing,
                "Explicit operator approval is required before activation.",
                planResult,
                evidence);
        }

        return VerifiedReleaseActivationReadinessReport.Success(
            planResult,
            new VerifiedReleaseActivationReadiness(
                plan,
                evidence.CapturedAt,
                evidence.Sessions),
            evidence.ServiceControlReady);
    }

    private static VerifiedReleaseActivationReadinessReport NotReady(
        VerifiedReleaseActivationReadinessFailureCode failureCode,
        string message,
        VerifiedReleaseActivationPlanCompositionResult planResult,
        VerifiedReleaseActivationReadinessEvidence evidence) =>
        VerifiedReleaseActivationReadinessReport.Failure(
            failureCode,
            message,
            planResult,
            evidence,
            releaseStatusStable: true,
            allSessionsConnected: true,
            allRadiosFreshIdle: true,
            allSessionSafetyDisarmed: true,
            allWatchdogsDisarmed: true);

    private static bool IsEligiblePlanResult(
        VerifiedReleaseActivationPlanCompositionResult result) =>
        result.Succeeded &&
        result.FailureCode == VerifiedReleaseActivationPlanFailureCode.None &&
        result.SetupRevision is > 0 &&
        result.PackageCount == 4 &&
        result.PublishedBytes > 0 &&
        result.TxLeaseAdmissionClosureRequired &&
        result.RadioAuthoritativeIdleRequired &&
        result.WatchdogsDisarmedRequired &&
        result.ConfigurationBackupRequired &&
        result.AtomicCurrentPointerSwitchRequired &&
        result.ServiceHealthVerificationRequired &&
        result.AutomaticRollbackRequired &&
        result.OperatorApprovalRequired &&
        !result.CurrentPointerMutationPerformed &&
        !result.ActivationPerformed;

    private static bool MatchesPlanResult(
        VerifiedReleaseActivationPlanCompositionResult result,
        VerifiedReleaseActivationPlan plan) =>
        result.SetupRevision == plan.SetupRevision &&
        string.Equals(
            result.InstalledReleaseIdentity,
            plan.InstalledReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            result.TargetReleaseIdentity,
            plan.TargetReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            result.TargetVersion,
            plan.TargetVersion,
            StringComparison.Ordinal) &&
        result.Architecture == plan.Architecture &&
        result.PackageCount == plan.Packages.Count &&
        result.TargetConfigurationSchemaVersion ==
            plan.TargetConfigurationSchemaVersion &&
        result.MigrationKind == plan.MigrationKind &&
        result.MigrationRequired == plan.MigrationRequired &&
        result.RestartServiceCount == plan.RestartServiceCount &&
        result.HostRestartRequired == plan.RestartHost &&
        plan.TxLeaseAdmissionClosureRequired &&
        plan.RadioAuthoritativeIdleRequired &&
        plan.WatchdogsDisarmedRequired &&
        plan.ConfigurationBackupRequired &&
        plan.AtomicCurrentPointerSwitchRequired &&
        plan.ServiceHealthVerificationRequired &&
        plan.AutomaticRollbackRequired &&
        plan.OperatorApprovalRequired;

    private static bool ValidateEvidenceShape(
        VerifiedReleaseActivationReadinessEvidence evidence,
        DateTimeOffset now)
    {
        if (evidence.ReleaseStatus is null ||
            evidence.ActiveTxLeases is null ||
            evidence.Sessions is null ||
            evidence.Watchdogs is null ||
            evidence.ActiveTxLeases.Count > MaximumLeaseCount ||
            evidence.Sessions.Count > MaximumSessionCount ||
            evidence.CapturedAt > now + MaximumFutureSkew ||
            now - evidence.CapturedAt > MaximumEvidenceAge)
        {
            return false;
        }

        HashSet<string> leaseIds = new(StringComparer.Ordinal);
        foreach (TxLease lease in evidence.ActiveTxLeases)
        {
            if (lease is null ||
                !ValidIdentifier(lease.LeaseId, 64) ||
                !ValidIdentifier(lease.RadioId, 128) ||
                !ValidIdentifier(lease.SessionId, 128) ||
                !leaseIds.Add(lease.LeaseId))
            {
                return false;
            }
        }

        HashSet<string> sessionIds = new(StringComparer.Ordinal);
        foreach (VerifiedReleaseActivationSessionEvidence session in
                 evidence.Sessions)
        {
            if (session is null ||
                !ValidIdentifier(session.SessionId, 128) ||
                !ValidIdentifier(session.RadioId, 128) ||
                !sessionIds.Add(session.SessionId) ||
                session.Occupancy is null)
            {
                return false;
            }
        }
        return true;
    }

    private static bool MatchesReleaseStatus(
        ReleaseStatusReadResult status,
        VerifiedReleaseActivationPlan plan,
        bool healthVerificationReady)
    {
        string expectedActiveIdentity = healthVerificationReady
            ? plan.TargetReleaseIdentity
            : plan.InstalledReleaseIdentity;
        string prohibitedActiveIdentity = healthVerificationReady
            ? plan.InstalledReleaseIdentity
            : plan.TargetReleaseIdentity;
        if (status.FailureCode != ReleaseStatusFailureCode.None ||
            status.SetupSchemaVersion is null or < 1 ||
            status.SetupRevision != plan.SetupRevision ||
            !status.SetupComplete ||
            status.SetupLockMode != InstallationSetupLockMode.Complete ||
            status.LastCompletedStep != InstallationSetupStep.Administrator ||
            status.UpdateChannel != plan.UpdateChannel ||
            !string.Equals(
                status.PinnedReleaseIdentity,
                plan.PinnedReleaseIdentity,
                StringComparison.Ordinal) ||
            status.InstallTransmitSupport != plan.InstallTransmitSupport ||
            !status.ReleaseDirectoryPresent ||
            status.AvailableReleaseIdentities is null ||
            status.AvailableReleaseCount !=
                status.AvailableReleaseIdentities.Count ||
            status.AvailableReleaseCount is < 2 or >
                ReleaseInstallationStatusReader.MaximumReleaseCount ||
            !status.CurrentPointerPresent ||
            !string.Equals(
                status.ActiveReleaseIdentity,
                expectedActiveIdentity,
                StringComparison.Ordinal) ||
            string.Equals(
                status.ActiveReleaseIdentity,
                prohibitedActiveIdentity,
                StringComparison.Ordinal))
        {
            return false;
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (string identity in status.AvailableReleaseIdentities)
        {
            if (!IsCanonicalReleaseIdentity(identity) || !identities.Add(identity))
            {
                return false;
            }
        }
        return identities.Contains(plan.InstalledReleaseIdentity) &&
            identities.Contains(plan.TargetReleaseIdentity);
    }

    private static SessionEvaluation EvaluateSessions(
        IReadOnlyList<VerifiedReleaseActivationSessionEvidence> sessions,
        DateTimeOffset now)
    {
        bool allConnected = true;
        bool allFreshIdle = true;
        bool allSafetyDisarmed = true;

        foreach (VerifiedReleaseActivationSessionEvidence session in sessions)
        {
            allConnected &= session.Connected;

            RadioTxOccupancySnapshot occupancy = session.Occupancy;
            bool freshIdle =
                string.Equals(
                    occupancy.RadioId,
                    session.RadioId,
                    StringComparison.OrdinalIgnoreCase) &&
                occupancy.State == RadioTxOccupancyState.Idle &&
                occupancy.ObservedAt is not null &&
                occupancy.FreshUntil is not null &&
                occupancy.ObservedAt <= now + MaximumFutureSkew &&
                occupancy.FreshUntil > now &&
                occupancy.Occupants.Count == 0;
            allFreshIdle &= freshIdle;

            bool safetyDisarmed =
                session.TxLifecycleRegistered &&
                !session.LeaseActive &&
                string.Equals(session.GateState, "Idle", StringComparison.Ordinal) &&
                !session.GateHasActiveIntent &&
                string.Equals(
                    session.SafetyState,
                    "Disarmed",
                    StringComparison.Ordinal) &&
                !session.SafetyActive &&
                !session.CommandTransactionActive &&
                !session.CommandTransactionReconciliationRequired &&
                !session.IndependentWatchdogArmed &&
                string.Equals(
                    session.IndependentWatchdogState,
                    "Disarmed",
                    StringComparison.Ordinal) &&
                !session.IndependentWatchdogReconciliationRequired;
            allSafetyDisarmed &= safetyDisarmed;
        }

        if (!allConnected)
        {
            return new SessionEvaluation(
                false,
                "Every active radio session must be connected before activation readiness can be proven.",
                allConnected,
                allFreshIdle,
                allSafetyDisarmed);
        }
        if (!allFreshIdle)
        {
            return new SessionEvaluation(
                false,
                "Every active radio session must carry fresh radio-authoritative idle evidence.",
                allConnected,
                allFreshIdle,
                allSafetyDisarmed);
        }
        if (!allSafetyDisarmed)
        {
            return new SessionEvaluation(
                false,
                "Every active session must have an idle gate, disarmed safety supervisor, inactive command transaction, and disarmed watchdog.",
                allConnected,
                allFreshIdle,
                allSafetyDisarmed);
        }
        return new SessionEvaluation(
            true,
            "Session safety evidence is ready.",
            allConnected,
            allFreshIdle,
            allSafetyDisarmed);
    }

    private static bool ValidateWatchdogs(
        StationTxIndependentWatchdogAggregate watchdogs,
        VerifiedReleaseActivationPlan plan,
        IReadOnlyList<VerifiedReleaseActivationSessionEvidence> sessions)
    {
        if (!watchdogs.SupervisionRegistered ||
            watchdogs.SessionCount < 0 ||
            watchdogs.RunningProcessCount < 0 ||
            watchdogs.ConnectedProcessCount < 0 ||
            watchdogs.RegisteredIdentityCount < 0 ||
            watchdogs.ArmedProcessCount != 0 ||
            watchdogs.ReconciliationRequiredCount != 0 ||
            watchdogs.SessionCount > MaximumSessionCount ||
            watchdogs.RunningProcessCount > watchdogs.SessionCount ||
            watchdogs.ConnectedProcessCount > watchdogs.SessionCount ||
            watchdogs.RegisteredIdentityCount > watchdogs.SessionCount ||
            !string.Equals(
                watchdogs.State,
                "supervised-empty-disarmed",
                StringComparison.Ordinal) &&
            !string.Equals(
                watchdogs.State,
                "supervised-disarmed",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (plan.InstallTransmitSupport)
        {
            return watchdogs.SessionCount == sessions.Count &&
                watchdogs.RunningProcessCount == watchdogs.SessionCount &&
                watchdogs.ConnectedProcessCount == watchdogs.SessionCount &&
                watchdogs.RegisteredIdentityCount == watchdogs.SessionCount &&
                string.Equals(
                    watchdogs.State,
                    sessions.Count == 0
                        ? "supervised-empty-disarmed"
                        : "supervised-disarmed",
                    StringComparison.Ordinal);
        }

        return watchdogs.SessionCount == 0
            ? string.Equals(
                watchdogs.State,
                "supervised-empty-disarmed",
                StringComparison.Ordinal)
            : watchdogs.RunningProcessCount == watchdogs.SessionCount &&
                watchdogs.ConnectedProcessCount == watchdogs.SessionCount &&
                watchdogs.RegisteredIdentityCount == watchdogs.SessionCount &&
                string.Equals(
                    watchdogs.State,
                    "supervised-disarmed",
                    StringComparison.Ordinal);
    }

    private static bool RequiresServiceControl(
        VerifiedReleaseActivationPlan plan) =>
        plan.RestartServiceCount > 0 || plan.RestartHost;

    private static bool ValidIdentifier(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 &&
            normalized.Length <= maximumLength &&
            string.Equals(value, normalized, StringComparison.Ordinal) &&
            normalized.All(character =>
                !char.IsControl(character) && !char.IsWhiteSpace(character));
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

    private sealed record SessionEvaluation(
        bool Succeeded,
        string Message,
        bool AllConnected,
        bool AllFreshIdle,
        bool AllSafetyDisarmed);
}
