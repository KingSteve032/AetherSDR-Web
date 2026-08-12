using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Broker;

public sealed record RemoteStationRadioSnapshot(
    string RadioId,
    string Family,
    string Model,
    string Serial,
    string Nickname,
    string Status,
    int AvailableClients,
    int LicensedClients,
    string CapabilityHash);

public sealed record RemoteStationSnapshot(
    string StationId,
    string InstanceId,
    string ConnectionId,
    string State,
    string SoftwareVersion,
    string ReleaseIdentity,
    string StationEngineVersion,
    string RemoteAddress,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeen,
    long HeartbeatSequence,
    long InventorySequence,
    int ConnectionCount,
    DateTimeOffset? LastDisconnectedAt,
    string? LastDisconnectReason,
    DateTimeOffset? LastRecoveredAt,
    long? LastRecoveryMilliseconds,
    IReadOnlyList<RemoteStationRadioSnapshot> Radios,
    IReadOnlyList<string> Capabilities);

public sealed class StationRegistry
{
    private const string ConnectionClosedReason = "connection_closed";
    private const string ReplacedReason = "replaced";
    private const string HeartbeatTimeoutReason = "heartbeat_timeout";
    private const string BrokerDisconnectReason = "broker_disconnect";
    private readonly object m_gate = new();
    private readonly Dictionary<string, StationState> m_stations =
        new(StringComparer.Ordinal);
    private readonly TimeProvider m_timeProvider;
    private readonly TimeSpan m_degradedAfter;
    private readonly TimeSpan m_disconnectAfter;

