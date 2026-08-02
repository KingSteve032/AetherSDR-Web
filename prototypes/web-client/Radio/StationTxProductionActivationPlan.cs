namespace AetherSDR.Web.Radio;

/// <summary>
/// Immutable description of the four runtime switches that a later reviewed
/// production activation phase would need to bind atomically. This record is
/// data only. Constructing it cannot enable a boundary, gate, browser ingress,
/// capability, transport, watchdog, or radio command path.
/// </summary>
public sealed record StationTxProductionActivationPlan(
    bool CommandBoundaryEnabled,
    bool CommandGateTransmitEnabled,
    bool BrowserTransactionIngressExecutionEnabled,
    bool BrowserKeyingCapabilityEnabled);

public sealed record StationTxProductionActivationPlanDiagnostics(
    bool Registered,
    bool ConfigurationInterlockAttached,
    bool ActivationRequested,
    bool ConfigurationValid,
    bool PlanAvailable,
    bool PlanApplied,
    string Reason,
    StationTxProductionActivationPlan Plan);

/// <summary>
/// Read-only planner for the all-or-nothing production TX runtime switch set.
/// It consumes only validated activation configuration diagnostics and exposes
/// only a fresh snapshot. Phase 2Y intentionally has no apply, activate,
/// execute, submit, lease, arm, key, unkey, browser, or radio operation.
/// </summary>
internal sealed class StationTxProductionActivationPlanner
{
    private static readonly StationTxProductionActivationPlan DisabledPlan =
        new(
            CommandBoundaryEnabled: false,
            CommandGateTransmitEnabled: false,
            BrowserTransactionIngressExecutionEnabled: false,
            BrowserKeyingCapabilityEnabled: false);

    private static readonly StationTxProductionActivationPlan EnabledPlan =
        new(
            CommandBoundaryEnabled: true,
            CommandGateTransmitEnabled: true,
            BrowserTransactionIngressExecutionEnabled: true,
            BrowserKeyingCapabilityEnabled: true);

    private readonly Func<
        StationTxProductionActivationConfigurationDiagnostics>
        m_resolveConfiguration;

    public StationTxProductionActivationPlanner(
        Func<StationTxProductionActivationConfigurationDiagnostics>
            resolveConfiguration)
    {
        ArgumentNullException.ThrowIfNull(resolveConfiguration);
        m_resolveConfiguration = resolveConfiguration;
    }

    public StationTxProductionActivationPlanDiagnostics Snapshot
    {
        get
        {
            StationTxProductionActivationConfigurationDiagnostics configuration =
                m_resolveConfiguration() ??
                throw new InvalidOperationException(
                    "Production TX activation configuration was unavailable.");
            bool available =
                configuration.ActivationRequested &&
                configuration.ConfigurationValid;
            return new StationTxProductionActivationPlanDiagnostics(
                Registered: true,
                ConfigurationInterlockAttached: configuration.Registered,
                configuration.ActivationRequested,
                configuration.ConfigurationValid,
                PlanAvailable: available,
                PlanApplied: false,
                Reason: available
                    ? "activation-plan-ready-not-applied"
                    : configuration.Reason,
                Plan: available ? EnabledPlan : DisabledPlan);
        }
    }
}
