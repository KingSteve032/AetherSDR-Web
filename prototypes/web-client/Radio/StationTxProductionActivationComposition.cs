namespace AetherSDR.Web.Radio;

public sealed record StationTxProductionActivationCompositionDiagnostics(
    bool Registered,
    bool ReadinessEvaluationAttached,
    bool ActivationAvailable,
    string Reason,
    StationTxProductionReadinessDiagnostics Readiness);

/// <summary>
/// Read-only production TX activation composition. It aggregates the current
/// station-owned readiness prerequisites into one typed diagnostic snapshot.
/// It has no activation, authorization, lease, browser, command, transport, or
/// configuration mutation method. A ready snapshot therefore proves only that
/// reviewed infrastructure is attached; it is never operator intent or TX
/// authority.
/// </summary>
internal sealed class StationTxProductionActivationComposition
{
    private readonly Func<StationTxProductionReadinessInputs> m_resolveInputs;

    public StationTxProductionActivationComposition(
        Func<StationTxProductionReadinessInputs> resolveInputs)
    {
        ArgumentNullException.ThrowIfNull(resolveInputs);
        m_resolveInputs = resolveInputs;
    }

    public StationTxProductionActivationCompositionDiagnostics Snapshot
    {
        get
        {
            StationTxProductionReadinessInputs inputs =
                m_resolveInputs() ??
                throw new InvalidOperationException(
                    "Production TX readiness inputs were unavailable.");
            StationTxProductionReadinessDiagnostics readiness =
                StationTxProductionReadinessPolicy.Evaluate(inputs);
            return new StationTxProductionActivationCompositionDiagnostics(
                Registered: true,
                ReadinessEvaluationAttached: true,
                ActivationAvailable: readiness.Ready,
                Reason: readiness.Reason,
                readiness);
        }
    }
}
