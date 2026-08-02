namespace AetherSDR.Web.Radio;

public sealed record StationTxProductionActivationBinding(
    bool CommandBoundaryEnabled,
    bool CommandGateTransmitEnabled,
    bool BrowserTransactionIngressExecutionEnabled,
    bool BrowserKeyingCapabilityEnabled);

public sealed record StationTxProductionActivationBindingDiagnostics(
    bool Registered,
    bool ActivationPlanAttached,
    bool PlanAvailable,
    bool SessionEligible,
    bool BindingApplied,
    string Reason,
    StationTxProductionActivationBinding Binding);

/// <summary>
/// Converts the reviewed all-or-nothing activation plan into one immutable
/// per-session binding. It performs no command, browser, lease, watchdog, or
/// radio operation. A binding is applied only for a local FLEX session whose
/// transmit and browser-lease configuration remain explicitly enabled.
/// </summary>
internal static class StationTxProductionActivationBinder
{
    private static readonly StationTxProductionActivationBinding Disabled =
        new(
            CommandBoundaryEnabled: false,
            CommandGateTransmitEnabled: false,
            BrowserTransactionIngressExecutionEnabled: false,
            BrowserKeyingCapabilityEnabled: false);

    public static StationTxProductionActivationBindingDiagnostics Bind(
        StationTxProductionActivationPlanDiagnostics plan,
        bool localFlexSessionEligible,
        bool allowTransmitConfigured,
        bool browserTxLeaseConfigured)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.Registered)
        {
            return DisabledResult(plan, "activation-plan-unregistered");
        }
        if (!plan.PlanAvailable)
        {
            return DisabledResult(plan, plan.Reason);
        }

        ValidateAtomicPlan(plan.Plan);
        bool sessionEligible =
            localFlexSessionEligible &&
            allowTransmitConfigured &&
            browserTxLeaseConfigured;
        if (!localFlexSessionEligible)
        {
            return DisabledResult(plan, "local-flex-session-required");
        }
        if (!allowTransmitConfigured)
        {
            return DisabledResult(plan, "transmit-disabled");
        }
        if (!browserTxLeaseConfigured)
        {
            return DisabledResult(plan, "browser-tx-lease-disabled");
        }

        return new StationTxProductionActivationBindingDiagnostics(
            Registered: true,
            ActivationPlanAttached: true,
            PlanAvailable: true,
            SessionEligible: sessionEligible,
            BindingApplied: true,
            Reason: "activation-binding-applied",
            new StationTxProductionActivationBinding(
                plan.Plan.CommandBoundaryEnabled,
                plan.Plan.CommandGateTransmitEnabled,
                plan.Plan.BrowserTransactionIngressExecutionEnabled,
                plan.Plan.BrowserKeyingCapabilityEnabled));
    }

    private static StationTxProductionActivationBindingDiagnostics DisabledResult(
        StationTxProductionActivationPlanDiagnostics plan,
        string reason) =>
        new(
            Registered: true,
            ActivationPlanAttached: plan.Registered,
            PlanAvailable: plan.PlanAvailable,
            SessionEligible: false,
            BindingApplied: false,
            Reason: reason,
            Disabled);

    private static void ValidateAtomicPlan(
        StationTxProductionActivationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        bool[] switches =
        [
            plan.CommandBoundaryEnabled,
            plan.CommandGateTransmitEnabled,
            plan.BrowserTransactionIngressExecutionEnabled,
            plan.BrowserKeyingCapabilityEnabled
        ];
        if (switches.Any(value => value) && switches.Any(value => !value))
        {
            throw new InvalidOperationException(
                "Production TX activation plan contained a partial runtime switch set.");
        }
        if (switches.All(value => !value))
        {
            throw new InvalidOperationException(
                "An available production TX activation plan must enable the complete runtime switch set.");
        }
    }
}
