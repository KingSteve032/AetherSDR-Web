using System.Collections.ObjectModel;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationEvidenceCollectionFailureCode
{
    None = 0,
    ActivationPlanNotEligible = 1,
    ActivationPlanUnavailable = 2,
    ActivationPlanMismatch = 3,
    EvidenceSourceUnavailable = 4,
    CollectionWindowExceeded = 5,
    ReleaseStatusDrift = 6,
    EvidenceMalformed = 7
}

public sealed record VerifiedReleaseActivationEvidenceCollectionReport(
    bool Succeeded,
    VerifiedReleaseActivationEvidenceCollectionFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int ObservedTxLeaseCount,
    int SessionCount,
    int RadioCount,
    int WatchdogSessionCount,
    int ArmedWatchdogCount,
    int WatchdogReconciliationCount,
    bool ReleaseStatusCollected,
    bool ReleaseStatusStable,
    bool ReleaseStatusSucceeded,
    bool ObservationOnlyTxLeaseSnapshot,
    bool SessionSafetyEvidenceCollected,
    bool WatchdogEvidenceCollected,
    bool TxLeaseAdmissionClosed,
    bool ConfigurationBackupReady,
    bool MigrationReady,
    bool ServiceControlReady,
    bool HealthVerificationReady,
    bool RollbackReady,
    bool OperatorApproved,
    bool CurrentPointerChanged,
    bool ActivationPerformed)
{
    internal VerifiedReleaseActivationEvidenceCollection? Collection { get; init; }

    internal static VerifiedReleaseActivationEvidenceCollectionReport Failure(
        VerifiedReleaseActivationEvidenceCollectionFailureCode failureCode,
        string message,
        VerifiedReleaseActivationPlanCompositionResult? planResult = null) =>
        new(
            false,
            failureCode,
            message,
            planResult?.SetupRevision,
            planResult?.InstalledReleaseIdentity ?? string.Empty,
            planResult?.TargetReleaseIdentity ?? string.Empty,
            ObservedTxLeaseCount: 0,
            SessionCount: 0,
            RadioCount: 0,
            WatchdogSessionCount: 0,
            ArmedWatchdogCount: 0,
            WatchdogReconciliationCount: 0,
            ReleaseStatusCollected: false,
            ReleaseStatusStable: false,
            ReleaseStatusSucceeded: false,
            ObservationOnlyTxLeaseSnapshot: false,
            SessionSafetyEvidenceCollected: false,
            WatchdogEvidenceCollected: false,
            TxLeaseAdmissionClosed: false,
            ConfigurationBackupReady: false,
            MigrationReady: false,
            ServiceControlReady: false,
            HealthVerificationReady: false,
            RollbackReady: false,
            OperatorApproved: false,
            CurrentPointerChanged: false,
            ActivationPerformed: false);

    internal static VerifiedReleaseActivationEvidenceCollectionReport Success(
        VerifiedReleaseActivationPlanCompositionResult planResult,
        VerifiedReleaseActivationEvidenceCollection collection) =>
        new(
            true,
            VerifiedReleaseActivationEvidenceCollectionFailureCode.None,
            "Authoritative activation evidence was collected without mutating release, lease, radio, watchdog, or service state.",
            collection.Plan.SetupRevision,
            collection.Plan.InstalledReleaseIdentity,
            collection.Plan.TargetReleaseIdentity,
            collection.Evidence.ActiveTxLeases.Count,
            collection.Evidence.Sessions.Count,
            collection.Evidence.Sessions
                .Select(session => session.RadioId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            collection.Evidence.Watchdogs.SessionCount,
            collection.Evidence.Watchdogs.ArmedProcessCount,
            collection.Evidence.Watchdogs.ReconciliationRequiredCount,
            ReleaseStatusCollected: true,
            ReleaseStatusStable: true,
            collection.Evidence.ReleaseStatus.Succeeded,
            ObservationOnlyTxLeaseSnapshot: true,
            SessionSafetyEvidenceCollected: true,
            WatchdogEvidenceCollected: true,
            collection.Evidence.TxLeaseAdmissionClosed,
            collection.Evidence.ConfigurationBackupReady,
            collection.Evidence.MigrationReady,
            collection.Evidence.ServiceControlReady,
            collection.Evidence.HealthVerificationReady,
            collection.Evidence.RollbackReady,
            collection.Evidence.OperatorApproved,
            CurrentPointerChanged: false,
            ActivationPerformed: false)
        {
            Collection = collection
        };
}

public sealed record VerifiedReleaseActivationEvidenceCollectionDiagnostics(
    bool Registered,
    bool ActivationPlanInputRegistered,
    bool ReleaseStatusDoubleReadRegistered,
    bool ObservationOnlyTxLeaseSnapshotRegistered,
    bool SessionDiagnosticsSnapshotRegistered,
    bool RadioOccupancySnapshotRegistered,
    bool WatchdogAggregateSnapshotRegistered,
    bool BoundedCollectionWindowRegistered,
    bool MissingPrerequisitesFailClosedRegistered,
    bool TxLeaseAdmissionClosureEvidenceRegistered,
    bool ConfigurationBackupEvidenceRegistered,
    bool MigrationExecutionEvidenceRegistered,
    bool ServiceControlEvidenceRegistered,
    bool HealthVerificationEvidenceRegistered,
    bool RollbackEvidenceRegistered,
    bool OperatorApprovalEvidenceRegistered,
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

internal sealed class VerifiedReleaseActivationEvidenceCollection
{
    private readonly ReadOnlyCollection<TxLease> m_leases;
    private readonly ReadOnlyCollection<VerifiedReleaseActivationSessionEvidence>
        m_sessions;

    internal VerifiedReleaseActivationEvidenceCollection(
        VerifiedReleaseActivationPlan plan,
        VerifiedReleaseActivationReadinessEvidence evidence)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        m_leases = Array.AsReadOnly(evidence.ActiveTxLeases.ToArray());
        m_sessions = Array.AsReadOnly(evidence.Sessions.ToArray());
        Evidence = evidence with
        {
            ActiveTxLeases = m_leases,
            Sessions = m_sessions
        };
    }

    internal VerifiedReleaseActivationPlan Plan { get; }
    internal VerifiedReleaseActivationReadinessEvidence Evidence { get; }
}

/// <summary>
/// Collects one bounded observation-only activation evidence snapshot from the
/// existing release-status reader, exact-plan TX-lease quiescence boundary,
/// radio-session registry, and independent-watchdog registry. It deliberately
/// leaves backup, required migration, required service control, health
/// verification, rollback, and operator approval unavailable until separately
/// reviewed authoritative boundaries exist. It performs no write, pointer mutation,
/// activation, lease mutation, radio/watchdog command, service control, health
/// probe, rollback, browser/Admin operation, hosted service, timer, AetherRemote,
/// command, or transmit action.
/// </summary>
public sealed class VerifiedReleaseActivationEvidenceCollector
{
    internal static readonly TimeSpan MaximumCollectionDuration =
        VerifiedReleaseActivationReadinessEvaluator.MaximumEvidenceAge;

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;
    private readonly Func<
        VerifiedReleaseActivationPlan,
        VerifiedReleaseActivationLeaseQuiescenceObservation>
        m_leaseQuiescenceReader;
    private readonly Func<IReadOnlyList<RadioSessionDiagnostics>>
        m_sessionSnapshotReader;
    private readonly Func<StationTxIndependentWatchdogAggregate>
        m_watchdogSnapshotReader;
    private readonly Func<
        RadioSessionDiagnostics,
        VerifiedReleaseActivationSessionEvidence> m_sessionEvidenceCapture;
    private readonly TimeProvider m_timeProvider;

    public VerifiedReleaseActivationEvidenceCollector(
        ReleaseInstallationStatusReader statusReader,
        VerifiedReleaseActivationLeaseQuiescenceBoundary leaseQuiescence,
        RadioSessionRegistry radioSessions,
        StationTxIndependentWatchdogRegistry independentWatchdogs)
        : this(
            CreateStatusReader(statusReader),
            CreateLeaseQuiescenceReader(leaseQuiescence),
            CreateSessionSnapshotReader(radioSessions),
            CreateWatchdogSnapshotReader(independentWatchdogs),
            TimeProvider.System)
    {
    }

    internal VerifiedReleaseActivationEvidenceCollector(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Func<IReadOnlyList<TxLease>> leaseSnapshotReader,
        Func<IReadOnlyList<RadioSessionDiagnostics>> sessionSnapshotReader,
        Func<StationTxIndependentWatchdogAggregate> watchdogSnapshotReader,
        TimeProvider timeProvider,
        Func<RadioSessionDiagnostics, VerifiedReleaseActivationSessionEvidence>?
            sessionEvidenceCapture = null)
        : this(
            statusReader,
            CreateLeaseQuiescenceReader(leaseSnapshotReader),
            sessionSnapshotReader,
            watchdogSnapshotReader,
            timeProvider,
            sessionEvidenceCapture)
    {
    }

    internal VerifiedReleaseActivationEvidenceCollector(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Func<
            VerifiedReleaseActivationPlan,
            VerifiedReleaseActivationLeaseQuiescenceObservation>
            leaseQuiescenceReader,
        Func<IReadOnlyList<RadioSessionDiagnostics>> sessionSnapshotReader,
        Func<StationTxIndependentWatchdogAggregate> watchdogSnapshotReader,
        TimeProvider timeProvider,
        Func<RadioSessionDiagnostics, VerifiedReleaseActivationSessionEvidence>?
            sessionEvidenceCapture = null)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_leaseQuiescenceReader = leaseQuiescenceReader ??
            throw new ArgumentNullException(nameof(leaseQuiescenceReader));
        m_sessionSnapshotReader = sessionSnapshotReader ??
            throw new ArgumentNullException(nameof(sessionSnapshotReader));
        m_watchdogSnapshotReader = watchdogSnapshotReader ??
            throw new ArgumentNullException(nameof(watchdogSnapshotReader));
        m_sessionEvidenceCapture = sessionEvidenceCapture ??
            VerifiedReleaseActivationSessionEvidence.Capture;
        m_timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));

        Snapshot = new VerifiedReleaseActivationEvidenceCollectionDiagnostics(
            Registered: true,
            ActivationPlanInputRegistered: true,
            ReleaseStatusDoubleReadRegistered: true,
            ObservationOnlyTxLeaseSnapshotRegistered: true,
            SessionDiagnosticsSnapshotRegistered: true,
            RadioOccupancySnapshotRegistered: true,
            WatchdogAggregateSnapshotRegistered: true,
            BoundedCollectionWindowRegistered: true,
            MissingPrerequisitesFailClosedRegistered: true,
            TxLeaseAdmissionClosureEvidenceRegistered: true,
            ConfigurationBackupEvidenceRegistered: false,
            MigrationExecutionEvidenceRegistered: false,
            ServiceControlEvidenceRegistered: false,
            HealthVerificationEvidenceRegistered: false,
            RollbackEvidenceRegistered: false,
            OperatorApprovalEvidenceRegistered: false,
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

    public VerifiedReleaseActivationEvidenceCollectionDiagnostics Snapshot { get; }

    internal async Task<VerifiedReleaseActivationEvidenceCollectionReport>
        CollectAsync(
            VerifiedReleaseActivationPlanCompositionResult planResult,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planResult);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsEligiblePlanResult(planResult))
        {
            return VerifiedReleaseActivationEvidenceCollectionReport.Failure(
                VerifiedReleaseActivationEvidenceCollectionFailureCode
                    .ActivationPlanNotEligible,
                "A successful non-mutating verified activation plan is required.",
                planResult);
        }

        VerifiedReleaseActivationPlan? plan = planResult.Plan;
        if (plan is null)
        {
            return VerifiedReleaseActivationEvidenceCollectionReport.Failure(
                VerifiedReleaseActivationEvidenceCollectionFailureCode
                    .ActivationPlanUnavailable,
                "The successful activation plan does not retain its internal verified plan.",
                planResult);
        }
        if (!MatchesPlanResult(planResult, plan))
        {
            return VerifiedReleaseActivationEvidenceCollectionReport.Failure(
                VerifiedReleaseActivationEvidenceCollectionFailureCode
                    .ActivationPlanMismatch,
                "Activation plan metadata does not match its public summary.",
                planResult);
        }

        DateTimeOffset startedAt = m_timeProvider.GetUtcNow();
        ReleaseStatusReadResult? beforeStatus;
        ReleaseStatusReadResult? afterStatus;
        VerifiedReleaseActivationLeaseQuiescenceObservation? leaseQuiescence;
        IReadOnlyList<TxLease>? leases;
        IReadOnlyList<RadioSessionDiagnostics>? sessions;
        StationTxIndependentWatchdogAggregate? watchdogs;
        try
        {
            beforeStatus = await m_statusReader(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            leaseQuiescence = m_leaseQuiescenceReader(plan);
            leases = leaseQuiescence.ActiveTxLeases;
            sessions = m_sessionSnapshotReader();
            watchdogs = m_watchdogSnapshotReader();
            cancellationToken.ThrowIfCancellationRequested();
            afterStatus = await m_statusReader(cancellationToken);
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
            return VerifiedReleaseActivationEvidenceCollectionReport.Failure(
                VerifiedReleaseActivationEvidenceCollectionFailureCode
                    .EvidenceSourceUnavailable,
                "One or more authoritative activation evidence sources could not be read.",
                planResult);
        }

        DateTimeOffset completedAt = m_timeProvider.GetUtcNow();
        if (completedAt < startedAt ||
            completedAt - startedAt > MaximumCollectionDuration)
        {
            return VerifiedReleaseActivationEvidenceCollectionReport.Failure(
                VerifiedReleaseActivationEvidenceCollectionFailureCode
                    .CollectionWindowExceeded,
                "Activation evidence collection exceeded its bounded freshness window.",
                planResult);
        }
        if (beforeStatus is null ||
            afterStatus is null ||
            leaseQuiescence is null ||
            leases is null ||
            sessions is null ||
            watchdogs is null)
        {
            return VerifiedReleaseActivationEvidenceCollectionReport.Failure(
                VerifiedReleaseActivationEvidenceCollectionFailureCode
                    .EvidenceMalformed,
                "An authoritative activation evidence source returned no snapshot.",
                planResult);
        }
        if (!EquivalentStatus(beforeStatus, afterStatus))
        {
            return VerifiedReleaseActivationEvidenceCollectionReport.Failure(
                VerifiedReleaseActivationEvidenceCollectionFailureCode
                    .ReleaseStatusDrift,
                "Release status changed while activation evidence was being collected.",
                planResult);
        }
        if (!ValidateCollectedShape(leases, sessions, watchdogs))
        {
            return VerifiedReleaseActivationEvidenceCollectionReport.Failure(
                VerifiedReleaseActivationEvidenceCollectionFailureCode
                    .EvidenceMalformed,
                "Collected activation evidence was oversized, duplicated, or malformed.",
                planResult);
        }

        VerifiedReleaseActivationSessionEvidence[] sessionEvidence;
        try
        {
            sessionEvidence = sessions
                .Select(m_sessionEvidenceCapture)
                .ToArray();
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or OverflowException)
        {
            return VerifiedReleaseActivationEvidenceCollectionReport.Failure(
                VerifiedReleaseActivationEvidenceCollectionFailureCode
                    .EvidenceMalformed,
                "Session diagnostics could not be converted into bounded activation evidence.",
                planResult);
        }

        ReleaseStatusReadResult frozenStatus = afterStatus with
        {
            AvailableReleaseIdentities =
                afterStatus.AvailableReleaseIdentities.ToArray()
        };
        bool migrationReady = !plan.MigrationRequired;
        bool serviceControlReady =
            plan.RestartServiceCount == 0 && !plan.RestartHost;
        VerifiedReleaseActivationReadinessEvidence evidence = new(
            startedAt,
            frozenStatus,
            leaseQuiescence.AdmissionClosed,
            leases.ToArray(),
            sessionEvidence,
            watchdogs,
            ConfigurationBackupReady: false,
            migrationReady,
            serviceControlReady,
            HealthVerificationReady: false,
            RollbackReady: false,
            OperatorApproved: false);
        VerifiedReleaseActivationEvidenceCollection collection = new(
            plan,
            evidence);
        return VerifiedReleaseActivationEvidenceCollectionReport.Success(
            planResult,
            collection);
    }

    private static Func<CancellationToken, Task<ReleaseStatusReadResult>>
        CreateStatusReader(ReleaseInstallationStatusReader statusReader)
    {
        ArgumentNullException.ThrowIfNull(statusReader);
        return statusReader.ReadAsync;
    }

    private static Func<
        VerifiedReleaseActivationPlan,
        VerifiedReleaseActivationLeaseQuiescenceObservation>
        CreateLeaseQuiescenceReader(
            VerifiedReleaseActivationLeaseQuiescenceBoundary leaseQuiescence)
    {
        ArgumentNullException.ThrowIfNull(leaseQuiescence);
        return leaseQuiescence.Observe;
    }

    private static Func<
        VerifiedReleaseActivationPlan,
        VerifiedReleaseActivationLeaseQuiescenceObservation>
        CreateLeaseQuiescenceReader(
            Func<IReadOnlyList<TxLease>> leaseSnapshotReader)
    {
        ArgumentNullException.ThrowIfNull(leaseSnapshotReader);
        return _ => new VerifiedReleaseActivationLeaseQuiescenceObservation(
            AdmissionClosed: false,
            leaseSnapshotReader());
    }

    private static Func<IReadOnlyList<RadioSessionDiagnostics>>
        CreateSessionSnapshotReader(RadioSessionRegistry radioSessions)
    {
        ArgumentNullException.ThrowIfNull(radioSessions);
        return radioSessions.GetDiagnostics;
    }

    private static Func<StationTxIndependentWatchdogAggregate>
        CreateWatchdogSnapshotReader(
            StationTxIndependentWatchdogRegistry independentWatchdogs)
    {
        ArgumentNullException.ThrowIfNull(independentWatchdogs);
        return () => independentWatchdogs.Snapshot;
    }

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

    private static bool ValidateCollectedShape(
        IReadOnlyList<TxLease> leases,
        IReadOnlyList<RadioSessionDiagnostics> sessions,
        StationTxIndependentWatchdogAggregate watchdogs)
    {
        if (leases.Count >
                VerifiedReleaseActivationReadinessEvaluator.MaximumLeaseCount ||
            sessions.Count >
                VerifiedReleaseActivationReadinessEvaluator.MaximumSessionCount ||
            watchdogs.SessionCount < 0 ||
            watchdogs.RunningProcessCount < 0 ||
            watchdogs.ConnectedProcessCount < 0 ||
            watchdogs.RegisteredIdentityCount < 0 ||
            watchdogs.ArmedProcessCount < 0 ||
            watchdogs.ReconciliationRequiredCount < 0 ||
            watchdogs.RestartCount < 0 ||
            watchdogs.UnkeyAttemptCount < 0 ||
            watchdogs.SessionCount >
                VerifiedReleaseActivationReadinessEvaluator.MaximumSessionCount ||
            watchdogs.RunningProcessCount > watchdogs.SessionCount ||
            watchdogs.ConnectedProcessCount > watchdogs.SessionCount ||
            watchdogs.RegisteredIdentityCount > watchdogs.SessionCount ||
            watchdogs.ArmedProcessCount > watchdogs.SessionCount ||
            watchdogs.ReconciliationRequiredCount > watchdogs.SessionCount)
        {
            return false;
        }

        HashSet<string> leaseIds = new(StringComparer.Ordinal);
        foreach (TxLease lease in leases)
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
        foreach (RadioSessionDiagnostics session in sessions)
        {
            if (session is null ||
                !ValidIdentifier(session.SessionId, 128) ||
                !ValidIdentifier(session.RadioId, 128) ||
                !sessionIds.Add(session.SessionId) ||
                session.TxOccupancy is null)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidIdentifier(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 &&
            normalized.Length <= maximumLength &&
            string.Equals(value, normalized, StringComparison.Ordinal) &&
            normalized.All(character =>
                !char.IsControl(character) && !char.IsWhiteSpace(character));
    }
}
