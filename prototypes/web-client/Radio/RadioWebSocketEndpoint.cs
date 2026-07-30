using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using AetherSDR.Web.Auth;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public static class RadioWebSocketEndpoint
{
    private const string Subprotocol = "aethersdr.experimental.v0";

    public static async Task HandleAsync(
        HttpContext context,
        RadioSessionRegistry sessions,
        RadioPresenceRegistry presenceRegistry,
        IHostApplicationLifetime applicationLifetime,
        IOptions<OriginSettings> originSettings,
        ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger("RadioWebSocket");
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { error = "A WebSocket upgrade is required." });
            return;
        }

        if (!OriginPolicy.IsAllowed(context, originSettings.Value))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new { error = "WebSocket origin is not allowed." });
            return;
        }

        string? requestedSubprotocol = context.WebSockets.WebSocketRequestedProtocols
            .FirstOrDefault(protocol =>
                string.Equals(protocol, Subprotocol, StringComparison.Ordinal));
        if (requestedSubprotocol is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { error = $"WebSocket subprotocol '{Subprotocol}' is required." });
            return;
        }

        string? requestedSessionId =
            context.Request.Query["sessionId"].FirstOrDefault();
        if (!sessions.TryAcquire(
                requestedSessionId,
                context.User,
                out RadioSession? radioSession) ||
            radioSession is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                new { error = "That radio session is not available." });
            return;
        }

        RadioCoordinator coordinator = radioSession.Coordinator;
        try
        {
            using WebSocket socket =
                await context.WebSockets.AcceptWebSocketAsync(Subprotocol);
            RadioClientConnection connection = coordinator.Register(context.User);
            string radioId = radioSession.Endpoint.RadioId;
            bool presenceRegistered = false;
            using CancellationTokenSource lifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted,
                    applicationLifetime.ApplicationStopping);

            try
            {
                IReadOnlyList<OperatorPresenceSnapshot> initialPresence =
                    presenceRegistry.Preview(
                        radioId,
                        connection.ToPresence());
                coordinator.SendJson(connection, new
                {
                    type = "welcome",
                    protocol = new
                    {
                        name = "aethersdr-web-experimental",
                        version = 0,
                        warning =
                            "Experimental receive path. This is not the production aetherd protocol."
                    },
                    clientId = connection.ClientId,
                    guiClientId = radioSession.GuiClientId,
                    sessionId = coordinator.Snapshot.SessionId,
                    capabilities = new
                    {
                        control = context.User.IsInRole(AetherRoles.Control) ||
                                  context.User.IsInRole(AetherRoles.Admin),
                        transmit = false,
                        slices = coordinator.Snapshot.Slices.Count,
                        spectrum = new
                        {
                            format = "AETF/v0",
                            frameVersion = 3,
                            headerBytes = SpectrumFrameCodec.HeaderSize,
                            bins = 1024,
                            units = "tenths-dBm",
                            frequencyFrame = "center-hz + bandwidth-hz"
                        },
                        audio = new
                        {
                            format = "AETA/v0",
                            sampleRate = FlexVitaAudioDecoder.SampleRate,
                            channels = 2,
                            sampleFormat = "pcm-s16le"
                        }
                    },
                    snapshot = coordinator.Snapshot,
                    presence = initialPresence,
                    txLease = coordinator.TxLeaseStatus
                });
                presenceRegistry.Register(radioId, coordinator, connection);
                presenceRegistered = true;

                Task sender = SendLoopAsync(socket, connection, lifetime.Token);
                Task receiver = ReceiveLoopAsync(
                    socket,
                    connection,
                    coordinator,
                    context.User,
                    logger,
                    lifetime.Token);

                try
                {
                    await Task.WhenAny(sender, receiver);
                }
                finally
                {
                    lifetime.Cancel();

                    if (socket.State is
                        WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await socket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Session ended.",
                                CancellationToken.None);
                        }
                        catch (WebSocketException)
                        {
                            // Peer disappeared; teardown is already in progress.
                        }
                    }
                }
            }
            finally
            {
                if (presenceRegistered)
                {
                    presenceRegistry.Unregister(radioId, connection.ClientId);
                }

                coordinator.Unregister(
                    connection.ClientId,
                    notifyPresence: false);
            }
        }
        finally
        {
            radioSession.ReleaseClient();
        }
    }

    private static async Task SendLoopAsync(
        WebSocket socket,
        RadioClientConnection connection,
        CancellationToken cancellationToken)
    {
        await foreach (
            OutboundMessage message in connection.Outbox.ReadAllAsync(cancellationToken))
        {
            connection.MarkDequeued();
            if (!connection.ShouldDeliver(message))
            {
                continue;
            }
            await socket.SendAsync(
                message.Payload,
                message.MessageType,
                endOfMessage: true,
                cancellationToken);
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        RadioClientConnection connection,
        RadioCoordinator coordinator,
        System.Security.Claims.ClaimsPrincipal user,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);

        try
        {
            while (socket.State == WebSocketState.Open &&
                   !cancellationToken.IsCancellationRequested)
            {
                using MemoryStream message = new();
                WebSocketReceiveResult? result;

                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        coordinator.SendJson(connection, new
                        {
                            ok = false,
                            error = "Browser-to-server binary frames are not accepted."
                        });
                        break;
                    }

                    if (message.Length + result.Count >
                        RadioCoordinator.MaxClientMessageBytes)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            "Message exceeds 64 KiB.",
                            cancellationToken);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                await HandleMessageAsync(
                    message.ToArray(),
                    connection,
                    coordinator,
                    user,
                    logger,
                    cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task HandleMessageAsync(
        byte[] utf8Json,
        RadioClientConnection connection,
        RadioCoordinator coordinator,
        System.Security.Claims.ClaimsPrincipal user,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            JsonElement root = document.RootElement;
            string? command = root.TryGetProperty("cmd", out JsonElement commandValue)
                ? commandValue.GetString()
                : null;
            JsonElement id = root.TryGetProperty("id", out JsonElement idValue)
                ? idValue.Clone()
                : default;

            switch (command)
            {
                case "hello":
                case "subscribe":
                case "ping":
                    coordinator.SendJson(connection, new
                    {
                        id = ResponseId(id),
                        ok = true,
                        snapshot = command == "subscribe" ? coordinator.Snapshot : null
                    });
                    break;

                case "intent":
                    await HandleIntentAsync(
                        root,
                        id,
                        connection,
                        coordinator,
                        user,
                        cancellationToken);
                    break;

                case "diagnostics.audio":
                    HandleAudioDiagnostics(root, connection, coordinator);
                    break;

                case "diagnostics.network":
                    HandleNetworkDiagnostics(root, connection, coordinator);
                    break;

                case "client.visibility":
                    HandleClientVisibility(root, connection, coordinator);
                    break;

                case "tx.acquire":
                    HandleTxAcquire(root, id, connection, coordinator, user);
                    break;

                case "tx.renew":
                    HandleTxRenew(root, id, connection, coordinator);
                    break;

                case "tx.release":
                    HandleTxRelease(root, id, connection, coordinator);
                    break;

                default:
                    coordinator.SendJson(connection, new
                    {
                        id = ResponseId(id),
                        ok = false,
                        error = "Unknown command."
                    });
                    break;
            }
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Rejected malformed browser message");
            coordinator.SendJson(connection, new
            {
                ok = false,
                error = "Malformed JSON request."
            });
        }

    }

    private static void HandleAudioDiagnostics(
        JsonElement root,
        RadioClientConnection connection,
        RadioCoordinator coordinator)
    {
        if (RadioBrowserAudioDiagnosticsParser.TryParse(
                root,
                DateTimeOffset.UtcNow,
                out RadioBrowserAudioDiagnostics? diagnostics,
                out string? error) &&
            diagnostics is not null)
        {
            connection.UpdateAudioDiagnostics(diagnostics);
            connection.SetPageVisible(diagnostics.PageVisible);
            return;
        }

        coordinator.SendJson(connection, new
        {
            ok = false,
            error
        });
    }

    private static void HandleNetworkDiagnostics(
        JsonElement root,
        RadioClientConnection connection,
        RadioCoordinator coordinator)
    {
        if (RadioBrowserNetworkDiagnosticsParser.TryParse(
                root,
                DateTimeOffset.UtcNow,
                out RadioBrowserNetworkDiagnostics? diagnostics,
                out string? error) &&
            diagnostics is not null)
        {
            connection.UpdateNetworkDiagnostics(diagnostics);
            connection.SetPageVisible(diagnostics.PageVisible);
            return;
        }

        coordinator.SendJson(connection, new
        {
            ok = false,
            error
        });
    }

    private static void HandleClientVisibility(
        JsonElement root,
        RadioClientConnection connection,
        RadioCoordinator coordinator)
    {
        if (!root.TryGetProperty("visible", out JsonElement visible) ||
            visible.ValueKind is not
                JsonValueKind.True and not JsonValueKind.False)
        {
            coordinator.SendJson(connection, new
            {
                ok = false,
                error = "Client visibility must be a boolean."
            });
            return;
        }

        connection.SetPageVisible(visible.GetBoolean());
    }

    private static async Task HandleIntentAsync(
        JsonElement root,
        JsonElement id,
        RadioClientConnection connection,
        RadioCoordinator coordinator,
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!user.IsInRole(AetherRoles.Control) &&
            !user.IsInRole(AetherRoles.Admin))
        {
            coordinator.SendJson(connection, new
            {
                id = ResponseId(id),
                ok = false,
                error = "The Aether.Control role is required."
            });
            return;
        }

        string action = root.TryGetProperty("action", out JsonElement actionValue)
            ? actionValue.GetString() ?? string.Empty
            : string.Empty;
        if (action.StartsWith("tx.", StringComparison.OrdinalIgnoreCase) ||
            action is "mox" or "ptt" or "tune")
        {
            coordinator.SendJson(connection, new
            {
                id = ResponseId(id),
                ok = false,
                error =
                    "Transmit is fail-closed in this prototype; no keying intent is accepted."
            });
            return;
        }

        string selector =
            root.TryGetProperty("selector", out JsonElement selectorValue)
                ? selectorValue.GetString() ?? string.Empty
                : string.Empty;
        JsonElement values =
            root.TryGetProperty("values", out JsonElement valuesValue)
                ? valuesValue.Clone()
                : default;
        IntentResult result = await coordinator.ApplyIntentAsync(
            new ControlIntent(action, selector, values),
            cancellationToken);
        coordinator.SendJson(connection, new
        {
            id = ResponseId(id),
            result.Ok,
            result.Error,
            result.Version,
            result.Model,
            result.Selector,
            result.Changes
        });
    }

    private static void HandleTxAcquire(
        JsonElement root,
        JsonElement id,
        RadioClientConnection connection,
        RadioCoordinator coordinator,
        System.Security.Claims.ClaimsPrincipal user)
    {
        if (!user.IsInRole(AetherRoles.Transmit) &&
            !user.IsInRole(AetherRoles.Admin))
        {
            coordinator.SendJson(connection, new
            {
                id = ResponseId(id),
                ok = false,
                error = "The Aether.Transmit role is required."
            });
            return;
        }

        int seconds = root.TryGetProperty("seconds", out JsonElement secondsValue) &&
                      secondsValue.TryGetInt32(out int parsed)
            ? parsed
            : 10;
        bool acquired = coordinator.TryAcquireTxLease(
            connection,
            TimeSpan.FromSeconds(seconds),
            out TxLease? lease,
            out string? error);
        coordinator.SendJson(connection, new
        {
            id = ResponseId(id),
            ok = acquired,
            error,
            lease
        });
    }

    private static void HandleTxRenew(
        JsonElement root,
        JsonElement id,
        RadioClientConnection connection,
        RadioCoordinator coordinator)
    {
        string leaseId = ReadLeaseId(root);
        int seconds = root.TryGetProperty("seconds", out JsonElement secondsValue) &&
                      secondsValue.TryGetInt32(out int parsed)
            ? parsed
            : 10;
        bool renewed = coordinator.TryRenewTxLease(
            connection,
            leaseId,
            TimeSpan.FromSeconds(seconds),
            out TxLease? lease,
            out string? error);
        coordinator.SendJson(connection, new
        {
            id = ResponseId(id),
            ok = renewed,
            error,
            lease
        });
    }

    private static void HandleTxRelease(
        JsonElement root,
        JsonElement id,
        RadioClientConnection connection,
        RadioCoordinator coordinator)
    {
        string leaseId = ReadLeaseId(root);
        bool released = coordinator.ReleaseTxLease(connection, leaseId);
        coordinator.SendJson(connection, new
        {
            id = ResponseId(id),
            ok = released,
            error = released
                ? null
                : "A current TX lease held by this browser is required."
        });
    }

    private static string ReadLeaseId(JsonElement root) =>
        root.TryGetProperty("leaseId", out JsonElement leaseId) &&
        leaseId.ValueKind == JsonValueKind.String
            ? leaseId.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static object? ResponseId(JsonElement id) =>
        id.ValueKind == JsonValueKind.Undefined ? null : id;
}

public sealed class OriginSettings
{
    public const string SectionName = "AllowedOrigins";
    public string[] Values { get; init; } = [];
}

public static class OriginPolicy
{
    public static bool IsAllowed(HttpContext context, OriginSettings settings)
    {
        string? origin = context.Request.Headers.Origin.FirstOrDefault();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri))
        {
            return false;
        }

        string requestAuthority = context.Request.Host.Value ?? string.Empty;
        if (string.Equals(
                originUri.Authority,
                requestAuthority,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return settings.Values.Any(
            allowed => string.Equals(
                allowed.TrimEnd('/'),
                originUri.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase));
    }
}
