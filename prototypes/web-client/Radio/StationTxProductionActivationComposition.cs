namespace AetherSDR.Web.Radio;

public sealed record StationTxProductionActivationCompositionDiagnostics(
    bool Registered,
    bool ConfigurationInterlockAttached,
    bool ActivationPlanAttached,
    bool ReadinessEvaluationAttached,
    bool ActivationRequested,
    bool ConfigurationValid,
    bool ActivationPlanAvailable,
    bool ActivationPlanApplied,
    bool ActivationAvailable,
    string Reason,
    StationTxProductionActivationConfigurationDiagnostics Configuration,
    StationTxProductionActivationPlanDiagnostics Plan,
    StationTxProductionReadinessDiagnostics Readiness);

/// <summary>
/// Read-only production TX activation composition. It aggregates the current
/// station-owned configuration interlock, immutable activation plan, and
/// dynamic readiness prerequisites into one typed diagnostic snapshot. It has
/// no activation, authorization, lease, browser, command, transport, or
/// configuration mutation method. A ready plan therefore proves only that the
/// reviewed switch set can be described; it is never applied authority.
/// </summary>
internal sealed class StationTxProductionActivationComposition
{
    private readonly Func<
        StationTxProductionActivationConfigurationDiagnostics>
        m_resolveConfiguration;
    private readonly Func<StationTxProductionActivationPlanDiagnostics>
        m_resolvePlan;
    private readonly Func<StationTxProductionReadinessInputs> m_resolveInputs;

    public StationTxProductionActivationComposition(
        Func<StationTxProductionActivationConfigurationDiagnostics>
            resolveConfiguration,
        Func<StationTxProductionActivationPlanDiagnostics> resolvePlan,
        Func<StationTxProductionReadinessInputs> resolveInputs)
    {
        ArgumentNullException.ThrowIfNull(resolveConfiguration);
        ArgumentNullException.ThrowIfNull(resolvePlan);
        ArgumentNullException.ThrowIfNull(resolveInputs);
        m_resolveConfiguration = resolveConfiguration;
        m_resolvePlan = resolvePlan;
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
            bool planApplied = planAvailable && plan.PlanApplied;
            bool available = planApplied && readiness.Ready;
            string reason = !configuration.ActivationRequested ||
                !configuration.ConfigurationValid
                    ? configuration.Reason
                    : !planAttached
                        ? "activation-plan-unattached"
                        : !planMatchesConfiguration
                            ? "activation-plan-configuration-mismatch"
                            : !planAvailable || !planApplied
                                ? plan.Reason
                                : readiness.Reason;

            return new StationTxProductionActivationCompositionDiagnostics(
                Registered: true,
                ConfigurationInterlockAttached: configuration.Registered,
                ActivationPlanAttached: plan.Registered,
                ReadinessEvaluationAttached: true,
                configuration.ActivationRequested,
                configuration.ConfigurationValid,
                ActivationPlanAvailable: planAvailable,
                ActivationPlanApplied: planApplied,
                ActivationAvailable: available,
                reason,
                configuration,
                plan,
                readiness);
        }
    }
}
