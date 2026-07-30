using System.Net.WebSockets;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Agent;

public sealed class StationReceiveSessionException(
    string code,
    string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class StationReceiveSessionManager(
    IStationRadioInventoryProvider inventory,
    IOptions<AgentSettings> settings,
    ILogger<StationReceiveSessionManager> logger)
{
    private const int MaximumSessions = 32;
    private readonly SemaphoreSlim m_gate = new(1, 1);
    private readonly object m_senderGate = new();
    private readonly Dictionary<string, StationEngineReceiveSession>
        m_sessions = new(StringComparer.Ordinal);
    private readonly AgentSettings m_settings = settings.Value;
    private Func<object, CancellationToken, Task>? m_brokerSender;
    private int m_activeCount;

    public int ActiveCount => Volatile.Read(ref m_activeCount);

    public IDisposable AttachBrokerSender(
        Func<object, CancellationToken, Task> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        lock (m_senderGate)
        {
            if (m_brokerSender is not null)
            {
                throw new InvalidOperationException(
                    "A broker projection sender is already attached.");
            }
            m_brokerSender = sender;
        }
        return new BrokerSenderLease(this, sender);
    }

    public async Task<StationReceiveSessionOpenedMessage> OpenAsync(
        BrokerOpenReceiveSessionMessage message,
        string stationId,
        CancellationToken cancellationToken)
    {
        string? validation =
            StationProtocolValidator.ValidateOpenReceiveSession(message);
        if (validation is not null)
        {
            throw new StationReceiveSessionException(
                "invalid_request",
                validation);
        }
        if (!inventory.TryResolve(
                message.RadioId,
                out LocalRadioEndpoint? endpoint) ||
            endpoint is null)
        {
            throw new StationReceiveSessionException(
                "radio_unavailable",
                "The requested radio is not available at this station.");
        }
        if (!string.Equals(
                endpoint.Advertisement.Family,
                "flex",
                StringComparison.Ordinal))
        {
            throw new StationReceiveSessionException(
                "unsupported_radio",
                "This radio family does not support receive projection.");
        }

        Func<object, CancellationToken, Task>? brokerSender;
        lock (m_senderGate)
        {
            brokerSender = m_brokerSender;
        }
        if (brokerSender is null)
        {
            throw new StationReceiveSessionException(
                "station_offline",
                "The station projection link is not ready.");
        }

        await m_gate.WaitAsync(cancellationToken);
        try
        {
            if (m_sessions.ContainsKey(message.SessionId))
            {
                throw new StationReceiveSessionException(
                    "duplicate_session",
                    "That receive session already exists.");
            }
            if (m_sessions.Count >= MaximumSessions)
            {
                throw new StationReceiveSessionException(
                    "station_capacity",
                    "This station has reached its receive-session limit.");
            }

            StationEngineReceiveSession session = new(
                message.SessionId,
                message.RadioId,
                message.GuiClientId,
                message.LowBandwidth,
                m_settings.LocalEngineUrl,
                m_settings.LocalEngineOrigin,
                brokerSender);
            try
            {
                StationReceiveSessionOpenedMessage opened =
                    await session.StartAsync(cancellationToken);
                m_sessions.Add(message.SessionId, session);
                Volatile.Write(ref m_activeCount, m_sessions.Count);
                logger.LogInformation(
                    "Started receive projection {SessionId} for radio {RadioId} as client {ClientHandle}",
                    message.SessionId,
                    message.RadioId,
                    opened.ClientHandle);
                return opened;
            }
            catch (StationReceiveSessionException)
            {
                await session.DisposeAsync();
                throw;
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                      WebSocketException or
                      IOException or
                      TimeoutException or
                      InvalidDataException or
                      OperationCanceledException)
            {
                await session.DisposeAsync();
                if (exception is OperationCanceledException &&
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                throw new StationReceiveSessionException(
                    "radio_unreachable",
                    "The station could not establish its receive projection.");
            }
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task ForwardTextAsync(
        BrokerReceiveTextMessage message,
        CancellationToken cancellationToken)
    {
        string? validation =
            StationProtocolValidator.ValidateBrokerReceiveText(message);
        if (validation is not null)
        {
            throw new StationReceiveSessionException(
                "invalid_request",
                validation);
        }

        StationEngineReceiveSession? session;
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            m_sessions.TryGetValue(message.SessionId, out session);
        }
        finally
        {
            m_gate.Release();
        }
        if (session is null)
        {
            throw new StationReceiveSessionException(
                "unknown_session",
                "The receive projection no longer exists.");
        }
        try
        {
            await session.SendProjectedTextAsync(
                message.Payload,
                cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new StationReceiveSessionException(
                "invalid_request",
                exception.Message);
        }
    }

    public async Task<bool> CloseAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!StationProtocolValidator.IsSessionId(sessionId))
        {
            return false;
        }

        StationEngineReceiveSession? session;
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            if (!m_sessions.Remove(sessionId, out session))
            {
                return false;
            }
            Volatile.Write(ref m_activeCount, m_sessions.Count);
        }
        finally
        {
            m_gate.Release();
        }

        await session.DisposeAsync();
        logger.LogInformation(
            "Closed station-local receive projection {SessionId}",
            sessionId);
        return true;
    }

    public async Task CloseAllAsync()
    {
        StationEngineReceiveSession[] sessions;
        await m_gate.WaitAsync();
        try
        {
            sessions = m_sessions.Values.ToArray();
            m_sessions.Clear();
            Volatile.Write(ref m_activeCount, 0);
        }
        finally
        {
            m_gate.Release();
        }

        foreach (StationEngineReceiveSession session in sessions)
        {
            await session.DisposeAsync();
        }
    }

    private void DetachBrokerSender(
        Func<object, CancellationToken, Task> sender)
    {
        lock (m_senderGate)
        {
            if (ReferenceEquals(m_brokerSender, sender))
            {
                m_brokerSender = null;
            }
        }
    }

    private sealed class BrokerSenderLease(
        StationReceiveSessionManager owner,
        Func<object, CancellationToken, Task> sender)
        : IDisposable
    {
        private int m_disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_disposed, 1) == 0)
            {
                owner.DetachBrokerSender(sender);
            }
        }
    }
}
