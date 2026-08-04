using System.Collections.ObjectModel;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationHealthVerificationPlanFailureCode
{
    None = 0,
    ServiceControlPlanNotEligible = 1,
    ServiceControlPlanUnavailable = 2,
    ServiceControlPlanMismatch = 3,
    HealthContractInvalid = 4
}

public enum VerifiedReleaseActivationHealthContractKind
{
    LoopbackHttp = 1,
    FreshBrokerLink = 2
}

public sealed record VerifiedReleaseActivationHealthVerificationPlanReport(
    bool Succeeded,
    VerifiedReleaseActivationHealthVerificationPlanFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int RestartServiceCount,
    bool HostRestartRequired,
    bool ServiceControlRequired,
    bool HealthVerificationRequired,
    int HealthTargetCount,
    int UnitActivityCheckCount,
    int LoopbackHttpCheckCount,
    int FreshBrokerLinkCheckCount,
    bool ExactServiceControlPlanBound,
    bool ExactActivationPlanBound,
    bool CompleteServiceCoverageBound,
    bool FixedHealthContractMappingBound,
    bool DeterministicOrderingBound,
    bool LoopbackOnlyHttpBound,
    bool CanonicalGatewayHostBindingRequired,
    bool BoundedDeadlinePlanningBound,
    bool PostSwitchVerificationPlanned,
    bool PostHostRestartVerificationPlanned,
    bool NetworkRequestPerformed,
    bool ProcessInvocationPerformed,
    bool SystemdCommandPerformed,
    bool JournalReadPerformed,
    bool HealthEvidenceProduced,
    bool ServiceHealthReady,
    bool CurrentPointerChanged,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationHealthVerificationPlan? Plan { get; init; }

    internal static VerifiedReleaseActivationHealthVerificationPlanReport Failure(
        VerifiedReleaseActivationHealthVerificationPlanFailureCode failureCode,
        string message,
        VerifiedReleaseActivationServiceControlPlanReport? serviceControl = null) =>
        new(
            false,
            failureCode,
            message,
            serviceControl?.SetupRevision,
            serviceControl?.InstalledReleaseIdentity ?? string.Empty,
            serviceControl?.TargetReleaseIdentity ?? string.Empty,
            serviceControl?.RestartServiceCount ?? 0,
            serviceControl?.HostRestartRequired ?? false,
            serviceControl?.ServiceControlRequired ?? false,
            HealthVerificationRequired: true,
            HealthTargetCount: 0,
            UnitActivityCheckCount: 0,
            LoopbackHttpCheckCount: 0,
            FreshBrokerLinkCheckCount: 0,
            ExactServiceControlPlanBound: false,
            ExactActivationPlanBound: false,
            CompleteServiceCoverageBound: false,
            FixedHealthContractMappingBound: false,
            DeterministicOrderingBound: false,
            LoopbackOnlyHttpBound: false,
            CanonicalGatewayHostBindingRequired: false,
            BoundedDeadlinePlanningBound: false,
            PostSwitchVerificationPlanned: false,
            PostHostRestartVerificationPlanned: false,
            NetworkRequestPerformed: false,
            ProcessInvocationPerformed: false,
            SystemdCommandPerformed: false,
            JournalReadPerformed: false,
            HealthEvidenceProduced: false,
            ServiceHealthReady: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationHealthVerificationPlanReport Success(
        VerifiedReleaseActivationHealthVerificationPlan plan) =>
        new(
            true,
            VerifiedReleaseActivationHealthVerificationPlanFailureCode.None,
            plan.HostRestartRequired
                ? "The exact activation transaction was composed into bounded post-host-restart service-health contracts without executing any probe or changing host state."
                : "The exact activation transaction was composed into bounded post-switch service-health contracts without executing any probe or changing service state.",
            plan.ActivationPlan.SetupRevision,
            plan.ActivationPlan.InstalledReleaseIdentity,
            plan.ActivationPlan.TargetReleaseIdentity,
            plan.ActivationPlan.RestartServiceCount,
            plan.HostRestartRequired,
            plan.ServiceControlPlan.ServiceControlRequired,
            HealthVerificationRequired: true,
            plan.Targets.Count,
            UnitActivityCheckCount:
                plan.Targets.Count(target => target.RequireUnitActive),
            LoopbackHttpCheckCount:
                plan.Targets.Count(target =>
                    target.ContractKind ==
                        VerifiedReleaseActivationHealthContractKind.LoopbackHttp),
            FreshBrokerLinkCheckCount:
                plan.Targets.Count(target =>
                    target.ContractKind ==
                        VerifiedReleaseActivationHealthContractKind
                            .FreshBrokerLink),
            ExactServiceControlPlanBound: true,
            ExactActivationPlanBound: true,
            CompleteServiceCoverageBound: true,
            FixedHealthContractMappingBound: true,
            DeterministicOrderingBound: true,
            LoopbackOnlyHttpBound: true,
            CanonicalGatewayHostBindingRequired:
                plan.Targets.Any(target =>
                    target.ServiceRole ==
                        VerifiedReleaseActivationServiceRole.GatewayWeb &&
                    target.RequireCanonicalHostHeader),
            BoundedDeadlinePlanningBound: true,
            PostSwitchVerificationPlanned: true,
            PostHostRestartVerificationPlanned: plan.HostRestartRequired,
            NetworkRequestPerformed: false,
            ProcessInvocationPerformed: false,
            SystemdCommandPerformed: false,
            JournalReadPerformed: false,
            HealthEvidenceProduced: false,
            ServiceHealthReady: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            Plan = plan
        };
}

public sealed record VerifiedReleaseActivationHealthVerificationPlanDiagnostics(
    bool Registered,
    bool ServiceControlPlanInputRegistered,
    bool ExactServiceControlPlanBindingRegistered,
    bool ExactActivationPlanBindingRegistered,
    bool CompleteServiceCoverageRegistered,
    bool UnitActivityPlanningRegistered,
    bool LoopbackHttpPlanningRegistered,
    bool FreshBrokerLinkPlanningRegistered,
    bool CanonicalGatewayHostBindingRegistered,
    bool FixedHealthContractMappingRegistered,
    bool DeterministicOrderingRegistered,
    bool BoundedDeadlinePlanningRegistered,
    bool PostSwitchPhasePlanningRegistered,
    bool PostHostRestartPhasePlanningRegistered,
    bool NetworkRequestRegistered,
    bool SocketCallerRegistered,
    bool HttpClientCallerRegistered,
    bool ProcessInvocationRegistered,
    bool SystemdCommandRegistered,
    bool JournalReadRegistered,
    bool HealthEvidenceRegistered,
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
    bool RollbackCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed record VerifiedReleaseActivationHealthVerificationTarget(
    int Sequence,
    VerifiedReleaseActivationServiceRole ServiceRole,
    string UnitIdentity,
    VerifiedReleaseActivationHealthContractKind ContractKind,
    int? LoopbackPort,
    string HealthPath,
    int? ExpectedHttpStatusCode,
    bool RequireCanonicalHostHeader,
    bool RequireUnitActive,
    bool RequireFreshObservation,
    int DeadlineMilliseconds);

internal sealed class VerifiedReleaseActivationHealthVerificationPlan
{
    private readonly ReadOnlyCollection<
        VerifiedReleaseActivationHealthVerificationTarget> m_targets;

    internal VerifiedReleaseActivationHealthVerificationPlan(
        VerifiedReleaseActivationServiceControlPlan serviceControlPlan,
        IReadOnlyList<VerifiedReleaseActivationHealthVerificationTarget> targets)
    {
        ServiceControlPlan = serviceControlPlan ??
            throw new ArgumentNullException(nameof(serviceControlPlan));
        m_targets = Array.AsReadOnly(
            (targets ?? throw new ArgumentNullException(nameof(targets)))
                .ToArray());
    }

    internal VerifiedReleaseActivationServiceControlPlan ServiceControlPlan
    {
        get;
    }

    internal VerifiedReleaseActivationPlan ActivationPlan =>
        ServiceControlPlan.ActivationPlan;

    internal IReadOnlyList<VerifiedReleaseActivationHealthVerificationTarget>
        Targets => m_targets;

    internal bool HostRestartRequired => ActivationPlan.RestartHost;
}

/// <summary>
/// Pure fail-closed composition of one exact service-control plan into the
/// post-switch health contracts required before activation can be considered
/// healthy. The four repository-owned service roles are always covered. The
/// station engine, broker, and gateway use fixed loopback-only HTTP health
/// contracts; the gateway additionally requires the runtime canonical host
/// binding. The agent uses a fresh broker-link observation contract. Every
/// target also requires its fixed unit to be active, and every deadline is
/// bounded. No socket, HTTP request, process, systemd command, journal read,
/// evidence, current-pointer mutation, activation authority, operational
/// caller, radio action, watchdog action, command action, lease action, or
/// transmit action is added.
/// </summary>
public sealed class VerifiedReleaseActivationHealthVerificationPlanComposer
{
    internal const string HealthPath = "/healthz";
    internal const int ExpectedHttpStatusCode = 200;
    internal const int GatewayWebLoopbackPort = 5080;
    internal const int BrokerLoopbackPort = 5090;
    internal const int StationEngineLoopbackPort = 5081;
    internal const int GatewayWebDeadlineMilliseconds = 45_000;
    internal const int BrokerDeadlineMilliseconds = 30_000;
    internal const int AetherRemoteAgentDeadlineMilliseconds = 60_000;
    internal const int StationEngineDeadlineMilliseconds = 45_000;
    internal const int MaximumDeadlineMilliseconds = 60_000;

    private static readonly HealthDescriptor[] VerificationOrder =
    [
        new(
            VerifiedReleaseActivationServiceRole.StationEngine,
            VerifiedReleaseActivationServiceControlPlanComposer
                .StationEngineUnitIdentity,
            VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
            StationEngineLoopbackPort,
            HealthPath,
            ExpectedHttpStatusCode,
            RequireCanonicalHostHeader: false,
            RequireFreshObservation: true,
            StationEngineDeadlineMilliseconds),
        new(
            VerifiedReleaseActivationServiceRole.Broker,
            VerifiedReleaseActivationServiceControlPlanComposer
                .BrokerUnitIdentity,
            VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
            BrokerLoopbackPort,
            HealthPath,
            ExpectedHttpStatusCode,
            RequireCanonicalHostHeader: false,
            RequireFreshObservation: true,
            BrokerDeadlineMilliseconds),
        new(
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
            VerifiedReleaseActivationServiceControlPlanComposer
                .AetherRemoteAgentUnitIdentity,
            VerifiedReleaseActivationHealthContractKind.FreshBrokerLink,
            LoopbackPort: null,
            HealthPath: string.Empty,
            ExpectedHttpStatusCode: null,
            RequireCanonicalHostHeader: false,
            RequireFreshObservation: true,
            AetherRemoteAgentDeadlineMilliseconds),
        new(
            VerifiedReleaseActivationServiceRole.GatewayWeb,
            VerifiedReleaseActivationServiceControlPlanComposer
                .GatewayWebUnitIdentity,
            VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
            GatewayWebLoopbackPort,
            HealthPath,
            ExpectedHttpStatusCode,
            RequireCanonicalHostHeader: true,
            RequireFreshObservation: true,
            GatewayWebDeadlineMilliseconds)
    ];

    public VerifiedReleaseActivationHealthVerificationPlanComposer()
    {
        Snapshot =
            new VerifiedReleaseActivationHealthVerificationPlanDiagnostics(
                Registered: true,
                ServiceControlPlanInputRegistered: true,
                ExactServiceControlPlanBindingRegistered: true,
                ExactActivationPlanBindingRegistered: true,
                CompleteServiceCoverageRegistered: true,
                UnitActivityPlanningRegistered: true,
                LoopbackHttpPlanningRegistered: true,
                FreshBrokerLinkPlanningRegistered: true,
                CanonicalGatewayHostBindingRegistered: true,
                FixedHealthContractMappingRegistered: true,
                DeterministicOrderingRegistered: true,
                BoundedDeadlinePlanningRegistered: true,
                PostSwitchPhasePlanningRegistered: true,
                PostHostRestartPhasePlanningRegistered: true,
                NetworkRequestRegistered: false,
                SocketCallerRegistered: false,
                HttpClientCallerRegistered: false,
                ProcessInvocationRegistered: false,
                SystemdCommandRegistered: false,
                JournalReadRegistered: false,
                HealthEvidenceRegistered: false,
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
                RollbackCallerRegistered: false,
                RadioCallerRegistered: false,
                WatchdogCallerRegistered: false,
                CommandCallerRegistered: false,
                LeaseCallerRegistered: false,
                TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationHealthVerificationPlanDiagnostics Snapshot
    {
        get;
    }

    public VerifiedReleaseActivationHealthVerificationPlanReport Compose(
        VerifiedReleaseActivationServiceControlPlanReport serviceControlResult)
    {
        ArgumentNullException.ThrowIfNull(serviceControlResult);
        if (!IsEligibleServiceControlResult(serviceControlResult))
        {
            return VerifiedReleaseActivationHealthVerificationPlanReport.Failure(
                VerifiedReleaseActivationHealthVerificationPlanFailureCode
                    .ServiceControlPlanNotEligible,
                "A successful exact non-executing service-control plan is required.",
                serviceControlResult);
        }

        VerifiedReleaseActivationServiceControlPlan? serviceControlPlan =
            serviceControlResult.Plan;
        if (serviceControlPlan is null)
        {
            return VerifiedReleaseActivationHealthVerificationPlanReport.Failure(
                VerifiedReleaseActivationHealthVerificationPlanFailureCode
                    .ServiceControlPlanUnavailable,
                "The successful service-control report does not retain its exact internal plan.",
                serviceControlResult);
        }
        if (!MatchesServiceControlResult(
                serviceControlResult,
                serviceControlPlan) ||
            !ValidateServiceControlPlan(serviceControlPlan))
        {
            return VerifiedReleaseActivationHealthVerificationPlanReport.Failure(
                VerifiedReleaseActivationHealthVerificationPlanFailureCode
                    .ServiceControlPlanMismatch,
                "Service-control metadata or actions do not match the exact activation plan.",
                serviceControlResult);
        }

        VerifiedReleaseActivationHealthVerificationTarget[] targets =
            VerificationOrder.Select((descriptor, index) =>
                new VerifiedReleaseActivationHealthVerificationTarget(
                    index + 1,
                    descriptor.Role,
                    descriptor.UnitIdentity,
                    descriptor.ContractKind,
                    descriptor.LoopbackPort,
                    descriptor.HealthPath,
                    descriptor.ExpectedHttpStatusCode,
                    descriptor.RequireCanonicalHostHeader,
                    RequireUnitActive: true,
                    descriptor.RequireFreshObservation,
                    descriptor.DeadlineMilliseconds))
                .ToArray();

        VerifiedReleaseActivationHealthVerificationPlan plan = new(
            serviceControlPlan,
            targets);
        if (!ValidateHealthPlan(plan))
        {
            return VerifiedReleaseActivationHealthVerificationPlanReport.Failure(
                VerifiedReleaseActivationHealthVerificationPlanFailureCode
                    .HealthContractInvalid,
                "The exact post-switch health contract plan is incomplete or unsafe.",
                serviceControlResult);
        }

        return VerifiedReleaseActivationHealthVerificationPlanReport.Success(plan);
    }

    private static bool IsEligibleServiceControlResult(
        VerifiedReleaseActivationServiceControlPlanReport result) =>
        result.Succeeded &&
        result.FailureCode ==
            VerifiedReleaseActivationServiceControlPlanFailureCode.None &&
        result.SetupRevision is > 0 &&
        !string.IsNullOrEmpty(result.InstalledReleaseIdentity) &&
        !string.IsNullOrEmpty(result.TargetReleaseIdentity) &&
        result.RestartServiceCount is >= 0 and <= 4 &&
        result.ServiceControlRequired ==
            (result.RestartServiceCount > 0 || result.HostRestartRequired) &&
        result.NoOpServiceControlResolved == !result.ServiceControlRequired &&
        result.StopActionCount is >= 0 and <= 4 &&
        result.StartActionCount is >= 0 and <= 4 &&
        result.HostRestartActionCount is >= 0 and <= 1 &&
        result.ExactActivationPlanBound &&
        result.FixedServiceMappingBound &&
        result.DeterministicOrderingBound &&
        !result.ProcessInvocationPerformed &&
        !result.SystemdCommandPerformed &&
        !result.HostRestartPerformed &&
        result.ServiceControlReady == !result.ServiceControlRequired &&
        !result.CurrentPointerChanged &&
        !result.ActivationAuthorized;

    private static bool MatchesServiceControlResult(
        VerifiedReleaseActivationServiceControlPlanReport result,
        VerifiedReleaseActivationServiceControlPlan plan)
    {
        VerifiedReleaseActivationPlan activation = plan.ActivationPlan;
        return result.SetupRevision == activation.SetupRevision &&
            string.Equals(
                result.InstalledReleaseIdentity,
                activation.InstalledReleaseIdentity,
                StringComparison.Ordinal) &&
            string.Equals(
                result.TargetReleaseIdentity,
                activation.TargetReleaseIdentity,
                StringComparison.Ordinal) &&
            result.RestartServiceCount == activation.RestartServiceCount &&
            result.HostRestartRequired == activation.RestartHost &&
            result.ServiceControlRequired == plan.ServiceControlRequired &&
            result.StopActionCount == plan.StopActions.Count &&
            result.StartActionCount == plan.StartActions.Count &&
            result.HostRestartActionCount == plan.HostRestartActions.Count &&
            result.PreSwitchStopPlanned == (plan.StopActions.Count > 0) &&
            result.PostSwitchStartPlanned == (plan.StartActions.Count > 0) &&
            result.HostRestartPlanned ==
                (plan.HostRestartActions.Count == 1) &&
            result.HostRestartSupersedesServiceActions ==
                (activation.RestartHost &&
                 plan.StopActions.Count == 0 &&
                 plan.StartActions.Count == 0) &&
            activation.SetupRevision > 0 &&
            activation.Packages.Count == 4 &&
            activation.TargetConfigurationSchemaVersion > 0 &&
            activation.MigrationKind is ReleaseMigrationKind.None or
                ReleaseMigrationKind.Required &&
            activation.TxLeaseAdmissionClosureRequired &&
            activation.RadioAuthoritativeIdleRequired &&
            activation.WatchdogsDisarmedRequired &&
            activation.ConfigurationBackupRequired &&
            activation.AtomicCurrentPointerSwitchRequired &&
            activation.ServiceHealthVerificationRequired &&
            activation.AutomaticRollbackRequired &&
            activation.OperatorApprovalRequired;
    }

    private static bool ValidateServiceControlPlan(
        VerifiedReleaseActivationServiceControlPlan plan)
    {
        VerifiedReleaseActivationPlan activation = plan.ActivationPlan;
        if (activation.RestartServiceCount is < 0 or > 4 ||
            activation.RestartHost &&
                (!activation.RestartGatewayWeb ||
                 !activation.RestartBroker ||
                 !activation.RestartAetherRemoteAgent ||
                 !activation.RestartStationEngine) ||
            activation.MigrationRequired && !activation.RestartGatewayWeb ||
            !HasAllPackageRoles(activation))
        {
            return false;
        }

        if (activation.RestartHost)
        {
            return plan.StopActions.Count == 0 &&
                plan.StartActions.Count == 0 &&
                plan.HostRestartActions.Count == 1 &&
                plan.HostRestartActions[0].Sequence == 1 &&
                plan.HostRestartActions[0].Kind ==
                    VerifiedReleaseActivationServiceControlActionKind.RestartHost &&
                plan.HostRestartActions[0].ServiceRole is null &&
                string.Equals(
                    plan.HostRestartActions[0].UnitIdentity,
                    VerifiedReleaseActivationServiceControlPlanComposer
                        .HostRestartIdentity,
                    StringComparison.Ordinal);
        }

        VerifiedReleaseActivationServiceRole[] expectedStops =
            ExpectedRestartRoles(activation, stopOrder: true);
        VerifiedReleaseActivationServiceRole[] expectedStarts =
            ExpectedRestartRoles(activation, stopOrder: false);
        return plan.HostRestartActions.Count == 0 &&
            ValidateServiceActions(
                plan.StopActions,
                expectedStops,
                VerifiedReleaseActivationServiceControlActionKind.Stop) &&
            ValidateServiceActions(
                plan.StartActions,
                expectedStarts,
                VerifiedReleaseActivationServiceControlActionKind.Start);
    }

    private static bool HasAllPackageRoles(
        VerifiedReleaseActivationPlan activation)
    {
        ReleasePackageRole[] expected =
        [
            ReleasePackageRole.GatewayWeb,
            ReleasePackageRole.Broker,
            ReleasePackageRole.AetherRemoteAgent,
            ReleasePackageRole.StationEngine
        ];
        return activation.Packages.Count == expected.Length &&
            activation.Packages.Select(package => package.Role)
                .OrderBy(role => role)
                .SequenceEqual(expected.OrderBy(role => role));
    }

    private static VerifiedReleaseActivationServiceRole[] ExpectedRestartRoles(
        VerifiedReleaseActivationPlan activation,
        bool stopOrder)
    {
        (VerifiedReleaseActivationServiceRole Role, bool Required)[] roles =
            stopOrder
                ?
                [
                    (VerifiedReleaseActivationServiceRole.GatewayWeb,
                        activation.RestartGatewayWeb),
                    (VerifiedReleaseActivationServiceRole.Broker,
                        activation.RestartBroker),
                    (VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
                        activation.RestartAetherRemoteAgent),
                    (VerifiedReleaseActivationServiceRole.StationEngine,
                        activation.RestartStationEngine)
                ]
                :
                [
                    (VerifiedReleaseActivationServiceRole.StationEngine,
                        activation.RestartStationEngine),
                    (VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
                        activation.RestartAetherRemoteAgent),
                    (VerifiedReleaseActivationServiceRole.Broker,
                        activation.RestartBroker),
                    (VerifiedReleaseActivationServiceRole.GatewayWeb,
                        activation.RestartGatewayWeb)
                ];
        return roles.Where(item => item.Required)
            .Select(item => item.Role)
            .ToArray();
    }

    private static bool ValidateServiceActions(
        IReadOnlyList<VerifiedReleaseActivationServiceControlAction> actions,
        IReadOnlyList<VerifiedReleaseActivationServiceRole> expectedRoles,
        VerifiedReleaseActivationServiceControlActionKind expectedKind)
    {
        if (actions.Count != expectedRoles.Count)
        {
            return false;
        }
        for (int index = 0; index < actions.Count; index++)
        {
            VerifiedReleaseActivationServiceControlAction action = actions[index];
            VerifiedReleaseActivationServiceRole expectedRole =
                expectedRoles[index];
            if (action.Sequence != index + 1 ||
                action.Kind != expectedKind ||
                action.ServiceRole != expectedRole ||
                !string.Equals(
                    action.UnitIdentity,
                    ExpectedUnitIdentity(expectedRole),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidateHealthPlan(
        VerifiedReleaseActivationHealthVerificationPlan plan)
    {
        if (plan.Targets.Count != VerificationOrder.Length ||
            plan.Targets.Select(target => target.ServiceRole)
                .Distinct().Count() != VerificationOrder.Length ||
            plan.Targets.Count(target => target.RequireUnitActive) !=
                VerificationOrder.Length ||
            plan.Targets.Count(target =>
                target.ContractKind ==
                    VerifiedReleaseActivationHealthContractKind.LoopbackHttp) != 3 ||
            plan.Targets.Count(target =>
                target.ContractKind ==
                    VerifiedReleaseActivationHealthContractKind.FreshBrokerLink) !=
                1)
        {
            return false;
        }

        for (int index = 0; index < VerificationOrder.Length; index++)
        {
            HealthDescriptor expected = VerificationOrder[index];
            VerifiedReleaseActivationHealthVerificationTarget target =
                plan.Targets[index];
            if (target.Sequence != index + 1 ||
                target.ServiceRole != expected.Role ||
                !string.Equals(
                    target.UnitIdentity,
                    expected.UnitIdentity,
                    StringComparison.Ordinal) ||
                target.ContractKind != expected.ContractKind ||
                target.LoopbackPort != expected.LoopbackPort ||
                !string.Equals(
                    target.HealthPath,
                    expected.HealthPath,
                    StringComparison.Ordinal) ||
                target.ExpectedHttpStatusCode != expected.ExpectedHttpStatusCode ||
                target.RequireCanonicalHostHeader !=
                    expected.RequireCanonicalHostHeader ||
                !target.RequireUnitActive ||
                target.RequireFreshObservation !=
                    expected.RequireFreshObservation ||
                target.DeadlineMilliseconds != expected.DeadlineMilliseconds ||
                target.DeadlineMilliseconds is < 1 or >
                    MaximumDeadlineMilliseconds)
            {
                return false;
            }

            if (target.ContractKind ==
                VerifiedReleaseActivationHealthContractKind.LoopbackHttp)
            {
                if (target.LoopbackPort is < 1 or > 65_535 ||
                    !string.Equals(
                        target.HealthPath,
                        HealthPath,
                        StringComparison.Ordinal) ||
                    target.ExpectedHttpStatusCode != ExpectedHttpStatusCode)
                {
                    return false;
                }
            }
            else if (target.LoopbackPort is not null ||
                !string.IsNullOrEmpty(target.HealthPath) ||
                target.ExpectedHttpStatusCode is not null ||
                target.RequireCanonicalHostHeader)
            {
                return false;
            }
        }

        return plan.Targets.Count(target =>
                target.RequireCanonicalHostHeader) == 1 &&
            plan.Targets.Single(target =>
                target.RequireCanonicalHostHeader).ServiceRole ==
                    VerifiedReleaseActivationServiceRole.GatewayWeb;
    }

    private static string ExpectedUnitIdentity(
        VerifiedReleaseActivationServiceRole role) =>
        role switch
        {
            VerifiedReleaseActivationServiceRole.GatewayWeb =>
                VerifiedReleaseActivationServiceControlPlanComposer
                    .GatewayWebUnitIdentity,
            VerifiedReleaseActivationServiceRole.Broker =>
                VerifiedReleaseActivationServiceControlPlanComposer
                    .BrokerUnitIdentity,
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent =>
                VerifiedReleaseActivationServiceControlPlanComposer
                    .AetherRemoteAgentUnitIdentity,
            VerifiedReleaseActivationServiceRole.StationEngine =>
                VerifiedReleaseActivationServiceControlPlanComposer
                    .StationEngineUnitIdentity,
            _ => string.Empty
        };

    private sealed record HealthDescriptor(
        VerifiedReleaseActivationServiceRole Role,
        string UnitIdentity,
        VerifiedReleaseActivationHealthContractKind ContractKind,
        int? LoopbackPort,
        string HealthPath,
        int? ExpectedHttpStatusCode,
        bool RequireCanonicalHostHeader,
        bool RequireFreshObservation,
        int DeadlineMilliseconds);
}
