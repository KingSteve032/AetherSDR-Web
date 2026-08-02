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
                        tx = coordinator.GetBrowserTxCapability(connection),
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
                    BrowserReceiveAttempt receive =
                        await BrowserConnectionReceiveGuard.ReceiveAsync(
                            token => socket.ReceiveAsync(buffer, token),
                            cancellationToken);
                    if (receive.TimedOut)
                    {
                        logger.LogWarning(
                            "Closing stale browser WebSocket {ClientId} after the receive heartbeat timeout.",
                            connection.ClientId);
                        if (socket.State == WebSocketState.Open)
                        {
                            await socket.CloseOutputAsync(
                                WebSocketCloseStatus.EndpointUnavailable,
                                "Browser heartbeat timed out.",
                                CancellationToken.None);
                        }
                        return;
                    }
                    result = receive.Result!;
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                coordinator.SendJson(connection, new
                {
                    ok = false,
                    error = "A browser request must be a JSON object."
                });
                return;
            }

            string? command =
                root.TryGetProperty("cmd", out JsonElement commandValue) &&
                commandValue.ValueKind == JsonValueKind.String
                    ? commandValue.GetString()
                    : null;
            JsonElement id = root.TryGetProperty("id", out JsonElement idValue)
                ? idValue.Clone()
                : default;

            coordinator.ObserveBrowserActivity(
                connection,
                user.Identity?.IsAuthenticated == true);

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
                case "tx.renew":
                case "tx.release":
                case "tx.intent":
                    await HandleTxRequestAsync(
                        root,
                        id,
                        connection,
                        coordinator,
                        user,
                        cancellationToken);
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

    private static async Task HandleTxRequestAsync(
        JsonElement root,
        JsonElement id,
        RadioClientConnection connection,
        RadioCoordinator coordinator,
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!RadioBrowserTxProtocol.TryParse(
                root,
                out BrowserTxRequest? request,
                out string parseError) ||
            request is null)
        {
            coordinator.SendJson(connection, new
            {
                id = ResponseId(id),
                protocolVersion = RadioBrowserTxProtocol.Version,
                sequence = ResponseSequence(root),
                ok = false,
                error = parseError,
                capability = coordinator.GetBrowserTxCapability(
                    connection,
                    user.Identity?.IsAuthenticated == true)
            });
            return;
        }

        if (!connection.TryAcceptTxEnvelope(
                request.Sequence,
                request.Intent?.IntentId,
                out string replayError))
        {
            coordinator.SendJson(connection, new
            {
                id = request.RequestId,
                protocolVersion = RadioBrowserTxProtocol.Version,
                sequence = request.Sequence,
                ok = false,
                error = replayError,
                capability = coordinator.GetBrowserTxCapability(
                    connection,
                    user.Identity?.IsAuthenticated == true)
            });
            return;
        }

        bool authenticated = user.Identity?.IsAuthenticated == true;
        switch (request.Kind)
        {
            case BrowserTxRequestKind.Acquire:
                {
                    bool acquired = coordinator.TryAcquireTxLease(
                        connection,
                        TimeSpan.FromSeconds(request.Seconds!.Value),
                        authenticated,
                        out TxLease? lease,
                        out string? error);
                    if (acquired && lease is not null)
                    {
                        await coordinator.FlushTxLifecycleAsync(cancellationToken);
                        if (!coordinator.TryConfirmTxLease(
                                connection,
                                lease.LeaseId,
                                out TxLease? confirmed,
                                out string? confirmationError))
                        {
                            acquired = false;
                            lease = null;
                            error = confirmationError ??
                                "The TX lease was revoked during lifecycle registration.";
                        }
                        else
                        {
                            lease = confirmed;
                        }
                    }
                    coordinator.SendJson(connection, new
                    {
                        id = request.RequestId,
                        protocolVersion = RadioBrowserTxProtocol.Version,
                        sequence = request.Sequence,
                        ok = acquired,
                        error,
                        lease,
                        capability = coordinator.GetBrowserTxCapability(
                            connection,
                            authenticated)
                    });
                    break;
                }
            case BrowserTxRequestKind.Renew:
                {
                    bool renewed = coordinator.TryRenewTxLease(
                        connection,
                        request.LeaseId!,
                        TimeSpan.FromSeconds(request.Seconds!.Value),
                        authenticated,
                        out TxLease? lease,
                        out string? error);
                    if (renewed && lease is not null)
                    {
                        await coordinator.FlushTxLifecycleAsync(cancellationToken);
                        if (!coordinator.TryConfirmTxLease(
                                connection,
                                lease.LeaseId,
                                out TxLease? confirmed,
                                out string? confirmationError))
                        {
                            renewed = false;
                            lease = null;
                            error = confirmationError ??
                                "The TX lease was revoked during lifecycle renewal.";
                        }
                        else
                        {
                            lease = confirmed;
                        }
                    }
                    coordinator.SendJson(connection, new
                    {
                        id = request.RequestId,
                        protocolVersion = RadioBrowserTxProtocol.Version,
                        sequence = request.Sequence,
                        ok = renewed,
                        error,
                        lease,
                        capability = coordinator.GetBrowserTxCapability(
                            connection,
                            authenticated)
                    });
                    break;
                }
            case BrowserTxRequestKind.Release:
                {
                    bool released = coordinator.ReleaseTxLease(
                        connection,
                        request.LeaseId!,
                        authenticated,
                        out string? error);
                    if (released)
                    {
                        await coordinator.FlushTxLifecycleAsync(cancellationToken);
                    }
                    coordinator.SendJson(connection, new
                    {
                        id = request.RequestId,
                        protocolVersion = RadioBrowserTxProtocol.Version,
                        sequence = request.Sequence,
                        ok = released,
                        error,
                        capability = coordinator.GetBrowserTxCapability(
                            connection,
                            authenticated)
                    });
                    break;
                }
            case BrowserTxRequestKind.Intent:
                {
                    await coordinator.FlushTxLifecycleAsync(cancellationToken);
                    BrowserTxIntentResult result =
                        await coordinator.ExecuteBrowserTxIntentAsync(
                            connection,
                            request,
                            authenticated,
                            cancellationToken);
                    coordinator.SendJson(connection, new
                    {
                        id = request.RequestId,
                        protocolVersion = RadioBrowserTxProtocol.Version,
                        result.Sequence,
                        result.Ok,
                        result.Validated,
                        result.Outcome,
                        result.Error,
                        result.IntentId,
                        result.Action,
                        result.ObservedAt,
                        result.Capability
                    });
                    break;
                }
            case BrowserTxRequestKind.Heartbeat:
                {
                    BrowserTxHeartbeatResult result =
                        await coordinator.HeartbeatBrowserTxAsync(
                            connection,
                            request,
                            authenticated,
                            cancellationToken);
                    coordinator.SendJson(connection, new
                    {
                        id = request.RequestId,
                        protocolVersion = RadioBrowserTxProtocol.Version,
                        result.Sequence,
                        result.Ok,
                        result.Outcome,
                        result.Error,
                        result.ObservedAt,
                        result.Capability
                    });
                    break;
                }
            default:
                throw new InvalidOperationException(
                    "An unsupported parsed TX request was received.");
        }
    }

    private static long? ResponseSequence(JsonElement root)
    {
        if (!root.TryGetProperty("sequence", out JsonElement sequence) ||
            sequence.ValueKind != JsonValueKind.Number ||
            !sequence.TryGetInt64(out long value) ||
            value <= 0 ||
            value > RadioBrowserTxProtocol.MaximumSafeInteger)
        {
            return null;
        }
        return value;
    }

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
