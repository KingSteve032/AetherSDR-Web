using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed class RemoteRadioIntentRouter : IRadioIntentTransport
{
    private const int MaximumPayloadBytes = 48 * 1024;
    private static readonly HashSet<string> AllowedActions =
        new(
            [
                "slice.set",
                "slice.create",
                "slice.remove",
                "pan.set",
                "pan.create",
                "pan.remove"
            ],
            StringComparer.Ordinal);
    private readonly object m_senderGate = new();
    private readonly ConcurrentDictionary<long, PendingIntent> m_pending = [];
    private Func<ReadOnlyMemory<byte>, CancellationToken, Task>? m_sender;
    private long m_sequence = 1_000;

    public IDisposable Attach(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        lock (m_senderGate)
        {
            if (m_sender is not null)
            {
                throw new InvalidOperationException(
                    "A remote receive-control channel is already attached.");
            }
            m_sender = sender;
        }
        return new SenderLease(this, sender);
    }

    public async Task<IntentResult> ApplyAsync(
        ControlIntent intent,
        long currentVersion,
        CancellationToken cancellationToken)
    {
        if (!AllowedActions.Contains(intent.Action) ||
            intent.Selector is null ||
            intent.Selector.Length > 64 ||
            intent.Selector.Any(char.IsControl) ||
            intent.Values.ValueKind is not (
                JsonValueKind.Object or JsonValueKind.Undefined))
        {
            return IntentResult.Failure(
                "Unsupported remote receive intent.",
                currentVersion);
        }

        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? sender;
        lock (m_senderGate)
        {
            sender = m_sender;
        }
        if (sender is null)
        {
            return IntentResult.Failure(
                "The remote station is reconnecting.",
                currentVersion);
        }

        long id = Interlocked.Increment(ref m_sequence);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                id,
                cmd = "intent",
                action = intent.Action,
                selector = intent.Selector,
                values = intent.Values.ValueKind == JsonValueKind.Undefined
                    ? JsonSerializer.SerializeToElement(new { })
                    : intent.Values
            });
        if (payload.Length > MaximumPayloadBytes)
        {
            return IntentResult.Failure(
                "The remote receive intent is too large.",
                currentVersion);
        }

        TaskCompletionSource<IntentResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!m_pending.TryAdd(
                id,
                new PendingIntent(completion, currentVersion)))
        {
            return IntentResult.Failure(
                "The remote receive-control sequence collided.",
                currentVersion);
        }
        try
        {
            await sender(payload, cancellationToken);
            return await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(8),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            return IntentResult.Failure(
                "The remote radio did not confirm the request.",
                currentVersion);
        }
        catch (Exception exception)
            when (exception is WebSocketException or
                  IOException or
                  InvalidOperationException or
                  ObjectDisposedException)
        {
            return IntentResult.Failure(
                "The remote station could not accept the request.",
                currentVersion);
        }
        finally
        {
            m_pending.TryRemove(id, out _);
        }
    }

    public bool TryHandleResponse(JsonElement root)
    {
        if (!root.TryGetProperty("id", out JsonElement idElement) ||
            !idElement.TryGetInt64(out long id) ||
            !m_pending.TryRemove(id, out PendingIntent? pending))
        {
            return false;
        }

        bool ok =
            root.TryGetProperty("ok", out JsonElement okElement) &&
            okElement.ValueKind == JsonValueKind.True;
        string? error =
            root.TryGetProperty("error", out JsonElement errorElement) &&
            errorElement.ValueKind == JsonValueKind.String
                ? BoundedText(errorElement.GetString(), 256)
                : null;
        long version =
            root.TryGetProperty("version", out JsonElement versionElement) &&
            versionElement.TryGetInt64(out long remoteVersion)
                ? Math.Max(pending.CurrentVersion, remoteVersion)
                : pending.CurrentVersion;
        string model =
            root.TryGetProperty("model", out JsonElement modelElement) &&
            modelElement.ValueKind == JsonValueKind.String
                ? BoundedText(modelElement.GetString(), 32) ?? string.Empty
                : string.Empty;
        string selector =
            root.TryGetProperty(
                "selector",
                out JsonElement selectorElement) &&
            selectorElement.ValueKind == JsonValueKind.String
                ? BoundedText(selectorElement.GetString(), 64) ?? string.Empty
                : string.Empty;
        IReadOnlyDictionary<string, object?> changes =
            root.TryGetProperty("changes", out JsonElement changesElement) &&
            changesElement.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    changesElement.GetRawText()) ??
                  new Dictionary<string, object?>()
                : new Dictionary<string, object?>();
        pending.Completion.TrySetResult(
            new IntentResult(
                ok,
                ok ? null : error ?? "The remote radio rejected the request.",
                version,
                model,
                selector,
                changes));
        return true;
    }

    private void Detach(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sender)
    {
        lock (m_senderGate)
        {
            if (!ReferenceEquals(m_sender, sender))
            {
                return;
            }
            m_sender = null;
        }
        foreach ((long id, PendingIntent pending) in m_pending.ToArray())
        {
            if (m_pending.TryRemove(id, out _))
            {
                pending.Completion.TrySetResult(
                    IntentResult.Failure(
                        "The remote station disconnected.",
                        pending.CurrentVersion));
            }
        }
    }

    private static string? BoundedText(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl)
            ? value
            : null;

    private sealed record PendingIntent(
        TaskCompletionSource<IntentResult> Completion,
        long CurrentVersion);

    private sealed class SenderLease(
        RemoteRadioIntentRouter owner,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sender)
        : IDisposable
    {
        private int m_disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_disposed, 1) == 0)
            {
                owner.Detach(sender);
            }
        }
    }
}

