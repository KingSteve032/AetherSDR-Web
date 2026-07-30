using System.Buffers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AetherRemote.Protocol;

namespace AetherRemote.Agent;

internal sealed class StationEngineReceiveSession : IAsyncDisposable
{
    private const string EngineSubprotocol = "aethersdr.experimental.v0";
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string m_remoteSessionId;
    private readonly string m_radioId;
    private readonly string m_guiClientId;
    private readonly bool m_lowBandwidth;
    private readonly Uri m_engineBaseUri;
    private readonly string m_engineOrigin;
    private readonly Func<object, CancellationToken, Task> m_brokerSender;
    private readonly HttpClient m_http;
    private readonly ClientWebSocket m_socket = new();
    private readonly CancellationTokenSource m_lifetime = new();
    private readonly SemaphoreSlim m_sendGate = new(1, 1);
    private readonly Channel<byte[]> m_binaryFrames =
        Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
    private readonly TaskCompletionSource<ConnectedRadio> m_connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? m_engineSessionId;
    private Task? m_reader;
    private Task? m_binarySender;
    private int m_disposed;

    public StationEngineReceiveSession(
        string remoteSessionId,
        string radioId,
        string guiClientId,
        bool lowBandwidth,
        string engineUrl,
        string engineOrigin,
        Func<object, CancellationToken, Task> brokerSender)
    {
        m_remoteSessionId = remoteSessionId;
        m_radioId = radioId;
        m_guiClientId = guiClientId;
        m_lowBandwidth = lowBandwidth;
        m_engineBaseUri = NormalizeBaseUri(engineUrl);
        m_engineOrigin = engineOrigin;
        m_brokerSender = brokerSender;
        m_http = new HttpClient
        {
            BaseAddress = m_engineBaseUri,
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<StationReceiveSessionOpenedMessage> StartAsync(
        CancellationToken cancellationToken)
    {
        string browserClientId =
            Guid.Parse(m_guiClientId).ToString("N");
        using HttpResponseMessage selection =
            await m_http.PostAsJsonAsync(
                "api/radios/select",
                new
                {
                    radioId = m_radioId,
                    browserClientId
                },
                cancellationToken);
        await EnsureSuccessAsync(selection, cancellationToken);
        EngineSelectionResponse? selected =
            await selection.Content.ReadFromJsonAsync<
                EngineSelectionResponse>(
                cancellationToken: cancellationToken);
        if (!StationProtocolValidator.IsSessionId(
                selected?.SessionId))
        {
            throw new InvalidDataException(
                "The station receive engine returned an invalid session.");
        }
        m_engineSessionId = selected!.SessionId;

        if (m_lowBandwidth)
        {
            using HttpResponseMessage lowBandwidth =
                await m_http.PostAsJsonAsync(
                    "api/radio/low-bandwidth",
                    new
                    {
                        enabled = true,
                        sessionId = m_engineSessionId
                    },
                    cancellationToken);
            await EnsureSuccessAsync(
                lowBandwidth,
                cancellationToken);
        }

        m_socket.Options.AddSubProtocol(EngineSubprotocol);
        m_socket.Options.SetRequestHeader(
            "Origin",
            m_engineOrigin);
        m_socket.Options.KeepAliveInterval =
            TimeSpan.FromSeconds(15);
        Uri socketUri = BuildWebSocketUri(
            m_engineBaseUri,
            m_engineSessionId);
        await m_socket.ConnectAsync(socketUri, cancellationToken);
        m_reader = ReadLoopAsync(m_lifetime.Token);
        m_binarySender = SendBinaryLoopAsync(m_lifetime.Token);
        await SendTextAsync(
            """{"id":1,"cmd":"hello","protocolVersion":0}""",
            cancellationToken);
        await SendTextAsync(
            """{"id":2,"cmd":"subscribe"}""",
            cancellationToken);

        ConnectedRadio connected = await m_connected.Task.WaitAsync(
            TimeSpan.FromSeconds(20),
            cancellationToken);
        uint clientHandle =
            await WaitForClientHandleAsync(cancellationToken);
        return new StationReceiveSessionOpenedMessage(
            StationMessageTypes.ReceiveSessionOpened,
            m_remoteSessionId,
            m_radioId,
            connected.Model,
            connected.Serial,
            clientHandle.ToString("x8"));
    }

    public async Task SendProjectedTextAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        string? validation =
            StationProtocolValidator.ValidateClientProjectionCommand(
                payload);
        if (validation is not null)
        {
            throw new InvalidDataException(validation);
        }
        await SendTextAsync(payload, cancellationToken);
    }

    private async Task ReadLoopAsync(
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            while (m_socket.State == WebSocketState.Open &&
                   !cancellationToken.IsCancellationRequested)
            {
                using MemoryStream message = new();
                WebSocketReceiveResult result;
                WebSocketMessageType? messageType = null;
                do
                {
                    result = await m_socket.ReceiveAsync(
                        buffer,
                        cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new IOException(
                            "The station receive engine closed its projection.");
                    }
                    messageType ??= result.MessageType;
                    if (result.MessageType != messageType)
                    {
                        throw new InvalidDataException(
                            "The station receive engine changed frame type mid-message.");
                    }
                    int maximum = result.MessageType ==
                        WebSocketMessageType.Binary
                        ? StationProtocol.MaximumProjectionBinaryBytes
                        : StationProtocol.MaximumProjectionTextBytes;
                    if (message.Length + result.Count > maximum)
                    {
                        throw new InvalidDataException(
                            "A station receive-engine frame exceeded its boundary.");
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (messageType == WebSocketMessageType.Text)
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
                            "The station receive-engine text was not valid UTF-8.",
                            exception);
                    }
                    ObserveText(text);
                    await m_brokerSender(
                        new StationReceiveTextMessage(
                            StationMessageTypes.ReceiveText,
                            m_remoteSessionId,
                            text),
                        cancellationToken);
                }
                else if (messageType == WebSocketMessageType.Binary)
                {
                    byte[] frame = message.ToArray();
                    string encoded = Convert.ToBase64String(frame);
                    string? error =
                        StationProtocolValidator.ValidateReceiveBinary(
                            new StationReceiveBinaryMessage(
                                StationMessageTypes.ReceiveBinary,
                                m_remoteSessionId,
                                encoded),
                            out _);
                    if (error is not null)
                    {
                        throw new InvalidDataException(error);
                    }
                    m_binaryFrames.Writer.TryWrite(frame);
                }
                else
                {
                    throw new InvalidDataException(
                        "The station receive engine sent an unsupported frame.");
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
            when (exception is WebSocketException or
                  IOException or
                  JsonException or
                  InvalidDataException)
        {
            m_connected.TrySetException(exception);
            throw;
        }
        finally
        {
            m_binaryFrames.Writer.TryComplete();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task SendBinaryLoopAsync(
        CancellationToken cancellationToken)
    {
        await foreach (
            byte[] frame in
            m_binaryFrames.Reader.ReadAllAsync(cancellationToken))
        {
            await m_brokerSender(
                new StationReceiveBinaryMessage(
                    StationMessageTypes.ReceiveBinary,
                    m_remoteSessionId,
                    Convert.ToBase64String(frame)),
                cancellationToken);
        }
    }

    private void ObserveText(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(
            payload,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        JsonElement root = document.RootElement;
        if (!TryReadSnapshot(root, out JsonElement snapshot))
        {
            return;
        }
        bool connected =
            snapshot.TryGetProperty(
                "connected",
                out JsonElement connectedElement) &&
            connectedElement.ValueKind ==
                JsonValueKind.True;
        string connectionState =
            snapshot.TryGetProperty(
                "connectionState",
                out JsonElement stateElement) &&
            stateElement.ValueKind == JsonValueKind.String
                ? stateElement.GetString() ?? string.Empty
                : string.Empty;
        if (!connected)
        {
            if (connectionState is "radio-busy" or "error")
            {
                string error =
                    snapshot.TryGetProperty(
                        "connectionError",
                        out JsonElement errorElement) &&
                    errorElement.ValueKind == JsonValueKind.String
                        ? errorElement.GetString() ??
                          "The radio rejected receive admission."
                        : "The radio rejected receive admission.";
                m_connected.TrySetException(
                    new StationReceiveSessionException(
                        connectionState == "radio-busy"
                            ? "radio_busy"
                            : "radio_unreachable",
                        SanitizeError(error)));
            }
            return;
        }
        string model = ReadBoundedText(
            snapshot,
            "radioModel",
            64,
            "FLEX");
        string serial = ReadBoundedText(
            snapshot,
            "serial",
            64,
            "UNKNOWN");
        m_connected.TrySetResult(new ConnectedRadio(model, serial));
    }

    private async Task<uint> WaitForClientHandleAsync(
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 25; attempt++)
        {
            using HttpResponseMessage response =
                await m_http.GetAsync(
                    "api/admin/radios",
                    cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            await using Stream content =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken);
            using JsonDocument document =
                await JsonDocument.ParseAsync(
                    content,
                    cancellationToken: cancellationToken);
            if (TryFindClientHandle(
                    document.RootElement,
                    m_engineSessionId!,
                    out uint handle))
            {
                return handle;
            }
            await Task.Delay(
                TimeSpan.FromMilliseconds(200),
                cancellationToken);
        }
        throw new InvalidDataException(
            "The station receive engine did not expose its admitted client handle.");
    }

    private async Task SendTextAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length >
            StationProtocol.MaximumProjectionTextBytes)
        {
            throw new InvalidDataException(
                "A projected receive command exceeded its boundary.");
        }
        await m_sendGate.WaitAsync(cancellationToken);
        try
        {
            await m_socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            m_sendGate.Release();
        }
    }

    private static bool TryReadSnapshot(
        JsonElement root,
        out JsonElement snapshot)
    {
        snapshot = default;
        bool welcome =
            root.TryGetProperty(
                "type",
                out JsonElement typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            string.Equals(
                typeElement.GetString(),
                "welcome",
                StringComparison.Ordinal);
        bool snapshotEvent =
            root.TryGetProperty(
                "event",
                out JsonElement eventElement) &&
            eventElement.ValueKind == JsonValueKind.String &&
            string.Equals(
                eventElement.GetString(),
                "snapshot",
                StringComparison.Ordinal);
        return (welcome || snapshotEvent) &&
               root.TryGetProperty("snapshot", out snapshot) &&
               snapshot.ValueKind == JsonValueKind.Object;
    }

    private static string ReadBoundedText(
        JsonElement objectElement,
        string propertyName,
        int maximumLength,
        string fallback)
    {
        if (!objectElement.TryGetProperty(
                propertyName,
                out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }
        string text = value.GetString() ?? string.Empty;
        return StationProtocolValidator.IsText(
            text,
            maximumLength)
            ? text
            : fallback;
    }

    private static bool TryFindClientHandle(
        JsonElement root,
        string engineSessionId,
        out uint handle)
    {
        handle = 0;
        if (!root.TryGetProperty(
                "radios",
                out JsonElement radios) ||
            radios.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (JsonElement radio in radios.EnumerateArray())
        {
            if (!radio.TryGetProperty(
                    "sessions",
                    out JsonElement sessions) ||
                sessions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (JsonElement session in sessions.EnumerateArray())
            {
                if (!session.TryGetProperty(
                        "sessionId",
                        out JsonElement idElement) ||
                    !string.Equals(
                        idElement.GetString(),
                        engineSessionId,
                        StringComparison.Ordinal) ||
                    !session.TryGetProperty(
                        "transport",
                        out JsonElement transport) ||
                    !transport.TryGetProperty(
                        "clientHandle",
                        out JsonElement handleElement) ||
                    !handleElement.TryGetUInt32(out handle) ||
                    handle == 0)
                {
                    continue;
                }
                return true;
            }
        }
        return false;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        string error = "The station receive engine rejected the request.";
        if (response.Content.Headers.ContentLength is not > 4096)
        {
            try
            {
                string body =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty(
                        "error",
                        out JsonElement errorElement) &&
                    errorElement.ValueKind == JsonValueKind.String)
                {
                    error = SanitizeError(
                        errorElement.GetString() ?? error);
                }
            }
            catch (JsonException)
            {
            }
        }
        throw new InvalidDataException(error);
    }

    private static string SanitizeError(string value)
    {
        string cleaned = new(
            value
                .Where(character =>
                    !char.IsControl(character))
                .Take(256)
                .ToArray());
        return string.IsNullOrWhiteSpace(cleaned)
            ? "The station receive engine rejected the request."
            : cleaned;
    }

    private static Uri NormalizeBaseUri(string value)
    {
        Uri uri = new(value, UriKind.Absolute);
        return uri.AbsoluteUri.EndsWith(
            "/",
            StringComparison.Ordinal)
            ? uri
            : new Uri($"{uri.AbsoluteUri}/", UriKind.Absolute);
    }

    private static Uri BuildWebSocketUri(
        Uri baseUri,
        string sessionId)
    {
        UriBuilder builder = new(
            new Uri(
                baseUri,
                $"ws/radio?sessionId={Uri.EscapeDataString(sessionId)}"))
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps
                ? "wss"
                : "ws"
        };
        return builder.Uri;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
        {
            return;
        }
        m_lifetime.Cancel();
        m_binaryFrames.Writer.TryComplete();
        if (m_socket.State is
            WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(2));
            try
            {
                await m_socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Station receive session closed.",
                    timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }
        if (m_reader is not null)
        {
            await IgnoreExpectedAsync(m_reader);
        }
        if (m_binarySender is not null)
        {
            await IgnoreExpectedAsync(m_binarySender);
        }
        if (m_engineSessionId is not null)
        {
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(5));
            try
            {
                using HttpResponseMessage response =
                    await m_http.PostAsync(
                        $"api/session/release?sessionId={Uri.EscapeDataString(m_engineSessionId)}",
                        content: null,
                        timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpRequestException)
            {
            }
        }
        m_socket.Dispose();
        m_http.Dispose();
        m_sendGate.Dispose();
        m_lifetime.Dispose();
    }

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
                  InvalidDataException or
                  ChannelClosedException)
        {
        }
    }

    private sealed record EngineSelectionResponse(
        string SessionId);

    private sealed record ConnectedRadio(
        string Model,
        string Serial);
}
