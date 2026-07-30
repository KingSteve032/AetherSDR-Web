using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Broker;

public sealed class ReceiveProjectionWebSocketEndpoint(
    StationCredentialVerifier credentials,
    RemoteReceiveSessionBroker sessions,
    IOptions<StationLinkSettings> settings,
    IHostApplicationLifetime applicationLifetime,
    ILogger<ReceiveProjectionWebSocketEndpoint> logger)
{
    public const string Subprotocol = "aetherremote.receive.v1";
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task HandleAsync(HttpContext context)
    {
        if (!IsSecureOrLoopback(
                context,
                settings.Value.RequireForwardedHttps) ||
            !context.WebSockets.IsWebSocketRequest ||
            !context.WebSockets.WebSocketRequestedProtocols.Contains(
                Subprotocol,
                StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        string credential = ReadBearerCredential(context.Request);
        if (!credentials.VerifyRuntime(credential))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        string sessionId =
            context.Request.Query["sessionId"].FirstOrDefault() ??
            string.Empty;
        if (!sessions.TryAttachGateway(
                sessionId,
                out RemoteReceiveSessionBroker.GatewayProjectionLease? lease,
                out ChannelReader<RemoteProjectionFrame>? frames) ||
            lease is null ||
            frames is null)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        using (lease)
        using (WebSocket socket =
            await context.WebSockets.AcceptWebSocketAsync(Subprotocol))
        using (CancellationTokenSource lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                applicationLifetime.ApplicationStopping))
        {
            Task sender = SendLoopAsync(
                socket,
                frames,
                lifetime.Token);
            Task receiver = ReceiveLoopAsync(
                socket,
                sessionId,
                lifetime.Token);
            try
            {
                await Task.WhenAny(sender, receiver);
            }
            finally
            {
                lifetime.Cancel();
                await CloseIfOpenAsync(socket);
            }
        }
    }

    private static async Task SendLoopAsync(
        WebSocket socket,
        ChannelReader<RemoteProjectionFrame> frames,
        CancellationToken cancellationToken)
    {
        await foreach (
            RemoteProjectionFrame frame in
            frames.ReadAllAsync(cancellationToken))
        {
            await socket.SendAsync(
                frame.Payload,
                frame.MessageType,
                endOfMessage: true,
                cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(
        WebSocket socket,
        string sessionId,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            while (socket.State == WebSocketState.Open &&
                   !cancellationToken.IsCancellationRequested)
            {
                using MemoryStream message = new();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(
                        buffer,
                        cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await socket.CloseOutputAsync(
                            WebSocketCloseStatus.InvalidMessageType,
                            "Only receive-control text is accepted.",
                            cancellationToken);
                        return;
                    }
                    if (message.Length + result.Count >
                        StationProtocol.MaximumProjectionTextBytes)
                    {
                        await socket.CloseOutputAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            "The receive-control message is too large.",
                            cancellationToken);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                string payload;
                try
                {
                    payload = StrictUtf8.GetString(
                        message.GetBuffer(),
                        0,
                        checked((int)message.Length));
                }
                catch (DecoderFallbackException)
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.InvalidPayloadData,
                        "Receive control must contain valid UTF-8.",
                        cancellationToken);
                    return;
                }

                try
                {
                    if (!await sessions.SendClientTextAsync(
                            sessionId,
                            payload,
                            cancellationToken))
                    {
                        return;
                    }
                }
                catch (RemoteReceiveSessionException exception)
                {
                    logger.LogWarning(
                        "Rejected projected receive command for {SessionId}: {Code}",
                        sessionId,
                        exception.Code);
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "The projected receive command was rejected.",
                        cancellationToken);
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsSecureOrLoopback(
        HttpContext context,
        bool requireForwardedHttps)
    {
        if (!requireForwardedHttps ||
            context.Request.IsHttps ||
            string.Equals(
                context.Request.Headers["X-Forwarded-Proto"]
                    .FirstOrDefault(),
                "https",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        IPAddress? remote = context.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote);
    }

    private static string ReadBearerCredential(HttpRequest request)
    {
        string authorization =
            request.Headers.Authorization.FirstOrDefault() ??
            string.Empty;
        const string prefix = "Bearer ";
        return authorization.StartsWith(
            prefix,
            StringComparison.Ordinal)
            ? authorization[prefix.Length..].Trim()
            : string.Empty;
    }

    private static async Task CloseIfOpenAsync(WebSocket socket)
    {
        if (socket.State is not (
                WebSocketState.Open or
                WebSocketState.CloseReceived))
        {
            return;
        }
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "Receive projection ended.",
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
