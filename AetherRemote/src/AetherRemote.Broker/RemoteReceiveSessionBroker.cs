using System.Security.Cryptography;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using AetherRemote.Protocol;

namespace AetherRemote.Broker;

public sealed record OpenRemoteReceiveSessionRequest(
    string StationId,
    string RadioId,
    string GuiClientId,
    bool LowBandwidth);

public sealed record RemoteReceiveSessionSnapshot(
    string SessionId,
    string StationId,
    string RadioId,
    string GuiClientId,
    string State,
    string RadioModel,
    string Serial,
    string ClientHandle,
    DateTimeOffset OpenedAt);

public sealed record RemoteProjectionFrame(
    WebSocketMessageType MessageType,
    ReadOnlyMemory<byte> Payload);

public sealed class RemoteReceiveSessionException(
    string code,
    string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class RemoteReceiveSessionBroker
{
    private const int MaximumSessionsPerStation = 32;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClosedSessionGrace =
        TimeSpan.FromSeconds(30);
    private readonly object m_gate = new();
    private readonly Dictionary<string, StationLink> m_links =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionState> m_sessions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClosedSessionState> m_closedSessions =
        new(StringComparer.Ordinal);
    private readonly ILogger<RemoteReceiveSessionBroker> m_logger;

    public RemoteReceiveSessionBroker(
        ILogger<RemoteReceiveSessionBroker> logger)
    {
        m_logger = logger;
    }

    public StationProjectionLease AttachStation(
        string stationId,
        string connectionId,
        IReadOnlyList<string> capabilities,
        Func<object, CancellationToken, Task> sender)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(sender);
        SessionState[] replacedSessions;
        lock (m_gate)
        {
            replacedSessions = m_sessions.Values
                .Where(session =>
                    string.Equals(
                        session.StationId,
                        stationId,
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        session.ConnectionId,
                        connectionId,
                        StringComparison.Ordinal))
                .ToArray();
            foreach (SessionState session in replacedSessions)
            {
                m_sessions.Remove(session.SessionId);
            }
            m_links[stationId] =
                new StationLink(
                    connectionId,
                    capabilities.ToArray(),
                    sender);
        }
        foreach (SessionState session in replacedSessions)
        {
            session.Frames.Writer.TryComplete();
            session.Opened.TrySetException(
                new RemoteReceiveSessionException(
                    "station_replaced",
                    "The station reconnected with a fresh identity."));
        }
        return new StationProjectionLease(
            this,
            stationId,
            connectionId);
    }

    public async Task<RemoteReceiveSessionSnapshot> OpenAsync(
        OpenRemoteReceiveSessionRequest request,
        CancellationToken cancellationToken)
    {
        string? error = ValidateOpenRequest(request);
        if (error is not null)
        {
            throw new RemoteReceiveSessionException(
                "invalid_request",
                error);
        }

        string sessionId = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(16));
        StationLink link;
        SessionState state;
        lock (m_gate)
        {
            if (!m_links.TryGetValue(
                    request.StationId,
                    out StationLink? found))
            {
                throw new RemoteReceiveSessionException(
                    "station_offline",
                    "The requested station is not connected.");
            }
            link = found;
            if (!link.Capabilities.Contains(
                    StationCapabilities.ReceiveProjectionV1,
                    StringComparer.Ordinal))
            {
                throw new RemoteReceiveSessionException(
                    "station_capability",
                    "The station does not grant receive projection.");
            }
            int stationSessions = m_sessions.Values.Count(
                session => string.Equals(
                    session.StationId,
                    request.StationId,
                    StringComparison.Ordinal));
            if (stationSessions >= MaximumSessionsPerStation)
            {
                throw new RemoteReceiveSessionException(
                    "station_capacity",
                    "The station has reached its receive-session limit.");
            }

            state = new SessionState(
                sessionId,
                request.StationId,
                link.ConnectionId,
                request.RadioId,
                request.GuiClientId,
                DateTimeOffset.UtcNow);
            m_sessions.Add(sessionId, state);
        }

        try
        {
            await link.Sender(
                new BrokerOpenReceiveSessionMessage(
                    StationMessageTypes.OpenReceiveSession,
                    sessionId,
                    request.RadioId,
                    request.GuiClientId,
                    request.LowBandwidth),
                cancellationToken);
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeout.CancelAfter(OpenTimeout);
            return await state.Opened.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            await CloseAbandonedOpenAsync(sessionId);
            throw new RemoteReceiveSessionException(
                "station_timeout",
                "The station did not finish radio admission in time.");
        }
        catch
        {
            await CloseAbandonedOpenAsync(sessionId);
            throw;
        }
    }

    public async Task<bool> CloseAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (!StationProtocolValidator.IsSessionId(sessionId))
        {
            return false;
        }

        SessionState? state;
        StationLink? link;
        lock (m_gate)
        {
            PruneClosedSessions(DateTimeOffset.UtcNow);
            if (!m_sessions.Remove(sessionId!, out state))
            {
                return false;
            }
            m_closedSessions[state.SessionId] =
                new ClosedSessionState(
                    state.StationId,
                    state.ConnectionId,
                    DateTimeOffset.UtcNow + ClosedSessionGrace);
            m_links.TryGetValue(state.StationId, out link);
        }

        state.Opened.TrySetException(
            new RemoteReceiveSessionException(
                "session_closed",
                "The receive session was closed."));
        state.Frames.Writer.TryComplete();
        if (link is not null &&
            string.Equals(
                link.ConnectionId,
                state.ConnectionId,
                StringComparison.Ordinal))
        {
            await link.Sender(
                new BrokerCloseReceiveSessionMessage(
                    StationMessageTypes.CloseReceiveSession,
                    state.SessionId),
                cancellationToken);
        }
        return true;
    }

    public IReadOnlyList<RemoteReceiveSessionSnapshot> GetSnapshot()
    {
        lock (m_gate)
        {
            return m_sessions.Values
                .Where(session => session.Snapshot is not null)
                .Select(session => session.Snapshot!)
                .OrderBy(
                    session => session.StationId,
                    StringComparer.Ordinal)
                .ThenBy(session => session.OpenedAt)
                .ToArray();
        }
    }

    public bool TryAttachGateway(
        string? sessionId,
        out GatewayProjectionLease? lease,
        out ChannelReader<RemoteProjectionFrame>? frames)
    {
        lease = null;
        frames = null;
        if (!StationProtocolValidator.IsSessionId(sessionId))
        {
            return false;
        }
        lock (m_gate)
        {
            if (!m_sessions.TryGetValue(
                    sessionId!,
                    out SessionState? state) ||
                state.Snapshot is null ||
                Interlocked.CompareExchange(
                    ref state.GatewayAttached,
                    1,
                    0) != 0)
            {
                return false;
            }
            state.LastActivity = DateTimeOffset.UtcNow;
            lease = new GatewayProjectionLease(
                this,
                state.SessionId);
            frames = state.Frames.Reader;
            return true;
        }
    }

    public async Task<bool> SendClientTextAsync(
        string sessionId,
        string payload,
        CancellationToken cancellationToken)
    {
        string? validation =
            StationProtocolValidator.ValidateClientProjectionCommand(payload);
        if (validation is not null)
        {
            throw new RemoteReceiveSessionException(
                "invalid_projection_command",
                validation);
        }

        SessionState? state;
        StationLink? link;
        lock (m_gate)
        {
            if (!m_sessions.TryGetValue(sessionId, out state) ||
                !m_links.TryGetValue(state.StationId, out link) ||
                !string.Equals(
                    link.ConnectionId,
                    state.ConnectionId,
                    StringComparison.Ordinal))
            {
                return false;
            }
            state.LastActivity = DateTimeOffset.UtcNow;
        }
        await link.Sender(
            new BrokerReceiveTextMessage(
                StationMessageTypes.SendReceiveText,
                sessionId,
                payload),
            cancellationToken);
        return true;
    }

    public bool HandleStationMessage(
        string stationId,
        string connectionId,
        string type,
        System.Text.Json.JsonElement root)
    {
        try
        {
            switch (type)
            {
                case StationMessageTypes.ReceiveSessionOpened:
                    return HandleOpened(
                        stationId,
                        connectionId,
                        root.Deserialize<StationReceiveSessionOpenedMessage>(
                            StationProtocol.JsonOptions));
                case StationMessageTypes.ReceiveSessionClosed:
                    return HandleClosed(
                        stationId,
                        connectionId,
                        root.Deserialize<StationReceiveSessionClosedMessage>(
                            StationProtocol.JsonOptions));
                case StationMessageTypes.ReceiveSessionError:
                    return HandleError(
                        stationId,
                        connectionId,
                        root.Deserialize<StationReceiveSessionErrorMessage>(
                            StationProtocol.JsonOptions));
                case StationMessageTypes.ReceiveText:
                    return HandleText(
                        stationId,
                        connectionId,
                        root.Deserialize<StationReceiveTextMessage>(
                            StationProtocol.JsonOptions));
                case StationMessageTypes.ReceiveBinary:
                    return HandleBinary(
                        stationId,
                        connectionId,
                        root.Deserialize<StationReceiveBinaryMessage>(
                            StationProtocol.JsonOptions));
                default:
                    return false;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private bool HandleOpened(
        string stationId,
        string connectionId,
        StationReceiveSessionOpenedMessage? message)
    {
        if (StationProtocolValidator.ValidateReceiveSessionOpened(message)
                is not null ||
            message is null)
        {
            return false;
        }

        SessionState? state;
        RemoteReceiveSessionSnapshot snapshot;
        lock (m_gate)
        {
            if (!TryGetSession(
                    message.SessionId,
                    stationId,
                    connectionId,
                    out state) ||
                state is null)
            {
                return IsRecentlyClosed(
                    message.SessionId,
                    stationId,
                    connectionId);
            }
            if (!string.Equals(
                    state.RadioId,
                    message.RadioId,
                    StringComparison.Ordinal))
            {
                return false;
            }
            snapshot = new RemoteReceiveSessionSnapshot(
                state.SessionId,
                state.StationId,
                state.RadioId,
                state.GuiClientId,
                "admitted",
                message.RadioModel,
                message.Serial,
                message.ClientHandle,
                state.OpenedAt);
            state.Snapshot = snapshot;
        }
        state.Opened.TrySetResult(snapshot);
        m_logger.LogInformation(
            "Station {StationId} admitted receive session {SessionId} for radio {RadioId}",
            stationId,
            message.SessionId,
            message.RadioId);
        return true;
    }

    private bool HandleClosed(
        string stationId,
        string connectionId,
        StationReceiveSessionClosedMessage? message)
    {
        if (StationProtocolValidator.ValidateReceiveSessionClosed(message)
                is not null ||
            message is null)
        {
            return false;
        }

        SessionState? state;
        lock (m_gate)
        {
            if (!TryGetSession(
                    message.SessionId,
                    stationId,
                    connectionId,
                    out state))
            {
                return RemoveClosedSession(
                    message.SessionId,
                    stationId,
                    connectionId);
            }
            m_sessions.Remove(message.SessionId);
        }
        state?.Frames.Writer.TryComplete();
        state?.Opened.TrySetException(
            new RemoteReceiveSessionException(
                "station_closed",
                message.Reason));
        return true;
    }

    private bool HandleError(
        string stationId,
        string connectionId,
        StationReceiveSessionErrorMessage? message)
    {
        if (StationProtocolValidator.ValidateReceiveSessionError(message)
                is not null ||
            message is null)
        {
            return false;
        }

        SessionState? state;
        lock (m_gate)
        {
            if (!TryGetSession(
                    message.SessionId,
                    stationId,
                    connectionId,
                    out state))
            {
                return RemoveClosedSession(
                    message.SessionId,
                    stationId,
                    connectionId);
            }
            m_sessions.Remove(message.SessionId);
        }
        state?.Frames.Writer.TryComplete();
        state?.Opened.TrySetException(
            new RemoteReceiveSessionException(
                message.Code,
                message.Message));
        return true;
    }

    private bool HandleText(
        string stationId,
        string connectionId,
        StationReceiveTextMessage? message)
    {
        if (StationProtocolValidator.ValidateReceiveText(message) is not null ||
            message is null)
        {
            return false;
        }
        SessionState? state;
        lock (m_gate)
        {
            if (!TryGetSession(
                    message.SessionId,
                    stationId,
                    connectionId,
                    out state) ||
                state is null)
            {
                return IsRecentlyClosed(
                    message.SessionId,
                    stationId,
                    connectionId);
            }
            state.LastActivity = DateTimeOffset.UtcNow;
        }
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(
            message.Payload);
        return state.Frames.Writer.TryWrite(
            new RemoteProjectionFrame(
                WebSocketMessageType.Text,
                payload));
    }

    private bool HandleBinary(
        string stationId,
        string connectionId,
        StationReceiveBinaryMessage? message)
    {
        string? validation =
            StationProtocolValidator.ValidateReceiveBinary(
                message,
                out byte[] payload);
        if (validation is not null || message is null)
        {
            return false;
        }
        SessionState? state;
        lock (m_gate)
        {
            if (!TryGetSession(
                    message.SessionId,
                    stationId,
                    connectionId,
                    out state) ||
                state is null)
            {
                return IsRecentlyClosed(
                    message.SessionId,
                    stationId,
                    connectionId);
            }
            state.LastActivity = DateTimeOffset.UtcNow;
        }
        return state.Frames.Writer.TryWrite(
            new RemoteProjectionFrame(
                WebSocketMessageType.Binary,
                payload));
    }

    private void DetachStation(
        string stationId,
        string connectionId)
    {
        SessionState[] sessions;
        lock (m_gate)
        {
            if (m_links.TryGetValue(
                    stationId,
                    out StationLink? link) &&
                string.Equals(
                    link.ConnectionId,
                    connectionId,
                    StringComparison.Ordinal))
            {
                m_links.Remove(stationId);
            }
            sessions = m_sessions.Values
                .Where(session =>
                    string.Equals(
                        session.StationId,
                        stationId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        session.ConnectionId,
                        connectionId,
                        StringComparison.Ordinal))
                .ToArray();
            foreach (SessionState session in sessions)
            {
                m_sessions.Remove(session.SessionId);
            }
        }
        foreach (SessionState session in sessions)
        {
            session.Frames.Writer.TryComplete();
            session.Opened.TrySetException(
                new RemoteReceiveSessionException(
                    "station_offline",
                    "The station connection ended."));
        }
    }

    private bool TryGetSession(
        string sessionId,
        string stationId,
        string connectionId,
        out SessionState? state) =>
        m_sessions.TryGetValue(sessionId, out state) &&
        string.Equals(
            state.StationId,
            stationId,
            StringComparison.Ordinal) &&
        string.Equals(
            state.ConnectionId,
            connectionId,
            StringComparison.Ordinal);

    private async Task CloseAbandonedOpenAsync(string sessionId)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(5));
        try
        {
            await CloseAsync(sessionId, timeout.Token);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or
                  WebSocketException or
                  IOException)
        {
            m_logger.LogWarning(
                exception,
                "Could not finish cleanup for abandoned receive session {SessionId}",
                sessionId);
        }
    }

    private bool IsRecentlyClosed(
        string sessionId,
        string stationId,
        string connectionId)
    {
        PruneClosedSessions(DateTimeOffset.UtcNow);
        return m_closedSessions.TryGetValue(
                   sessionId,
                   out ClosedSessionState? closed) &&
               string.Equals(
                   closed.StationId,
                   stationId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   closed.ConnectionId,
                   connectionId,
                   StringComparison.Ordinal);
    }

    private bool RemoveClosedSession(
        string sessionId,
        string stationId,
        string connectionId)
    {
        bool matches = IsRecentlyClosed(
            sessionId,
            stationId,
            connectionId);
        if (matches)
        {
            m_closedSessions.Remove(sessionId);
        }
        return matches;
    }

    private void PruneClosedSessions(DateTimeOffset now)
    {
        foreach (
            string sessionId in
            m_closedSessions
                .Where(pair => pair.Value.ExpiresAt <= now)
                .Select(pair => pair.Key)
                .ToArray())
        {
            m_closedSessions.Remove(sessionId);
        }
    }

    private void DetachGateway(string sessionId)
    {
        lock (m_gate)
        {
            if (m_sessions.TryGetValue(
                    sessionId,
                    out SessionState? state))
            {
                Volatile.Write(ref state.GatewayAttached, 0);
                state.LastActivity = DateTimeOffset.UtcNow;
            }
        }
    }

    private static string? ValidateOpenRequest(
        OpenRemoteReceiveSessionRequest request)
    {
        if (!StationProtocolValidator.IsIdentifier(
                request.StationId,
                StationProtocol.MaximumStationIdLength) ||
            !StationProtocolValidator.IsIdentifier(
                request.RadioId,
                StationProtocol.MaximumRadioIdLength) ||
            !Guid.TryParse(request.GuiClientId, out _))
        {
            return "A valid station, radio, and GUI client identity are required.";
        }
        return null;
    }

    private sealed record StationLink(
        string ConnectionId,
        IReadOnlyList<string> Capabilities,
        Func<object, CancellationToken, Task> Sender);

    private sealed record ClosedSessionState(
        string StationId,
        string ConnectionId,
        DateTimeOffset ExpiresAt);

    private sealed class SessionState(
        string sessionId,
        string stationId,
        string connectionId,
        string radioId,
        string guiClientId,
        DateTimeOffset openedAt)
    {
        public string SessionId { get; } = sessionId;
        public string StationId { get; } = stationId;
        public string ConnectionId { get; } = connectionId;
        public string RadioId { get; } = radioId;
        public string GuiClientId { get; } = guiClientId;
        public DateTimeOffset OpenedAt { get; } = openedAt;
        public TaskCompletionSource<RemoteReceiveSessionSnapshot> Opened
        { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RemoteReceiveSessionSnapshot? Snapshot { get; set; }
        public Channel<RemoteProjectionFrame> Frames { get; } =
            Channel.CreateBounded<RemoteProjectionFrame>(
                new BoundedChannelOptions(64)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });
        public int GatewayAttached;
        public DateTimeOffset LastActivity { get; set; } = openedAt;
    }

    public sealed class StationProjectionLease : IDisposable
    {
        private readonly RemoteReceiveSessionBroker m_owner;
        private readonly string m_stationId;
        private readonly string m_connectionId;
        private int m_disposed;

        internal StationProjectionLease(
            RemoteReceiveSessionBroker owner,
            string stationId,
            string connectionId)
        {
            m_owner = owner;
            m_stationId = stationId;
            m_connectionId = connectionId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_disposed, 1) == 0)
            {
                m_owner.DetachStation(m_stationId, m_connectionId);
            }
        }
    }

    public sealed class GatewayProjectionLease : IDisposable
    {
        private readonly RemoteReceiveSessionBroker m_owner;
        private readonly string m_sessionId;
        private int m_disposed;

        internal GatewayProjectionLease(
            RemoteReceiveSessionBroker owner,
            string sessionId)
        {
            m_owner = owner;
            m_sessionId = sessionId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_disposed, 1) == 0)
            {
                m_owner.DetachGateway(m_sessionId);
            }
        }
    }
}
