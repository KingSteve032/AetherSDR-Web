namespace AetherSDR.Web.Radio;

internal sealed record StationTxEngineConnectionObservation(
    string EngineInstanceId,
    string LeaseId,
    uint ProtectedClientHandle,
    bool IsConnected);

internal sealed record StationTxEngineConnectionResult(
    bool Success,
    string Code,
    string Message,
    bool SawConnected,
    bool LossSignaled,
    StationTxSafetySnapshot SafetySnapshot);

/// <summary>
/// Private station-local monitor that converts one exact engine connection
/// transition into an ownership-safe safety-supervisor abort. It has no radio
/// command transport and cannot key or unkey by itself.
///
/// A disconnect is actionable only after this monitor observed the matching
/// engine, lease, and FLEX handle connected while the same safety arm was
/// active. Starting while disconnected, stale/mismatched identity, and repeated
/// loss observations never invent ownership or issue a duplicate abort.
/// </summary>
internal sealed class StationTxEngineConnectionMonitor : IAsyncDisposable
{
    private readonly SemaphoreSlim m_gate = new(1, 1);
    private readonly StationTxSafetySupervisor m_supervisor;

    private bool m_sawConnected;
    private bool m_lossSignaled;
    private int m_disposed;

    public StationTxEngineConnectionMonitor(
        StationTxSafetySupervisor supervisor)
    {
        m_supervisor = supervisor ??
            throw new ArgumentNullException(nameof(supervisor));
    }

    public async Task<StationTxEngineConnectionResult> EvaluateAsync(
        StationTxEngineConnectionObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            StationTxSafetySnapshot safety = m_supervisor.Snapshot;
            if (!safety.Active)
            {
                return Result(
                    true,
                    "disarmed",
                    "No active station TX safety arm requires engine connection monitoring.",
                    safety);
            }

            string engineInstanceId = NormalizeIdentifier(
                observation.EngineInstanceId,
                128);
            string leaseId = NormalizeIdentifier(observation.LeaseId, 64);
            if (engineInstanceId.Length == 0 ||
                leaseId.Length == 0 ||
                observation.ProtectedClientHandle == 0 ||
                !string.Equals(
                    safety.EngineInstanceId,
                    engineInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    safety.LeaseId,
                    leaseId,
                    StringComparison.Ordinal) ||
                safety.ProtectedClientHandle !=
                    observation.ProtectedClientHandle)
            {
                return Result(
                    false,
                    "engine_connection_owner_mismatch",
                    "The engine connection observation does not match the exact active safety arm.",
                    safety);
            }

            if (observation.IsConnected)
            {
                if (m_lossSignaled)
                {
                    return Result(
                        false,
                        "engine_connection_loss_already_signaled",
                        "The prior connection loss remains under safety-supervisor reconciliation.",
                        safety);
                }
                m_sawConnected = true;
                return Result(
                    true,
                    "engine_connected",
                    "The exact protected station engine connection is present.",
                    safety);
            }

            if (!m_sawConnected)
            {
                return Result(
                    true,
                    "engine_connection_not_established",
                    "The monitor started while the engine was disconnected; no ownership or unkey action was inferred.",
                    safety);
            }

            if (!m_lossSignaled)
            {
                m_lossSignaled = true;
                StationTxSafetyResult abort =
                    await m_supervisor.AbortAsync(
                        "station-engine-connection-lost",
                        cancellationToken);
                return Result(
                    abort.Success,
                    abort.Code,
                    abort.Message,
                    abort.Snapshot);
            }

            StationTxSafetyResult reconciled =
                await m_supervisor.EvaluateAsync(
                    "station-engine-connection-loss-reconcile",
                    cancellationToken);
            return Result(
                reconciled.Success,
                reconciled.Code,
                reconciled.Message,
                reconciled.Snapshot);
        }
        finally
        {
            m_gate.Release();
        }
    }

    private StationTxEngineConnectionResult Result(
        bool success,
        string code,
        string message,
        StationTxSafetySnapshot safety) =>
        new(
            success,
            code,
            message,
            m_sawConnected,
            m_lossSignaled,
            safety);

    private static string NormalizeIdentifier(
        string? value,
        int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 &&
               normalized.Length <= maximumLength &&
               normalized.All(character => !char.IsControl(character))
            ? normalized
            : string.Empty;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref m_disposed) != 0,
            this);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) == 0)
        {
            m_gate.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
