using System.Threading.Channels;

namespace AetherSDR.Web.Radio;

public sealed record StationTxLifecycleDiagnostics(
    string RadioId,
    string SessionId,
    string BrowserClientId,
    string GatewayInstanceId,
    string EngineInstanceId,
    bool Registered,
    bool ProductionTransmitEnabled,
    bool CommandTransportAvailable,
    bool EmergencyUnkeyTransportAvailable,
    bool GatewayConnected,
    bool EngineConnected,
    bool BrowserConnected,
    bool Authenticated,
    string? ConnectionClientId,
    uint StationClientHandle,
    bool LeaseActive,
    string? LeaseId,
    string GateState,
    string GateReason,
    bool GateHasActiveIntent,
    string SafetyState,
    string SafetyReason,
    bool SafetyActive,
    bool ObservationFaulted,
    string LastObservation,
    DateTimeOffset LastObservedAt);

/// <summary>
/// Production registration boundary for the accepted station TX gate and
/// safety state machines. This lifecycle is deliberately command-incapable:
/// its transports always report unavailable, the command gate is constructed
/// with transmit disabled, and no arm/key/unkey surface is exposed.
///
/// Real gateway, engine, browser/authentication, and lease observations are
/// serialized here so diagnostics and future reviewed transport integration
/// have one exact ownership boundary rather than reconstructing authority from
/// browser state.
/// </summary>
internal sealed class StationTxProductionLifecycle : IAsyncDisposable
{
    private const int ObservationCapacity = 64;

    private readonly object m_stateGate = new();
    private readonly string m_radioId;
    private readonly string m_sessionId;
    private readonly string m_browserClientId;
    private readonly string m_gatewayInstanceId;
    private readonly string m_engineInstanceId;
    private readonly TxLeaseManager m_leases;
    private readonly ILogger<StationTxProductionLifecycle> m_logger;
    private readonly StationTxCommandGate m_commandGate;
    private readonly StationTxSafetySupervisor m_supervisor;
    private readonly StationTxAuthenticationMonitor m_authenticationMonitor;
    private readonly StationTxEngineConnectionMonitor m_engineMonitor;
    private readonly StationTxGatewayConnectionMonitor m_gatewayMonitor;
    private readonly Channel<LifecycleObservation> m_observations;
    private readonly Task m_observationTask;

    private bool m_gatewayConnected = true;
    private bool m_engineConnected;
    private bool m_browserConnected;
    private bool m_authenticated;
    private string? m_connectionClientId;
    private uint m_stationClientHandle;
    private bool m_leaseActive;
    private string? m_leaseId;
    private bool m_observationFaulted;
    private string m_lastObservation = "registered-disabled";
    private DateTimeOffset m_lastObservedAt = DateTimeOffset.UtcNow;
    private int m_overflowSignaled;
    private int m_disposed;

    public StationTxProductionLifecycle(
        string radioId,
        string sessionId,
        string browserClientId,
        string gatewayInstanceId,
        TxLeaseManager leases,
        RadioTxOccupancyRegistry occupancy,
        ILogger<StationTxProductionLifecycle> logger)
    {
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(occupancy);
        ArgumentNullException.ThrowIfNull(logger);

        m_radioId = NormalizeRequired(radioId, 128).ToUpperInvariant();
        m_sessionId = NormalizeRequired(sessionId, 128);
        m_browserClientId = NormalizeRequired(browserClientId, 128);
        m_gatewayInstanceId = NormalizeRequired(gatewayInstanceId, 128);
        m_engineInstanceId = $"engine-{Guid.NewGuid():N}";
        m_leases = leases;
        m_logger = logger;

        StationTxUnavailableCommandTransport commandTransport = new();
        StationTxUnavailableEmergencyUnkeyTransport emergencyTransport = new();
        m_commandGate = new StationTxCommandGate(
            allowTransmit: false,
            m_radioId,
            leases,
            occupancy,
            commandTransport);
        m_supervisor = new StationTxSafetySupervisor(
            m_radioId,
            occupancy,
            emergencyTransport);
        m_authenticationMonitor = new StationTxAuthenticationMonitor(m_supervisor);
        m_engineMonitor = new StationTxEngineConnectionMonitor(m_supervisor);
        m_gatewayMonitor = new StationTxGatewayConnectionMonitor(m_supervisor);

        m_observations = Channel.CreateBounded<LifecycleObservation>(
            new BoundedChannelOptions(ObservationCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        m_observationTask = Task.Run(ProcessObservationsAsync);
    }

    public StationTxLifecycleDiagnostics Snapshot
    {
        get
        {
            StationTxGateSnapshot gate = m_commandGate.Snapshot;
            StationTxSafetySnapshot safety = m_supervisor.Snapshot;
            lock (m_stateGate)
            {
                return new StationTxLifecycleDiagnostics(
                    m_radioId,
                    m_sessionId,
                    m_browserClientId,
                    m_gatewayInstanceId,
                    m_engineInstanceId,
                    Registered: true,
                    ProductionTransmitEnabled: false,
                    CommandTransportAvailable: false,
                    EmergencyUnkeyTransportAvailable: false,
                    m_gatewayConnected,
                    m_engineConnected,
                    m_browserConnected,
                    m_authenticated,
                    m_connectionClientId,
                    m_stationClientHandle,
                    m_leaseActive,
                    m_leaseId,
                    gate.State.ToString(),
                    gate.Reason,
                    gate.HasActiveIntent,
                    safety.State.ToString(),
                    safety.Reason,
                    safety.Active,
                    m_observationFaulted,
                    m_lastObservation,
                    m_lastObservedAt);
            }
        }
    }

    public void ObserveBrowserConnection(
        string connectionClientId,
        bool connected,
        bool authenticated) =>
        Enqueue(new BrowserObservation(
            NormalizeRequired(connectionClientId, 128),
            connected,
            authenticated));

    public void ObserveEngineConnection(bool connected, uint clientHandle) =>
        Enqueue(new EngineObservation(connected, clientHandle));

    public void ObserveGatewayConnection(bool connected) =>
        Enqueue(new GatewayObservation(connected));

    public void ObserveLeaseChange(TxLeaseChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Enqueue(new LeaseObservation(change));
    }

    internal Task FlushAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new BarrierObservation(completion));
        return completion.Task.WaitAsync(cancellationToken);
    }

