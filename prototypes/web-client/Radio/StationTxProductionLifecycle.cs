using System.Threading.Channels;
using AetherSDR.TxWatchdog.Protocol;

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
    string? LeaseDisplayName,
    DateTimeOffset? LeaseExpiresAt,
    string? LastLeaseChangeReason,
    string GateState,
    string GateReason,
    bool GateHasActiveIntent,
    string SafetyState,
    string SafetyReason,
    bool SafetyActive,
    bool ObservationFaulted,
    long BrowserObservationSequence,
    DateTimeOffset? LastBrowserObservedAt,
    long EngineObservationSequence,
    DateTimeOffset? LastEngineObservedAt,
    long GatewayObservationSequence,
    DateTimeOffset? LastGatewayObservedAt,
    long LeaseObservationSequence,
    DateTimeOffset? LastLeaseObservedAt,
    long BrowserTxIntentObservationSequence,
    long LastBrowserTxIntentRequestSequence,
    string? LastBrowserTxIntentAction,
    string? LastBrowserTxIntentOutcome,
    string? LastBrowserTxIntentReason,
    DateTimeOffset? LastBrowserTxIntentAt,
    bool WatchdogRunning,
    long WatchdogEvaluationSequence,
    DateTimeOffset? LastWatchdogEvaluatedAt,
    bool BrowserFresh,
    bool EngineFresh,
    bool GatewayFresh,
    bool AuthorityFresh,
    string AuthorityReason,
    StationTxIndependentWatchdogDiagnostics IndependentWatchdog,
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
    internal static readonly TimeSpan WatchdogInterval =
        TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan BrowserFreshnessTimeout =
        TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan EngineFreshnessTimeout =
        TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan GatewayFreshnessTimeout =
        TimeSpan.FromSeconds(10);

    private readonly object m_stateGate = new();
    private readonly string m_radioId;
    private readonly string m_sessionId;
    private readonly string m_browserClientId;
    private readonly string m_gatewayInstanceId;
    private readonly string m_engineInstanceId;
    private readonly TxLeaseManager m_leases;
    private readonly ILogger<StationTxProductionLifecycle> m_logger;
    private readonly TimeProvider m_timeProvider;
    private readonly CancellationTokenSource m_watchdogCancellation = new();
    private readonly StationTxCommandGate m_commandGate;
    private readonly StationTxSafetySupervisor m_supervisor;
    private readonly StationTxAuthenticationMonitor m_authenticationMonitor;
    private readonly StationTxEngineConnectionMonitor m_engineMonitor;
    private readonly StationTxGatewayConnectionMonitor m_gatewayMonitor;
    private readonly IStationTxIndependentWatchdog m_independentWatchdog;
    private readonly Channel<LifecycleObservation> m_observations;
    private readonly Task m_observationTask;
    private readonly Task m_watchdogTask;

    private bool m_gatewayConnected = true;
    private bool m_engineConnected;
    private bool m_browserConnected;
    private bool m_authenticated;
    private string? m_connectionClientId;
    private uint m_stationClientHandle;
    private bool m_leaseActive;
    private string? m_leaseId;
    private string? m_leaseDisplayName;
    private DateTimeOffset? m_leaseExpiresAt;
    private string? m_lastLeaseChangeReason;
    private WatchdogIdentity? m_independentWatchdogIdentity;
    private bool m_observationFaulted;
    private long m_browserObservationSequence;
    private DateTimeOffset? m_lastBrowserObservedAt;
    private long m_engineObservationSequence;
    private DateTimeOffset? m_lastEngineObservedAt;
    private long m_gatewayObservationSequence = 1;
    private DateTimeOffset? m_lastGatewayObservedAt;
    private long m_leaseObservationSequence;
    private DateTimeOffset? m_lastLeaseObservedAt;
    private long m_browserTxIntentObservationSequence;
    private long m_lastBrowserTxIntentRequestSequence;
    private string? m_lastBrowserTxIntentAction;
    private string? m_lastBrowserTxIntentOutcome;
    private string? m_lastBrowserTxIntentReason;
    private DateTimeOffset? m_lastBrowserTxIntentAt;
    private long m_watchdogEvaluationSequence;
    private DateTimeOffset? m_lastWatchdogEvaluatedAt;
    private string m_lastObservation = "registered-disabled";
    private DateTimeOffset m_lastObservedAt;
    private int m_overflowSignaled;
    private int m_disposed;

    public StationTxProductionLifecycle(
        string radioId,
        string sessionId,
        string browserClientId,
        string gatewayInstanceId,
        TxLeaseManager leases,
        RadioTxOccupancyRegistry occupancy,
        ILogger<StationTxProductionLifecycle> logger,
        TimeProvider? timeProvider = null,
        IStationTxIndependentWatchdogFactory? independentWatchdogFactory = null)
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
        m_timeProvider = timeProvider ?? TimeProvider.System;
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        m_lastGatewayObservedAt = now;
        m_lastObservedAt = now;

        StationTxUnavailableCommandTransport commandTransport = new();
        StationTxUnavailableEmergencyUnkeyTransport emergencyTransport = new();
        m_commandGate = new StationTxCommandGate(
            allowTransmit: false,
            m_radioId,
            leases,
            occupancy,
            commandTransport,
            m_timeProvider);
        m_supervisor = new StationTxSafetySupervisor(
            m_radioId,
            occupancy,
            emergencyTransport,
            m_timeProvider);
        m_authenticationMonitor = new StationTxAuthenticationMonitor(m_supervisor);
        m_engineMonitor = new StationTxEngineConnectionMonitor(m_supervisor);
        m_gatewayMonitor = new StationTxGatewayConnectionMonitor(m_supervisor);
        m_independentWatchdog = independentWatchdogFactory?.Create(
            new StationTxIndependentWatchdogOwner(
                m_radioId,
                m_sessionId,
                m_browserClientId,
                m_gatewayInstanceId,
                m_engineInstanceId),
            ObserveIndependentWatchdogEventAsync) ??
            new StationTxUnavailableIndependentWatchdog();

        m_observations = Channel.CreateBounded<LifecycleObservation>(
            new BoundedChannelOptions(ObservationCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        m_observationTask = Task.Run(ProcessObservationsAsync);
        m_watchdogTask = Task.Run(RunWatchdogAsync);
    }

    public StationTxLifecycleDiagnostics Snapshot
    {
        get
        {
            StationTxGateSnapshot gate = m_commandGate.Snapshot;
            StationTxSafetySnapshot safety = m_supervisor.Snapshot;
            lock (m_stateGate)
            {
                LifecycleFreshness freshness =
                    EvaluateFreshnessLocked(m_timeProvider.GetUtcNow());
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
                    m_leaseDisplayName,
                    m_leaseExpiresAt,
                    m_lastLeaseChangeReason,
                    gate.State.ToString(),
                    gate.Reason,
                    gate.HasActiveIntent,
                    safety.State.ToString(),
                    safety.Reason,
                    safety.Active,
                    m_observationFaulted,
                    m_browserObservationSequence,
                    m_lastBrowserObservedAt,
                    m_engineObservationSequence,
                    m_lastEngineObservedAt,
                    m_gatewayObservationSequence,
                    m_lastGatewayObservedAt,
                    m_leaseObservationSequence,
                    m_lastLeaseObservedAt,
                    m_browserTxIntentObservationSequence,
                    m_lastBrowserTxIntentRequestSequence,
                    m_lastBrowserTxIntentAction,
                    m_lastBrowserTxIntentOutcome,
                    m_lastBrowserTxIntentReason,
                    m_lastBrowserTxIntentAt,
                    WatchdogRunning: Volatile.Read(ref m_disposed) == 0,
                    m_watchdogEvaluationSequence,
                    m_lastWatchdogEvaluatedAt,
                    freshness.BrowserFresh,
                    freshness.EngineFresh,
                    freshness.GatewayFresh,
                    freshness.AuthorityFresh,
                    freshness.Reason,
                    m_independentWatchdog.Snapshot,
                    m_lastObservation,
                    m_lastObservedAt);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        m_independentWatchdog.StartAsync(cancellationToken);

    public void ObserveBrowserConnection(
        string connectionClientId,
        bool connected,
        bool authenticated) =>
        Enqueue(new BrowserObservation(
            NormalizeRequired(connectionClientId, 128),
            connected,
            authenticated));

    public void ObserveBrowserActivity(
        string connectionClientId,
        bool authenticated) =>
        Enqueue(new BrowserActivityObservation(
            NormalizeRequired(connectionClientId, 128),
            authenticated));

    public void ObserveEngineConnection(bool connected, uint clientHandle) =>
        Enqueue(new EngineObservation(connected, clientHandle));

    public void ObserveEngineHeartbeat(uint clientHandle) =>
        Enqueue(new EngineHeartbeatObservation(clientHandle));

    public void ObserveGatewayConnection(bool connected) =>
        Enqueue(new GatewayObservation(connected));

    public void ObserveGatewayHeartbeat() =>
        Enqueue(new GatewayHeartbeatObservation());

    public void ObserveLeaseChange(TxLeaseChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Enqueue(new LeaseObservation(change));
    }

    public void ObserveBrowserTxIntent(
        string connectionClientId,
        long requestSequence,
        string action,
        string outcome,
        string reason,
        DateTimeOffset observedAt)
    {
        if (requestSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestSequence));
        }
        Enqueue(new BrowserTxIntentObservation(
            NormalizeRequired(connectionClientId, 128),
            requestSequence,
            NormalizeRequired(action, 64),
            NormalizeRequired(outcome, 128),
            NormalizeRequired(reason, 512),
            observedAt));
    }

    internal Task FlushAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(new BarrierObservation(completion)))
        {
            completion.TrySetException(new InvalidOperationException(
                "The station TX lifecycle cannot accept a flush barrier."));
        }
        return completion.Task.WaitAsync(cancellationToken);
    }

    internal Task EvaluateWatchdogAsync(
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(new WatchdogObservation(
                m_timeProvider.GetUtcNow(),
                completion)))
        {
            completion.TrySetException(new InvalidOperationException(
                "The station TX lifecycle cannot accept a watchdog evaluation."));
        }
        return completion.Task.WaitAsync(cancellationToken);
    }

    private bool Enqueue(LifecycleObservation observation)
    {
        if (Volatile.Read(ref m_disposed) != 0)
        {
            return false;
        }

        if (m_observations.Writer.TryWrite(observation))
        {
            return true;
        }

        FailClosed(
            "observation-queue-full",
            "The bounded station TX lifecycle observation queue is full.");
        return false;
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
                    case BrowserActivityObservation browserActivity:
                        await ProcessBrowserActivityAsync(browserActivity);
                        break;
                    case EngineObservation engine:
                        await ProcessEngineAsync(engine);
                        break;
                    case EngineHeartbeatObservation engineHeartbeat:
                        await ProcessEngineHeartbeatAsync(engineHeartbeat);
                        break;
                    case GatewayObservation gateway:
                        await ProcessGatewayAsync(gateway);
                        break;
                    case GatewayHeartbeatObservation:
                        await ProcessGatewayHeartbeatAsync();
                        break;
                    case LeaseObservation lease:
                        await ProcessLeaseAsync(lease.Change);
                        break;
                    case BrowserTxIntentObservation txIntent:
                        ProcessBrowserTxIntent(txIntent);
                        break;
                    case WatchdogObservation watchdog:
                        await ProcessWatchdogAsync(watchdog);
                        break;
                    case IndependentWatchdogEventObservation independent:
                        ProcessIndependentWatchdogEvent(independent.Event);
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

    private async Task RunWatchdogAsync()
    {
        try
        {
            using PeriodicTimer timer = new(
                WatchdogInterval,
                m_timeProvider);
            while (await timer.WaitForNextTickAsync(
                m_watchdogCancellation.Token))
            {
                Enqueue(new WatchdogObservation(
                    m_timeProvider.GetUtcNow(),
                    Completion: null));
            }
        }
        catch (OperationCanceledException) when (
            m_watchdogCancellation.IsCancellationRequested)
        {
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
                m_browserObservationSequence++;
                m_lastBrowserObservedAt = m_timeProvider.GetUtcNow();
                RecordLocked(observation.Connected
                    ? "browser-connected-authenticated"
                    : "browser-disconnected");
            }
        }

        if (!applies)
        {
            return;
        }

        if (!observation.Connected || !observation.Authenticated)
        {
            m_leases.TryReleaseOwner(
                m_radioId,
                m_sessionId,
                observation.ConnectionClientId,
                "authentication-lost",
                out _);
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
        await SynchronizeIndependentWatchdogAsync();
    }

    private async Task ProcessBrowserActivityAsync(
        BrowserActivityObservation observation)
    {
        lock (m_stateGate)
        {
            if (!m_browserConnected ||
                !string.Equals(
                    m_connectionClientId,
                    observation.ConnectionClientId,
                    StringComparison.Ordinal))
            {
                return;
            }

            m_authenticated = observation.Authenticated;
            m_browserObservationSequence++;
            m_lastBrowserObservedAt = m_timeProvider.GetUtcNow();
            RecordLocked(observation.Authenticated
                ? "browser-activity-authenticated"
                : "browser-activity-unauthenticated");
        }

        if (!observation.Authenticated)
        {
            m_leases.TryReleaseOwner(
                m_radioId,
                m_sessionId,
                observation.ConnectionClientId,
                "authentication-lost",
                out _);
        }

        StationTxSafetySnapshot safety = m_supervisor.Snapshot;
        await m_authenticationMonitor.EvaluateAsync(
            new StationTxAuthenticationObservation(
                safety.EngineInstanceId ?? m_engineInstanceId,
                safety.LeaseId ?? m_leaseId ?? "no-active-lease",
                safety.SessionId ?? m_sessionId,
                safety.BrowserClientId ?? observation.ConnectionClientId,
                safety.ProtectedClientHandle,
                observation.Authenticated));
        await SynchronizeIndependentWatchdogAsync();
    }

    private async Task ProcessEngineAsync(EngineObservation observation)
    {
        lock (m_stateGate)
        {
            m_engineConnected = observation.Connected;
            m_stationClientHandle = observation.Connected
                ? observation.ClientHandle
                : 0;
            m_engineObservationSequence++;
            m_lastEngineObservedAt = m_timeProvider.GetUtcNow();
            RecordLocked(observation.Connected
                ? "station-engine-connected"
                : "station-engine-disconnected");
        }

        if (!observation.Connected)
        {
            ReleaseTrackedLease("engine-disconnected");
        }

        StationTxSafetySnapshot safety = m_supervisor.Snapshot;
        await m_engineMonitor.EvaluateAsync(
            new StationTxEngineConnectionObservation(
                safety.EngineInstanceId ?? m_engineInstanceId,
                safety.LeaseId ?? m_leaseId ?? "no-active-lease",
                safety.ProtectedClientHandle,
                observation.Connected));
        await SynchronizeIndependentWatchdogAsync();
    }

    private async Task ProcessEngineHeartbeatAsync(
        EngineHeartbeatObservation observation)
    {
        lock (m_stateGate)
        {
            if (!m_engineConnected ||
                m_stationClientHandle == 0 ||
                m_stationClientHandle != observation.ClientHandle)
            {
                return;
            }

            m_engineObservationSequence++;
            m_lastEngineObservedAt = m_timeProvider.GetUtcNow();
            RecordLocked("station-engine-heartbeat");
        }
        await SynchronizeIndependentWatchdogAsync();
    }

    private async Task ProcessGatewayAsync(GatewayObservation observation)
    {
        lock (m_stateGate)
        {
            m_gatewayConnected = observation.Connected;
            m_gatewayObservationSequence++;
            m_lastGatewayObservedAt = m_timeProvider.GetUtcNow();
            RecordLocked(observation.Connected
                ? "gateway-connected"
                : "gateway-disconnected");
        }

        if (!observation.Connected)
        {
            ReleaseTrackedLease("gateway-disconnected");
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
        await SynchronizeIndependentWatchdogAsync();
    }

    private async Task ProcessGatewayHeartbeatAsync()
    {
        lock (m_stateGate)
        {
            if (!m_gatewayConnected)
            {
                return;
            }

            m_gatewayObservationSequence++;
            m_lastGatewayObservedAt = m_timeProvider.GetUtcNow();
            RecordLocked("gateway-heartbeat");
        }
        await SynchronizeIndependentWatchdogAsync();
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
                m_leaseDisplayName = change.Lease.DisplayName;
                m_leaseExpiresAt = change.Lease.ExpiresAt;
                m_lastLeaseChangeReason = change.Reason;
                m_leaseObservationSequence++;
                m_lastLeaseObservedAt = m_timeProvider.GetUtcNow();
                RecordLocked(change.Active
                    ? $"lease-{change.Reason}"
                    : $"lease-released-{change.Reason}");
            }
        }

        if (applies)
        {
            await m_commandGate.HandleLeaseChangeAsync(change);
            await SynchronizeIndependentWatchdogAsync();
        }
    }

    private void ProcessBrowserTxIntent(
        BrowserTxIntentObservation observation)
    {
        lock (m_stateGate)
        {
            if (!m_browserConnected ||
                !string.Equals(
                    m_connectionClientId,
                    observation.ConnectionClientId,
                    StringComparison.Ordinal) ||
                observation.RequestSequence <=
                    m_lastBrowserTxIntentRequestSequence)
            {
                return;
            }

            m_browserTxIntentObservationSequence++;
            m_lastBrowserTxIntentRequestSequence =
                observation.RequestSequence;
            m_lastBrowserTxIntentAction = observation.Action;
            m_lastBrowserTxIntentOutcome = observation.Outcome;
            m_lastBrowserTxIntentReason = observation.Reason;
            m_lastBrowserTxIntentAt = observation.ObservedAt;
            RecordLocked($"browser-tx-intent-{observation.Outcome}");
        }
    }

    private async Task ProcessWatchdogAsync(WatchdogObservation observation)
    {
        try
        {
            string? releaseReason = null;
            lock (m_stateGate)
            {
                m_watchdogEvaluationSequence++;
                m_lastWatchdogEvaluatedAt = observation.ObservedAt;
                LifecycleFreshness freshness =
                    EvaluateFreshnessLocked(observation.ObservedAt);
                if (m_leaseActive && !freshness.AuthorityFresh)
                {
                    releaseReason = $"watchdog-{freshness.Reason}";
                    RecordLocked(releaseReason);
                }
            }

            if (releaseReason is not null)
            {
                await DisconnectIndependentWatchdogAsync(releaseReason);
                ReleaseTrackedLease(releaseReason);
            }
        }
        finally
        {
            observation.Completion?.TrySetResult();
        }
    }

    private ValueTask ObserveIndependentWatchdogEventAsync(
        StationTxIndependentWatchdogEvent watchdogEvent)
    {
        Enqueue(new IndependentWatchdogEventObservation(watchdogEvent));
        return ValueTask.CompletedTask;
    }

    private void ProcessIndependentWatchdogEvent(
        StationTxIndependentWatchdogEvent watchdogEvent)
    {
        lock (m_stateGate)
        {
            if (watchdogEvent.Kind ==
                StationTxIndependentWatchdogEventKind.Lost)
            {
                m_independentWatchdogIdentity = null;
            }
            RecordLocked(watchdogEvent.Reason);
        }

        if (watchdogEvent.Kind ==
            StationTxIndependentWatchdogEventKind.Lost)
        {
            ReleaseTrackedLease($"independent-{watchdogEvent.Reason}");
        }
    }

    private async Task SynchronizeIndependentWatchdogAsync()
    {
        StationTxIndependentWatchdogDiagnostics initial =
            m_independentWatchdog.Snapshot;
        if (!initial.SupervisionEnabled)
        {
            return;
        }

        WatchdogIdentity? current;
        WatchdogIdentity? candidate;
        lock (m_stateGate)
        {
            current = m_independentWatchdogIdentity;
            candidate = CreateIndependentWatchdogIdentityLocked();
        }

        if (candidate is null)
        {
            if (current is not null)
            {
                await DisconnectIndependentWatchdogAsync(
                    "authority-incomplete");
            }
            return;
        }

        if (current is null)
        {
            StationTxIndependentWatchdogDiagnostics registered =
                await m_independentWatchdog.RegisterAsync(candidate);
            if (!IndependentAuthorityAccepted(registered))
            {
                lock (m_stateGate)
                {
                    m_independentWatchdogIdentity = null;
                    RecordLocked("independent-watchdog-registration-failed");
                }
                ReleaseTrackedLease(
                    "independent-watchdog-registration-failed");
                return;
            }

            lock (m_stateGate)
            {
                if (CreateIndependentWatchdogIdentityLocked() is
                        WatchdogIdentity confirmed &&
                    Equals(confirmed, candidate))
                {
                    m_independentWatchdogIdentity = candidate;
                    RecordLocked("independent-watchdog-registered");
                }
            }
            return;
        }

        if (!Equals(current, candidate))
        {
            await DisconnectIndependentWatchdogAsync(
                "identity-changed");
            ReleaseTrackedLease(
                "independent-watchdog-identity-changed");
            return;
        }

        long previousSequence = initial.LastSequence;
        StationTxIndependentWatchdogDiagnostics heartbeat =
            await m_independentWatchdog.HeartbeatAsync(current);
        if (!IndependentAuthorityAccepted(heartbeat) ||
            heartbeat.LastSequence <= previousSequence)
        {
            lock (m_stateGate)
            {
                m_independentWatchdogIdentity = null;
                RecordLocked("independent-watchdog-heartbeat-failed");
            }
            ReleaseTrackedLease(
                "independent-watchdog-heartbeat-failed");
            return;
        }

        lock (m_stateGate)
        {
            RecordLocked("independent-watchdog-heartbeat");
        }
    }

    private static bool IndependentAuthorityAccepted(
        StationTxIndependentWatchdogDiagnostics snapshot) =>
        snapshot.SupervisionEnabled &&
        snapshot.ProcessRunning &&
        snapshot.IpcConnected &&
        snapshot.Registered &&
        snapshot.Connected &&
        snapshot.LeaseBound &&
        string.Equals(snapshot.State, "Disarmed", StringComparison.Ordinal) &&
        !snapshot.RadioCommandTransportAvailable &&
        !snapshot.ArmingAvailable;

    private async Task DisconnectIndependentWatchdogAsync(string reason)
    {
        WatchdogIdentity? identity;
        lock (m_stateGate)
        {
            identity = m_independentWatchdogIdentity;
            m_independentWatchdogIdentity = null;
        }
        if (identity is null)
        {
            return;
        }

        await m_independentWatchdog.DisconnectAndResetAsync(identity);
        lock (m_stateGate)
        {
            RecordLocked($"independent-watchdog-disconnected-{reason}");
        }
    }

    private WatchdogIdentity? CreateIndependentWatchdogIdentityLocked()
    {
        if (!m_gatewayConnected ||
            !m_engineConnected ||
            !m_browserConnected ||
            !m_authenticated ||
            !m_leaseActive ||
            m_stationClientHandle == 0 ||
            m_connectionClientId is null ||
            m_leaseId is null)
        {
            return null;
        }

        return new WatchdogIdentity(
            m_radioId,
            m_sessionId,
            m_browserClientId,
            m_gatewayInstanceId,
            m_engineInstanceId,
            m_connectionClientId,
            m_leaseId,
            m_stationClientHandle);
    }

    private LifecycleFreshness EvaluateFreshnessLocked(DateTimeOffset now)
    {
        bool browserFresh =
            m_browserConnected &&
            m_authenticated &&
            IsFresh(m_lastBrowserObservedAt, now, BrowserFreshnessTimeout);
        bool engineFresh =
            m_engineConnected &&
            m_stationClientHandle != 0 &&
            IsFresh(m_lastEngineObservedAt, now, EngineFreshnessTimeout);
        bool gatewayFresh =
            m_gatewayConnected &&
            IsFresh(m_lastGatewayObservedAt, now, GatewayFreshnessTimeout);

        string reason;
        if (!m_leaseActive)
        {
            reason = "no-active-lease";
        }
        else if (m_observationFaulted)
        {
            reason = "observation-faulted";
        }
        else if (!m_gatewayConnected)
        {
            reason = "gateway-disconnected";
        }
        else if (!gatewayFresh)
        {
            reason = "gateway-stale";
        }
        else if (!m_browserConnected)
        {
            reason = "browser-disconnected";
        }
        else if (!m_authenticated)
        {
            reason = "browser-unauthenticated";
        }
        else if (!browserFresh)
        {
            reason = "browser-stale";
        }
        else if (!m_engineConnected || m_stationClientHandle == 0)
        {
            reason = "engine-disconnected";
        }
        else if (!engineFresh)
        {
            reason = "engine-stale";
        }
        else
        {
            reason = "fresh";
        }

        return new LifecycleFreshness(
            browserFresh,
            engineFresh,
            gatewayFresh,
            string.Equals(reason, "fresh", StringComparison.Ordinal),
            reason);
    }

    private void ReleaseTrackedLease(string reason)
    {
        string? leaseId;
        string? connectionClientId;
        lock (m_stateGate)
        {
            if (!m_leaseActive ||
                m_leaseId is null ||
                m_connectionClientId is null)
            {
                return;
            }
            leaseId = m_leaseId;
            connectionClientId = m_connectionClientId;
        }

        m_leases.TryRelease(
            m_radioId,
            leaseId,
            m_sessionId,
            connectionClientId,
            reason,
            out _);
    }

    private static bool IsFresh(
        DateTimeOffset? observedAt,
        DateTimeOffset now,
        TimeSpan timeout) =>
        observedAt.HasValue &&
        now >= observedAt.Value &&
        now - observedAt.Value <= timeout;

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
        m_lastObservedAt = m_timeProvider.GetUtcNow();
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
            m_independentWatchdogIdentity = null;
            m_gatewayConnected = false;
            m_engineConnected = false;
            m_browserConnected = false;
            m_authenticated = false;
            m_connectionClientId = null;
            m_stationClientHandle = 0;
            RecordLocked("disposed");
        }

        m_watchdogCancellation.Cancel();
        await m_watchdogTask;
        m_observations.Writer.TryComplete();
        await m_observationTask;
        m_watchdogCancellation.Dispose();
        await m_gatewayMonitor.DisposeAsync();
        await m_engineMonitor.DisposeAsync();
        await m_authenticationMonitor.DisposeAsync();
        await m_supervisor.DisposeAsync();
        await m_commandGate.DisposeAsync();
        await m_independentWatchdog.DisposeAsync();
    }

    private abstract record LifecycleObservation;

    private sealed record BrowserObservation(
        string ConnectionClientId,
        bool Connected,
        bool Authenticated) : LifecycleObservation;

    private sealed record BrowserActivityObservation(
        string ConnectionClientId,
        bool Authenticated) : LifecycleObservation;

    private sealed record EngineObservation(
        bool Connected,
        uint ClientHandle) : LifecycleObservation;

    private sealed record EngineHeartbeatObservation(
        uint ClientHandle) : LifecycleObservation;

    private sealed record GatewayObservation(bool Connected) : LifecycleObservation;

    private sealed record GatewayHeartbeatObservation : LifecycleObservation;

    private sealed record LeaseObservation(
        TxLeaseChange Change) : LifecycleObservation;

    private sealed record BrowserTxIntentObservation(
        string ConnectionClientId,
        long RequestSequence,
        string Action,
        string Outcome,
        string Reason,
        DateTimeOffset ObservedAt) : LifecycleObservation;

    private sealed record WatchdogObservation(
        DateTimeOffset ObservedAt,
        TaskCompletionSource? Completion) : LifecycleObservation;

    private sealed record IndependentWatchdogEventObservation(
        StationTxIndependentWatchdogEvent Event) : LifecycleObservation;

    private sealed record BarrierObservation(
        TaskCompletionSource Completion) : LifecycleObservation;

    private sealed record LifecycleFreshness(
        bool BrowserFresh,
        bool EngineFresh,
        bool GatewayFresh,
        bool AuthorityFresh,
        string Reason);
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