    public StationRegistry(
        IOptions<StationLinkSettings> settings,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Value.DegradedAfterSeconds is < 10 or > 300)
        {
            throw new InvalidOperationException(
                "StationLink:DegradedAfterSeconds must be between 10 and 300.");
        }
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_degradedAfter =
            TimeSpan.FromSeconds(settings.Value.DegradedAfterSeconds);
        if (settings.Value.DisconnectAfterSeconds <=
            settings.Value.DegradedAfterSeconds)
        {
            throw new InvalidOperationException(
                "StationLink:DisconnectAfterSeconds must be greater than " +
                "StationLink:DegradedAfterSeconds.");
        }
        m_disconnectAfter =
            TimeSpan.FromSeconds(settings.Value.DisconnectAfterSeconds);
    }

    public StationConnectionLease Open(
        string stationId,
        string instanceId,
        string softwareVersion,
        string remoteAddress,
        IReadOnlyList<string>? capabilities = null,
        string releaseIdentity = "",
        string stationEngineVersion = "")
    {
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        string connectionId = Guid.NewGuid().ToString("N");
        CancellationTokenSource replacement = new();
        CancellationTokenSource liveness = new();
        lock (m_gate)
        {
            int connectionCount = 1;
            DateTimeOffset? lastDisconnectedAt = null;
            string? lastDisconnectReason = null;
            DateTimeOffset? lastRecoveredAt = null;
            long? lastRecoveryMilliseconds = null;
            if (m_stations.TryGetValue(
                    stationId,
                    out StationState? previous))
            {
                if (previous.Connected)
                {
                    previous.Connected = false;
                    previous.LastSeen = now;
                    previous.LastDisconnectedAt = now;
                    previous.LastDisconnectReason = ReplacedReason;
                }
                connectionCount = previous.ConnectionCount == int.MaxValue
                    ? int.MaxValue
                    : previous.ConnectionCount + 1;
                lastDisconnectedAt = previous.LastDisconnectedAt;
                lastDisconnectReason = previous.LastDisconnectReason;
                if (lastDisconnectedAt.HasValue)
                {
                    lastRecoveredAt = now;
                    lastRecoveryMilliseconds = ToMilliseconds(
                        now - lastDisconnectedAt.Value);
                }
                previous.Replacement.Cancel();
                previous.Replacement.Dispose();
            }
            m_stations[stationId] = new StationState(
                stationId,
                instanceId,
                connectionId,
                softwareVersion,
                releaseIdentity,
                stationEngineVersion,
                remoteAddress,
                now,
                now,
                0,
                0,
                connectionCount,
                lastDisconnectedAt,
                lastDisconnectReason,
                lastRecoveredAt,
                lastRecoveryMilliseconds,
                [],
                true,
                replacement,
                liveness,
                (capabilities ?? []).ToArray());
        }
        return new StationConnectionLease(
            this,
            stationId,
            connectionId,
            replacement,
            liveness);
    }

    public IReadOnlyList<RemoteStationSnapshot> GetSnapshot()
    {
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        lock (m_gate)
        {
            return m_stations.Values
                .Select(station => ToSnapshot(station, now))
                .OrderBy(
                    station => station.StationId,
                    StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<string> ExpireStaleConnections()
    {
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        List<(string StationId, CancellationTokenSource Liveness)> expired =
            [];
        lock (m_gate)
        {
            foreach (StationState station in m_stations.Values)
            {
                if (!station.Connected ||
                    now - station.LastSeen <= m_disconnectAfter)
                {
                    continue;
                }
                station.Connected = false;
                station.LastDisconnectedAt = now;
                station.LastDisconnectReason = HeartbeatTimeoutReason;
                expired.Add((station.StationId, station.Liveness));
            }
        }

        foreach ((string _, CancellationTokenSource liveness) in expired)
        {
            liveness.Cancel();
        }
        return expired
            .Select(item => item.StationId)
            .OrderBy(stationId => stationId, StringComparer.Ordinal)
            .ToArray();
    }

    public bool Disconnect(string stationId)
    {
        CancellationTokenSource? liveness = null;
        lock (m_gate)
        {
            if (!m_stations.TryGetValue(
                    stationId,
                    out StationState? station) ||
                !station.Connected)
            {
                return false;
            }
            station.Connected = false;
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            station.LastSeen = now;
            station.LastDisconnectedAt = now;
            station.LastDisconnectReason = BrokerDisconnectReason;
            liveness = station.Liveness;
        }
        liveness.Cancel();
        return true;
    }

    internal bool UpdateInventory(
        string stationId,
        string connectionId,
        StationInventoryMessage inventory)
    {
        lock (m_gate)
        {
            if (!TryGetCurrent(
                    stationId,
                    connectionId,
                    out StationState? station) ||
                station is null ||
                inventory.Sequence <= station.InventorySequence)
            {
                return false;
            }
            station.InventorySequence = inventory.Sequence;
            station.LastSeen = m_timeProvider.GetUtcNow();
            station.Radios = inventory.Radios
                .Select(radio => new RemoteStationRadioSnapshot(
                    radio.RadioId,
                    radio.Family,
                    radio.Model,
                    radio.Serial,
                    radio.Nickname,
                    radio.Status,
                    radio.AvailableClients,
                    radio.LicensedClients,
                    radio.CapabilityHash))
                .ToArray();
            return true;
        }
    }

    internal bool Heartbeat(
        string stationId,
        string connectionId,
        long sequence)
    {
        lock (m_gate)
        {
            if (!TryGetCurrent(
                    stationId,
                    connectionId,
                    out StationState? station) ||
                station is null ||
                sequence <= station.HeartbeatSequence)
            {
                return false;
            }
            station.HeartbeatSequence = sequence;
            station.LastSeen = m_timeProvider.GetUtcNow();
            return true;
        }
    }

    internal void Close(string stationId, string connectionId)
    {
        lock (m_gate)
        {
            if (TryGetCurrent(
                    stationId,
                    connectionId,
                    out StationState? station) &&
                station is not null &&
                station.Connected)
            {
                station.Connected = false;
                DateTimeOffset now = m_timeProvider.GetUtcNow();
                station.LastSeen = now;
                station.LastDisconnectedAt = now;
                station.LastDisconnectReason = ConnectionClosedReason;
            }
        }
    }

    private bool TryGetCurrent(
        string stationId,
        string connectionId,
        out StationState? station) =>
        m_stations.TryGetValue(stationId, out station) &&
        string.Equals(
            station.ConnectionId,
            connectionId,
            StringComparison.Ordinal);

    private RemoteStationSnapshot ToSnapshot(
        StationState station,
        DateTimeOffset now)
    {
        string state = !station.Connected
            ? "offline"
            : now - station.LastSeen > m_degradedAfter
                ? "degraded"
                : "online";
        return new RemoteStationSnapshot(
            station.StationId,
            station.InstanceId,
            station.ConnectionId,
            state,
            station.SoftwareVersion,
            station.ReleaseIdentity,
            station.StationEngineVersion,
            station.RemoteAddress,
            station.ConnectedAt,
            station.LastSeen,
            station.HeartbeatSequence,
            station.InventorySequence,
            station.ConnectionCount,
            station.LastDisconnectedAt,
            station.LastDisconnectReason,
            station.LastRecoveredAt,
            station.LastRecoveryMilliseconds,
            station.Radios,
            station.Capabilities);
    }

    private sealed class StationState(
        string stationId,
        string instanceId,
        string connectionId,
        string softwareVersion,
        string releaseIdentity,
        string stationEngineVersion,
        string remoteAddress,
        DateTimeOffset connectedAt,
        DateTimeOffset lastSeen,
        long heartbeatSequence,
        long inventorySequence,
        int connectionCount,
        DateTimeOffset? lastDisconnectedAt,
        string? lastDisconnectReason,
        DateTimeOffset? lastRecoveredAt,
        long? lastRecoveryMilliseconds,
        IReadOnlyList<RemoteStationRadioSnapshot> radios,
        bool connected,
        CancellationTokenSource replacement,
        CancellationTokenSource liveness,
        IReadOnlyList<string> capabilities)
    {
        public string StationId { get; } = stationId;
        public string InstanceId { get; } = instanceId;
        public string ConnectionId { get; } = connectionId;
        public string SoftwareVersion { get; } = softwareVersion;
        public string ReleaseIdentity { get; } = releaseIdentity;
        public string StationEngineVersion { get; } = stationEngineVersion;
        public string RemoteAddress { get; } = remoteAddress;
        public DateTimeOffset ConnectedAt { get; } = connectedAt;
        public DateTimeOffset LastSeen { get; set; } = lastSeen;
        public long HeartbeatSequence { get; set; } = heartbeatSequence;
        public long InventorySequence { get; set; } = inventorySequence;
        public int ConnectionCount { get; } = connectionCount;
        public DateTimeOffset? LastDisconnectedAt { get; set; } =
            lastDisconnectedAt;
        public string? LastDisconnectReason { get; set; } =
            lastDisconnectReason;
        public DateTimeOffset? LastRecoveredAt { get; } = lastRecoveredAt;
        public long? LastRecoveryMilliseconds { get; } =
            lastRecoveryMilliseconds;
        public IReadOnlyList<RemoteStationRadioSnapshot> Radios { get; set; } =
            radios;
        public bool Connected { get; set; } = connected;
        public CancellationTokenSource Replacement { get; } = replacement;
        public CancellationTokenSource Liveness { get; } = liveness;
        public IReadOnlyList<string> Capabilities { get; } = capabilities;
    }

    private static long ToMilliseconds(TimeSpan duration)
    {
        double milliseconds = Math.Max(0, duration.TotalMilliseconds);
        return milliseconds >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Round(milliseconds);
    }
}

public sealed class StationConnectionLease : IDisposable
{
    private readonly StationRegistry m_registry;
    private readonly CancellationToken m_replacementToken;
    private readonly CancellationTokenSource m_liveness;
    private int m_disposed;

    internal StationConnectionLease(
        StationRegistry registry,
        string stationId,
        string connectionId,
        CancellationTokenSource replacement,
        CancellationTokenSource liveness)
    {
        m_registry = registry;
        StationId = stationId;
        ConnectionId = connectionId;
        m_replacementToken = replacement.Token;
        m_liveness = liveness;
    }

    public string StationId { get; }
    public string ConnectionId { get; }
    public CancellationToken ReplacementToken => m_replacementToken;
    public CancellationToken LivenessToken => m_liveness.Token;

    public bool UpdateInventory(StationInventoryMessage inventory) =>
        m_registry.UpdateInventory(StationId, ConnectionId, inventory);

    public bool Heartbeat(long sequence) =>
        m_registry.Heartbeat(StationId, ConnectionId, sequence);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) == 0)
        {
            m_registry.Close(StationId, ConnectionId);
            m_liveness.Dispose();
        }
    }
}