    private void Enqueue(LifecycleObservation observation)
    {
        if (Volatile.Read(ref m_disposed) != 0)
        {
            return;
        }

        if (!m_observations.Writer.TryWrite(observation))
        {
            FailClosed(
                "observation-queue-full",
                "The bounded station TX lifecycle observation queue is full.");
        }
    }

    private async Task ProcessObservationsAsync()
    {
        try
        {
            await foreach (
                LifecycleObservation observation in
                m_observations.Reader.ReadAllAsync())
            {
                switch (observation)
                {
                    case BrowserObservation browser:
                        await ProcessBrowserAsync(browser);
                        break;
                    case EngineObservation engine:
                        await ProcessEngineAsync(engine);
                        break;
                    case GatewayObservation gateway:
                        await ProcessGatewayAsync(gateway);
                        break;
                    case LeaseObservation lease:
                        await ProcessLeaseAsync(lease.Change);
                        break;
                    case BarrierObservation barrier:
                        barrier.Completion.TrySetResult();
                        break;
                }
            }
        }
        catch (ObjectDisposedException) when (
            Volatile.Read(ref m_disposed) != 0)
        {
        }
        catch (Exception exception)
        {
            FailClosed(
                "observation-processing-fault",
                "The station TX lifecycle observation processor faulted.");
            m_logger.LogCritical(
                exception,
                "Station TX lifecycle observation processing failed for radio {RadioId} session {SessionId}",
                m_radioId,
                m_sessionId);
        }
    }

    private async Task ProcessBrowserAsync(BrowserObservation observation)
    {
        bool applies;
        lock (m_stateGate)
        {
            applies = observation.Connected ||
                string.Equals(
                    m_connectionClientId,
                    observation.ConnectionClientId,
                    StringComparison.Ordinal);
            if (applies)
            {
                m_browserConnected = observation.Connected;
                m_authenticated =
                    observation.Connected && observation.Authenticated;
                m_connectionClientId = observation.Connected
                    ? observation.ConnectionClientId
                    : null;
                RecordLocked(observation.Connected
                    ? "browser-connected-authenticated"
                    : "browser-disconnected");
            }
        }

        if (!applies)
        {
            return;
        }

        StationTxSafetySnapshot safety = m_supervisor.Snapshot;
        await m_authenticationMonitor.EvaluateAsync(
            new StationTxAuthenticationObservation(
                safety.EngineInstanceId ?? m_engineInstanceId,
                safety.LeaseId ?? m_leaseId ?? "no-active-lease",
                safety.SessionId ?? m_sessionId,
                safety.BrowserClientId ?? observation.ConnectionClientId,
                safety.ProtectedClientHandle,
                observation.Connected && observation.Authenticated));
    }

    private async Task ProcessEngineAsync(EngineObservation observation)
    {
        lock (m_stateGate)
        {
            m_engineConnected = observation.Connected;
            m_stationClientHandle = observation.Connected
                ? observation.ClientHandle
                : 0;
            RecordLocked(observation.Connected
                ? "station-engine-connected"
                : "station-engine-disconnected");
        }

        StationTxSafetySnapshot safety = m_supervisor.Snapshot;
        await m_engineMonitor.EvaluateAsync(
            new StationTxEngineConnectionObservation(
                safety.EngineInstanceId ?? m_engineInstanceId,
                safety.LeaseId ?? m_leaseId ?? "no-active-lease",
                safety.ProtectedClientHandle,
                observation.Connected));
    }

