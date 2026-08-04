using System.Collections.ObjectModel;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationServiceControlPlanFailureCode
{
    None = 0,
    ActivationPlanNotEligible = 1,
    ActivationPlanUnavailable = 2,
    ActivationPlanMismatch = 3,
    RestartDeclarationInvalid = 4
}

public enum VerifiedReleaseActivationServiceRole
{
    GatewayWeb = 1,
    Broker = 2,
    AetherRemoteAgent = 3,
    StationEngine = 4
}

public enum VerifiedReleaseActivationServiceControlActionKind
{
    Stop = 1,
    Start = 2,
    RestartHost = 3
}

public sealed record VerifiedReleaseActivationServiceControlPlanReport(
    bool Succeeded,
    VerifiedReleaseActivationServiceControlPlanFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int RestartServiceCount,
    bool HostRestartRequired,
    bool ServiceControlRequired,
    bool NoOpServiceControlResolved,
    int StopActionCount,
    int StartActionCount,
    int HostRestartActionCount,
    bool ExactActivationPlanBound,
    bool FixedServiceMappingBound,
    bool DeterministicOrderingBound,
    bool PreSwitchStopPlanned,
    bool PostSwitchStartPlanned,
    bool HostRestartPlanned,
    bool HostRestartSupersedesServiceActions,
    bool ProcessInvocationPerformed,
    bool SystemdCommandPerformed,
    bool HostRestartPerformed,
    bool ServiceControlReady,
    bool CurrentPointerChanged,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationServiceControlPlan? Plan { get; init; }

    internal static VerifiedReleaseActivationServiceControlPlanReport Failure(
        VerifiedReleaseActivationServiceControlPlanFailureCode failureCode,
        string message,
        VerifiedReleaseActivationPlanCompositionResult? activationPlan = null) =>
        new(
            false,
            failureCode,
            message,
            activationPlan?.SetupRevision,
            activationPlan?.InstalledReleaseIdentity ?? string.Empty,
            activationPlan?.TargetReleaseIdentity ?? string.Empty,
            activationPlan?.RestartServiceCount ?? 0,
            activationPlan?.HostRestartRequired ?? false,
            ServiceControlRequired:
                (activationPlan?.RestartServiceCount ?? 0) > 0 ||
                (activationPlan?.HostRestartRequired ?? false),
            NoOpServiceControlResolved: false,
            StopActionCount: 0,
            StartActionCount: 0,
            HostRestartActionCount: 0,
            ExactActivationPlanBound: false,
            FixedServiceMappingBound: false,
            DeterministicOrderingBound: false,
            PreSwitchStopPlanned: false,
            PostSwitchStartPlanned: false,
            HostRestartPlanned: false,
            HostRestartSupersedesServiceActions: false,
            ProcessInvocationPerformed: false,
            SystemdCommandPerformed: false,
            HostRestartPerformed: false,
            ServiceControlReady: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationServiceControlPlanReport Success(
        VerifiedReleaseActivationServiceControlPlan plan) =>
        new(
            true,
            VerifiedReleaseActivationServiceControlPlanFailureCode.None,
            plan.ServiceControlRequired
                ? plan.HostRestartRequired
                    ? "The exact signed host-restart declaration was composed into one callerless post-switch host-restart action without invoking a process or changing host state."
                    : "The exact signed service-restart declaration was composed into deterministic pre-switch stop and post-switch start actions without invoking a process or changing service state."
                : "The exact signed release requires no service or host restart; service control resolved as a no-op.",
            plan.ActivationPlan.SetupRevision,
            plan.ActivationPlan.InstalledReleaseIdentity,
            plan.ActivationPlan.TargetReleaseIdentity,
            plan.ActivationPlan.RestartServiceCount,
            plan.HostRestartRequired,
            plan.ServiceControlRequired,
            NoOpServiceControlResolved: !plan.ServiceControlRequired,
            plan.StopActions.Count,
            plan.StartActions.Count,
            plan.HostRestartActions.Count,
            ExactActivationPlanBound: true,
            FixedServiceMappingBound: true,
            DeterministicOrderingBound: true,
            PreSwitchStopPlanned: plan.StopActions.Count > 0,
            PostSwitchStartPlanned: plan.StartActions.Count > 0,
            HostRestartPlanned: plan.HostRestartActions.Count == 1,
            HostRestartSupersedesServiceActions:
                plan.HostRestartRequired &&
                plan.StopActions.Count == 0 &&
                plan.StartActions.Count == 0,
            ProcessInvocationPerformed: false,
            SystemdCommandPerformed: false,
            HostRestartPerformed: false,
            ServiceControlReady: !plan.ServiceControlRequired,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            Plan = plan
        };
}

public sealed record VerifiedReleaseActivationServiceControlPlanDiagnostics(
    bool Registered,
    bool ActivationPlanInputRegistered,
    bool ExactActivationPlanBindingRegistered,
    bool NoOpResolutionRegistered,
    bool ServiceRestartPlanningRegistered,
    bool HostRestartPlanningRegistered,
    bool FixedServiceMappingRegistered,
    bool DeterministicStopOrderingRegistered,
    bool DeterministicStartOrderingRegistered,
    bool HostRestartSupersessionRegistered,
    bool PreSwitchPhasePlanningRegistered,
    bool PostSwitchPhasePlanningRegistered,
    bool ProcessInvocationRegistered,
    bool SystemdCommandRegistered,
    bool HostRestartExecutionRegistered,
    bool ServiceControlEvidenceRegistered,
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
    bool HealthProbeCallerRegistered,
    bool RollbackCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed record VerifiedReleaseActivationServiceControlAction(
    int Sequence,
    VerifiedReleaseActivationServiceControlActionKind Kind,
    VerifiedReleaseActivationServiceRole? ServiceRole,
    string UnitIdentity);

internal sealed class VerifiedReleaseActivationServiceControlPlan
{
    private readonly ReadOnlyCollection<
        VerifiedReleaseActivationServiceControlAction> m_stopActions;
    private readonly ReadOnlyCollection<
        VerifiedReleaseActivationServiceControlAction> m_startActions;
    private readonly ReadOnlyCollection<
        VerifiedReleaseActivationServiceControlAction> m_hostRestartActions;

    internal VerifiedReleaseActivationServiceControlPlan(
        VerifiedReleaseActivationPlan activationPlan,
        IReadOnlyList<VerifiedReleaseActivationServiceControlAction> stopActions,
        IReadOnlyList<VerifiedReleaseActivationServiceControlAction> startActions,
        IReadOnlyList<VerifiedReleaseActivationServiceControlAction>
            hostRestartActions)
    {
        ActivationPlan = activationPlan ??
            throw new ArgumentNullException(nameof(activationPlan));
        m_stopActions = Array.AsReadOnly(
            (stopActions ?? throw new ArgumentNullException(nameof(stopActions)))
                .ToArray());
        m_startActions = Array.AsReadOnly(
            (startActions ?? throw new ArgumentNullException(nameof(startActions)))
                .ToArray());
        m_hostRestartActions = Array.AsReadOnly(
            (hostRestartActions ??
                throw new ArgumentNullException(nameof(hostRestartActions)))
                .ToArray());
    }

    internal VerifiedReleaseActivationPlan ActivationPlan { get; }
    internal IReadOnlyList<VerifiedReleaseActivationServiceControlAction>
        StopActions => m_stopActions;
    internal IReadOnlyList<VerifiedReleaseActivationServiceControlAction>
        StartActions => m_startActions;
    internal IReadOnlyList<VerifiedReleaseActivationServiceControlAction>
        HostRestartActions => m_hostRestartActions;
    internal bool HostRestartRequired => ActivationPlan.RestartHost;
    internal bool ServiceControlRequired =>
        ActivationPlan.RestartServiceCount > 0 || ActivationPlan.RestartHost;
}

/// <summary>
/// Pure fail-closed composition of one exact verified activation plan into a
/// deterministic service-control transaction plan. Only the repository-owned
/// gateway, broker, AetherRemote agent, and station-engine systemd unit
/// identities are mapped. Non-host restarts become ordered pre-switch stop and
/// post-switch start actions. A signed host restart supersedes those actions and
/// becomes one post-switch host-restart marker. No process, systemd command,
/// host restart, service-control evidence, current-pointer mutation, activation
/// authority, operational caller, radio action, watchdog action, lease action,
/// command action, or transmit action is added.
/// </summary>
public sealed class VerifiedReleaseActivationServiceControlPlanComposer
{
    internal const string GatewayWebUnitIdentity = "aethersdr-web.service";
    internal const string BrokerUnitIdentity = "aetherremote-broker.service";
    internal const string AetherRemoteAgentUnitIdentity =
        "aetherremote-agent.service";
    internal const string StationEngineUnitIdentity =
        "aetherremote-station-engine.service";
    internal const string HostRestartIdentity =
        "aethersdr-host-restart-action";

    private static readonly ServiceDescriptor[] StopOrder =
    [
        new(
            VerifiedReleaseActivationServiceRole.GatewayWeb,
            GatewayWebUnitIdentity,
            static plan => plan.RestartGatewayWeb),
        new(
            VerifiedReleaseActivationServiceRole.Broker,
            BrokerUnitIdentity,
            static plan => plan.RestartBroker),
        new(
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
            AetherRemoteAgentUnitIdentity,
            static plan => plan.RestartAetherRemoteAgent),
        new(
            VerifiedReleaseActivationServiceRole.StationEngine,
            StationEngineUnitIdentity,
            static plan => plan.RestartStationEngine)
    ];

    private static readonly ServiceDescriptor[] StartOrder =
    [
        new(
            VerifiedReleaseActivationServiceRole.StationEngine,
            StationEngineUnitIdentity,
            static plan => plan.RestartStationEngine),
        new(
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
            AetherRemoteAgentUnitIdentity,
            static plan => plan.RestartAetherRemoteAgent),
        new(
            VerifiedReleaseActivationServiceRole.Broker,
            BrokerUnitIdentity,
            static plan => plan.RestartBroker),
        new(
            VerifiedReleaseActivationServiceRole.GatewayWeb,
            GatewayWebUnitIdentity,
            static plan => plan.RestartGatewayWeb)
    ];

    public VerifiedReleaseActivationServiceControlPlanComposer()
    {
        Snapshot = new VerifiedReleaseActivationServiceControlPlanDiagnostics(
            Registered: true,
            ActivationPlanInputRegistered: true,
            ExactActivationPlanBindingRegistered: true,
            NoOpResolutionRegistered: true,
            ServiceRestartPlanningRegistered: true,
            HostRestartPlanningRegistered: true,
            FixedServiceMappingRegistered: true,
            DeterministicStopOrderingRegistered: true,
            DeterministicStartOrderingRegistered: true,
            HostRestartSupersessionRegistered: true,
            PreSwitchPhasePlanningRegistered: true,
            PostSwitchPhasePlanningRegistered: true,
            ProcessInvocationRegistered: false,
            SystemdCommandRegistered: false,
            HostRestartExecutionRegistered: false,
            ServiceControlEvidenceRegistered: false,
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
            HealthProbeCallerRegistered: false,
            RollbackCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationServiceControlPlanDiagnostics Snapshot
    {
        get;
    }

    public VerifiedReleaseActivationServiceControlPlanReport Compose(
        VerifiedReleaseActivationPlanCompositionResult activationPlanResult)
    {
        ArgumentNullException.ThrowIfNull(activationPlanResult);
        if (!IsEligibleActivationPlanResult(activationPlanResult))
        {
            return VerifiedReleaseActivationServiceControlPlanReport.Failure(
                VerifiedReleaseActivationServiceControlPlanFailureCode
                    .ActivationPlanNotEligible,
                "A successful non-mutating exact activation plan is required.",
                activationPlanResult);
        }

        VerifiedReleaseActivationPlan? activationPlan =
            activationPlanResult.Plan;
        if (activationPlan is null)
        {
            return VerifiedReleaseActivationServiceControlPlanReport.Failure(
                VerifiedReleaseActivationServiceControlPlanFailureCode
                    .ActivationPlanUnavailable,
                "The successful activation-plan report does not retain its exact internal plan.",
                activationPlanResult);
        }
        if (!MatchesActivationPlanResult(activationPlanResult, activationPlan))
        {
            return VerifiedReleaseActivationServiceControlPlanReport.Failure(
                VerifiedReleaseActivationServiceControlPlanFailureCode
                    .ActivationPlanMismatch,
                "Activation-plan metadata does not match its exact internal plan.",
                activationPlanResult);
        }
        if (!ValidateRestartDeclaration(activationPlan))
        {
            return VerifiedReleaseActivationServiceControlPlanReport.Failure(
                VerifiedReleaseActivationServiceControlPlanFailureCode
                    .RestartDeclarationInvalid,
                "The exact signed service or host restart declaration is contradictory.",
                activationPlanResult);
        }

        VerifiedReleaseActivationServiceControlAction[] stopActions;
        VerifiedReleaseActivationServiceControlAction[] startActions;
        VerifiedReleaseActivationServiceControlAction[] hostRestartActions;
        if (activationPlan.RestartHost)
        {
            stopActions = [];
            startActions = [];
            hostRestartActions =
            [
                new VerifiedReleaseActivationServiceControlAction(
                    Sequence: 1,
                    VerifiedReleaseActivationServiceControlActionKind.RestartHost,
                    ServiceRole: null,
                    HostRestartIdentity)
            ];
        }
        else
        {
            stopActions = CreateActions(
                activationPlan,
                StopOrder,
                VerifiedReleaseActivationServiceControlActionKind.Stop);
            startActions = CreateActions(
                activationPlan,
                StartOrder,
                VerifiedReleaseActivationServiceControlActionKind.Start);
            hostRestartActions = [];
        }

        VerifiedReleaseActivationServiceControlPlan plan = new(
            activationPlan,
            stopActions,
            startActions,
            hostRestartActions);
        if (!ValidateComposedPlan(plan))
        {
            return VerifiedReleaseActivationServiceControlPlanReport.Failure(
                VerifiedReleaseActivationServiceControlPlanFailureCode
                    .RestartDeclarationInvalid,
                "The exact service-control action plan is incomplete or duplicated.",
                activationPlanResult);
        }

        return VerifiedReleaseActivationServiceControlPlanReport.Success(plan);
    }

    private static VerifiedReleaseActivationServiceControlAction[] CreateActions(
        VerifiedReleaseActivationPlan plan,
        IReadOnlyList<ServiceDescriptor> descriptors,
        VerifiedReleaseActivationServiceControlActionKind kind)
    {
        List<VerifiedReleaseActivationServiceControlAction> actions = [];
        foreach (ServiceDescriptor descriptor in descriptors)
        {
            if (!descriptor.Required(plan))
            {
                continue;
            }
            actions.Add(
                new VerifiedReleaseActivationServiceControlAction(
                    actions.Count + 1,
                    kind,
                    descriptor.Role,
                    descriptor.UnitIdentity));
        }
        return actions.ToArray();
    }

    private static bool IsEligibleActivationPlanResult(
        VerifiedReleaseActivationPlanCompositionResult result) =>
        result.Succeeded &&
        result.FailureCode == VerifiedReleaseActivationPlanFailureCode.None &&
        result.SetupRevision is > 0 &&
        !string.IsNullOrEmpty(result.InstalledReleaseIdentity) &&
        !string.IsNullOrEmpty(result.TargetReleaseIdentity) &&
        !string.IsNullOrEmpty(result.TargetVersion) &&
        result.Architecture is ReleaseManifestArchitecture.LinuxX64 or
            ReleaseManifestArchitecture.LinuxArm64 &&
        result.PackageCount == 4 &&
        result.PublishedBytes > 0 &&
        result.TargetConfigurationSchemaVersion is > 0 &&
        result.MigrationKind is ReleaseMigrationKind.None or
            ReleaseMigrationKind.Required &&
        result.RestartServiceCount is >= 0 and <= 4 &&
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

    private static bool MatchesActivationPlanResult(
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

    private static bool ValidateRestartDeclaration(
        VerifiedReleaseActivationPlan plan)
    {
        if (plan.RestartServiceCount is < 0 or > 4)
        {
            return false;
        }
        if (plan.RestartHost &&
            (!plan.RestartGatewayWeb ||
             !plan.RestartBroker ||
             !plan.RestartAetherRemoteAgent ||
             !plan.RestartStationEngine))
        {
            return false;
        }
        if (plan.MigrationRequired && !plan.RestartGatewayWeb)
        {
            return false;
        }
        return true;
    }

    private static bool ValidateComposedPlan(
        VerifiedReleaseActivationServiceControlPlan plan)
    {
        if (plan.HostRestartRequired)
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
                    HostRestartIdentity,
                    StringComparison.Ordinal);
        }

        if (plan.HostRestartActions.Count != 0 ||
            plan.StopActions.Count != plan.ActivationPlan.RestartServiceCount ||
            plan.StartActions.Count != plan.ActivationPlan.RestartServiceCount)
        {
            return false;
        }

        return ValidateActionList(
                plan.StopActions,
                VerifiedReleaseActivationServiceControlActionKind.Stop) &&
            ValidateActionList(
                plan.StartActions,
                VerifiedReleaseActivationServiceControlActionKind.Start) &&
            plan.StopActions.Select(action => action.ServiceRole)
                .OrderBy(role => role)
                .SequenceEqual(
                    plan.StartActions.Select(action => action.ServiceRole)
                        .OrderBy(role => role)) &&
            plan.StopActions.Select(action => action.UnitIdentity)
                .Distinct(StringComparer.Ordinal).Count() ==
                plan.StopActions.Count &&
            plan.StartActions.Select(action => action.UnitIdentity)
                .Distinct(StringComparer.Ordinal).Count() ==
                plan.StartActions.Count;
    }

    private static bool ValidateActionList(
        IReadOnlyList<VerifiedReleaseActivationServiceControlAction> actions,
        VerifiedReleaseActivationServiceControlActionKind expectedKind)
    {
        for (int index = 0; index < actions.Count; index++)
        {
            VerifiedReleaseActivationServiceControlAction action = actions[index];
            if (action.Sequence != index + 1 ||
                action.Kind != expectedKind ||
                action.ServiceRole is null ||
                !IsExpectedUnit(action.ServiceRole.Value, action.UnitIdentity))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsExpectedUnit(
        VerifiedReleaseActivationServiceRole role,
        string unitIdentity) =>
        role switch
        {
            VerifiedReleaseActivationServiceRole.GatewayWeb => string.Equals(
                unitIdentity,
                GatewayWebUnitIdentity,
                StringComparison.Ordinal),
            VerifiedReleaseActivationServiceRole.Broker => string.Equals(
                unitIdentity,
                BrokerUnitIdentity,
                StringComparison.Ordinal),
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent => string.Equals(
                unitIdentity,
                AetherRemoteAgentUnitIdentity,
                StringComparison.Ordinal),
            VerifiedReleaseActivationServiceRole.StationEngine => string.Equals(
                unitIdentity,
                StationEngineUnitIdentity,
                StringComparison.Ordinal),
            _ => false
        };

    private sealed record ServiceDescriptor(
        VerifiedReleaseActivationServiceRole Role,
        string UnitIdentity,
        Func<VerifiedReleaseActivationPlan, bool> Required);
}
