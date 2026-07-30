namespace AetherSDR.Web.Radio;

internal sealed record StationTxAuthenticationObservation(
    string EngineInstanceId,
    string LeaseId,
    string SessionId,
    string BrowserClientId,
    uint ProtectedClientHandle,
    bool IsAuthenticated);

internal sealed record StationTxAuthenticationResult(
    bool Success,
    string Code,
    string Message,
    bool SawAuthenticated,
    bool LossSignaled,
    StationTxSafetySnapshot SafetySnapshot);

/// <summary>
/// Private station-local monitor that converts one exact authenticated-to-
/// unauthenticated transition into an ownership-safe safety-supervisor abort.
/// It has no radio command transport and cannot key or unkey by itself.
///
/// Authentication loss is actionable only after this monitor observed the
/// matching engine, lease, session, browser client, and FLEX handle authenticated
/// while the same safety arm was active. Starting unauthenticated,
/// stale/mismatched identity, and repeated loss observations never invent
/// ownership or issue a duplicate abort.
/// </summary>
internal sealed class StationTxAuthenticationMonitor : IAsyncDisposable
{
    private readonly SemaphoreSlim m_gate = new(1, 1);
    private readonly StationTxSafetySupervisor m_supervisor;

    private bool m_sawAuthenticated;
    private bool m_lossSignaled;
    private int m_disposed;

    public StationTxAuthenticationMonitor(
        StationTxSafetySupervisor supervisor)
    {
        m_supervisor = supervisor ??
            throw new ArgumentNullException(nameof(supervisor));
    }

    public async Task<StationTxAuthenticationResult> EvaluateAsync(
        StationTxAuthenticationObservation observation,
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
                    "No active station TX safety arm requires authentication monitoring.",
                    safety);
            }

            string engineInstanceId = NormalizeIdentifier(
                observation.EngineInstanceId,
                128);
            string leaseId = NormalizeIdentifier(observation.LeaseId, 64);
            string sessionId = NormalizeIdentifier(observation.SessionId, 128);
            string browserClientId = NormalizeIdentifier(
                observation.BrowserClientId,
                128);
            if (engineInstanceId.Length == 0 ||
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
                safety.ProtectedClientHandle !=
                    observation.ProtectedClientHandle)
            {
                return Result(
                    false,
                    "authentication_owner_mismatch",
                    "The authentication observation does not match the exact active safety arm.",
                    safety);
            }

            if (observation.IsAuthenticated)
            {
                if (m_lossSignaled)
                {
                    return Result(
                        false,
                        "authentication_loss_already_signaled",
                        "The prior authentication loss remains under safety-supervisor reconciliation.",
                        safety);
                }
                m_sawAuthenticated = true;
                return Result(
                    true,
                    "authenticated",
                    "The exact protected browser authority is authenticated.",
                    safety);
            }

            if (!m_sawAuthenticated)
            {
                return Result(
                    true,
                    "authentication_not_established",
                    "The monitor started while the authority was unauthenticated; no ownership or unkey action was inferred.",
                    safety);
            }

            if (!m_lossSignaled)
            {
                m_lossSignaled = true;
                StationTxSafetyResult abort =
                    await m_supervisor.AbortAsync(
                        "authentication-lost",
                        cancellationToken);
                return Result(
                    abort.Success,
                    abort.Code,
                    abort.Message,
                    abort.Snapshot);
            }

            StationTxSafetyResult reconciled =
                await m_supervisor.EvaluateAsync(
                    "authentication-loss-reconcile",
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

    private StationTxAuthenticationResult Result(
        bool success,
        string code,
        string message,
        StationTxSafetySnapshot safety) =>
        new(
            success,
            code,
            message,
            m_sawAuthenticated,
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
