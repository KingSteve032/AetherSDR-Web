using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationLeaseQuiescenceFailureCode
{
    None = 0,
    ActivationPlanNotEligible = 1,
    ActivationPlanUnavailable = 2,
    ActivationPlanMismatch = 3,
    QuiescencePlanNotEligible = 4,
    QuiescencePlanUnavailable = 5,
    QuiescencePlanMismatch = 6,
    DifferentTransactionActive = 7,
    AdmissionClosureRejected = 8,
    AdmissionReopenRejected = 9
}

public sealed record VerifiedReleaseActivationLeaseQuiescenceReport(
    bool Succeeded,
    VerifiedReleaseActivationLeaseQuiescenceFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    bool AdmissionClosureComposed,
    bool AdmissionClosed,
    int ObservedTxLeaseCount,
    bool DrainSatisfied,
    bool TxLeaseMutationPerformed,
    bool RadioAuthoritativeIdleProven,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationLeaseQuiescencePlan? Plan { get; init; }

    internal static VerifiedReleaseActivationLeaseQuiescenceReport Failure(
        VerifiedReleaseActivationLeaseQuiescenceFailureCode failureCode,
        string message,
        VerifiedReleaseActivationPlanCompositionResult? activationPlan = null,
        VerifiedReleaseActivationLeaseQuiescenceReport? quiescence = null) =>
        new(
            false,
            failureCode,
            message,
            quiescence?.SetupRevision ?? activationPlan?.SetupRevision,
            quiescence?.InstalledReleaseIdentity ??
                activationPlan?.InstalledReleaseIdentity ?? string.Empty,
            quiescence?.TargetReleaseIdentity ??
                activationPlan?.TargetReleaseIdentity ?? string.Empty,
            AdmissionClosureComposed: false,
            AdmissionClosed: false,
            ObservedTxLeaseCount: 0,
            DrainSatisfied: false,
            TxLeaseMutationPerformed: false,
            RadioAuthoritativeIdleProven: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationLeaseQuiescenceReport Composed(
        VerifiedReleaseActivationLeaseQuiescencePlan plan) =>
        new(
            true,
            VerifiedReleaseActivationLeaseQuiescenceFailureCode.None,
            "A transaction-bound TX-lease admission closure plan was composed without changing lease admission or lease state.",
            plan.ActivationPlan.SetupRevision,
            plan.ActivationPlan.InstalledReleaseIdentity,
            plan.ActivationPlan.TargetReleaseIdentity,
            AdmissionClosureComposed: true,
            AdmissionClosed: false,
            ObservedTxLeaseCount: 0,
            DrainSatisfied: false,
            TxLeaseMutationPerformed: false,
            RadioAuthoritativeIdleProven: false,
            ActivationAuthorized: false)
        {
            Plan = plan
        };

    internal static VerifiedReleaseActivationLeaseQuiescenceReport Observed(
        VerifiedReleaseActivationLeaseQuiescencePlan plan,
        TxLeaseAdmissionClosureObservation observation,
        string message) =>
        new(
            true,
            VerifiedReleaseActivationLeaseQuiescenceFailureCode.None,
            message,
            plan.ActivationPlan.SetupRevision,
            plan.ActivationPlan.InstalledReleaseIdentity,
            plan.ActivationPlan.TargetReleaseIdentity,
            AdmissionClosureComposed: true,
            observation.AdmissionClosed,
            observation.Leases.Count,
            observation.Drained,
            TxLeaseMutationPerformed: false,
            RadioAuthoritativeIdleProven: false,
            ActivationAuthorized: false)
        {
            Plan = plan
        };
}

public sealed record VerifiedReleaseActivationLeaseQuiescenceDiagnostics(
    bool Registered,
    bool ActivationPlanInputRegistered,
    bool TransactionBoundPlanCompositionRegistered,
    bool AdmissionClosureAuthorityRegistered,
    bool ActiveClosureStateRegistered,
    bool AcquisitionSuppressionRegistered,
    bool RenewalSuppressionRegistered,
    bool ObservationOnlyLeaseSnapshotRegistered,
    bool DrainEvaluationRegistered,
    bool ExistingLeaseForceReleaseRegistered,
    bool TxLeaseMutationRegistered,
    bool RadioIdleInferenceRegistered,
    bool RadioCommandRegistered,
    bool WatchdogMutationRegistered,
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
    bool CommandCallerRegistered,
    bool TxCallerRegistered);

public sealed record VerifiedReleaseActivationLeaseQuiescenceStateDiagnostics(
    bool AdmissionClosureActive,
    bool ExactTransactionBoundClosureActive,
    int ObservedTxLeaseCount,
    bool DrainSatisfied,
    bool TxLeaseMutationAuthorityAvailable,
    bool RadioAuthoritativeIdleProven,
    bool ActivationAuthorized);

internal sealed class VerifiedReleaseActivationLeaseQuiescencePlan
{
    internal VerifiedReleaseActivationLeaseQuiescencePlan(
        VerifiedReleaseActivationPlan activationPlan)
    {
        ActivationPlan = activationPlan ??
            throw new ArgumentNullException(nameof(activationPlan));
        AdmissionAuthority = new TxLeaseAdmissionClosureAuthority();
    }

    internal VerifiedReleaseActivationPlan ActivationPlan { get; }
    internal TxLeaseAdmissionClosureAuthority AdmissionAuthority { get; }
}

internal sealed record VerifiedReleaseActivationLeaseQuiescenceObservation(
    bool AdmissionClosed,
    IReadOnlyList<TxLease> ActiveTxLeases)
{
    internal bool Drained => AdmissionClosed && ActiveTxLeases.Count == 0;
}

/// <summary>
/// Composes and owns one exact-plan TX-lease admission closure transaction. The
/// boundary shares the lease manager's serialization lock so closure cannot race
/// with acquisition or renewal. Existing leases are never force-released; release,
/// validation, ordinary expiry, radio-authoritative occupancy, and independent
/// watchdog safety remain separate. There is no public operational caller and no
/// activation authority.
/// </summary>
public sealed class VerifiedReleaseActivationLeaseQuiescenceBoundary
{
    private readonly object m_gate = new();
    private readonly TxLeaseManager m_leases;
    private VerifiedReleaseActivationLeaseQuiescencePlan? m_activePlan;

    public VerifiedReleaseActivationLeaseQuiescenceBoundary(TxLeaseManager leases)
    {
        m_leases = leases ?? throw new ArgumentNullException(nameof(leases));
        Snapshot = new VerifiedReleaseActivationLeaseQuiescenceDiagnostics(
            Registered: true,
            ActivationPlanInputRegistered: true,
            TransactionBoundPlanCompositionRegistered: true,
            AdmissionClosureAuthorityRegistered: true,
            ActiveClosureStateRegistered: true,
            AcquisitionSuppressionRegistered: true,
            RenewalSuppressionRegistered: true,
            ObservationOnlyLeaseSnapshotRegistered: true,
            DrainEvaluationRegistered: true,
            ExistingLeaseForceReleaseRegistered: false,
            TxLeaseMutationRegistered: false,
            RadioIdleInferenceRegistered: false,
            RadioCommandRegistered: false,
            WatchdogMutationRegistered: false,
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
            CommandCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationLeaseQuiescenceDiagnostics Snapshot { get; }

    public VerifiedReleaseActivationLeaseQuiescenceStateDiagnostics State
    {
        get
        {
            lock (m_gate)
            {
                TxLeaseAdmissionClosureObservation observation =
                    m_leases.ObserveAdmissionClosure(
                        m_activePlan?.AdmissionAuthority);
                bool anyClosure = observation.AdmissionClosed ||
                    observation.DifferentClosureActive;
                return new VerifiedReleaseActivationLeaseQuiescenceStateDiagnostics(
                    anyClosure,
                    observation.AdmissionClosed,
                    observation.Leases.Count,
                    observation.Drained,
                    TxLeaseMutationAuthorityAvailable: false,
                    RadioAuthoritativeIdleProven: false,
                    ActivationAuthorized: false);
            }
        }
    }

    internal VerifiedReleaseActivationLeaseQuiescenceReport Compose(
        VerifiedReleaseActivationPlanCompositionResult activationPlanResult)
    {
        ArgumentNullException.ThrowIfNull(activationPlanResult);

        if (!IsEligibleActivationPlan(activationPlanResult))
        {
            return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                VerifiedReleaseActivationLeaseQuiescenceFailureCode
                    .ActivationPlanNotEligible,
                "A successful non-mutating verified activation plan is required.",
                activationPlanResult);
        }

        VerifiedReleaseActivationPlan? activationPlan = activationPlanResult.Plan;
        if (activationPlan is null)
        {
            return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                VerifiedReleaseActivationLeaseQuiescenceFailureCode
                    .ActivationPlanUnavailable,
                "The successful activation plan does not retain its exact internal plan.",
                activationPlanResult);
        }
        if (!MatchesActivationPlan(activationPlanResult, activationPlan))
        {
            return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                VerifiedReleaseActivationLeaseQuiescenceFailureCode
                    .ActivationPlanMismatch,
                "Activation plan metadata does not match its exact internal plan.",
                activationPlanResult);
        }

        return VerifiedReleaseActivationLeaseQuiescenceReport.Composed(
            new VerifiedReleaseActivationLeaseQuiescencePlan(activationPlan));
    }

    internal VerifiedReleaseActivationLeaseQuiescenceReport CloseAdmission(
        VerifiedReleaseActivationLeaseQuiescenceReport quiescencePlan)
    {
        ArgumentNullException.ThrowIfNull(quiescencePlan);

        if (!IsEligibleQuiescencePlan(quiescencePlan))
        {
            return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                VerifiedReleaseActivationLeaseQuiescenceFailureCode
                    .QuiescencePlanNotEligible,
                "A successful non-mutating lease-quiescence composition is required.",
                quiescence: quiescencePlan);
        }

        VerifiedReleaseActivationLeaseQuiescencePlan? plan = quiescencePlan.Plan;
        if (plan is null)
        {
            return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                VerifiedReleaseActivationLeaseQuiescenceFailureCode
                    .QuiescencePlanUnavailable,
                "The lease-quiescence composition does not retain its exact transaction token.",
                quiescence: quiescencePlan);
        }
        if (!MatchesQuiescencePlan(quiescencePlan, plan))
        {
            return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                VerifiedReleaseActivationLeaseQuiescenceFailureCode
                    .QuiescencePlanMismatch,
                "Lease-quiescence metadata does not match its exact transaction token.",
                quiescence: quiescencePlan);
        }

        lock (m_gate)
        {
            if (m_activePlan is not null &&
                !ReferenceEquals(m_activePlan, plan))
            {
                return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                    VerifiedReleaseActivationLeaseQuiescenceFailureCode
                        .DifferentTransactionActive,
                    "A different verified activation lease-quiescence transaction is already active.",
                    quiescence: quiescencePlan);
            }

            if (!m_leases.TryCloseAdmission(
                    plan.AdmissionAuthority,
                    out TxLeaseAdmissionClosureObservation observation))
            {
                return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                    VerifiedReleaseActivationLeaseQuiescenceFailureCode
                        .AdmissionClosureRejected,
                    "TX-lease admission closure was rejected because another authority is active.",
                    quiescence: quiescencePlan);
            }

            m_activePlan = plan;
            return VerifiedReleaseActivationLeaseQuiescenceReport.Observed(
                plan,
                observation,
                observation.Drained
                    ? "TX-lease admission is closed and the stored lease set is drained; radio-authoritative idle and all other activation prerequisites remain separately required."
                    : "TX-lease admission is closed; existing leases must release or expire through their normal safety lifecycle before drain is satisfied.");
        }
    }

    internal VerifiedReleaseActivationLeaseQuiescenceReport EvaluateDrain(
        VerifiedReleaseActivationLeaseQuiescenceReport quiescencePlan)
    {
        ArgumentNullException.ThrowIfNull(quiescencePlan);
        VerifiedReleaseActivationLeaseQuiescencePlan? plan = quiescencePlan.Plan;
        if (!IsEligibleQuiescencePlan(quiescencePlan) ||
            plan is null ||
            !MatchesQuiescencePlan(quiescencePlan, plan))
        {
            return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                VerifiedReleaseActivationLeaseQuiescenceFailureCode
                    .QuiescencePlanNotEligible,
                "An exact successful lease-quiescence transaction token is required.",
                quiescence: quiescencePlan);
        }

        lock (m_gate)
        {
            if (!ReferenceEquals(m_activePlan, plan))
            {
                return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                    VerifiedReleaseActivationLeaseQuiescenceFailureCode
                        .DifferentTransactionActive,
                    "The supplied lease-quiescence transaction is not the active closure authority.",
                    quiescence: quiescencePlan);
            }

            TxLeaseAdmissionClosureObservation observation =
                m_leases.ObserveAdmissionClosure(plan.AdmissionAuthority);
            if (!observation.AdmissionClosed)
            {
                return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                    VerifiedReleaseActivationLeaseQuiescenceFailureCode
                        .AdmissionClosureRejected,
                    "The exact transaction no longer owns TX-lease admission closure.",
                    quiescence: quiescencePlan);
            }

            return VerifiedReleaseActivationLeaseQuiescenceReport.Observed(
                plan,
                observation,
                observation.Drained
                    ? "TX-lease admission remains closed and the stored lease set is drained; no radio-idle or activation authority is inferred."
                    : "TX-lease admission remains closed and existing leases have not yet drained.");
        }
    }

    internal VerifiedReleaseActivationLeaseQuiescenceReport ReleaseAdmission(
        VerifiedReleaseActivationLeaseQuiescenceReport quiescencePlan)
    {
        ArgumentNullException.ThrowIfNull(quiescencePlan);
        VerifiedReleaseActivationLeaseQuiescencePlan? plan = quiescencePlan.Plan;
        if (!IsEligibleQuiescencePlan(quiescencePlan) ||
            plan is null ||
            !MatchesQuiescencePlan(quiescencePlan, plan))
        {
            return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                VerifiedReleaseActivationLeaseQuiescenceFailureCode
                    .QuiescencePlanNotEligible,
                "An exact successful lease-quiescence transaction token is required to reopen admission.",
                quiescence: quiescencePlan);
        }

        lock (m_gate)
        {
            if (!ReferenceEquals(m_activePlan, plan) ||
                !m_leases.TryOpenAdmission(
                    plan.AdmissionAuthority,
                    out TxLeaseAdmissionClosureObservation observation))
            {
                return VerifiedReleaseActivationLeaseQuiescenceReport.Failure(
                    VerifiedReleaseActivationLeaseQuiescenceFailureCode
                        .AdmissionReopenRejected,
                    "TX-lease admission could not be reopened because the exact closure authority is no longer active.",
                    quiescence: quiescencePlan);
            }

            m_activePlan = null;
            return VerifiedReleaseActivationLeaseQuiescenceReport.Observed(
                plan,
                observation,
                "TX-lease admission was reopened by the exact activation transaction without changing any lease or radio state.");
        }
    }

    internal VerifiedReleaseActivationLeaseQuiescenceObservation Observe(
        VerifiedReleaseActivationPlan activationPlan)
    {
        ArgumentNullException.ThrowIfNull(activationPlan);
        lock (m_gate)
        {
            VerifiedReleaseActivationLeaseQuiescencePlan? active = m_activePlan;
            TxLeaseAdmissionClosureObservation observation =
                m_leases.ObserveAdmissionClosure(
                    active is not null &&
                    ReferenceEquals(active.ActivationPlan, activationPlan)
                        ? active.AdmissionAuthority
                        : null);
            return new VerifiedReleaseActivationLeaseQuiescenceObservation(
                observation.AdmissionClosed,
                observation.Leases.ToArray());
        }
    }

    private static bool IsEligibleActivationPlan(
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

    private static bool MatchesActivationPlan(
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
        string.Equals(result.TargetVersion, plan.TargetVersion, StringComparison.Ordinal) &&
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

    private static bool IsEligibleQuiescencePlan(
        VerifiedReleaseActivationLeaseQuiescenceReport result) =>
        result.Succeeded &&
        result.FailureCode ==
            VerifiedReleaseActivationLeaseQuiescenceFailureCode.None &&
        result.SetupRevision is > 0 &&
        !string.IsNullOrEmpty(result.InstalledReleaseIdentity) &&
        !string.IsNullOrEmpty(result.TargetReleaseIdentity) &&
        result.AdmissionClosureComposed &&
        !result.TxLeaseMutationPerformed &&
        !result.RadioAuthoritativeIdleProven &&
        !result.ActivationAuthorized;

    private static bool MatchesQuiescencePlan(
        VerifiedReleaseActivationLeaseQuiescenceReport result,
        VerifiedReleaseActivationLeaseQuiescencePlan plan) =>
        result.SetupRevision == plan.ActivationPlan.SetupRevision &&
        string.Equals(
            result.InstalledReleaseIdentity,
            plan.ActivationPlan.InstalledReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            result.TargetReleaseIdentity,
            plan.ActivationPlan.TargetReleaseIdentity,
            StringComparison.Ordinal) &&
        result.AdmissionClosureComposed &&
        !result.TxLeaseMutationPerformed &&
        !result.RadioAuthoritativeIdleProven &&
        !result.ActivationAuthorized;
}
