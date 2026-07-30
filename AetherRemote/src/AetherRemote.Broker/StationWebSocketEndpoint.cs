using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Broker;

public sealed class StationWebSocketEndpoint(
    IOptions<StationLinkSettings> settings,
    StationLinkTokenService tokens,
    StationRegistry registry,
    RemoteReceiveSessionBroker receiveSessions,
    IHostApplicationLifetime applicationLifetime,
    ILogger<StationWebSocketEndpoint> logger)
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly StationLinkSettings m_settings = settings.Value;

    public async Task HandleAsync(HttpContext context)
    {
        if (!m_settings.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (m_settings.RequireForwardedHttps &&
            !context.Request.IsHttps &&
            !ForwardedAsHttps(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest ||
            !context.WebSockets.WebSocketRequestedProtocols.Contains(
                StationProtocol.WebSocketSubprotocol,
                StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string stationId =
            context.Request.Headers["X-Aether-Station-Id"]
                .FirstOrDefault()?
                .Trim() ??
            string.Empty;
        string accessToken = ReadBearerCredential(context.Request);
        if (!tokens.TryConsume(
                stationId,
                accessToken,
                out StationLinkTokenGrant? grant) ||
            grant is null)
        {
            logger.LogWarning(
                "Rejected a station-link token from {RemoteAddress}",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using WebSocket socket =
            await context.WebSockets.AcceptWebSocketAsync(
                StationProtocol.WebSocketSubprotocol);
        using SemaphoreSlim sendGate = new(1, 1);
        using CancellationTokenSource lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                applicationLifetime.ApplicationStopping);
        try
        {
            await RunAuthenticatedAsync(
                socket,
                sendGate,
                stationId,
                grant.Capabilities,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            await CloseIfOpenAsync(
                socket,
                WebSocketCloseStatus.NormalClosure,
                "Station link ended.");
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation(
                exception,
                "Station {StationId} link closed",
                stationId);
        }
    }

    private async Task RunAuthenticatedAsync(
        WebSocket socket,
        SemaphoreSlim sendGate,
        string stationId,
        IReadOnlyList<string> authorizedCapabilities,
        string remoteAddress,
        CancellationToken requestAborted)
    {
        using CancellationTokenSource handshakeTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        string? helloJson = await ReceiveTextAsync(
            socket,
            sendGate,
            handshakeTimeout.Token);
        if (helloJson is null)
        {
            return;
        }

        StationHelloMessage? hello;
        try
        {
            hello = JsonSerializer.Deserialize<StationHelloMessage>(
                helloJson,
                StationProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            await RejectAsync(
                socket,
                sendGate,
                "invalid_hello",
                "The station hello message is malformed.");
            return;
        }

        string? helloError =
            StationProtocolValidator.ValidateHello(hello, stationId);
        if (helloError is not null)
        {
            await RejectAsync(
                socket,
                sendGate,
                "invalid_hello",
                helloError);
            return;
        }
        if (!StationProtocolValidator.CapabilitiesMatch(
                hello!.Capabilities,
                authorizedCapabilities))
        {
            await RejectAsync(
                socket,
                sendGate,
                "capability_mismatch",
                "The station hello capabilities do not match its link token.");
            return;
        }

        using StationConnectionLease lease = registry.Open(
            stationId,
            hello.InstanceId,
            hello.SoftwareVersion,
            remoteAddress,
            authorizedCapabilities);
        logger.LogInformation(
            "Station {StationId} connected as {ConnectionId} from {RemoteAddress}",
            stationId,
            lease.ConnectionId,
            remoteAddress);

        await SendJsonAsync(
            socket,
            sendGate,
            new BrokerWelcomeMessage(
                StationMessageTypes.Welcome,
                StationProtocol.Version,
                lease.ConnectionId,
                m_settings.HeartbeatSeconds,
                StationProtocol.MaximumMessageBytes),
            requestAborted);

        using RemoteReceiveSessionBroker.StationProjectionLease projection =
            receiveSessions.AttachStation(
                stationId,
                lease.ConnectionId,
                authorizedCapabilities,
                (message, cancellationToken) =>
                    SendJsonAsync(
                        socket,
                        sendGate,
                        message,
                        cancellationToken));
        using CancellationTokenSource connectionLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                requestAborted,
                lease.ReplacementToken,
                lease.LivenessToken);
        while (!connectionLifetime.IsCancellationRequested &&
               socket.State == WebSocketState.Open)
        {
            string? json = await ReceiveTextAsync(
                socket,
                sendGate,
                connectionLifetime.Token);
            if (json is null)
            {
                break;
            }
            if (!await HandleMessageAsync(
                    socket,
                    sendGate,
                    lease,
                    json,
                    connectionLifetime.Token))
            {
                break;
            }
        }

        if (lease.ReplacementToken.IsCancellationRequested)
        {
            await CloseIfOpenAsync(
                socket,
                WebSocketCloseStatus.NormalClosure,
                "Replaced by a newer authenticated station instance.");
        }
        logger.LogInformation(
            "Station {StationId} connection {ConnectionId} ended",
            stationId,
            lease.ConnectionId);
    }

    private async Task<bool> HandleMessageAsync(
        WebSocket socket,
        SemaphoreSlim sendGate,
        StationConnectionLease lease,
        string json,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            await RejectAsync(
                socket,
                sendGate,
                "invalid_json",
                "The station message is malformed.");
            return false;
        }

        using (document)
        {
            try
            {
                string? type =
                    StationProtocolValidator.ReadMessageType(
                        document.RootElement);
                switch (type)
                {
                    case StationMessageTypes.Inventory:
                    {
                        StationInventoryMessage? inventory =
                            document.RootElement
                                .Deserialize<StationInventoryMessage>(
                                    StationProtocol.JsonOptions);
                        string? error =
                            StationProtocolValidator.ValidateInventory(
                                inventory);
                        if (error is not null ||
                            !lease.UpdateInventory(inventory!))
                        {
                            await RejectAsync(
                                socket,
                                sendGate,
                                "invalid_inventory",
                                error ??
                                    "Inventory sequence must increase.");
                            return false;
                        }
                        return true;
                    }
                    case StationMessageTypes.Heartbeat:
                    {
                        StationHeartbeatMessage? heartbeat =
                            document.RootElement
                                .Deserialize<StationHeartbeatMessage>(
                                    StationProtocol.JsonOptions);
                        string? error =
                            StationProtocolValidator.ValidateHeartbeat(
                                heartbeat);
                        if (error is not null ||
                            !lease.Heartbeat(heartbeat!.Sequence))
                        {
                            await RejectAsync(
                                socket,
                                sendGate,
                                "invalid_heartbeat",
                                error ??
                                    "Heartbeat sequence must increase.");
                            return false;
                        }
                        return true;
                    }
                    case StationMessageTypes.ReceiveSessionOpened:
                    case StationMessageTypes.ReceiveSessionClosed:
                    case StationMessageTypes.ReceiveSessionError:
                    case StationMessageTypes.ReceiveText:
                    case StationMessageTypes.ReceiveBinary:
                    {
                        if (!receiveSessions.HandleStationMessage(
                                lease.StationId,
                                lease.ConnectionId,
                                type,
                                document.RootElement))
                        {
                            await RejectAsync(
                                socket,
                                sendGate,
                                "invalid_receive_session",
                                "The receive-session message is invalid.");
                            return false;
                        }
                        return true;
                    }
                    default:
                        await RejectAsync(
                            socket,
                            sendGate,
                            "unsupported_message",
                            "That station message type is not supported.");
                        return false;
                }
            }
            catch (JsonException)
            {
                await RejectAsync(
                    socket,
                    sendGate,
                    "invalid_message",
                    "The station message does not match the protocol.");
                return false;
            }
        }
    }

    private static async Task<string?> ReceiveTextAsync(
        WebSocket socket,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4 * 1024];
        using MemoryStream message = new();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                buffer,
                cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await RejectAsync(
                    socket,
                    sendGate,
                    "text_only",
                    "Binary station messages are not supported.");
                return null;
            }
            if (message.Length + result.Count >
                StationProtocol.MaximumMessageBytes)
            {
                await RejectAsync(
                    socket,
                    sendGate,
                    "message_too_large",
                    "The station message exceeds the configured boundary.");
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                try
                {
                    return StrictUtf8.GetString(
                        message.GetBuffer(),
                        0,
                        (int)message.Length);
                }
                catch (DecoderFallbackException)
                {
                    await RejectAsync(
                        socket,
                        sendGate,
                        "invalid_utf8",
                        "Station messages must contain valid UTF-8.");
                    return null;
                }
            }
        }
    }

    private static async Task SendJsonAsync<T>(
        WebSocket socket,
        SemaphoreSlim sendGate,
        T message,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            message,
            StationProtocol.JsonOptions);
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static async Task RejectAsync(
        WebSocket socket,
        SemaphoreSlim sendGate,
        string code,
        string message)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        await SendJsonAsync(
            socket,
            sendGate,
            new BrokerErrorMessage(
                StationMessageTypes.Error,
                code,
                message),
            CancellationToken.None);
        await CloseIfOpenAsync(
            socket,
            WebSocketCloseStatus.PolicyViolation,
            message);
    }

    private static async Task CloseIfOpenAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(2));
            try
            {
                await socket.CloseOutputAsync(
                    status,
                    description[..Math.Min(description.Length, 123)],
                    timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }
    }

    private static string ReadBearerCredential(HttpRequest request)
    {
        string authorization =
            request.Headers.Authorization.FirstOrDefault() ?? string.Empty;
        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.Ordinal)
            ? authorization[prefix.Length..].Trim()
            : string.Empty;
    }

    private static bool ForwardedAsHttps(HttpRequest request)
    {
        string forwarded =
            request.Headers["X-Forwarded-Proto"].FirstOrDefault() ??
            string.Empty;
        string firstValue = forwarded
            .Split(',', 2, StringSplitOptions.TrimEntries)[0];
        return string.Equals(firstValue, "https", StringComparison.OrdinalIgnoreCase);
    }
}
