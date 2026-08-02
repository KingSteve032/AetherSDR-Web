namespace AetherSDR.Web.Radio;

public sealed record StationTxProductionActivationCompositionDiagnostics(
    bool Registered,
    bool ConfigurationInterlockAttached,
    bool ActivationPlanAttached,
    bool ActivationBindingAttached,
    bool ReadinessEvaluationAttached,
    bool ActivationRequested,
    bool ConfigurationValid,
    bool ActivationPlanAvailable,
    bool ActivationPlanApplied,
    bool ActivationBindingApplied,
    bool ActivationAvailable,
    string Reason,
    StationTxProductionActivationConfigurationDiagnostics Configuration,
    StationTxProductionActivationPlanDiagnostics Plan,
    StationTxProductionActivationBindingDiagnostics Binding,
    StationTxProductionReadinessDiagnostics Readiness);

/// <summary>
/// Read-only production TX activation composition. It aggregates the current
/// station-owned configuration interlock, immutable activation plan, per-session
/// binding, and dynamic readiness prerequisites into one typed diagnostic
/// snapshot. It owns no operator intent, command, lease, transport, watchdog,
/// browser, or radio operation.
/// </summary>
internal sealed class StationTxProductionActivationComposition
{
    private readonly Func<
        StationTxProductionActivationConfigurationDiagnostics>
        m_resolveConfiguration;
    private readonly Func<StationTxProductionActivationPlanDiagnostics>
        m_resolvePlan;
    private readonly Func<StationTxProductionActivationBindingDiagnostics>
        m_resolveBinding;
    private readonly Func<StationTxProductionReadinessInputs> m_resolveInputs;

    public StationTxProductionActivationComposition(
        Func<StationTxProductionActivationConfigurationDiagnostics>
            resolveConfiguration,
        Func<StationTxProductionActivationPlanDiagnostics> resolvePlan,
        Func<StationTxProductionActivationBindingDiagnostics> resolveBinding,
        Func<StationTxProductionReadinessInputs> resolveInputs)
    {
        ArgumentNullException.ThrowIfNull(resolveConfiguration);
        ArgumentNullException.ThrowIfNull(resolvePlan);
        ArgumentNullException.ThrowIfNull(resolveBinding);
        ArgumentNullException.ThrowIfNull(resolveInputs);
        m_resolveConfiguration = resolveConfiguration;
        m_resolvePlan = resolvePlan;
        m_resolveBinding = resolveBinding;
        m_resolveInputs = resolveInputs;
    }

    public StationTxProductionActivationCompositionDiagnostics Snapshot
    {
        get
        {
            StationTxProductionActivationConfigurationDiagnostics configuration =
                m_resolveConfiguration() ??
                throw new InvalidOperationException(
                    "Production TX activation configuration was unavailable.");
            StationTxProductionActivationPlanDiagnostics plan =
                m_resolvePlan() ??
                throw new InvalidOperationException(
                    "Production TX activation plan was unavailable.");
            StationTxProductionActivationBindingDiagnostics binding =
                m_resolveBinding() ??
                throw new InvalidOperationException(
                    "Production TX activation binding was unavailable.");
            StationTxProductionReadinessInputs inputs =
                m_resolveInputs() ??
                throw new InvalidOperationException(
                    "Production TX readiness inputs were unavailable.");
            StationTxProductionReadinessDiagnostics readiness =
                StationTxProductionReadinessPolicy.Evaluate(inputs);

            bool planAttached =
                plan.Registered &&
                plan.ConfigurationInterlockAttached;
            bool planMatchesConfiguration =
                plan.ActivationRequested == configuration.ActivationRequested &&
                plan.ConfigurationValid == configuration.ConfigurationValid;
            bool planAvailable =
                planAttached &&
                planMatchesConfiguration &&
                plan.PlanAvailable;
            bool bindingAttached =
                binding.Registered &&
                binding.ActivationPlanAttached;
            bool bindingMatchesPlan =
                binding.PlanAvailable == planAvailable &&
                (!binding.BindingApplied || BindingMatchesPlan(
                    binding.Binding,
                    plan.Plan));
            bool bindingApplied =
                planAvailable &&
                bindingAttached &&
                bindingMatchesPlan &&
                binding.BindingApplied;
            bool available = bindingApplied && readiness.Ready;
            string reason = !configuration.ActivationRequested ||
                !configuration.ConfigurationValid
                    ? configuration.Reason
                    : !planAttached
                        ? "activation-plan-unattached"
                        : !planMatchesConfiguration
                            ? "activation-plan-configuration-mismatch"
                            : !planAvailable
                                ? plan.Reason
                                : !bindingAttached
                                    ? "activation-binding-unattached"
                                    : !bindingMatchesPlan
                                        ? "activation-binding-plan-mismatch"
                                        : !bindingApplied
                                            ? binding.Reason
                                            : readiness.Reason;

            return new StationTxProductionActivationCompositionDiagnostics(
                Registered: true,
                ConfigurationInterlockAttached: configuration.Registered,
                ActivationPlanAttached: plan.Registered,
                ActivationBindingAttached: binding.Registered,
                ReadinessEvaluationAttached: true,
                configuration.ActivationRequested,
                configuration.ConfigurationValid,
                ActivationPlanAvailable: planAvailable,
                ActivationPlanApplied: bindingApplied,
                ActivationBindingApplied: bindingApplied,
                ActivationAvailable: available,
                reason,
                configuration,
                plan,
                binding,
                readiness);
        }
    }

    private static bool BindingMatchesPlan(
        StationTxProductionActivationBinding binding,
        StationTxProductionActivationPlan plan) =>
        binding.CommandBoundaryEnabled == plan.CommandBoundaryEnabled &&
        binding.CommandGateTransmitEnabled == plan.CommandGateTransmitEnabled &&
        binding.BrowserTransactionIngressExecutionEnabled ==
            plan.BrowserTransactionIngressExecutionEnabled &&
        binding.BrowserKeyingCapabilityEnabled ==
            plan.BrowserKeyingCapabilityEnabled;
}