    private async Task ProcessGatewayAsync(GatewayObservation observation)
    {
        lock (m_stateGate)
        {
            m_gatewayConnected = observation.Connected;
            RecordLocked(observation.Connected
                ? "gateway-connected"
                : "gateway-disconnected");
        }

        StationTxSafetySnapshot safety = m_supervisor.Snapshot;
        await m_gatewayMonitor.EvaluateAsync(
            new StationTxGatewayConnectionObservation(
                m_gatewayInstanceId,
                safety.EngineInstanceId ?? m_engineInstanceId,
                safety.LeaseId ?? m_leaseId ?? "no-active-lease",
                safety.SessionId ?? m_sessionId,
                safety.BrowserClientId ??
                    m_connectionClientId ??
                    m_browserClientId,
                safety.ProtectedClientHandle,
                observation.Connected));
    }

    private async Task ProcessLeaseAsync(TxLeaseChange change)
    {
        if (!string.Equals(
                change.Lease.RadioId,
                m_radioId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                change.Lease.SessionId,
                m_sessionId,
                StringComparison.Ordinal))
        {
            return;
        }

        bool applies;
        lock (m_stateGate)
        {
            bool exactCurrentBrowser =
                m_connectionClientId is not null &&
                string.Equals(
                    change.Lease.ClientId,
                    m_connectionClientId,
                    StringComparison.Ordinal);
            bool exactTrackedRelease =
                !change.Active &&
                m_leaseId is not null &&
                string.Equals(
                    change.Lease.LeaseId,
                    m_leaseId,
                    StringComparison.Ordinal);
            applies = exactCurrentBrowser || exactTrackedRelease;
            if (applies)
            {
                m_leaseActive = change.Active;
                m_leaseId = change.Active ? change.Lease.LeaseId : null;
                RecordLocked(change.Active
                    ? $"lease-{change.Reason}"
                    : $"lease-released-{change.Reason}");
            }
        }

        if (applies)
        {
            await m_commandGate.HandleLeaseChangeAsync(change);
        }
    }

    private void FailClosed(string reason, string message)
    {
        lock (m_stateGate)
        {
            m_observationFaulted = true;
            RecordLocked(reason);
        }

        if (Interlocked.Exchange(ref m_overflowSignaled, 1) == 0)
        {
            m_leases.ReleaseSession(m_sessionId, reason);
            m_logger.LogCritical(
                "{Message} Radio {RadioId}; session {SessionId} was fail-closed and its lease was released.",
                message,
                m_radioId,
                m_sessionId);
        }
    }

    private void RecordLocked(string reason)
    {
        m_lastObservation = reason;
        m_lastObservedAt = DateTimeOffset.UtcNow;
    }

    private static string NormalizeRequired(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 ||
            normalized.Length > maximumLength ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException("A valid station TX lifecycle identifier is required.");
        }
        return normalized;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
        {
            return;
        }

        lock (m_stateGate)
        {
            m_gatewayConnected = false;
            m_engineConnected = false;
            m_browserConnected = false;
            m_authenticated = false;
            m_connectionClientId = null;
            m_stationClientHandle = 0;
            RecordLocked("disposed");
        }

        m_observations.Writer.TryComplete();
        await m_observationTask;
        await m_gatewayMonitor.DisposeAsync();
        await m_engineMonitor.DisposeAsync();
        await m_authenticationMonitor.DisposeAsync();
        await m_supervisor.DisposeAsync();
        await m_commandGate.DisposeAsync();
    }

    private abstract record LifecycleObservation;

    private sealed record BrowserObservation(
        string ConnectionClientId,
        bool Connected,
        bool Authenticated) : LifecycleObservation;

    private sealed record EngineObservation(
        bool Connected,
        uint ClientHandle) : LifecycleObservation;

    private sealed record GatewayObservation(bool Connected) : LifecycleObservation;

    private sealed record LeaseObservation(
        TxLeaseChange Change) : LifecycleObservation;

    private sealed record BarrierObservation(
        TaskCompletionSource Completion) : LifecycleObservation;
}

internal sealed class StationTxUnavailableCommandTransport : IStationTxCommandTransport
{
    public bool IsConnected => false;
    public uint ClientHandle => 0;

    public Task<StationTxTransportResult> SetTransmitAsync(
        bool enabled,
        CancellationToken cancellationToken) =>
        Task.FromResult(StationTxTransportResult.Rejected(
            "Production station TX command transport is not registered."));
}

internal sealed class StationTxUnavailableEmergencyUnkeyTransport :
    IStationTxEmergencyUnkeyTransport
{
    public bool IsConnected => false;

    public Task<StationTxTransportResult> RequestUnkeyAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(StationTxTransportResult.Rejected(
            "Production emergency-unkey transport is not registered."));
}
