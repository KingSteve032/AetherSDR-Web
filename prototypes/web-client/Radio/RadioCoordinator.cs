using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using AetherSDR.Web.Auth;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed record OutboundMessage(
    WebSocketMessageType MessageType,
    ReadOnlyMemory<byte> Payload);

public sealed class RadioClientConnection
{
    public const int QueueCapacity = 64;
    internal const int TxIntentReplayCapacity = 64;

    private readonly Channel<OutboundMessage> m_outbox;
    private readonly object m_txProtocolGate = new();
    private readonly HashSet<string> m_seenTxIntentIds =
        new(StringComparer.Ordinal);
    private readonly Queue<string> m_seenTxIntentOrder = new();
    private long m_lastTxSequence;
    private int m_queueDepth;
    private long m_enqueuedMessages;
    private long m_droppedMessages;
    private long m_lastEnqueuedUnixMilliseconds;
    private long m_lastDequeuedUnixMilliseconds;
    private int m_pageVisible = 1;
    private RadioBrowserAudioDiagnostics? m_audioDiagnostics;
    private RadioBrowserNetworkDiagnostics? m_networkDiagnostics;

    public RadioClientConnection(
        string clientId,
        string userId,
        string displayName,
        IReadOnlyList<string> roles)
    {
        ClientId = clientId;
        UserId = userId;
        DisplayName = displayName;
        Roles = roles;
        ConnectedAt = DateTimeOffset.UtcNow;
        m_outbox = Channel.CreateBounded<OutboundMessage>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            _ =>
            {
                Interlocked.Decrement(ref m_queueDepth);
                Interlocked.Increment(ref m_droppedMessages);
            });
    }

    public string ClientId { get; }
    public string UserId { get; }
    public string DisplayName { get; }
    public IReadOnlyList<string> Roles { get; }
    public DateTimeOffset ConnectedAt { get; }
    public ChannelReader<OutboundMessage> Outbox => m_outbox.Reader;
    public bool PageVisible => Volatile.Read(ref m_pageVisible) != 0;

    public bool TryEnqueue(OutboundMessage message)
    {
        Interlocked.Increment(ref m_queueDepth);
        if (!m_outbox.Writer.TryWrite(message))
        {
            Interlocked.Decrement(ref m_queueDepth);
            return false;
        }

        Interlocked.Increment(ref m_enqueuedMessages);
        Interlocked.Exchange(
            ref m_lastEnqueuedUnixMilliseconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return true;
    }

    public void MarkDequeued()
    {
        Interlocked.Decrement(ref m_queueDepth);
        Interlocked.Exchange(
            ref m_lastDequeuedUnixMilliseconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Complete() => m_outbox.Writer.TryComplete();

    public void SetPageVisible(bool visible) =>
        Volatile.Write(ref m_pageVisible, visible ? 1 : 0);

    public bool ShouldDeliver(OutboundMessage message) =>
        message.MessageType != WebSocketMessageType.Binary || PageVisible;

    public void UpdateAudioDiagnostics(
        RadioBrowserAudioDiagnostics diagnostics) =>
        Volatile.Write(ref m_audioDiagnostics, diagnostics);

    public void UpdateNetworkDiagnostics(
        RadioBrowserNetworkDiagnostics diagnostics) =>
        Volatile.Write(ref m_networkDiagnostics, diagnostics);

    internal bool TryAcceptTxEnvelope(
        long sequence,
        string? intentId,
        out string error)
    {
        lock (m_txProtocolGate)
        {
            if (sequence <= m_lastTxSequence)
            {
                error = "stale-tx-sequence";
                return false;
            }

            m_lastTxSequence = sequence;
            if (intentId is not null && !m_seenTxIntentIds.Add(intentId))
            {
                error = "replayed-tx-intent";
                return false;
            }

            if (intentId is not null)
            {
                m_seenTxIntentOrder.Enqueue(intentId);
                while (m_seenTxIntentOrder.Count > TxIntentReplayCapacity)
                {
                    string expired = m_seenTxIntentOrder.Dequeue();
                    m_seenTxIntentIds.Remove(expired);
                }
            }

            error = string.Empty;
            return true;
        }
    }

    internal long LastTxSequence
    {
        get
        {
            lock (m_txProtocolGate)
            {
                return m_lastTxSequence;
            }
        }
    }

    public PresenceSnapshot ToPresence() =>
        new(ClientId, UserId, DisplayName, Roles, ConnectedAt);

    public RadioClientQueueDiagnostics GetDiagnostics() =>
        new(
            ClientId,
            ConnectedAt,
            Math.Max(0, Volatile.Read(ref m_queueDepth)),
            QueueCapacity,
            Volatile.Read(ref m_enqueuedMessages),
            Volatile.Read(ref m_droppedMessages),
            FromUnixMilliseconds(
                Volatile.Read(ref m_lastEnqueuedUnixMilliseconds)),
            FromUnixMilliseconds(
                Volatile.Read(ref m_lastDequeuedUnixMilliseconds)),
            Volatile.Read(ref m_audioDiagnostics),
            Volatile.Read(ref m_networkDiagnostics));

    private static DateTimeOffset? FromUnixMilliseconds(long value) =>
        value <= 0
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(value);
}

public sealed class RadioCoordinator : IDisposable
{
    public const int MaxClientMessageBytes = 64 * 1024;

    private static readonly HashSet<string> AllowedModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "LSB", "USB", "CW", "CWR", "DIGL", "DIGU", "AM", "SAM", "FM", "NFM"
        };
    private static readonly HashSet<string> AllowedAgcModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "OFF", "SLOW", "MED", "FAST"
        };
    private static readonly HashSet<string> AllowedRxAntennas =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ANT1", "ANT2", "RX_A", "RX_B", "XVTR"
        };
    private static readonly IReadOnlyDictionary<string, SimulationBand> SimulationBands =
        new Dictionary<string, SimulationBand>(StringComparer.Ordinal)
        {
            ["160"] = new(1_900_000, "LSB", -3_000, -300),
            ["80"] = new(3_750_000, "LSB", -3_000, -300),
            ["60"] = new(5_357_000, "USB", 300, 3_000),
            ["40"] = new(7_150_000, "LSB", -3_000, -300),
            ["30"] = new(10_125_000, "CW", -250, 250),
            ["20"] = new(14_175_000, "USB", 300, 3_000),
            ["17"] = new(18_118_000, "USB", 300, 3_000),
            ["15"] = new(21_225_000, "USB", 300, 3_000),
            ["12"] = new(24_940_000, "USB", 300, 3_000),
            ["10"] = new(28_400_000, "USB", 300, 3_000),
            ["6"] = new(50_150_000, "USB", 300, 3_000),
            ["33"] = new(10_000_000, "AM", -3_000, 3_000)
        };

    private readonly object m_stateGate = new();
    private readonly object m_sliceRefreshGate = new();
    private readonly ConcurrentDictionary<string, RadioClientConnection> m_clients = new();
    private readonly ILogger<RadioCoordinator> m_logger;
    private readonly JsonSerializerOptions m_jsonOptions;
    private readonly TxLeaseManager m_txLeaseManager;
    private readonly RadioTxOccupancyRegistry m_txOccupancyRegistry;
    private readonly StationTxProductionLifecycle? m_txLifecycle;
    private readonly FlexRadioCommandRouter m_flexRouter;
    private readonly IRadioIntentTransport? m_intentTransport;
    private readonly RadioSettings m_radioSettings;
    private readonly RadioTuneTracker m_tuneTracker = new();
    private readonly bool m_allowTransmit;
    private readonly bool m_browserTxLeaseEnabled;
    private readonly string m_radioMode;
    private CancellationTokenSource? m_sliceRefreshCancellation;
    private RadioSnapshot m_snapshot;
    private int m_disposed;

    internal RadioCoordinator(
        ILogger<RadioCoordinator> logger,
        IOptions<RadioSettings> settings,
        TxLeaseManager txLeaseManager,
        FlexRadioCommandRouter? flexRouter = null,
        IRadioIntentTransport? intentTransport = null,
        RadioTxOccupancyRegistry? txOccupancyRegistry = null,
        StationTxProductionLifecycle? txLifecycle = null)
    {
        m_logger = logger;
        m_txLeaseManager = txLeaseManager;
        m_txOccupancyRegistry =
            txOccupancyRegistry ?? new RadioTxOccupancyRegistry();
        m_txLifecycle = txLifecycle;
        m_flexRouter = flexRouter ?? new FlexRadioCommandRouter();
        m_intentTransport = intentTransport;
        m_radioSettings = settings.Value;
        m_allowTransmit = settings.Value.AllowTransmit;
        m_browserTxLeaseEnabled = settings.Value.BrowserTxLeaseEnabled;
        m_radioMode = settings.Value.Mode;
        string sessionId = string.IsNullOrWhiteSpace(settings.Value.SessionId)
            ? "radio-1"
            : settings.Value.SessionId;
        bool isSimulation = string.Equals(
            m_radioMode,
            "Simulation",
            StringComparison.OrdinalIgnoreCase);
        long centerFrequencyHz = settings.Value.CenterFrequencyHz;
        int bandwidthHz = settings.Value.BandwidthHz;

        m_jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PanadapterSnapshot initialPanadapter = new(
            CenterFrequencyHz: centerFrequencyHz,
            BandwidthHz: bandwidthHz,
            MinDbm: isSimulation ? -120 : settings.Value.MinDbm,
            MaxDbm: settings.Value.MaxDbm,
            FftAverage: 35,
            FramesPerSecond: isSimulation
                ? 30
                : settings.Value.FramesPerSecond,
            Id: isSimulation ? "SIM-PAN-1" : "PENDING",
            StreamId: isSimulation ? 1u : 0u);
        m_snapshot = new RadioSnapshot(
            Version: 1,
            SessionId: sessionId,
            RadioModel: isSimulation
                ? "FLEX-8600 (simulated)"
                : "FLEX (connecting)",
            Serial: isSimulation ? "SIM-8600" : "RX-ONLY",
            Connected: isSimulation,
            CanTransmit: false,
            ActiveSliceId: "A",
            Panadapter: initialPanadapter,
            Slices:
            [
                new SliceSnapshot(
                    "A",
                    Math.Clamp(
                        settings.Value.InitialSliceFrequencyHz,
                        centerFrequencyHz - (bandwidthHz / 2L),
                        centerFrequencyHz + (bandwidthHz / 2L)),
                    "USB", 300, 3_000, 50, 20, true, false,
                    PanStreamId: initialPanadapter.StreamId),
                new SliceSnapshot(
                    "B",
                    Math.Clamp(
                        settings.Value.SecondarySliceFrequencyHz,
                        centerFrequencyHz - (bandwidthHz / 2L),
                        centerFrequencyHz + (bandwidthHz / 2L)),
                    "USB", 300, 3_000, 45, 0, false, false,
                    PanStreamId: initialPanadapter.StreamId)
            ],
            Panadapters: [initialPanadapter],
            ConnectionState: isSimulation
                ? "connected"
                : "connecting");
        m_txLeaseManager.Changed += HandleTxLeaseChange;
    }

    public bool AllowTransmit => m_allowTransmit;
    public bool BrowserTxLeaseEnabled => m_browserTxLeaseEnabled;
    public TxLeaseStatus? TxLeaseStatus =>
        m_txLeaseManager
            .GetCurrent(m_radioSettings.RadioId)?
            .ToStatus();
    public RadioTxOccupancySnapshot TxOccupancy =>
        m_txOccupancyRegistry.GetSnapshot(m_radioSettings.RadioId);

    public RadioSnapshot Snapshot
    {
        get
        {
            lock (m_stateGate)
            {
                return m_snapshot;
            }
        }
    }

    public IReadOnlyList<PresenceSnapshot> Presence =>
        m_clients.Values
            .OrderBy(connection => connection.ConnectedAt)
            .Select(connection => connection.ToPresence())
            .ToArray();

    public IReadOnlyList<RadioClientQueueDiagnostics> ClientDiagnostics =>
        m_clients.Values
            .OrderBy(connection => connection.ConnectedAt)
            .Select(connection => connection.GetDiagnostics())
            .ToArray();

    public RadioTuneTimingDiagnostics TuneDiagnostics =>
        m_tuneTracker.Snapshot;

    public RadioClientConnection Register(ClaimsPrincipal user)
    {
        string clientId = Guid.NewGuid().ToString("N");
        string userId =
            user.FindFirstValue("oid") ??
            user.FindFirstValue(ClaimTypes.NameIdentifier) ??
            "unknown";
        string displayName =
            user.FindFirstValue("name") ??
            user.Identity?.Name ??
            user.FindFirstValue("preferred_username") ??
            "Authenticated operator";
        string[] roles = AetherSDR.Web.Auth.AetherRoles.All
            .Where(user.IsInRole)
            .ToArray();

        RadioClientConnection connection =
            new(clientId, userId, displayName, roles);
        if (!m_clients.TryAdd(clientId, connection))
        {
            throw new InvalidOperationException("Could not register browser client.");
        }

        m_txLifecycle?.ObserveBrowserConnection(
            connection.ClientId,
            connected: true,
            authenticated: user.Identity?.IsAuthenticated == true);
        m_logger.LogInformation(
            "Web client {ClientId} connected as {UserId}",
            clientId,
            userId);
        return connection;
    }

    public void NotifyPresenceChanged()
    {
        BroadcastPresence();
    }

    public void ObserveBrowserActivity(
        RadioClientConnection connection,
        bool authenticated)
    {
        ArgumentNullException.ThrowIfNull(connection);
        m_txLifecycle?.ObserveGatewayHeartbeat();
        m_txLifecycle?.ObserveBrowserActivity(
            connection.ClientId,
            authenticated);
    }

    public void ObserveEngineHeartbeat(uint stationClientHandle)
    {
        m_txLifecycle?.ObserveGatewayHeartbeat();
        m_txLifecycle?.ObserveEngineHeartbeat(stationClientHandle);
    }

    public void Unregister(string clientId, bool notifyPresence = true)
    {
        if (!m_clients.TryRemove(clientId, out RadioClientConnection? connection))
        {
            return;
        }

        connection.Complete();
        m_txLeaseManager.TryReleaseOwner(
            m_radioSettings.RadioId,
            m_radioSettings.SessionId,
            clientId,
            "client-disconnected",
            out _);
        m_txLifecycle?.ObserveBrowserConnection(
            clientId,
            connected: false,
            authenticated: false);

        m_logger.LogInformation("Web client {ClientId} disconnected", clientId);
        if (notifyPresence)
        {
            BroadcastPresence();
        }
    }

    public void SendJson(RadioClientConnection connection, object message)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, m_jsonOptions);
        connection.TryEnqueue(
            new OutboundMessage(WebSocketMessageType.Text, bytes));
    }

    public void BroadcastJson(object message)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, m_jsonOptions);
        OutboundMessage outbound =
            new(WebSocketMessageType.Text, bytes);

        foreach (RadioClientConnection connection in m_clients.Values)
        {
            connection.TryEnqueue(outbound);
        }
    }

    public void BroadcastSpectrum(ReadOnlyMemory<byte> frame)
    {
        OutboundMessage outbound =
            new(WebSocketMessageType.Binary, frame);

        foreach (RadioClientConnection connection in m_clients.Values)
        {
            if (connection.PageVisible)
            {
                connection.TryEnqueue(outbound);
            }
        }
    }

    public void BroadcastAudio(ReadOnlyMemory<byte> frame)
    {
        if (Snapshot.Slices.Count == 0)
        {
            return;
        }

        OutboundMessage outbound =
            new(WebSocketMessageType.Binary, frame);

        foreach (RadioClientConnection connection in m_clients.Values)
        {
            if (connection.PageVisible)
            {
                connection.TryEnqueue(outbound);
            }
        }
    }

    public void SetRadioConnection(
        bool connected,
        string? radioModel = null,
        string? serial = null,
        string? connectionState = null,
        string? connectionError = null,
        uint stationClientHandle = 0)
    {
        RadioSnapshot updated;
        lock (m_stateGate)
        {
            updated = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                RadioModel = string.IsNullOrWhiteSpace(radioModel)
                    ? m_snapshot.RadioModel
                    : radioModel,
                Serial = string.IsNullOrWhiteSpace(serial)
                    ? m_snapshot.Serial
                    : serial,
                Connected = connected,
                CanTransmit = false,
                ConnectionState = connected
                    ? "connected"
                    : string.IsNullOrWhiteSpace(connectionState)
                        ? "connecting"
                        : connectionState,
                ConnectionError = connected
                    ? null
                    : string.IsNullOrWhiteSpace(connectionError)
                        ? null
                        : connectionError
            };
            m_snapshot = updated;
        }

        m_txLifecycle?.ObserveGatewayHeartbeat();
        m_txLifecycle?.ObserveEngineConnection(
            connected,
            stationClientHandle);
        BroadcastJson(new
        {
            @event = "snapshot",
            snapshot = updated
        });
    }

    public void SetLiveSlices(IReadOnlyList<SliceSnapshot> slices)
    {
        m_tuneTracker.Observe(slices);
        RadioSnapshot? updated = null;
        lock (m_stateGate)
        {
            SliceSnapshot[] ordered = slices
                .OrderBy(slice => slice.RadioId)
                .ToArray();
            string activeSliceId =
                ordered.FirstOrDefault(slice => slice.IsActive)?.Id ??
                ordered.FirstOrDefault(
                    slice => string.Equals(
                        slice.Id,
                        m_snapshot.ActiveSliceId,
                        StringComparison.OrdinalIgnoreCase))?.Id ??
                ordered.FirstOrDefault()?.Id ??
                string.Empty;

            if (m_snapshot.Slices.SequenceEqual(ordered) &&
                string.Equals(
                    m_snapshot.ActiveSliceId,
                    activeSliceId,
                    StringComparison.Ordinal))
            {
                return;
            }

            updated = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                ActiveSliceId = activeSliceId,
                Slices = ordered
            };
            m_snapshot = updated;
        }

        BroadcastJson(new
        {
            @event = "snapshot",
            snapshot = updated
        });
    }

    public void SetPanadapter(
        long centerFrequencyHz,
        int bandwidthHz,
        int? minDbm = null,
        int? maxDbm = null,
        int? fftAverage = null,
        int? framesPerSecond = null,
        bool? wnbEnabled = null,
        int? wnbLevel = null)
    {
        SetPanadapter(
            Snapshot.Panadapter.Id,
            centerFrequencyHz,
            bandwidthHz,
            minDbm,
            maxDbm,
            fftAverage,
            framesPerSecond,
            wnbEnabled,
            wnbLevel);
    }

    public void SetPanadapter(
        string panId,
        long centerFrequencyHz,
        int bandwidthHz,
        int? minDbm = null,
        int? maxDbm = null,
        int? fftAverage = null,
        int? framesPerSecond = null,
        bool? wnbEnabled = null,
        int? wnbLevel = null)
    {
        UpdatePanadapter(
            panId,
            centerFrequencyHz,
            bandwidthHz,
            minDbm,
            maxDbm,
            fftAverage,
            framesPerSecond,
            wnbEnabled,
            wnbLevel,
            broadcastSnapshot: true);
    }

    private void UpdatePanadapter(
        string panId,
        long centerFrequencyHz,
        int bandwidthHz,
        int? minDbm,
        int? maxDbm,
        int? fftAverage,
        int? framesPerSecond,
        bool? wnbEnabled,
        int? wnbLevel,
        bool broadcastSnapshot)
    {
        if (string.IsNullOrWhiteSpace(panId) ||
            centerFrequencyHz is < 100_000 or > 60_000_000 ||
            bandwidthHz is < 10_000 or > 14_000_000 ||
            minDbm is < -200 or > 0 ||
            maxDbm is < -200 or > 0 ||
            fftAverage is < 0 or > 100 ||
            framesPerSecond is < 1 or > 30 ||
            wnbLevel is < 0 or > 100)
        {
            return;
        }

        RadioSnapshot? updated = null;
        lock (m_stateGate)
        {
            PanadapterSnapshot[] panadapters =
                (m_snapshot.Panadapters ?? [m_snapshot.Panadapter]).ToArray();
            int panIndex = Array.FindIndex(
                panadapters,
                pan => string.Equals(
                    pan.Id,
                    panId,
                    StringComparison.OrdinalIgnoreCase));
            if (panIndex < 0)
            {
                return;
            }

            PanadapterSnapshot current = panadapters[panIndex];
            int nextMinDbm = minDbm ?? current.MinDbm;
            int nextMaxDbm = maxDbm ?? current.MaxDbm;
            int nextAverage =
                fftAverage ?? current.FftAverage;
            int nextFramesPerSecond =
                framesPerSecond ?? current.FramesPerSecond;
            bool nextWnbEnabled =
                wnbEnabled ?? current.WnbEnabled;
            int nextWnbLevel =
                wnbLevel ?? current.WnbLevel;
            if (nextMinDbm >= nextMaxDbm)
            {
                return;
            }

            if (current.CenterFrequencyHz == centerFrequencyHz &&
                current.BandwidthHz == bandwidthHz &&
                current.MinDbm == nextMinDbm &&
                current.MaxDbm == nextMaxDbm &&
                current.FftAverage == nextAverage &&
                current.FramesPerSecond == nextFramesPerSecond &&
                current.WnbEnabled == nextWnbEnabled &&
                current.WnbLevel == nextWnbLevel)
            {
                return;
            }

            PanadapterSnapshot nextPanadapter = current with
            {
                CenterFrequencyHz = centerFrequencyHz,
                BandwidthHz = bandwidthHz,
                MinDbm = nextMinDbm,
                MaxDbm = nextMaxDbm,
                FftAverage = nextAverage,
                FramesPerSecond = nextFramesPerSecond,
                WnbEnabled = nextWnbEnabled,
                WnbLevel = nextWnbLevel
            };
            panadapters[panIndex] = nextPanadapter;
            updated = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                Panadapter = panIndex == 0
                    ? nextPanadapter
                    : m_snapshot.Panadapter,
                Panadapters = panadapters
            };
            m_snapshot = updated;
        }

        if (broadcastSnapshot)
        {
            BroadcastJson(new
            {
                @event = "snapshot",
                snapshot = updated
            });
        }
    }

    public void ReplacePanadapters(
        IReadOnlyList<PanadapterSnapshot> panadapters)
    {
        PanadapterSnapshot[] normalized = panadapters
            .Where(pan => !string.IsNullOrWhiteSpace(pan.Id))
            .DistinctBy(pan => pan.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            return;
        }

        RadioSnapshot updated;
        lock (m_stateGate)
        {
            updated = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                Panadapter = normalized[0],
                Panadapters = normalized
            };
            m_snapshot = updated;
        }
        BroadcastJson(new { @event = "snapshot", snapshot = updated });
    }

    private void AddPanadapter(PanadapterSnapshot panadapter)
    {
        RadioSnapshot updated;
        lock (m_stateGate)
        {
            PanadapterSnapshot[] existing =
                (m_snapshot.Panadapters ?? [m_snapshot.Panadapter]).ToArray();
            if (existing.Any(
                    pan => string.Equals(
                        pan.Id,
                        panadapter.Id,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            PanadapterSnapshot[] next = [.. existing, panadapter];
            updated = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                Panadapters = next
            };
            m_snapshot = updated;
        }
        BroadcastJson(new { @event = "snapshot", snapshot = updated });
    }

    private void RemovePanadapter(string panId)
    {
        RadioSnapshot? updated = null;
        lock (m_stateGate)
        {
            PanadapterSnapshot[] existing =
                (m_snapshot.Panadapters ?? [m_snapshot.Panadapter]).ToArray();
            PanadapterSnapshot? removed = existing.FirstOrDefault(
                pan => string.Equals(
                    pan.Id,
                    panId,
                    StringComparison.OrdinalIgnoreCase));
            if (existing.Length <= 1 || removed is null)
            {
                return;
            }
            PanadapterSnapshot[] next = existing
                .Where(
                    pan => !string.Equals(
                        pan.Id,
                        panId,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            SliceSnapshot[] slices = m_snapshot.Slices
                .Where(slice => slice.PanStreamId != removed.StreamId)
                .ToArray();
            string activeSliceId = slices.Any(
                slice => string.Equals(
                    slice.Id,
                    m_snapshot.ActiveSliceId,
                    StringComparison.OrdinalIgnoreCase))
                ? m_snapshot.ActiveSliceId
                : slices.FirstOrDefault()?.Id ?? string.Empty;
            updated = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                Panadapter = next[0],
                Panadapters = next,
                Slices = slices,
                ActiveSliceId = activeSliceId
            };
            m_snapshot = updated;
        }
        BroadcastJson(new { @event = "snapshot", snapshot = updated });
    }

    public async Task<IntentResult> ApplyIntentAsync(
        ControlIntent intent,
        CancellationToken cancellationToken)
    {
        if (m_intentTransport is not null)
        {
            return await m_intentTransport.ApplyAsync(
                intent,
                Snapshot.Version,
                cancellationToken);
        }
        if (string.Equals(
                m_radioMode,
                "Simulation",
                StringComparison.OrdinalIgnoreCase))
        {
            return ApplyIntent(intent);
        }

        return await ApplyFlexIntentAsync(intent, cancellationToken);
    }

    public bool ApplyReceiveProjection(RadioSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        PanadapterSnapshot[] panadapters =
            (candidate.Panadapters ?? [candidate.Panadapter]).ToArray();
        SliceSnapshot[] slices = candidate.Slices?.ToArray() ?? [];
        if (!IsBoundedProjectionText(candidate.RadioModel, 64) ||
            !IsBoundedProjectionText(candidate.Serial, 64) ||
            !IsBoundedProjectionText(candidate.ConnectionState, 64) ||
            candidate.ConnectionError is { Length: > 256 } ||
            candidate.ConnectionError?.Any(char.IsControl) == true ||
            panadapters.Length is < 1 or > 8 ||
            slices.Length > 8 ||
            panadapters.Select(pan => pan.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != panadapters.Length ||
            slices.Select(slice => slice.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != slices.Length ||
            panadapters.Any(pan => !IsValidProjectedPanadapter(pan)) ||
            slices.Any(slice => !IsValidProjectedSlice(slice)))
        {
            return false;
        }

        string activeSliceId = slices.Any(
            slice => string.Equals(
                slice.Id,
                candidate.ActiveSliceId,
                StringComparison.OrdinalIgnoreCase))
            ? candidate.ActiveSliceId
            : slices.FirstOrDefault(slice => slice.IsActive)?.Id ??
              slices.FirstOrDefault()?.Id ??
              string.Empty;
        RadioSnapshot updated;
        lock (m_stateGate)
        {
            updated = candidate with
            {
                Version = m_snapshot.Version + 1,
                SessionId = m_snapshot.SessionId,
                CanTransmit = false,
                ActiveSliceId = activeSliceId,
                Panadapter = panadapters[0],
                Panadapters = panadapters,
                Slices = slices,
                ConnectionError = candidate.Connected
                    ? null
                    : candidate.ConnectionError
            };
            m_snapshot = updated;
        }
        m_tuneTracker.Observe(slices);
        BroadcastJson(new { @event = "snapshot", snapshot = updated });
        return true;
    }

    private static bool IsValidProjectedPanadapter(
        PanadapterSnapshot pan) =>
        IsBoundedProjectionText(pan.Id, 64) &&
        pan.WaterfallId is { Length: <= 64 } &&
        !pan.WaterfallId.Any(char.IsControl) &&
        pan.CenterFrequencyHz is >= 100_000 and <= 60_000_000 &&
        pan.BandwidthHz is >= 10_000 and <= 14_000_000 &&
        pan.MinDbm is >= -200 and <= 0 &&
        pan.MaxDbm is >= -200 and <= 0 &&
        pan.MinDbm < pan.MaxDbm &&
        pan.FftAverage is >= 0 and <= 100 &&
        pan.FramesPerSecond is >= 1 and <= 30 &&
        pan.WnbLevel is >= 0 and <= 100;

    private static bool IsValidProjectedSlice(SliceSnapshot slice) =>
        IsBoundedProjectionText(slice.Id, 16) &&
        slice.FrequencyHz is >= 100_000 and <= 60_000_000 &&
        AllowedModes.Contains(slice.Mode) &&
        slice.FilterLowHz is >= -12_000 and <= 12_000 &&
        slice.FilterHighHz is >= -12_000 and <= 12_000 &&
        slice.FilterLowHz < slice.FilterHighHz &&
        slice.AfGain is >= 0 and <= 100 &&
        slice.Squelch is >= 0 and <= 100 &&
        slice.AudioPan is >= 0 and <= 100 &&
        AllowedAgcModes.Contains(slice.AgcMode) &&
        slice.AgcThreshold is >= 0 and <= 100 &&
        IsBoundedProjectionText(slice.RxAntenna, 16) &&
        slice.NbLevel is >= 0 and <= 100 &&
        slice.NrLevel is >= 0 and <= 100 &&
        slice.AnfLevel is >= 0 and <= 100 &&
        slice.NrlLevel is >= 0 and <= 100 &&
        slice.NrsLevel is >= 0 and <= 100 &&
        slice.NrfLevel is >= 0 and <= 100 &&
        slice.AnflLevel is >= 0 and <= 100 &&
        slice.DaxChannel is >= 0 and <= 8;

    private static bool IsBoundedProjectionText(
        string? value,
        int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    public IntentResult ApplyIntent(ControlIntent intent)
    {
        if (!string.Equals(
                m_radioMode,
                "Simulation",
                StringComparison.OrdinalIgnoreCase))
        {
            return IntentResult.Failure(
                "The live Flex bridge is receive-only; radio control is not connected yet.",
                Snapshot.Version);
        }

        if (string.Equals(
                intent.Action,
                "slice.create",
                StringComparison.Ordinal))
        {
            return CreateSimulationSlice(intent.Values);
        }

        if (string.Equals(
                intent.Action,
                "slice.remove",
                StringComparison.Ordinal))
        {
            return RemoveSimulationSlice(intent.Selector);
        }

        if (string.Equals(
                intent.Action,
                "pan.create",
                StringComparison.Ordinal))
        {
            return CreateSimulationPan(intent.Values);
        }

        if (string.Equals(
                intent.Action,
                "pan.remove",
                StringComparison.Ordinal))
        {
            return RemoveSimulationPan(intent.Selector);
        }

        if (string.Equals(
                intent.Action,
                "pan.set",
                StringComparison.Ordinal))
        {
            return SetSimulationPan(intent.Selector, intent.Values);
        }

        if (!string.Equals(
                intent.Action,
                "slice.set",
                StringComparison.Ordinal))
        {
            return IntentResult.Failure(
                "Unsupported radio intent.",
                Snapshot.Version);
        }

        lock (m_stateGate)
        {
            int index = m_snapshot.Slices
                .Select((slice, position) => (slice, position))
                .Where(pair =>
                    string.Equals(
                        pair.slice.Id,
                        intent.Selector,
                        StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.position)
                .DefaultIfEmpty(-1)
                .First();

            if (index < 0)
            {
                return IntentResult.Failure(
                    $"Unknown slice '{intent.Selector}'.",
                    m_snapshot.Version);
            }

            SliceSnapshot current = m_snapshot.Slices[index];
            SliceSnapshot updated = current;
            Dictionary<string, object?> changes = new(StringComparer.Ordinal);
            bool filterChanged = false;

            if (intent.Values.ValueKind != JsonValueKind.Object)
            {
                return IntentResult.Failure(
                    "Intent values must be a JSON object.",
                    m_snapshot.Version);
            }

            foreach (JsonProperty property in intent.Values.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "isActive":
                        {
                            if (property.Value.ValueKind != JsonValueKind.True)
                            {
                                return IntentResult.Failure(
                                    "isActive can only be set to true.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { IsActive = true };
                            changes[property.Name] = true;
                            break;
                        }

                    case "frequencyHz":
                        {
                            if (!property.Value.TryGetInt64(out long value) ||
                                value is < 100_000 or > 60_000_000)
                            {
                                return IntentResult.Failure(
                                    "frequencyHz must be between 100 kHz and 60 MHz.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { FrequencyHz = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "mode":
                        {
                            string? value = property.Value.GetString()?.ToUpperInvariant();
                            if (value is null || !AllowedModes.Contains(value))
                            {
                                return IntentResult.Failure(
                                    "Unsupported mode.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { Mode = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "filterLowHz":
                        {
                            if (!property.Value.TryGetInt32(out int value) ||
                                value is < -12_000 or > 12_000)
                            {
                                return IntentResult.Failure(
                                    "filterLowHz must be between -12000 and 12000.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { FilterLowHz = value };
                            changes[property.Name] = value;
                            filterChanged = true;
                            break;
                        }

                    case "filterHighHz":
                        {
                            if (!property.Value.TryGetInt32(out int value) ||
                                value is < -12_000 or > 12_000)
                            {
                                return IntentResult.Failure(
                                    "filterHighHz must be between -12000 and 12000.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { FilterHighHz = value };
                            changes[property.Name] = value;
                            filterChanged = true;
                            break;
                        }

                    case "afGain":
                        {
                            if (!property.Value.TryGetInt32(out int value) ||
                                value is < 0 or > 100)
                            {
                                return IntentResult.Failure(
                                    "afGain must be between 0 and 100.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { AfGain = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "squelch":
                        {
                            if (!property.Value.TryGetInt32(out int value) ||
                                value is < 0 or > 100)
                            {
                                return IntentResult.Failure(
                                    "squelch must be between 0 and 100.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { Squelch = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "squelchEnabled":
                        {
                            if (property.Value.ValueKind is not
                                (JsonValueKind.True or JsonValueKind.False))
                            {
                                return IntentResult.Failure(
                                    "squelchEnabled must be a boolean.",
                                    m_snapshot.Version);
                            }

                            bool value = property.Value.GetBoolean();
                            updated = updated with { SquelchEnabled = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "audioMute":
                        {
                            if (property.Value.ValueKind is not
                                (JsonValueKind.True or JsonValueKind.False))
                            {
                                return IntentResult.Failure(
                                    "audioMute must be a boolean.",
                                    m_snapshot.Version);
                            }

                            bool value = property.Value.GetBoolean();
                            updated = updated with { IsMuted = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "audioPan":
                        {
                            if (!property.Value.TryGetInt32(out int value) ||
                                value is < 0 or > 100)
                            {
                                return IntentResult.Failure(
                                    "audioPan must be between 0 and 100.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { AudioPan = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "agcMode":
                        {
                            string? value =
                                property.Value.GetString()?.ToUpperInvariant();
                            if (value is null || !AllowedAgcModes.Contains(value))
                            {
                                return IntentResult.Failure(
                                    "Unsupported AGC mode.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { AgcMode = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "agcThreshold":
                        {
                            if (!property.Value.TryGetInt32(out int value) ||
                                value is < 0 or > 100)
                            {
                                return IntentResult.Failure(
                                    "agcThreshold must be between 0 and 100.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { AgcThreshold = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "rxAntenna":
                        {
                            string? value =
                                property.Value.GetString()?.ToUpperInvariant();
                            if (value is null || !AllowedRxAntennas.Contains(value))
                            {
                                return IntentResult.Failure(
                                    "Unsupported receive antenna.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { RxAntenna = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "daxChannel":
                        {
                            if (!property.Value.TryGetInt32(out int value) ||
                                value is < 0 or > 8)
                            {
                                return IntentResult.Failure(
                                    "daxChannel must be between 0 and 8.",
                                    m_snapshot.Version);
                            }

                            updated = updated with { DaxChannel = value };
                            changes[property.Name] = value;
                            break;
                        }

                    case "nb":
                    case "nr":
                    case "anf":
                    case "nrl":
                    case "nrs":
                    case "rnn":
                    case "nrf":
                    case "anfl":
                    case "anft":
                        {
                            if (property.Value.ValueKind is not
                                (JsonValueKind.True or JsonValueKind.False))
                            {
                                return IntentResult.Failure(
                                    $"{property.Name} must be a boolean.",
                                    m_snapshot.Version);
                            }

                            bool value = property.Value.GetBoolean();
                            updated = property.Name switch
                            {
                                "nb" => updated with { Nb = value },
                                "nr" => updated with { Nr = value },
                                "anf" => updated with { Anf = value },
                                "nrl" => updated with { Nrl = value },
                                "nrs" => updated with { Nrs = value },
                                "rnn" => updated with { Rnn = value },
                                "nrf" => updated with { Nrf = value },
                                "anfl" => updated with { Anfl = value },
                                "anft" => updated with { Anft = value },
                                _ => updated
                            };
                            changes[property.Name] = value;
                            break;
                        }

                    case "nbLevel":
                    case "nrLevel":
                    case "anfLevel":
                    case "nrlLevel":
                    case "nrsLevel":
                    case "nrfLevel":
                    case "anflLevel":
                        {
                            if (!property.Value.TryGetInt32(out int value) ||
                                value is < 0 or > 100)
                            {
                                return IntentResult.Failure(
                                    $"{property.Name} must be between 0 and 100.",
                                    m_snapshot.Version);
                            }

                            updated = property.Name switch
                            {
                                "nbLevel" => updated with { NbLevel = value },
                                "nrLevel" => updated with { NrLevel = value },
                                "anfLevel" => updated with { AnfLevel = value },
                                "nrlLevel" => updated with { NrlLevel = value },
                                "nrsLevel" => updated with { NrsLevel = value },
                                "nrfLevel" => updated with { NrfLevel = value },
                                "anflLevel" => updated with { AnflLevel = value },
                                _ => updated
                            };
                            changes[property.Name] = value;
                            break;
                        }

                    default:
                        return IntentResult.Failure(
                            $"Property '{property.Name}' is not controllable.",
                            m_snapshot.Version);
                }
            }

            if (filterChanged &&
                !TryValidateFilterEdges(
                    updated.Mode,
                    updated.FilterLowHz,
                    updated.FilterHighHz,
                    out string filterError))
            {
                return IntentResult.Failure(
                    filterError,
                    m_snapshot.Version);
            }

            if (changes.Count == 0)
            {
                return IntentResult.Failure(
                    "No supported changes were supplied.",
                    m_snapshot.Version);
            }

            List<SliceSnapshot> slices = m_snapshot.Slices.ToList();
            string activeSliceId = m_snapshot.ActiveSliceId;
            if (updated.IsActive)
            {
                for (int sliceIndex = 0; sliceIndex < slices.Count; sliceIndex++)
                {
                    SliceSnapshot slice = slices[sliceIndex];
                    slices[sliceIndex] = slice with
                    {
                        IsActive = sliceIndex == index
                    };
                }

                activeSliceId = updated.Id;
                updated = updated with { IsActive = true };
            }

            slices[index] = updated;
            m_snapshot = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                ActiveSliceId = activeSliceId,
                Slices = slices
            };

            IntentResult result = new(
                true,
                null,
                m_snapshot.Version,
                "slice",
                updated.Id,
                changes);
            BroadcastJson(new
            {
                @event = "changed",
                sessionId = m_snapshot.SessionId,
                model = result.Model,
                selector = result.Selector,
                version = result.Version,
                changes = result.Changes
            });
            return result;
        }
    }

    private IntentResult CreateSimulationSlice(JsonElement values)
    {
        RadioSnapshot updatedSnapshot;
        IntentResult result;
        lock (m_stateGate)
        {
            if (m_snapshot.Slices.Count >= 8)
            {
                return IntentResult.Failure(
                    "The simulated radio supports at most eight slices.",
                    m_snapshot.Version);
            }

            if (values.ValueKind != JsonValueKind.Object)
            {
                return IntentResult.Failure(
                    "Intent values must be a JSON object.",
                    m_snapshot.Version);
            }

            SliceSnapshot? template = m_snapshot.Slices.FirstOrDefault(
                slice => string.Equals(
                    slice.Id,
                    m_snapshot.ActiveSliceId,
                    StringComparison.OrdinalIgnoreCase));
            PanadapterSnapshot[] panadapters =
                (m_snapshot.Panadapters ?? [m_snapshot.Panadapter]).ToArray();
            string targetPanId = panadapters.FirstOrDefault(
                pan => pan.StreamId == template?.PanStreamId)?.Id ??
                panadapters[0].Id;
            long frequencyHz =
                template?.FrequencyHz ??
                panadapters[0].CenterFrequencyHz;
            string mode = template?.Mode ?? "USB";

            foreach (JsonProperty property in values.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "frequencyHz":
                        if (!property.Value.TryGetInt64(out frequencyHz) ||
                            frequencyHz is < 100_000 or > 60_000_000)
                        {
                            return IntentResult.Failure(
                                "frequencyHz must be between 100 kHz and 60 MHz.",
                                m_snapshot.Version);
                        }
                        break;

                    case "mode":
                        mode = property.Value.GetString()?.ToUpperInvariant() ??
                            string.Empty;
                        if (!AllowedModes.Contains(mode))
                        {
                            return IntentResult.Failure(
                                "Unsupported mode.",
                                m_snapshot.Version);
                        }
                        break;

                    case "panId":
                        targetPanId =
                            property.Value.GetString() ?? string.Empty;
                        break;

                    default:
                        return IntentResult.Failure(
                            $"Property '{property.Name}' is not valid when creating a slice.",
                            m_snapshot.Version);
                }
            }

            PanadapterSnapshot? targetPan = panadapters.FirstOrDefault(
                pan => string.Equals(
                    pan.Id,
                    targetPanId,
                    StringComparison.OrdinalIgnoreCase));
            if (targetPan is null)
            {
                return IntentResult.Failure(
                    $"Unknown panadapter '{targetPanId}'.",
                    m_snapshot.Version);
            }
            if (!IsWithinPan(targetPan, frequencyHz))
            {
                frequencyHz = targetPan.CenterFrequencyHz;
            }

            string id = Enumerable.Range('A', 26)
                .Select(value => ((char)value).ToString())
                .First(candidate => !m_snapshot.Slices.Any(
                    slice => string.Equals(
                        slice.Id,
                        candidate,
                        StringComparison.OrdinalIgnoreCase)));
            List<SliceSnapshot> slices = m_snapshot.Slices
                .Select(slice => slice with { IsActive = false })
                .ToList();
            SliceSnapshot created = new(
                id,
                frequencyHz,
                mode,
                template?.FilterLowHz ?? 300,
                template?.FilterHighHz ?? 3_000,
                template?.AfGain ?? 50,
                template?.Squelch ?? 0,
                true,
                false,
                PanStreamId: targetPan.StreamId);
            slices.Add(created);
            updatedSnapshot = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                ActiveSliceId = id,
                Slices = slices
            };
            m_snapshot = updatedSnapshot;
            result = new IntentResult(
                true,
                null,
                updatedSnapshot.Version,
                "slice",
                id,
                new Dictionary<string, object?>
                {
                    ["created"] = true,
                    ["frequencyHz"] = frequencyHz,
                    ["mode"] = mode
                });
        }

        BroadcastJson(new
        {
            @event = "snapshot",
            snapshot = updatedSnapshot
        });
        return result;
    }

    private IntentResult RemoveSimulationSlice(string selector)
    {
        RadioSnapshot updatedSnapshot;
        IntentResult result;
        lock (m_stateGate)
        {
            SliceSnapshot? removed = m_snapshot.Slices.FirstOrDefault(
                slice => string.Equals(
                    slice.Id,
                    selector,
                    StringComparison.OrdinalIgnoreCase));
            if (removed is null)
            {
                return IntentResult.Failure(
                    $"Unknown slice '{selector}'.",
                    m_snapshot.Version);
            }

            List<SliceSnapshot> slices = m_snapshot.Slices
                .Where(slice => !string.Equals(
                    slice.Id,
                    removed.Id,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            string activeSliceId = m_snapshot.ActiveSliceId;
            if (removed.IsActive ||
                string.Equals(
                    activeSliceId,
                    removed.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                activeSliceId = slices.FirstOrDefault()?.Id ?? string.Empty;
                for (int index = 0; index < slices.Count; index++)
                {
                    slices[index] = slices[index] with
                    {
                        IsActive = index == 0
                    };
                }
            }

            updatedSnapshot = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                ActiveSliceId = activeSliceId,
                Slices = slices
            };
            m_snapshot = updatedSnapshot;
            result = new IntentResult(
                true,
                null,
                updatedSnapshot.Version,
                "slice",
                removed.Id,
                new Dictionary<string, object?> { ["removed"] = true });
        }

        BroadcastJson(new
        {
            @event = "snapshot",
            snapshot = updatedSnapshot
        });
        return result;
    }

    private async Task<IntentResult> ApplyFlexIntentAsync(
        ControlIntent intent,
        CancellationToken cancellationToken)
    {
        try
        {
            return intent.Action switch
            {
                "pan.set" => await SetFlexPanAsync(
                    intent.Selector,
                    intent.Values,
                    cancellationToken),
                "pan.create" => await CreateFlexPanAsync(
                    intent.Values,
                    cancellationToken),
                "pan.remove" => await RemoveFlexPanAsync(
                    intent.Selector,
                    cancellationToken),
                "slice.create" => await CreateFlexSliceAsync(
                    intent.Values,
                    cancellationToken),
                "slice.remove" => await RemoveFlexSliceAsync(
                    intent.Selector,
                    cancellationToken),
                "slice.set" => await SetFlexSliceAsync(
                    intent.Selector,
                    intent.Values,
                    cancellationToken),
                _ => IntentResult.Failure(
                    "Unsupported radio intent.",
                    Snapshot.Version)
            };
        }
        catch (Exception exception)
            when (exception is IOException or
                  InvalidOperationException or
                  ObjectDisposedException or
                  TimeoutException)
        {
            m_logger.LogWarning(
                exception,
                "Flex receive-control intent {Action} failed",
                intent.Action);
            return IntentResult.Failure(
                "The Flex radio did not accept the receive-control request.",
                Snapshot.Version);
        }
    }

    private IntentResult CreateSimulationPan(JsonElement values)
    {
        RadioSnapshot snapshot = Snapshot;
        PanadapterSnapshot[] existing =
            (snapshot.Panadapters ?? [snapshot.Panadapter]).ToArray();
        if (existing.Length >= 4)
        {
            return IntentResult.Failure(
                "The web client supports at most four panadapters.",
                snapshot.Version);
        }

        long centerFrequencyHz = existing[^1].CenterFrequencyHz;
        if (values.ValueKind == JsonValueKind.Object &&
            values.TryGetProperty(
                "centerFrequencyHz",
                out JsonElement centerElement))
        {
            if (!centerElement.TryGetInt64(out centerFrequencyHz) ||
                centerFrequencyHz is < 100_000 or > 60_000_000)
            {
                return IntentResult.Failure(
                    "centerFrequencyHz must be between 100 kHz and 60 MHz.",
                    snapshot.Version);
            }
        }

        uint streamId = existing.Max(pan => pan.StreamId) + 1;
        string id = $"SIM-PAN-{streamId}";
        PanadapterSnapshot source = existing[0];
        PanadapterSnapshot created = source with
        {
            Id = id,
            StreamId = streamId,
            WaterfallId = string.Empty,
            CenterFrequencyHz = ClampPanCenter(
                centerFrequencyHz,
                source.BandwidthHz)
        };
        AddPanadapter(created);
        return new IntentResult(
            true,
            null,
            Snapshot.Version,
            "panadapter",
            id,
            new Dictionary<string, object?>
            {
                ["created"] = true,
                ["streamId"] = streamId
            });
    }

    private IntentResult RemoveSimulationPan(string selector)
    {
        RadioSnapshot snapshot = Snapshot;
        PanadapterSnapshot[] existing =
            (snapshot.Panadapters ?? [snapshot.Panadapter]).ToArray();
        PanadapterSnapshot? pan = FindPan(snapshot, selector);
        if (pan is null)
        {
            return IntentResult.Failure(
                $"Unknown panadapter '{selector}'.",
                snapshot.Version);
        }
        if (existing.Length <= 1)
        {
            return IntentResult.Failure(
                "The final panadapter cannot be removed.",
                snapshot.Version);
        }

        RemovePanadapter(pan.Id);
        return new IntentResult(
            true,
            null,
            Snapshot.Version,
            "panadapter",
            pan.Id,
            new Dictionary<string, object?> { ["removed"] = true });
    }

    private IntentResult SetSimulationPan(
        string selector,
        JsonElement values)
    {
        RadioSnapshot initialSnapshot = Snapshot;
        PanadapterSnapshot? pan = FindPan(initialSnapshot, selector);
        if (pan is null)
        {
            return IntentResult.Failure(
                $"Unknown panadapter '{selector}'.",
                initialSnapshot.Version);
        }
        if (!TryReadPanControl(
                values,
                pan,
                out PanControlRequest request,
                out string? error))
        {
            return IntentResult.Failure(
                error ?? "Invalid panadapter request.",
                initialSnapshot.Version);
        }

        if (request.BandKey is not null)
        {
            return SetSimulationBand(pan.Id, request);
        }

        SetPanadapter(
            pan.Id,
            request.CenterFrequencyHz,
            request.BandwidthHz,
            request.MinDbm,
            pan.MaxDbm,
            request.FftAverage,
            request.FramesPerSecond,
            request.WnbEnabled,
            request.WnbLevel);
        RadioSnapshot snapshot = Snapshot;
        return new IntentResult(
            true,
            null,
            snapshot.Version,
            "panadapter",
            snapshot.SessionId,
            request.Changes);
    }

    private IntentResult SetSimulationBand(
        string panId,
        PanControlRequest request)
    {
        if (request.BandKey is null ||
            !SimulationBands.TryGetValue(
                request.BandKey,
                out SimulationBand? band))
        {
            return IntentResult.Failure(
                "Unsupported band.",
                Snapshot.Version);
        }

        RadioSnapshot updated;
        lock (m_stateGate)
        {
            PanadapterSnapshot[] panadapters =
                (m_snapshot.Panadapters ?? [m_snapshot.Panadapter]).ToArray();
            int panIndex = Array.FindIndex(
                panadapters,
                candidate => string.Equals(
                    candidate.Id,
                    panId,
                    StringComparison.OrdinalIgnoreCase));
            if (panIndex < 0)
            {
                return IntentResult.Failure(
                    $"Unknown panadapter '{panId}'.",
                    m_snapshot.Version);
            }

            PanadapterSnapshot currentPan = panadapters[panIndex];
            long centerFrequencyHz = ClampPanCenter(
                band.FrequencyHz,
                currentPan.BandwidthHz);
            PanadapterSnapshot nextPan = currentPan with
            {
                CenterFrequencyHz = centerFrequencyHz
            };
            panadapters[panIndex] = nextPan;

            List<SliceSnapshot> slices = m_snapshot.Slices.ToList();
            int sliceIndex = slices.FindIndex(
                slice =>
                    slice.PanStreamId == currentPan.StreamId &&
                    (slice.IsActive ||
                     string.Equals(
                         slice.Id,
                         m_snapshot.ActiveSliceId,
                         StringComparison.OrdinalIgnoreCase)));
            if (sliceIndex < 0)
            {
                sliceIndex = slices.FindIndex(
                    slice => slice.PanStreamId == currentPan.StreamId);
            }
            if (sliceIndex >= 0)
            {
                SliceSnapshot currentSlice = slices[sliceIndex];
                slices[sliceIndex] = currentSlice with
                {
                    FrequencyHz = band.FrequencyHz,
                    Mode = band.Mode,
                    FilterLowHz = band.FilterLowHz,
                    FilterHighHz = band.FilterHighHz
                };
            }

            updated = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                Panadapter = panIndex == 0 ? nextPan : m_snapshot.Panadapter,
                Panadapters = panadapters,
                Slices = slices
            };
            m_snapshot = updated;
        }

        BroadcastJson(new { @event = "snapshot", snapshot = updated });
        return new IntentResult(
            true,
            null,
            updated.Version,
            "panadapter",
            panId,
            request.Changes);
    }

    private async Task<IntentResult> SetFlexPanAsync(
        string selector,
        JsonElement values,
        CancellationToken cancellationToken)
    {
        RadioSnapshot snapshot = Snapshot;
        PanadapterSnapshot? pan = FindPan(snapshot, selector);
        if (pan is null)
        {
            return IntentResult.Failure(
                $"Unknown panadapter '{selector}'.",
                snapshot.Version);
        }
        if (!TryReadPanControl(
                values,
                pan,
                out PanControlRequest request,
                out string? error))
        {
            return IntentResult.Failure(
                error ?? "Invalid panadapter request.",
                snapshot.Version);
        }

        string panId = pan.Id;
        if (!m_flexRouter.IsOwnedPan(pan.StreamId))
        {
            return IntentResult.Failure(
                "The Flex panadapter is not ready.",
                snapshot.Version);
        }

        List<string> commandFields = [];
        if (request.BandKey is not null)
        {
            commandFields.Add($"band={request.BandKey}");
        }
        if (request.Changes.ContainsKey("centerFrequencyHz"))
        {
            commandFields.Add(
                FormattableString.Invariant(
                    $"center={request.CenterFrequencyHz / 1_000_000d:F6}"));
        }
        if (request.Changes.ContainsKey("bandwidthHz"))
        {
            commandFields.Add(
                FormattableString.Invariant(
                    $"bandwidth={request.BandwidthHz / 1_000_000d:F6}"));
        }
        if (request.Changes.ContainsKey("minDbm"))
        {
            commandFields.Add($"min_dbm={request.MinDbm}");
            commandFields.Add($"max_dbm={pan.MaxDbm}");
        }
        if (request.Changes.ContainsKey("fftAverage"))
        {
            commandFields.Add($"average={request.FftAverage}");
        }
        if (request.Changes.ContainsKey("framesPerSecond"))
        {
            commandFields.Add($"fps={request.FramesPerSecond}");
        }
        if (request.Changes.ContainsKey("wnbEnabled"))
        {
            commandFields.Add($"wnb={(request.WnbEnabled ? 1 : 0)}");
        }
        if (request.Changes.ContainsKey("wnbLevel"))
        {
            commandFields.Add($"wnb_level={request.WnbLevel}");
        }

        FlexCommandResponse response = await m_flexRouter.SendAsync(
            $"display pan set {panId} {string.Join(' ', commandFields)}",
            TimeSpan.FromSeconds(4),
            cancellationToken);
        if (!response.IsSuccess)
        {
            return IntentResult.Failure(
                $"The radio rejected a panadapter control (0x{response.Code:x8}).",
                Snapshot.Version);
        }

        // FLEX firmware can ACK a display-pan command without echoing the
        // changed center to the issuing client. Match AetherSDR's native
        // behavior by advancing the model only after the wire command
        // succeeds; later radio status remains authoritative and reconciles it.
        if (request.BandKey is null)
        {
            m_flexRouter.ObservePanCenter(
                panId,
                request.CenterFrequencyHz);
            UpdatePanadapter(
                panId,
                request.CenterFrequencyHz,
                request.BandwidthHz,
                request.MinDbm,
                pan.MaxDbm,
                request.FftAverage,
                request.FramesPerSecond,
                request.WnbEnabled,
                request.WnbLevel,
                broadcastSnapshot: false);
        }
        RadioSnapshot confirmed = Snapshot;
        if (request.BandKey is null)
        {
            BroadcastJson(new
            {
                @event = "changed",
                sessionId = confirmed.SessionId,
                model = "panadapter",
                selector = panId,
                version = confirmed.Version,
                changes = request.Changes
            });
        }
        return new IntentResult(
            true,
            null,
            confirmed.Version,
            "panadapter",
            panId,
            request.Changes);
    }

    private async Task<IntentResult> CreateFlexPanAsync(
        JsonElement values,
        CancellationToken cancellationToken)
    {
        RadioSnapshot snapshot = Snapshot;
        PanadapterSnapshot[] existing =
            (snapshot.Panadapters ?? [snapshot.Panadapter]).ToArray();
        if (existing.Length >= 4)
        {
            return IntentResult.Failure(
                "The web client supports at most four panadapters.",
                snapshot.Version);
        }
        if (values.ValueKind != JsonValueKind.Object)
        {
            return IntentResult.Failure(
                "Intent values must be a JSON object.",
                snapshot.Version);
        }

        PanadapterSnapshot source = existing[0];
        long centerFrequencyHz = source.CenterFrequencyHz;
        foreach (JsonProperty property in values.EnumerateObject())
        {
            if (property.Name != "centerFrequencyHz")
            {
                return IntentResult.Failure(
                    $"Property '{property.Name}' is not valid when creating a panadapter.",
                    snapshot.Version);
            }
            if (!property.Value.TryGetInt64(out centerFrequencyHz) ||
                centerFrequencyHz is < 100_000 or > 60_000_000)
            {
                return IntentResult.Failure(
                    "centerFrequencyHz must be between 100 kHz and 60 MHz.",
                    snapshot.Version);
            }
        }
        centerFrequencyHz = ClampPanCenter(
            centerFrequencyHz,
            source.BandwidthHz);

        FlexCommandResponse create = await m_flexRouter.SendAsync(
            "display panafall create x=100 y=100",
            TimeSpan.FromSeconds(4),
            cancellationToken);
        if (!create.IsSuccess)
        {
            return IntentResult.Failure(
                $"The radio rejected panadapter creation (0x{create.Code:x8}).",
                Snapshot.Version);
        }

        (string? panId, string? waterfallId) =
            FlexRadioRxService.ParsePanafallCreateIds(create.Body);
        if (panId is null ||
            !FlexStatusParser.TryParseFlexUInt(panId, out uint streamId))
        {
            return IntentResult.Failure(
                "The radio created a panadapter without returning its stream ID.",
                Snapshot.Version);
        }

        m_flexRouter.RegisterPan(panId, centerFrequencyHz);
        PanadapterSnapshot created = source with
        {
            Id = panId,
            StreamId = streamId,
            WaterfallId = waterfallId ?? string.Empty,
            CenterFrequencyHz = centerFrequencyHz
        };
        AddPanadapter(created);

        string[] setupCommands =
        [
            $"display pan set {panId} xpixels={m_radioSettings.XPixels} ypixels={m_radioSettings.YPixels}",
            $"display pan set {panId} min_dbm={source.MinDbm} max_dbm={source.MaxDbm}",
            FormattableString.Invariant(
                $"display pan set {panId} center={centerFrequencyHz / 1_000_000d:F6} bandwidth={source.BandwidthHz / 1_000_000d:F6}"),
            $"display pan set {panId} fps={source.FramesPerSecond}"
        ];
        foreach (string command in setupCommands)
        {
            FlexCommandResponse setup = await m_flexRouter.SendAsync(
                command,
                TimeSpan.FromSeconds(4),
                cancellationToken);
            if (!setup.IsSuccess)
            {
                m_flexRouter.UnregisterPan(panId);
                RemovePanadapter(panId);
                await TryRemoveFlexDisplayAsync(
                    panId,
                    waterfallId,
                    cancellationToken);
                return IntentResult.Failure(
                    $"The radio rejected panadapter setup (0x{setup.Code:x8}).",
                    Snapshot.Version);
            }
        }

        return new IntentResult(
            true,
            null,
            Snapshot.Version,
            "panadapter",
            panId,
            new Dictionary<string, object?>
            {
                ["created"] = true,
                ["streamId"] = streamId
            });
    }

    private async Task<IntentResult> RemoveFlexPanAsync(
        string selector,
        CancellationToken cancellationToken)
    {
        RadioSnapshot snapshot = Snapshot;
        PanadapterSnapshot[] existing =
            (snapshot.Panadapters ?? [snapshot.Panadapter]).ToArray();
        PanadapterSnapshot? pan = FindPan(snapshot, selector);
        if (pan is null)
        {
            return IntentResult.Failure(
                $"Unknown panadapter '{selector}'.",
                snapshot.Version);
        }
        if (existing.Length <= 1)
        {
            return IntentResult.Failure(
                "The final panadapter cannot be removed.",
                snapshot.Version);
        }

        foreach (SliceSnapshot slice in snapshot.Slices.Where(
                     slice => slice.PanStreamId == pan.StreamId &&
                              slice.RadioId >= 0))
        {
            FlexCommandResponse removeSlice = await m_flexRouter.SendAsync(
                $"slice remove {slice.RadioId}",
                TimeSpan.FromSeconds(4),
                cancellationToken);
            if (!removeSlice.IsSuccess)
            {
                return IntentResult.Failure(
                    $"The radio rejected slice removal (0x{removeSlice.Code:x8}).",
                    Snapshot.Version);
            }
        }

        bool removed = await TryRemoveFlexDisplayAsync(
            pan.Id,
            pan.WaterfallId,
            cancellationToken);
        if (!removed)
        {
            return IntentResult.Failure(
                "The radio rejected panadapter removal.",
                Snapshot.Version);
        }
        m_flexRouter.UnregisterPan(pan.Id);
        RemovePanadapter(pan.Id);
        return new IntentResult(
            true,
            null,
            Snapshot.Version,
            "panadapter",
            pan.Id,
            new Dictionary<string, object?> { ["removed"] = true });
    }

    private async Task<bool> TryRemoveFlexDisplayAsync(
        string panId,
        string? waterfallId,
        CancellationToken cancellationToken)
    {
        FlexCommandResponse removePan = await m_flexRouter.SendAsync(
            $"display pan remove {panId}",
            TimeSpan.FromSeconds(4),
            cancellationToken);
        if (!removePan.IsSuccess)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(waterfallId))
        {
            FlexCommandResponse removeWaterfall =
                await m_flexRouter.SendAsync(
                    $"display panafall remove {waterfallId}",
                    TimeSpan.FromSeconds(4),
                    cancellationToken);
            if (!removeWaterfall.IsSuccess)
            {
                m_logger.LogWarning(
                    "Flex waterfall cleanup returned 0x{Code:x8}",
                    removeWaterfall.Code);
            }
        }
        return true;
    }

    private async Task<IntentResult> CreateFlexSliceAsync(
        JsonElement values,
        CancellationToken cancellationToken)
    {
        if (values.ValueKind != JsonValueKind.Object)
        {
            return IntentResult.Failure(
                "Intent values must be a JSON object.",
                Snapshot.Version);
        }

        RadioSnapshot snapshot = Snapshot;
        SliceSnapshot? template = snapshot.Slices.FirstOrDefault(
            slice => string.Equals(
                slice.Id,
                snapshot.ActiveSliceId,
                StringComparison.OrdinalIgnoreCase));
        PanadapterSnapshot[] panadapters =
            (snapshot.Panadapters ?? [snapshot.Panadapter]).ToArray();
        string targetPanId = panadapters.FirstOrDefault(
            pan => pan.StreamId == template?.PanStreamId)?.Id ??
            panadapters[0].Id;
        long frequencyHz =
            template?.FrequencyHz ??
            panadapters[0].CenterFrequencyHz;
        string? mode = template?.Mode;

        foreach (JsonProperty property in values.EnumerateObject())
        {
            switch (property.Name)
            {
                case "frequencyHz":
                    if (!property.Value.TryGetInt64(out frequencyHz) ||
                        frequencyHz is < 100_000 or > 60_000_000)
                    {
                        return IntentResult.Failure(
                            "frequencyHz must be between 100 kHz and 60 MHz.",
                            snapshot.Version);
                    }
                    break;

                case "mode":
                    mode = property.Value.GetString()?.ToUpperInvariant();
                    if (mode is null || !AllowedModes.Contains(mode))
                    {
                        return IntentResult.Failure(
                            "Unsupported mode.",
                            snapshot.Version);
                    }
                    break;

                case "panId":
                    targetPanId =
                        property.Value.GetString() ?? string.Empty;
                    break;

                default:
                    return IntentResult.Failure(
                        $"Property '{property.Name}' is not valid when creating a slice.",
                        snapshot.Version);
            }
        }

        PanadapterSnapshot? targetPan = panadapters.FirstOrDefault(
            pan => string.Equals(
                pan.Id,
                targetPanId,
                StringComparison.OrdinalIgnoreCase));
        if (targetPan is null ||
            !m_flexRouter.IsOwnedPan(targetPan.StreamId))
        {
            return IntentResult.Failure(
                $"Unknown panadapter '{targetPanId}'.",
                snapshot.Version);
        }

        if (!IsWithinPan(targetPan, frequencyHz))
        {
            return IntentResult.Failure(
                "The new slice frequency must be inside the selected panadapter.",
                snapshot.Version);
        }

        string command = FormattableString.Invariant(
            $"slice create pan={targetPan.Id} freq={frequencyHz / 1_000_000d:F6}");
        if (mode is not null)
        {
            command += $" mode={mode}";
        }

        FlexCommandResponse response = await m_flexRouter.SendAsync(
            command,
            TimeSpan.FromSeconds(4),
            cancellationToken);
        if (!response.IsSuccess)
        {
            return IntentResult.Failure(
                $"The radio rejected slice creation (0x{response.Code:x8}).",
                Snapshot.Version);
        }

        return new IntentResult(
            true,
            null,
            Snapshot.Version,
            "slice",
            response.Body.Trim(),
            new Dictionary<string, object?> { ["created"] = true });
    }

    private async Task<IntentResult> RemoveFlexSliceAsync(
        string selector,
        CancellationToken cancellationToken)
    {
        RadioSnapshot snapshot = Snapshot;
        SliceSnapshot? slice = FindSlice(snapshot, selector);
        if (slice is null || slice.RadioId < 0)
        {
            return IntentResult.Failure(
                $"Unknown live slice '{selector}'.",
                snapshot.Version);
        }

        FlexCommandResponse response = await m_flexRouter.SendAsync(
            $"slice remove {slice.RadioId}",
            TimeSpan.FromSeconds(4),
            cancellationToken);
        if (!response.IsSuccess)
        {
            return IntentResult.Failure(
                $"The radio rejected slice removal (0x{response.Code:x8}).",
                Snapshot.Version);
        }

        return new IntentResult(
            true,
            null,
            Snapshot.Version,
            "slice",
            slice.Id,
            new Dictionary<string, object?> { ["removed"] = true });
    }

    private async Task<IntentResult> SetFlexSliceAsync(
        string selector,
        JsonElement values,
        CancellationToken cancellationToken)
    {
        RadioSnapshot snapshot = Snapshot;
        SliceSnapshot? slice = FindSlice(snapshot, selector);
        if (slice is null || slice.RadioId < 0)
        {
            return IntentResult.Failure(
                $"Unknown live slice '{selector}'.",
                snapshot.Version);
        }

        if (values.ValueKind != JsonValueKind.Object)
        {
            return IntentResult.Failure(
                "Intent values must be a JSON object.",
                snapshot.Version);
        }

        List<string> commands = [];
        Dictionary<string, object?> changes = new(StringComparer.Ordinal);
        int filterLowHz = slice.FilterLowHz;
        int filterHighHz = slice.FilterHighHz;
        bool filterChanged = false;
        string effectiveMode = slice.Mode;
        string? squelchEnabledCommand = null;
        bool activeSelectionRequested = false;

        foreach (JsonProperty property in values.EnumerateObject())
        {
            switch (property.Name)
            {
                case "isActive":
                    if (property.Value.ValueKind != JsonValueKind.True)
                    {
                        return IntentResult.Failure(
                            "isActive can only be set to true.",
                            snapshot.Version);
                    }
                    activeSelectionRequested = true;
                    break;

                case "frequencyHz":
                    if (!property.Value.TryGetInt64(out long frequencyHz) ||
                        frequencyHz is < 100_000 or > 60_000_000)
                    {
                        return IntentResult.Failure(
                            "frequencyHz must be between 100 kHz and 60 MHz.",
                            snapshot.Version);
                    }
                    commands.Add(
                        IsWithinPan(
                            FindPan(snapshot, slice.PanStreamId) ??
                                snapshot.Panadapter,
                            frequencyHz)
                            ? FormattableString.Invariant(
                                $"slice tune {slice.RadioId} {frequencyHz / 1_000_000d:F6} autopan=0")
                            : FormattableString.Invariant(
                                $"slice tune {slice.RadioId} {frequencyHz / 1_000_000d:F6}"));
                    changes[property.Name] = frequencyHz;
                    break;

                case "mode":
                    string? mode = property.Value.GetString()?.ToUpperInvariant();
                    if (mode is null || !AllowedModes.Contains(mode))
                    {
                        return IntentResult.Failure(
                            "Unsupported mode.",
                            snapshot.Version);
                    }
                    commands.Insert(
                        0,
                        $"slice set {slice.RadioId} mode={mode}");
                    changes[property.Name] = mode;
                    effectiveMode = mode;
                    break;

                case "filterLowHz":
                    if (!property.Value.TryGetInt32(out filterLowHz) ||
                        filterLowHz is < -12_000 or > 12_000)
                    {
                        return IntentResult.Failure(
                            "filterLowHz must be between -12000 and 12000.",
                            snapshot.Version);
                    }
                    filterChanged = true;
                    changes[property.Name] = filterLowHz;
                    break;

                case "filterHighHz":
                    if (!property.Value.TryGetInt32(out filterHighHz) ||
                        filterHighHz is < -12_000 or > 12_000)
                    {
                        return IntentResult.Failure(
                            "filterHighHz must be between -12000 and 12000.",
                            snapshot.Version);
                    }
                    filterChanged = true;
                    changes[property.Name] = filterHighHz;
                    break;

                case "afGain":
                    if (!property.Value.TryGetInt32(out int afGain) ||
                        afGain is < 0 or > 100)
                    {
                        return IntentResult.Failure(
                            "afGain must be between 0 and 100.",
                            snapshot.Version);
                    }
                    commands.Add(
                        $"slice set {slice.RadioId} audio_level={afGain}");
                    changes[property.Name] = afGain;
                    break;

                case "squelch":
                    if (!property.Value.TryGetInt32(out int squelch) ||
                        squelch is < 0 or > 100)
                    {
                        return IntentResult.Failure(
                            "squelch must be between 0 and 100.",
                            snapshot.Version);
                    }
                    commands.Add(
                        $"slice set {slice.RadioId} squelch_level={squelch}");
                    changes[property.Name] = squelch;
                    break;

                case "squelchEnabled":
                    if (property.Value.ValueKind is not
                        (JsonValueKind.True or JsonValueKind.False))
                    {
                        return IntentResult.Failure(
                            "squelchEnabled must be a boolean.",
                            snapshot.Version);
                    }
                    bool squelchEnabled = property.Value.GetBoolean();
                    squelchEnabledCommand =
                        $"slice set {slice.RadioId} squelch={(squelchEnabled ? 1 : 0)}";
                    changes[property.Name] = squelchEnabled;
                    break;

                case "audioMute":
                    if (property.Value.ValueKind is not
                        (JsonValueKind.True or JsonValueKind.False))
                    {
                        return IntentResult.Failure(
                            "audioMute must be a boolean.",
                            snapshot.Version);
                    }
                    bool audioMute = property.Value.GetBoolean();
                    commands.Add(
                        $"slice set {slice.RadioId} audio_mute={(audioMute ? 1 : 0)}");
                    changes[property.Name] = audioMute;
                    break;

                case "audioPan":
                    if (!property.Value.TryGetInt32(out int audioPan) ||
                        audioPan is < 0 or > 100)
                    {
                        return IntentResult.Failure(
                            "audioPan must be between 0 and 100.",
                            snapshot.Version);
                    }
                    commands.Add(
                        $"slice set {slice.RadioId} audio_pan={audioPan}");
                    changes[property.Name] = audioPan;
                    break;

                case "agcMode":
                    string? agcMode =
                        property.Value.GetString()?.ToUpperInvariant();
                    if (agcMode is null ||
                        !AllowedAgcModes.Contains(agcMode))
                    {
                        return IntentResult.Failure(
                            "Unsupported AGC mode.",
                            snapshot.Version);
                    }
                    commands.Add(
                        $"slice set {slice.RadioId} " +
                        $"agc_mode={agcMode.ToLowerInvariant()}");
                    changes[property.Name] = agcMode;
                    break;

                case "agcThreshold":
                    if (!property.Value.TryGetInt32(out int agcThreshold) ||
                        agcThreshold is < 0 or > 100)
                    {
                        return IntentResult.Failure(
                            "agcThreshold must be between 0 and 100.",
                            snapshot.Version);
                    }
                    commands.Add(
                        $"slice set {slice.RadioId} agc_threshold={agcThreshold}");
                    changes[property.Name] = agcThreshold;
                    break;

                case "rxAntenna":
                    string? rxAntenna =
                        property.Value.GetString()?.ToUpperInvariant();
                    if (rxAntenna is null ||
                        !AllowedRxAntennas.Contains(rxAntenna))
                    {
                        return IntentResult.Failure(
                            "Unsupported receive antenna.",
                            snapshot.Version);
                    }
                    commands.Add(
                        $"slice set {slice.RadioId} rxant={rxAntenna}");
                    changes[property.Name] = rxAntenna;
                    break;

                case "daxChannel":
                    if (!property.Value.TryGetInt32(out int daxChannel) ||
                        daxChannel is < 0 or > 8)
                    {
                        return IntentResult.Failure(
                            "daxChannel must be between 0 and 8.",
                            snapshot.Version);
                    }
                    commands.Add(
                        $"slice set {slice.RadioId} dax={daxChannel}");
                    changes[property.Name] = daxChannel;
                    break;

                case "nb":
                case "nr":
                case "anf":
                case "nrl":
                case "nrs":
                case "rnn":
                case "nrf":
                case "anfl":
                case "anft":
                    if (property.Value.ValueKind is not
                        (JsonValueKind.True or JsonValueKind.False))
                    {
                        return IntentResult.Failure(
                            $"{property.Name} must be a boolean.",
                            snapshot.Version);
                    }
                    bool dspEnabled = property.Value.GetBoolean();
                    string dspCommandKey = property.Name switch
                    {
                        "nrl" => "lms_nr",
                        "nrs" => "speex_nr",
                        "rnn" => "rnnoise",
                        "anfl" => "lms_anf",
                        _ => property.Name
                    };
                    commands.Add(
                        $"slice set {slice.RadioId} {dspCommandKey}=" +
                        (dspEnabled ? "1" : "0"));
                    changes[property.Name] = dspEnabled;
                    break;

                case "nbLevel":
                case "nrLevel":
                case "anfLevel":
                case "nrlLevel":
                case "nrsLevel":
                case "nrfLevel":
                case "anflLevel":
                    if (!property.Value.TryGetInt32(out int dspLevel) ||
                        dspLevel is < 0 or > 100)
                    {
                        return IntentResult.Failure(
                            $"{property.Name} must be between 0 and 100.",
                            snapshot.Version);
                    }
                    string dspLevelCommandKey = property.Name switch
                    {
                        "nbLevel" => "nb_level",
                        "nrLevel" => "nr_level",
                        "anfLevel" => "anf_level",
                        "nrlLevel" => "lms_nr_level",
                        "nrsLevel" => "speex_nr_level",
                        "nrfLevel" => "nrf_level",
                        "anflLevel" => "lms_anf_level",
                        _ => throw new InvalidOperationException(
                            "Unsupported DSP level.")
                    };
                    commands.Add(
                        $"slice set {slice.RadioId} " +
                        $"{dspLevelCommandKey}={dspLevel}");
                    changes[property.Name] = dspLevel;
                    break;

                default:
                    return IntentResult.Failure(
                        $"Property '{property.Name}' is not controllable.",
                        snapshot.Version);
            }
        }

        bool activeSelectionChanged =
            activeSelectionRequested &&
            !string.Equals(
                snapshot.ActiveSliceId,
                slice.Id,
                StringComparison.OrdinalIgnoreCase);
        if (activeSelectionChanged)
        {
            commands = BuildActiveSliceCommands(
                snapshot.Slices,
                slice.Id,
                commands).ToList();
            changes["isActive"] = true;
        }

        if (filterChanged)
        {
            if (!TryValidateFilterEdges(
                    effectiveMode,
                    filterLowHz,
                    filterHighHz,
                    out string filterError))
            {
                return IntentResult.Failure(
                    filterError,
                    snapshot.Version);
            }
            commands.Add(
                $"filt {slice.RadioId} {filterLowHz} {filterHighHz}");
        }
        if (squelchEnabledCommand is not null)
        {
            commands.Add(squelchEnabledCommand);
        }

        if (commands.Count == 0)
        {
            if (activeSelectionRequested)
            {
                return new IntentResult(
                    true,
                    null,
                    Snapshot.Version,
                    "slice",
                    slice.Id,
                    changes);
            }
            return IntentResult.Failure(
                "No supported changes were supplied.",
                snapshot.Version);
        }

        long? requestedFrequencyHz =
            changes.TryGetValue("frequencyHz", out object? requestedValue) &&
            requestedValue is long frequencyValue
                ? frequencyValue
                : null;
        if (requestedFrequencyHz.HasValue)
        {
            m_tuneTracker.RecordRequest(
                slice.Id,
                slice.RadioId,
                requestedFrequencyHz.Value);
        }

        try
        {
            foreach (string command in commands)
            {
                FlexCommandResponse response = await m_flexRouter.SendAsync(
                    command,
                    TimeSpan.FromSeconds(4),
                    cancellationToken);
                if (!response.IsSuccess)
                {
                    string error =
                        $"The radio rejected a slice control (0x{response.Code:x8}).";
                    if (requestedFrequencyHz.HasValue)
                    {
                        m_tuneTracker.RecordFailure(
                            slice.Id,
                            slice.RadioId,
                            requestedFrequencyHz.Value,
                            error);
                    }
                    return IntentResult.Failure(
                        error,
                        Snapshot.Version);
                }
            }
        }
        catch (Exception exception)
        {
            if (requestedFrequencyHz.HasValue)
            {
                m_tuneTracker.RecordFailure(
                    slice.Id,
                    slice.RadioId,
                    requestedFrequencyHz.Value,
                    "The radio command transport failed.");
            }
            m_logger.LogWarning(
                exception,
                "Slice control transport failed for web slice {WebSliceId}",
                slice.Id);
            throw;
        }

        if (activeSelectionChanged)
        {
            SelectSliceProjection(slice.Id);
        }

        if (changes.TryGetValue("frequencyHz", out object? tunedValue))
        {
            m_logger.LogInformation(
                "Accepted web tune command for slice {WebSliceId} (radio slice {RadioSliceId}) to {FrequencyHz} Hz; awaiting radio status echo",
                slice.Id,
                slice.RadioId,
                tunedValue);
            ScheduleSliceStatusRefresh();
        }

        // Firmware 1.4 can acknowledge some slice property commands without
        // echoing every changed field. Frequency tuning does emit status and
        // may arrive continuously while a slice is dragged, so avoid turning
        // each drag update into a full subscription burst.
        if (changes.Keys.Any(
                property => !string.Equals(
                    property,
                    "frequencyHz",
                    StringComparison.Ordinal)))
        {
            FlexCommandResponse refresh = await m_flexRouter.SendAsync(
                "sub slice all",
                TimeSpan.FromSeconds(4),
                cancellationToken);
            if (!refresh.IsSuccess)
            {
                m_logger.LogWarning(
                    "Flex slice refresh returned 0x{Code:x8}: {Body}",
                    refresh.Code,
                    refresh.Body);
            }
        }

        return new IntentResult(
            true,
            null,
            Snapshot.Version,
            "slice",
            slice.Id,
            changes);
    }

    private void ScheduleSliceStatusRefresh()
    {
        CancellationTokenSource scheduled = new();
        CancellationTokenSource? previous;
        lock (m_sliceRefreshGate)
        {
            previous = m_sliceRefreshCancellation;
            m_sliceRefreshCancellation = scheduled;
        }
        previous?.Cancel();
        previous?.Dispose();
        _ = RefreshSliceStatusAfterTuneAsync(scheduled);
    }

    private async Task RefreshSliceStatusAfterTuneAsync(
        CancellationTokenSource scheduled)
    {
        try
        {
            // FLEX firmware 4.2.18 can acknowledge slice tune without
            // echoing RF_frequency to the originating subscription. Debounce
            // the authoritative refresh so a live drag causes one status
            // replay after the pointer settles, not one replay per movement.
            await Task.Delay(
                TimeSpan.FromMilliseconds(150),
                scheduled.Token);
            FlexCommandResponse refresh = await m_flexRouter.SendAsync(
                "sub slice all",
                TimeSpan.FromSeconds(4),
                CancellationToken.None);
            if (!refresh.IsSuccess)
            {
                m_logger.LogDebug(
                    "Post-tune slice refresh returned 0x{Code:x8}: {Body}",
                    refresh.Code,
                    refresh.Body);
            }
        }
        catch (OperationCanceledException)
            when (scheduled.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            m_logger.LogDebug(
                exception,
                "Post-tune slice status refresh could not complete");
        }
        finally
        {
            lock (m_sliceRefreshGate)
            {
                if (ReferenceEquals(
                        m_sliceRefreshCancellation,
                        scheduled))
                {
                    m_sliceRefreshCancellation = null;
                }
            }
            scheduled.Dispose();
        }
    }

    internal static string[] BuildActiveSliceCommands(
        IReadOnlyList<SliceSnapshot> slices,
        string selectedSliceId,
        IReadOnlyList<string> requestedCommands)
    {
        SliceSnapshot? selected = slices.FirstOrDefault(
            slice => string.Equals(
                slice.Id,
                selectedSliceId,
                StringComparison.OrdinalIgnoreCase));
        if (selected is null || selected.RadioId < 0)
        {
            return requestedCommands.ToArray();
        }

        List<string> commands =
        [
            $"slice set {selected.RadioId} active=1",
            .. requestedCommands
        ];
        return commands.ToArray();
    }

    private void SelectSliceProjection(string selectedSliceId)
    {
        lock (m_stateGate)
        {
            if (!m_snapshot.Slices.Any(
                    slice => string.Equals(
                        slice.Id,
                        selectedSliceId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            SliceSnapshot[] slices = m_snapshot.Slices
                .Select(
                    slice => slice with
                    {
                        IsActive = string.Equals(
                            slice.Id,
                            selectedSliceId,
                            StringComparison.OrdinalIgnoreCase)
                    })
                .ToArray();
            if (string.Equals(
                    m_snapshot.ActiveSliceId,
                    selectedSliceId,
                    StringComparison.OrdinalIgnoreCase) &&
                m_snapshot.Slices.SequenceEqual(slices))
            {
                return;
            }

            m_snapshot = m_snapshot with
            {
                Version = m_snapshot.Version + 1,
                ActiveSliceId = selectedSliceId,
                Slices = slices
            };
        }
    }

    private static bool TryValidateFilterEdges(
        string mode,
        int filterLowHz,
        int filterHighHz,
        out string error)
    {
        if (filterHighHz - filterLowHz < 50)
        {
            error = "Filter width must be at least 50 Hz.";
            return false;
        }

        (int minimumHz, int maximumHz) = mode.ToUpperInvariant() switch
        {
            "LSB" or "DIGL" or "CWR" => (-12_000, 0),
            "AM" or "SAM" or "FM" or "NFM" => (-12_000, 12_000),
            _ => (0, 12_000)
        };

        if (filterLowHz < minimumHz ||
            filterLowHz > maximumHz ||
            filterHighHz < minimumHz ||
            filterHighHz > maximumHz)
        {
            error =
                $"{mode.ToUpperInvariant()} filter edges must stay between " +
                $"{minimumHz} and {maximumHz} Hz.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static SliceSnapshot? FindSlice(
        RadioSnapshot snapshot,
        string selector) =>
        snapshot.Slices.FirstOrDefault(
            slice => string.Equals(
                slice.Id,
                selector,
                StringComparison.OrdinalIgnoreCase));

    private static PanadapterSnapshot? FindPan(
        RadioSnapshot snapshot,
        string selector)
    {
        PanadapterSnapshot[] panadapters =
            (snapshot.Panadapters ?? [snapshot.Panadapter]).ToArray();
        if (string.IsNullOrWhiteSpace(selector))
        {
            return panadapters[0];
        }
        return panadapters.FirstOrDefault(
            pan => string.Equals(
                pan.Id,
                selector,
                StringComparison.OrdinalIgnoreCase));
    }

    private static PanadapterSnapshot? FindPan(
        RadioSnapshot snapshot,
        uint streamId) =>
        (snapshot.Panadapters ?? [snapshot.Panadapter])
            .FirstOrDefault(pan => pan.StreamId == streamId);

    private static bool IsWithinPan(
        PanadapterSnapshot pan,
        long frequencyHz)
    {
        long halfBandwidth = pan.BandwidthHz / 2L;
        return frequencyHz >=
                   pan.CenterFrequencyHz - halfBandwidth &&
               frequencyHz <=
                   pan.CenterFrequencyHz + halfBandwidth;
    }

    private static long ClampPanCenter(long centerFrequencyHz, int bandwidthHz)
    {
        long halfBandwidth = bandwidthHz / 2L;
        return Math.Clamp(
            centerFrequencyHz,
            Math.Max(100_000, halfBandwidth),
            60_000_000 - halfBandwidth);
    }

    private static bool TryReadPanControl(
        JsonElement values,
        PanadapterSnapshot pan,
        out PanControlRequest request,
        out string? error)
    {
        long centerFrequencyHz = pan.CenterFrequencyHz;
        int bandwidthHz = pan.BandwidthHz;
        int minDbm = pan.MinDbm;
        int fftAverage = pan.FftAverage;
        int framesPerSecond = pan.FramesPerSecond;
        bool wnbEnabled = pan.WnbEnabled;
        int wnbLevel = pan.WnbLevel;
        string? bandKey = null;
        Dictionary<string, object?> changes =
            new(StringComparer.Ordinal);
        request = new PanControlRequest(
            centerFrequencyHz,
            bandwidthHz,
            minDbm,
            fftAverage,
            framesPerSecond,
            wnbEnabled,
            wnbLevel,
            bandKey,
            changes);
        error = null;
        if (values.ValueKind != JsonValueKind.Object)
        {
            error = "Intent values must be a JSON object.";
            return false;
        }

        foreach (JsonProperty property in values.EnumerateObject())
        {
            switch (property.Name)
            {
                case "bandKey":
                    bandKey = property.Value.GetString();
                    if (bandKey is null ||
                        !SimulationBands.ContainsKey(bandKey))
                    {
                        error = "Unsupported band.";
                        return false;
                    }
                    changes[property.Name] = bandKey;
                    break;

                case "centerFrequencyHz":
                    if (!property.Value.TryGetInt64(
                            out centerFrequencyHz) ||
                        centerFrequencyHz is < 100_000 or > 60_000_000)
                    {
                        error =
                            "centerFrequencyHz must be between 100 kHz and 60 MHz.";
                        return false;
                    }
                    changes[property.Name] = centerFrequencyHz;
                    break;

                case "bandwidthHz":
                    if (!property.Value.TryGetInt32(out bandwidthHz) ||
                        bandwidthHz is < 10_000 or > 14_000_000)
                    {
                        error =
                            "bandwidthHz must be between 10 kHz and 14 MHz.";
                        return false;
                    }
                    changes[property.Name] = bandwidthHz;
                    break;

                case "minDbm":
                    if (!property.Value.TryGetInt32(out minDbm) ||
                        minDbm is < -200 or > -1 ||
                        minDbm >= pan.MaxDbm)
                    {
                        error =
                            "minDbm must be below the current maximum and between -200 and -1.";
                        return false;
                    }
                    changes[property.Name] = minDbm;
                    break;

                case "fftAverage":
                    if (!property.Value.TryGetInt32(out fftAverage) ||
                        fftAverage is < 0 or > 100)
                    {
                        error =
                            "fftAverage must be between 0 and 100.";
                        return false;
                    }
                    changes[property.Name] = fftAverage;
                    break;

                case "framesPerSecond":
                    if (!property.Value.TryGetInt32(
                            out framesPerSecond) ||
                        framesPerSecond is < 1 or > 30)
                    {
                        error =
                            "framesPerSecond must be between 1 and 30.";
                        return false;
                    }
                    changes[property.Name] = framesPerSecond;
                    break;

                case "wnbEnabled":
                    if (property.Value.ValueKind is not
                        (JsonValueKind.True or JsonValueKind.False))
                    {
                        error = "wnbEnabled must be a boolean.";
                        return false;
                    }
                    wnbEnabled = property.Value.GetBoolean();
                    changes[property.Name] = wnbEnabled;
                    break;

                case "wnbLevel":
                    if (!property.Value.TryGetInt32(out wnbLevel) ||
                        wnbLevel is < 0 or > 100)
                    {
                        error = "wnbLevel must be between 0 and 100.";
                        return false;
                    }
                    changes[property.Name] = wnbLevel;
                    break;

                default:
                    error =
                        $"Property '{property.Name}' is not controllable on the panadapter.";
                    return false;
            }
        }

        if (changes.Count == 0)
        {
            error = "At least one panadapter property is required.";
            return false;
        }
        if (bandKey is not null && changes.Count != 1)
        {
            error = "bandKey cannot be combined with other panadapter controls.";
            return false;
        }
        centerFrequencyHz = ClampPanCenter(
            centerFrequencyHz,
            bandwidthHz);
        if (changes.ContainsKey("centerFrequencyHz"))
        {
            changes["centerFrequencyHz"] = centerFrequencyHz;
        }

        request = new PanControlRequest(
            centerFrequencyHz,
            bandwidthHz,
            minDbm,
            fftAverage,
            framesPerSecond,
            wnbEnabled,
            wnbLevel,
            bandKey,
            changes);
        return true;
    }

    private sealed record PanControlRequest(
        long CenterFrequencyHz,
        int BandwidthHz,
        int MinDbm,
        int FftAverage,
        int FramesPerSecond,
        bool WnbEnabled,
        int WnbLevel,
        string? BandKey,
        IReadOnlyDictionary<string, object?> Changes);

    private sealed record SimulationBand(
        long FrequencyHz,
        string Mode,
        int FilterLowHz,
        int FilterHighHz);

    public BrowserTxCapability GetBrowserTxCapability(
        RadioClientConnection connection) =>
        GetBrowserTxCapability(connection, authenticated: true);

    internal BrowserTxCapability GetBrowserTxCapability(
        RadioClientConnection connection,
        bool authenticated)
    {
        ArgumentNullException.ThrowIfNull(connection);
        bool roleAuthorized = connection.Roles.Any(role =>
            string.Equals(role, AetherRoles.Transmit, StringComparison.Ordinal) ||
            string.Equals(role, AetherRoles.Admin, StringComparison.Ordinal));
        bool connectionCurrent =
            m_clients.TryGetValue(
                connection.ClientId,
                out RadioClientConnection? currentConnection) &&
            ReferenceEquals(currentConnection, connection);
        RadioSnapshot snapshot = Snapshot;
        RadioTxOccupancySnapshot occupancy =
            m_txOccupancyRegistry.GetSnapshot(m_radioSettings.RadioId);
        TxLease? current =
            m_txLeaseManager.GetCurrent(m_radioSettings.RadioId);
        bool leaseHeldByBrowser = current is not null &&
            string.Equals(
                current.SessionId,
                m_radioSettings.SessionId,
                StringComparison.Ordinal) &&
            string.Equals(
                current.ClientId,
                connection.ClientId,
                StringComparison.Ordinal);
        bool anotherLeaseHeld = current is not null && !leaseHeldByBrowser;
        StationTxLifecycleDiagnostics? lifecycle = m_txLifecycle?.Snapshot;
        bool exactLifecycleAuthority =
            current is not null &&
            lifecycle is not null &&
            lifecycle.Registered &&
            lifecycle.GatewayConnected &&
            lifecycle.EngineConnected &&
            lifecycle.BrowserConnected &&
            lifecycle.Authenticated &&
            lifecycle.StationClientHandle != 0 &&
            lifecycle.LeaseActive &&
            lifecycle.AuthorityFresh &&
            string.Equals(
                lifecycle.ConnectionClientId,
                connection.ClientId,
                StringComparison.Ordinal) &&
            string.Equals(
                lifecycle.LeaseId,
                current.LeaseId,
                StringComparison.Ordinal) &&
            lifecycle.IndependentWatchdog.SupervisionEnabled &&
            lifecycle.IndependentWatchdog.ProcessRunning &&
            lifecycle.IndependentWatchdog.IpcConnected &&
            lifecycle.IndependentWatchdog.Registered &&
            lifecycle.IndependentWatchdog.Connected &&
            lifecycle.IndependentWatchdog.LeaseBound &&
            !lifecycle.IndependentWatchdog.RadioCommandTransportAvailable &&
            !lifecycle.IndependentWatchdog.ArmingAvailable;
        bool intentValidationAvailable =
            m_browserTxLeaseEnabled &&
            authenticated &&
            roleAuthorized &&
            connectionCurrent &&
            snapshot.Connected &&
            occupancy.BrowserLeaseAllowed &&
            leaseHeldByBrowser &&
            exactLifecycleAuthority;
        bool leaseAvailable =
            m_browserTxLeaseEnabled &&
            authenticated &&
            roleAuthorized &&
            connectionCurrent &&
            snapshot.Connected &&
            occupancy.BrowserLeaseAllowed &&
            current is null;

        (string state, string message) =
            !m_browserTxLeaseEnabled
                ? (
                    "lease-disabled",
                    "Browser TX lease acquisition is disabled by server configuration.")
                : !authenticated
                    ? (
                        "authentication-required",
                        "A current authenticated browser connection is required.")
                    : !roleAuthorized
                        ? (
                            "role-required",
                            "The authenticated Aether.Transmit role is required.")
                        : !connectionCurrent
                            ? (
                                "connection-replaced",
                                "This browser connection is no longer current.")
                            : !snapshot.Connected
                                ? (
                                    "radio-disconnected",
                                    "The radio must be connected before a browser TX lease can be acquired.")
                                : !occupancy.BrowserLeaseAllowed
                                    ? (
                                        $"occupancy-{occupancy.StateName}",
                                        "Fresh radio-authoritative idle occupancy is required before lease acquisition.")
                                    : leaseHeldByBrowser && intentValidationAvailable
                                        ? (
                                            "intent-validation-ready",
                                            "Exact TX authority is validated; production radio command transport remains unavailable.")
                                        : leaseHeldByBrowser
                                            ? (
                                                "lease-held-by-browser",
                                                $"This browser holds the TX lease; exact lifecycle authority is {lifecycle?.AuthorityReason ?? "unavailable"}.")
                                            : anotherLeaseHeld
                                                ? (
                                                    "lease-held-by-other",
                                                    $"TX is held by {current!.DisplayName}.")
                                                : (
                                                    "lease-available",
                                                    "The browser may acquire the ownership lease, but keying remains unavailable.");

        return new BrowserTxCapability(
            RadioBrowserTxProtocol.Version,
            m_browserTxLeaseEnabled,
            authenticated,
            roleAuthorized,
            connectionCurrent,
            snapshot.Connected,
            occupancy.BrowserLeaseAllowed,
            leaseHeldByBrowser,
            leaseAvailable,
            intentValidationAvailable,
            KeyingAvailable: false,
            MicrophoneAvailable: false,
            TuneAvailable: false,
            CwAvailable: false,
            state,
            message);
    }

    public bool TryAcquireTxLease(
        RadioClientConnection connection,
        TimeSpan duration,
        out TxLease? lease,
        out string? error) =>
        TryAcquireTxLease(
            connection,
            duration,
            authenticated: true,
            out lease,
            out error);

    internal bool TryAcquireTxLease(
        RadioClientConnection connection,
        TimeSpan duration,
        bool authenticated,
        out TxLease? lease,
        out string? error)
    {
        BrowserTxCapability capability =
            GetBrowserTxCapability(connection, authenticated);
        if (!capability.LeaseAvailable)
        {
            lease = null;
            error = capability.Message;
            return false;
        }

        return m_txLeaseManager.TryAcquire(
            m_radioSettings.RadioId,
            m_radioSettings.SessionId,
            connection.ClientId,
            connection.UserId,
            connection.DisplayName,
            duration,
            out lease,
            out error);
    }

    public bool TryRenewTxLease(
        RadioClientConnection connection,
        string leaseId,
        TimeSpan duration,
        out TxLease? lease,
        out string? error) =>
        TryRenewTxLease(
            connection,
            leaseId,
            duration,
            authenticated: true,
            out lease,
            out error);

    internal bool TryRenewTxLease(
        RadioClientConnection connection,
        string leaseId,
        TimeSpan duration,
        bool authenticated,
        out TxLease? lease,
        out string? error)
    {
        BrowserTxCapability capability =
            GetBrowserTxCapability(connection, authenticated);
        bool supervisedAuthorityRequired = m_txLifecycle is not null;
        if (!capability.LeaseConfigured ||
            !capability.Authenticated ||
            !capability.RoleAuthorized ||
            !capability.ConnectionCurrent ||
            !capability.RadioConnected ||
            !capability.OccupancyAllowsLease ||
            !capability.LeaseHeldByBrowser ||
            (supervisedAuthorityRequired &&
                !capability.IntentValidationAvailable))
        {
            lease = null;
            error = capability.Message;
            if (capability.LeaseHeldByBrowser)
            {
                m_txLeaseManager.TryRelease(
                    m_radioSettings.RadioId,
                    leaseId,
                    m_radioSettings.SessionId,
                    connection.ClientId,
                    "renewal-authority-lost",
                    out _);
            }
            return false;
        }

        return m_txLeaseManager.TryRenew(
            m_radioSettings.RadioId,
            leaseId,
            m_radioSettings.SessionId,
            connection.ClientId,
            duration,
            out lease,
            out error);
    }

    public bool ReleaseTxLease(
        RadioClientConnection connection,
        string leaseId) =>
        ReleaseTxLease(
            connection,
            leaseId,
            authenticated: true,
            out _);

    internal bool ReleaseTxLease(
        RadioClientConnection connection,
        string leaseId,
        bool authenticated,
        out string? error)
    {
        BrowserTxCapability capability =
            GetBrowserTxCapability(connection, authenticated);
        if (!capability.LeaseConfigured ||
            !capability.Authenticated ||
            !capability.RoleAuthorized ||
            !capability.ConnectionCurrent)
        {
            error = capability.Message;
            return false;
        }

        bool released = m_txLeaseManager.TryRelease(
            m_radioSettings.RadioId,
            leaseId,
            m_radioSettings.SessionId,
            connection.ClientId,
            "operator-request",
            out _);
        error = released
            ? null
            : "A current TX lease held by this browser is required.";
        return released;
    }

    internal Task FlushTxLifecycleAsync(
        CancellationToken cancellationToken = default) =>
        m_txLifecycle?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    internal bool TryConfirmTxLease(
        RadioClientConnection connection,
        string leaseId,
        out TxLease? lease,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return m_txLeaseManager.TryValidate(
            m_radioSettings.RadioId,
            leaseId,
            m_radioSettings.SessionId,
            connection.ClientId,
            out lease,
            out error);
    }

    internal BrowserTxIntentResult EvaluateBrowserTxIntent(
        RadioClientConnection connection,
        BrowserTxRequest request,
        bool authenticated)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        BrowserTxIntent intent = request.Intent ??
            throw new ArgumentException(
                "A parsed browser TX intent is required.",
                nameof(request));
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        BrowserTxCapability capability =
            GetBrowserTxCapability(connection, authenticated);

        BrowserTxIntentResult Result(
            bool validated,
            string outcome,
            string error)
        {
            m_txLifecycle?.ObserveBrowserTxIntent(
                connection.ClientId,
                request.Sequence,
                intent.Action,
                outcome,
                error,
                observedAt);
            return new BrowserTxIntentResult(
                Ok: false,
                validated,
                outcome,
                error,
                request.Sequence,
                intent.IntentId,
                intent.Action,
                observedAt,
                GetBrowserTxCapability(connection, authenticated));
        }

        if (!capability.LeaseConfigured)
        {
            return Result(
                validated: false,
                "lease-disabled",
                capability.Message);
        }
        if (!authenticated || !capability.Authenticated)
        {
            m_txLeaseManager.TryReleaseOwner(
                m_radioSettings.RadioId,
                m_radioSettings.SessionId,
                connection.ClientId,
                "authentication-lost",
                out _);
            return Result(
                validated: false,
                "authentication-required",
                capability.Message);
        }
        if (!capability.RoleAuthorized)
        {
            return Result(
                validated: false,
                "role-required",
                capability.Message);
        }
        if (!capability.ConnectionCurrent)
        {
            return Result(
                validated: false,
                "connection-replaced",
                capability.Message);
        }
        if (!capability.RadioConnected)
        {
            return Result(
                validated: false,
                "radio-disconnected",
                capability.Message);
        }
        if (!m_txLeaseManager.TryValidate(
                m_radioSettings.RadioId,
                request.LeaseId ?? string.Empty,
                m_radioSettings.SessionId,
                connection.ClientId,
                out _,
                out string? leaseError))
        {
            return Result(
                validated: false,
                "lease-invalid",
                leaseError ?? "A current TX lease held by this browser is required.");
        }
        if (!capability.OccupancyAllowsLease)
        {
            return Result(
                validated: false,
                "occupancy-not-idle",
                capability.Message);
        }
        if (!capability.IntentValidationAvailable)
        {
            StationTxLifecycleDiagnostics? lifecycle = m_txLifecycle?.Snapshot;
            string reason = lifecycle is null
                ? "lifecycle-unavailable"
                : $"lifecycle-{lifecycle.AuthorityReason}";
            return Result(
                validated: false,
                reason,
                "Exact fresh browser, gateway, engine, lease, FLEX-handle, and watchdog authority is required.");
        }

        return Result(
            validated: true,
            "transport-unavailable",
            "The deliberate TX intent was validated, but production radio command transport is unavailable.");
    }

    private void HandleTxLeaseChange(TxLeaseChange change)
    {
        if (Volatile.Read(ref m_disposed) != 0 ||
            !string.Equals(
                change.Lease.RadioId,
                m_radioSettings.RadioId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        m_txLifecycle?.ObserveLeaseChange(change);
        foreach (RadioClientConnection connection in m_clients.Values)
        {
            SendJson(connection, change.Active
                ? new
                {
                    @event = "tx.lease.changed",
                    protocolVersion = RadioBrowserTxProtocol.Version,
                    reason = change.Reason,
                    occurredAt = change.OccurredAt,
                    lease = change.Lease.ToStatus(),
                    capability = GetBrowserTxCapability(connection)
                }
                : new
                {
                    @event = "tx.lease.released",
                    protocolVersion = RadioBrowserTxProtocol.Version,
                    reason = change.Reason,
                    occurredAt = change.OccurredAt,
                    lease = change.Lease.ToStatus(),
                    capability = GetBrowserTxCapability(connection)
                });
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
        {
            return;
        }

        m_txLeaseManager.Changed -= HandleTxLeaseChange;
        m_txLeaseManager.ReleaseSession(
            m_radioSettings.SessionId,
            "session-disposed");
        lock (m_sliceRefreshGate)
        {
            m_sliceRefreshCancellation?.Cancel();
            m_sliceRefreshCancellation?.Dispose();
            m_sliceRefreshCancellation = null;
        }
    }

    private void BroadcastPresence()
    {
        BroadcastJson(new
        {
            @event = "presence",
            clients = Presence
        });
    }
}

public sealed class RadioSettings
{
    public const string SectionName = "Radio";

    public string Mode { get; init; } = "Simulation";
    public bool AllowTransmit { get; init; }
    public bool BrowserTxLeaseEnabled { get; init; }
    public string RadioId { get; init; } = "radio-1";
    public string SessionId { get; init; } = "radio-1";
    public string Host { get; init; } = "127.0.0.1";
    public int TcpPort { get; init; } = 4992;
    public long CenterFrequencyHz { get; init; } = 14_280_000;
    public int BandwidthHz { get; init; } = 200_000;
    public long InitialSliceFrequencyHz { get; init; } = 14_263_000;
    public long SecondarySliceFrequencyHz { get; init; } = 14_300_000;
    public int MinDbm { get; init; } = -130;
    public int MaxDbm { get; init; } = -40;
    public int XPixels { get; init; } = 1024;
    public int YPixels { get; init; } = 700;
    public int FramesPerSecond { get; init; } = 15;
    public int NetworkMtu { get; init; } = 1_200;
    public bool LowBandwidthConnect { get; init; }
    public string StationName { get; init; } = "AETHER-WEB-RX";
    public string GuiClientId { get; init; } = "";
}

public sealed class SpectrumSimulationService(
    RadioCoordinator coordinator,
    IOptions<RadioSettings> settings,
    ILogger<SpectrumSimulationService> logger)
    : BackgroundService, IRadioTransportDiagnostics
{
    private const int BinCount = 1024;
    private readonly Random m_random = new(0xA37E);
    private uint m_sequence;
    private long m_spectrumFrames;
    private long m_startedUnixMilliseconds;
    private long m_lastSpectrumFrameUnixMilliseconds;

    public RadioTransportDiagnostics GetDiagnostics() =>
        new(
            "Simulation",
            0,
            0,
            0,
            1,
            0,
            Volatile.Read(ref m_spectrumFrames),
            0,
            FromUnixMilliseconds(
                Volatile.Read(ref m_startedUnixMilliseconds)),
            null,
            FromUnixMilliseconds(
                Volatile.Read(ref m_lastSpectrumFrameUnixMilliseconds)),
            null,
            null,
            []);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(
                settings.Value.Mode,
                "Simulation",
                StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Simulated spectrum feed disabled for radio mode {RadioMode}",
                settings.Value.Mode);
            return;
        }

        logger.LogInformation("Starting simulated spectrum feed");
        Interlocked.Exchange(
            ref m_startedUnixMilliseconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            RadioSnapshot snapshot = coordinator.Snapshot;
            foreach (PanadapterSnapshot pan in
                     snapshot.Panadapters ?? [snapshot.Panadapter])
            {
                coordinator.BroadcastSpectrum(CreateFrame(pan));
                Interlocked.Increment(ref m_spectrumFrames);
                Interlocked.Exchange(
                    ref m_lastSpectrumFrameUnixMilliseconds,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }
    }

    private byte[] CreateFrame(PanadapterSnapshot pan)
    {
        float[] bins = new float[BinCount];
        uint sequence = ++m_sequence;

        double phase = sequence / 25.0;
        (double Position, double Width, double Strength)[] signals =
        [
            (0.18, 0.003, 54),
            (0.31, 0.008, 30),
            (0.49 + (Math.Sin(phase) * 0.002), 0.004, 61),
            (0.65, 0.011, 35),
            (0.82, 0.003, 50)
        ];

        for (int index = 0; index < BinCount; index++)
        {
            double normalized = index / (double)(BinCount - 1);
            double dbm = -118 + (m_random.NextDouble() * 8);
            dbm += Math.Sin((normalized * 27) + phase) * 1.8;

            foreach ((double position, double width, double strength) in signals)
            {
                double distance = normalized - position;
                dbm += strength * Math.Exp(-(distance * distance) / (2 * width * width));
            }

            bins[index] = (float)dbm;
        }

        return SpectrumFrameCodec.Encode(
            bins,
            sequence,
            pan.CenterFrequencyHz,
            pan.BandwidthHz,
            pan.StreamId);
    }

    private static DateTimeOffset? FromUnixMilliseconds(long value) =>
        value <= 0
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(value);
}

public static class SpectrumFrameCodec
{
    public const int HeaderSize = 28;

    public static byte[] Encode(
        ReadOnlySpan<float> bins,
        uint sequence,
        long centerFrequencyHz,
        int bandwidthHz,
        uint streamId = 1)
    {
        if (bins.Length is < 64 or > 8192)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bins),
                "Spectrum frames must contain between 64 and 8192 bins.");
        }
        if (bandwidthHz is < 10_000 or > 14_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bandwidthHz),
                "Spectrum bandwidth must be between 10 kHz and 14 MHz.");
        }

        byte[] frame = new byte[HeaderSize + (bins.Length * sizeof(short))];
        frame[0] = (byte)'A';
        frame[1] = (byte)'E';
        frame[2] = (byte)'T';
        frame[3] = (byte)'F';
        frame[4] = 0;
        frame[5] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(
            frame.AsSpan(6, sizeof(ushort)),
            (ushort)bins.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(8, sizeof(uint)),
            sequence);
        BinaryPrimitives.WriteInt64LittleEndian(
            frame.AsSpan(12, sizeof(long)),
            centerFrequencyHz);
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(20, sizeof(uint)),
            streamId);
        BinaryPrimitives.WriteInt32LittleEndian(
            frame.AsSpan(24, sizeof(int)),
            bandwidthHz);

        for (int index = 0; index < bins.Length; index++)
        {
            short tenthsDbm = (short)Math.Clamp(
                (int)Math.Round(bins[index] * 10),
                short.MinValue,
                short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                frame.AsSpan(
                    HeaderSize + (index * sizeof(short)),
                    sizeof(short)),
                tenthsDbm);
        }

        return frame;
    }
}
