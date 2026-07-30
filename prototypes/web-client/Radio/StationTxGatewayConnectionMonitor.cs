namespace AetherSDR.Web.Radio;

internal sealed record StationTxGatewayConnectionObservation(
    string GatewayInstanceId,
    string EngineInstanceId,
    string LeaseId,
    string SessionId,
    string BrowserClientId,
    uint ProtectedClientHandle,
    bool IsConnected);

internal sealed record StationTxGatewayConnectionResult(
    bool Success,
    string Code,
    string Message,
    bool SawConnected,
    bool LossSignaled,
    StationTxSafetySnapshot SafetySnapshot);

/// <summary>
/// Private station-local monitor that converts one exact gateway-control-link
/// connected-to-disconnected transition into an ownership-safe safety abort.
/// It has no radio command transport and cannot key or unkey by itself.
///
/// Gateway loss is actionable only after the monitor observed the same gateway
/// process instance connected for the exact engine, lease, session, browser,
/// and FLEX handle named by the active arm. Starting disconnected, replacing
/// the gateway instance, mismatched owner identity, and repeated loss reports
/// never invent ownership or issue a duplicate immediate unkey.
/// </summary>
internal sealed class StationTxGatewayConnectionMonitor : IAsyncDisposable
{
    private readonly SemaphoreSlim m_gate = new(1, 1);
    private readonly StationTxSafetySupervisor m_supervisor;

    private string? m_gatewayInstanceId;
    private bool m_sawConnected;
    private bool m_lossSignaled;
    private int m_disposed;

    public StationTxGatewayConnectionMonitor(
        StationTxSafetySupervisor supervisor)
    {
        m_supervisor = supervisor ??
            throw new ArgumentNullException(nameof(supervisor));
    }

    public async Task<StationTxGatewayConnectionResult> EvaluateAsync(
        StationTxGatewayConnectionObservation observation,
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
                    "No active station TX safety arm requires gateway monitoring.",
                    safety);
            }

            string gatewayInstanceId = NormalizeIdentifier(
                observation.GatewayInstanceId,
                128);
            string engineInstanceId = NormalizeIdentifier(
                observation.EngineInstanceId,
                128);
            string leaseId = NormalizeIdentifier(observation.LeaseId, 64);
            string sessionId = NormalizeIdentifier(observation.SessionId, 128);
            string browserClientId = NormalizeIdentifier(
                observation.BrowserClientId,
                128);
            if (gatewayInstanceId.Length == 0 ||
                engineInstanceId.Length == 0 ||
                leaseId.Length == 0 ||
                sessionId.Length == 0 ||
                browserClientId.Length == 0 ||
                observation.ProtectedClientHandle == 0 ||
                !string.Equals(
                    safety.EngineInstanceId,
                    engineInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    safety.LeaseId,
                    leaseId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    safety.SessionId,
                    sessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    safety.BrowserClientId,
                    browserClientId,
                    StringComparison.Ordinal) ||
                safety.ProtectedClientHandle != observation.ProtectedClientHandle)
            {
                return Result(
                    false,
                    "gateway_connection_owner_mismatch",
                    "The gateway connection observation does not match the exact active safety arm.",
                    safety);
            }

            if (m_gatewayInstanceId is not null &&
                !string.Equals(
                    m_gatewayInstanceId,
                    gatewayInstanceId,
                    StringComparison.Ordinal))
            {
                return Result(
                    false,
                    "gateway_instance_mismatch",
                    "The gateway process instance changed while the safety arm was active.",
                    safety);
            }

            if (observation.IsConnected)
            {
                if (m_lossSignaled)
                {
                    return Result(
                        false,
                        "gateway_connection_loss_already_signaled",
                        "The prior gateway loss remains under safety-supervisor reconciliation.",
                        safety);
                }
                m_gatewayInstanceId = gatewayInstanceId;
                m_sawConnected = true;
                return Result(
                    true,
                    "gateway_connected",
                    "The exact protected gateway control link is present.",
                    safety);
            }

            if (!m_sawConnected)
            {
                return Result(
                    true,
                    "gateway_connection_not_established",
                    "The monitor started while the gateway was disconnected; no ownership or unkey action was inferred.",
                    safety);
            }

            if (!m_lossSignaled)
            {
                m_lossSignaled = true;
                StationTxSafetyResult abort =
                    await m_supervisor.AbortAsync(
                        "gateway-process-lost",
                        cancellationToken);
                return Result(
                    abort.Success,
                    abort.Code,
                    abort.Message,
                    abort.Snapshot);
            }

            StationTxSafetyResult reconciled =
                await m_supervisor.EvaluateAsync(
                    "gateway-process-loss-reconcile",
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

    private StationTxGatewayConnectionResult Result(
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