public sealed class RemoteRadioProjectionService(
    RadioCoordinator coordinator,
    RemoteRadioIntentRouter intentRouter,
    IRadioConnectionSelection selection,
    IOptions<RemoteStationSettings> settings,
    string guiClientId,
    ILogger<RemoteRadioProjectionService> logger)
    : BackgroundService, IRadioTransportDiagnostics
{
    private const string ProjectionSubprotocol = "aetherremote.receive.v1";
    private const int MaximumTextBytes = 48 * 1024;
    private const int MaximumBinaryBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly RemoteStationSettings m_settings = settings.Value;
    private long m_connectionAttempts;
    private long m_spectrumFrames;
    private long m_audioFrames;
    private long m_connectedAt;
    private long m_lastFrameAt;
    private long m_lastSpectrumAt;
    private long m_lastAudioAt;
    private long m_clientHandle;

    public RadioTransportDiagnostics GetDiagnostics() =>
        new(
            "RemoteProjection",
            unchecked((uint)Math.Max(0, Volatile.Read(ref m_clientHandle))),
            0,
            0,
            Volatile.Read(ref m_connectionAttempts),
            Volatile.Read(ref m_spectrumFrames) +
                Volatile.Read(ref m_audioFrames),
            Volatile.Read(ref m_spectrumFrames),
            Volatile.Read(ref m_audioFrames),
            FromUnixMilliseconds(Volatile.Read(ref m_connectedAt)),
            FromUnixMilliseconds(Volatile.Read(ref m_lastFrameAt)),
            FromUnixMilliseconds(Volatile.Read(ref m_lastSpectrumAt)),
            FromUnixMilliseconds(Volatile.Read(ref m_lastAudioAt)),
            null,
            []);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (!m_settings.Enabled)
        {
            coordinator.SetRadioConnection(
                false,
                connectionState: "error",
                connectionError: "Remote station support is disabled.");
            return;
        }

        Uri brokerBase =
            RemoteStationSettingsValidator.GetBrokerBaseUri(m_settings);
        string credential =
            RemoteStationSettingsValidator.ReadCredential(
                m_settings.RuntimeCredentialFile,
                "runtime");
        while (!stoppingToken.IsCancellationRequested)
        {
            SelectedRadioEndpoint endpoint = selection.Selected;
            if (!string.Equals(
                    endpoint.Source,
                    "remote",
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(endpoint.StationId) ||
                string.IsNullOrWhiteSpace(endpoint.SourceRadioId))
            {
                coordinator.SetRadioConnection(
                    false,
                    connectionState: "error",
                    connectionError:
                        "The remote radio selection is invalid.");
                return;
            }

            Interlocked.Increment(ref m_connectionAttempts);
            coordinator.SetRadioConnection(
                false,
                connectionState: "connecting");
            using CancellationTokenSource projectionLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken);
            try
            {
                Task projection = RunProjectionAsync(
                    brokerBase,
                    credential,
                    endpoint,
                    projectionLifetime.Token);
                Task changed = selection.WaitForChangeAsync(
                    endpoint.Revision,
                    projectionLifetime.Token);
                Task completed = await Task.WhenAny(projection, changed);
                if (ReferenceEquals(completed, changed))
                {
                    await changed;
                    projectionLifetime.Cancel();
                    await IgnoreExpectedAsync(projection);
                    continue;
                }
                await projection;
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                      WebSocketException or
                      IOException or
                      JsonException or
                      InvalidDataException or
                      TimeoutException)
            {
                logger.LogWarning(
                    exception,
                    "Remote receive projection ended; retrying");
                coordinator.SetRadioConnection(
                    false,
                    connectionState: "reconnecting",
                    connectionError:
                        "The remote station connection stopped. Retrying.");
                await Task.Delay(
                    TimeSpan.FromSeconds(3),
                    stoppingToken);
            }
        }
    }

    private async Task RunProjectionAsync(
        Uri brokerBase,
        string credential,
        SelectedRadioEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using HttpClient http = new()
        {
            BaseAddress = brokerBase,
            Timeout = TimeSpan.FromSeconds(20)
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);

        string? remoteSessionId = null;
        using ClientWebSocket socket = new();
        using SemaphoreSlim sendGate = new(1, 1);
        try
        {
            using HttpResponseMessage opened =
                await http.PostAsJsonAsync(
                    "api/receive-sessions",
                    new OpenProjectionRequest(
                        endpoint.StationId,
                        endpoint.SourceRadioId,
                        guiClientId,
                        selection.LowBandwidth),
                    cancellationToken);
            if (!opened.IsSuccessStatusCode)
            {
                throw await CreateBrokerExceptionAsync(
                    opened,
                    cancellationToken);
            }
            OpenProjectionResponse? projection =
                await opened.Content.ReadFromJsonAsync<
                    OpenProjectionResponse>(
                    cancellationToken: cancellationToken);
            if (!IsSessionId(projection?.SessionId))
            {
                throw new InvalidDataException(
                    "The broker returned an invalid remote session.");
            }
            remoteSessionId = projection!.SessionId;
            if (uint.TryParse(
                    projection.ClientHandle,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out uint clientHandle))
            {
                Interlocked.Exchange(ref m_clientHandle, clientHandle);
            }

            socket.Options.AddSubProtocol(ProjectionSubprotocol);
            socket.Options.SetRequestHeader(
                "Authorization",
                $"Bearer {credential}");
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            await socket.ConnectAsync(
                BuildProjectionUri(brokerBase, remoteSessionId),
                cancellationToken);
            using IDisposable intentLease = intentRouter.Attach(
                (payload, token) =>
                    SendTextAsync(
                        socket,
                        sendGate,
                        payload,
                        token));
            Interlocked.Exchange(
                ref m_connectedAt,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await ReceiveLoopAsync(socket, cancellationToken);
        }
        finally
        {
            await CloseIfOpenAsync(socket);
            Interlocked.Exchange(ref m_clientHandle, 0);
            if (remoteSessionId is not null)
            {
                using CancellationTokenSource cleanup =
                    new(TimeSpan.FromSeconds(5));
                try
                {
                    using HttpResponseMessage response =
                        await http.DeleteAsync(
                            $"api/receive-sessions/{remoteSessionId}",
                            cleanup.Token);
                }
                catch (Exception exception)
                    when (exception is HttpRequestException or
                          OperationCanceledException or
                          ObjectDisposedException)
                {
                    logger.LogDebug(
                        exception,
                        "Remote projection cleanup was not acknowledged");
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   socket.State == WebSocketState.Open)
            {
                using MemoryStream message = new();
                WebSocketReceiveResult result;
                WebSocketMessageType? messageType = null;
                do
                {
                    result = await socket.ReceiveAsync(
                        buffer,
                        cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new IOException(
                            "The remote projection closed.");
                    }
                    messageType ??= result.MessageType;
                    if (messageType != result.MessageType)
                    {
                        throw new InvalidDataException(
                            "The remote projection changed frame type.");
                    }
                    int maximum = result.MessageType ==
                        WebSocketMessageType.Binary
                        ? MaximumBinaryBytes
                        : MaximumTextBytes;
                    if (message.Length + result.Count > maximum)
                    {
                        throw new InvalidDataException(
                            "A remote projection frame was too large.");
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                long now =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Interlocked.Exchange(ref m_lastFrameAt, now);
                if (messageType == WebSocketMessageType.Text)
                {
                    HandleText(message);
                }
                else if (messageType == WebSocketMessageType.Binary)
                {
                    byte[] frame = message.ToArray();
                    if (IsSpectrumFrame(frame))
                    {
                        Interlocked.Increment(ref m_spectrumFrames);
                        Interlocked.Exchange(ref m_lastSpectrumAt, now);
                        coordinator.BroadcastSpectrum(frame);
                    }
                    else if (IsAudioFrame(frame))
                    {
                        Interlocked.Increment(ref m_audioFrames);
                        Interlocked.Exchange(ref m_lastAudioAt, now);
                        coordinator.BroadcastAudio(frame);
                    }
                    else
                    {
                        throw new InvalidDataException(
                            "The remote projection sent malformed binary data.");
                    }
                }
                else
                {
                    throw new InvalidDataException(
                        "The remote projection sent an unsupported frame.");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleText(MemoryStream message)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(
                message.GetBuffer(),
                0,
                checked((int)message.Length));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Remote projection text was not valid UTF-8.",
                exception);
        }
        using JsonDocument document = JsonDocument.Parse(
            text,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        JsonElement root = document.RootElement;
        if (intentRouter.TryHandleResponse(root))
        {
            return;
        }
        if (!TryReadSnapshot(root, out JsonElement snapshot))
        {
            return;
        }
        RadioSnapshot? projected =
            snapshot.Deserialize<RadioSnapshot>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (projected is null ||
            !coordinator.ApplyReceiveProjection(projected))
        {
            throw new InvalidDataException(
                "The remote station sent an invalid radio snapshot.");
        }
    }

    private static bool TryReadSnapshot(
        JsonElement root,
        out JsonElement snapshot)
    {
        snapshot = default;
        bool welcome =
            root.TryGetProperty("type", out JsonElement type) &&
            type.ValueKind == JsonValueKind.String &&
            string.Equals(
                type.GetString(),
                "welcome",
                StringComparison.Ordinal);
        bool snapshotEvent =
            root.TryGetProperty("event", out JsonElement eventName) &&
            eventName.ValueKind == JsonValueKind.String &&
            string.Equals(
                eventName.GetString(),
                "snapshot",
                StringComparison.Ordinal);
        return (welcome || snapshotEvent) &&
               root.TryGetProperty("snapshot", out snapshot) &&
               snapshot.ValueKind == JsonValueKind.Object;
    }

    private static bool IsSpectrumFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 20 ||
            !frame[..4].SequenceEqual("AETF"u8) ||
            frame[4] != 0)
        {
            return false;
        }
        int headerSize = frame[5] switch
        {
            1 => 20,
            2 => 24,
            3 => 28,
            _ => 0
        };
        if (headerSize == 0 || frame.Length < headerSize)
        {
            return false;
        }
        ushort binCount =
            BinaryPrimitives.ReadUInt16LittleEndian(frame[6..]);
        if (binCount is < 64 or > 8192 ||
            frame.Length != headerSize + (binCount * sizeof(short)))
        {
            return false;
        }
        long center =
            BinaryPrimitives.ReadInt64LittleEndian(frame[12..]);
        if (center is < 100_000 or > 60_000_000)
        {
            return false;
        }
        return frame[5] < 3 ||
               BinaryPrimitives.ReadInt32LittleEndian(frame[24..])
                   is >= 10_000 and <= 14_000_000;
    }

    private static bool IsAudioFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < AudioFrameCodec.HeaderSize ||
            !frame[..4].SequenceEqual("AETA"u8) ||
            frame[4] != 0 ||
            frame[5] != 2)
        {
            return false;
        }
        ushort sampleRate =
            BinaryPrimitives.ReadUInt16LittleEndian(frame[6..]);
        uint frameCount =
            BinaryPrimitives.ReadUInt32LittleEndian(frame[12..]);
        return sampleRate >= 8_000 &&
               frameCount is > 0 and <= 8_000 &&
               frame.Length ==
                   AudioFrameCodec.HeaderSize +
                   checked((int)frameCount * 2 * sizeof(short));
    }

    private static async Task SendTextAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendGate,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
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

    private static Uri BuildProjectionUri(
        Uri brokerBase,
        string sessionId)
    {
        UriBuilder builder = new(
            new Uri(
                brokerBase,
                $"receive/v1?sessionId={Uri.EscapeDataString(sessionId)}"))
        {
            Scheme = string.Equals(
                brokerBase.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        };
        return builder.Uri;
    }

    private static async Task<Exception> CreateBrokerExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string message =
            "The remote station could not admit another browser.";
        if (response.Content.Headers.ContentLength is not > 4096)
        {
            try
            {
                using JsonDocument body = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(
                        cancellationToken));
                if (body.RootElement.TryGetProperty(
                        "error",
                        out JsonElement error) &&
                    error.ValueKind == JsonValueKind.String)
                {
                    string value = error.GetString() ?? string.Empty;
                    if (value.Length is > 0 and <= 256 &&
                        !value.Any(char.IsControl))
                    {
                        message = value;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
        return new InvalidDataException(message);
    }

    private static bool IsSessionId(string? value) =>
        value is { Length: 32 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static DateTimeOffset? FromUnixMilliseconds(long value) =>
        value > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : null;

    private static async Task IgnoreExpectedAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or
                  WebSocketException or
                  IOException or
                  ObjectDisposedException)
        {
        }
    }

    private static async Task CloseIfOpenAsync(ClientWebSocket socket)
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
                "Remote receive projection ended.",
                timeout.Token);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or
                  WebSocketException)
        {
        }
    }

    private sealed record OpenProjectionRequest(
        string StationId,
        string RadioId,
        string GuiClientId,
        bool LowBandwidth);

    private sealed record OpenProjectionResponse(
        string SessionId,
        string ClientHandle);
}
