namespace AetherSDR.Web.Radio;

public sealed record StationTxProductionActivationCompositionDiagnostics(
    bool Registered,
    bool ConfigurationInterlockAttached,
    bool ReadinessEvaluationAttached,
    bool ActivationRequested,
    bool ConfigurationValid,
    bool ActivationAvailable,
    string Reason,
    StationTxProductionActivationConfigurationDiagnostics Configuration,
    StationTxProductionReadinessDiagnostics Readiness);

/// <summary>
/// Read-only production TX activation composition. It aggregates the current
/// station-owned configuration interlock and dynamic readiness prerequisites
/// into one typed diagnostic snapshot. It has no activation, authorization,
/// lease, browser, command, transport, or configuration mutation method. A
/// ready snapshot therefore proves only that reviewed infrastructure is
/// attached; it is never operator intent or TX authority.
/// </summary>
internal sealed class StationTxProductionActivationComposition
{
    private readonly Func<
        StationTxProductionActivationConfigurationDiagnostics>
        m_resolveConfiguration;
    private readonly Func<StationTxProductionReadinessInputs> m_resolveInputs;

    public StationTxProductionActivationComposition(
        Func<StationTxProductionActivationConfigurationDiagnostics>
            resolveConfiguration,
        Func<StationTxProductionReadinessInputs> resolveInputs)
    {
        ArgumentNullException.ThrowIfNull(resolveConfiguration);
        ArgumentNullException.ThrowIfNull(resolveInputs);
        m_resolveConfiguration = resolveConfiguration;
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
            StationTxProductionReadinessInputs inputs =
                m_resolveInputs() ??
                throw new InvalidOperationException(
                    "Production TX readiness inputs were unavailable.");
            StationTxProductionReadinessDiagnostics readiness =
                StationTxProductionReadinessPolicy.Evaluate(inputs);
            bool available =
                configuration.ActivationRequested &&
                configuration.ConfigurationValid &&
                readiness.Ready;
            string reason = !configuration.ActivationRequested ||
                !configuration.ConfigurationValid
                    ? configuration.Reason
                    : readiness.Reason;
            return new StationTxProductionActivationCompositionDiagnostics(
                Registered: true,
                ConfigurationInterlockAttached: configuration.Registered,
                ReadinessEvaluationAttached: true,
                configuration.ActivationRequested,
                configuration.ConfigurationValid,
                ActivationAvailable: available,
                reason,
                configuration,
                readiness);
        }
    }
}
