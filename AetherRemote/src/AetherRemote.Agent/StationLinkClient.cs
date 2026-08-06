using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Agent;

public sealed class StationLinkClient(
    IOptions<AgentSettings> settings,
    IStationRadioInventoryProvider inventory,
    StationReceiveSessionManager receiveSessions,
    StationReleaseServiceControlService releaseServiceControl,
    ILogger<StationLinkClient> logger)
    : BackgroundService
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly AgentSettings m_settings = settings.Value;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        string credential = ReadCredentialFile(
            m_settings.CredentialFile);
        int consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(
                    credential,
                    stoppingToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
                when (exception is WebSocketException or
                      HttpRequestException or
                      IOException or
                      JsonException or
                      InvalidDataException or
                      OperationCanceledException)
            {
                consecutiveFailures++;
                TimeSpan delay = ReconnectDelay(consecutiveFailures);
                logger.LogWarning(
                    exception,
                    "Station link failed; retrying in {DelaySeconds:F1} seconds",
                    delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task RunConnectionAsync(
        string credential,
        CancellationToken cancellationToken)
    {
        StationLinkTokenResponse linkToken =
            await RequestLinkTokenAsync(
                credential,
                cancellationToken);
        using ClientWebSocket socket = new();
        using SemaphoreSlim sendGate = new(1, 1);
        socket.Options.AddSubProtocol(
            StationProtocol.WebSocketSubprotocol);
        socket.Options.SetRequestHeader(
            "X-Aether-Station-Id",
            m_settings.StationId);
        socket.Options.SetRequestHeader(
            "Authorization",
            $"Bearer {linkToken.AccessToken}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        Uri brokerUri = new(m_settings.BrokerUrl, UriKind.Absolute);
        await socket.ConnectAsync(brokerUri, cancellationToken);
        string instanceId = Guid.NewGuid().ToString("N");
        await SendJsonAsync(
            socket,
            sendGate,
            new StationHelloMessage(
                StationMessageTypes.Hello,
                StationProtocol.Version,
                m_settings.StationId,
                instanceId,
                m_settings.SoftwareVersion,
                linkToken.Capabilities),
            cancellationToken);

        using CancellationTokenSource welcomeTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        welcomeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        BrokerWelcomeMessage welcome = await ReceiveWelcomeAsync(
            socket,
            welcomeTimeout.Token);
        logger.LogInformation(
            "Station {StationId} connected to broker as {ConnectionId}",
            m_settings.StationId,
            welcome.ConnectionId);

        using IDisposable projectionSender =
            receiveSessions.AttachBrokerSender(
                (message, token) =>
                    SendJsonAsync(
                        socket,
                        sendGate,
                        message,
                        token));
        long inventorySequence = 0;
        long heartbeatSequence = 0;
        await SendInventoryAsync(
            socket,
            sendGate,
            ++inventorySequence,
            cancellationToken);

        using CancellationTokenSource connectionLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        Task receiveTask = ReceiveBrokerMessagesAsync(
            socket,
            sendGate,
            connectionLifetime.Token);
        DateTimeOffset nextInventory = DateTimeOffset.UtcNow.AddSeconds(
            m_settings.InventorySeconds);
        DateTimeOffset nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(
            welcome.HeartbeatSeconds);

        try
        {
            while (!connectionLifetime.IsCancellationRequested &&
                   socket.State == WebSocketState.Open)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset nextAction =
                    nextInventory <= nextHeartbeat
                        ? nextInventory
                        : nextHeartbeat;
                TimeSpan delay = nextAction - now;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }
                Task delayTask = Task.Delay(
                    delay,
                    connectionLifetime.Token);
                Task completed =
                    await Task.WhenAny(receiveTask, delayTask);
                if (completed == receiveTask)
                {
                    await receiveTask;
                    break;
                }

                now = DateTimeOffset.UtcNow;
                if (now >= nextInventory)
                {
                    await SendInventoryAsync(
                        socket,
                        sendGate,
                        ++inventorySequence,
                        connectionLifetime.Token);
                    nextInventory = now.AddSeconds(
                        m_settings.InventorySeconds);
                }
                if (now >= nextHeartbeat)
                {
                    await SendJsonAsync(
                        socket,
                        sendGate,
                        new StationHeartbeatMessage(
                            StationMessageTypes.Heartbeat,
                            ++heartbeatSequence),
                        connectionLifetime.Token);
                    nextHeartbeat = now.AddSeconds(
                        welcome.HeartbeatSeconds);
                }
            }
        }
        finally
        {
            connectionLifetime.Cancel();
            await receiveSessions.CloseAllAsync();
            await CloseIfOpenAsync(socket);
        }
    }

    private async Task<StationLinkTokenResponse> RequestLinkTokenAsync(
        string credential,
        CancellationToken cancellationToken)
    {
        using HttpClient http = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            BuildTokenUri(m_settings.BrokerUrl));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Add("X-Aether-Station-Id", m_settings.StationId);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(
            new StationLinkTokenRequest(
                m_settings.StationId,
                m_settings.Capabilities),
            options: StationProtocol.JsonOptions);

        using HttpResponseMessage response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogWarning(
                "The broker does not support short-lived station-link tokens; using legacy authentication for this upgrade connection");
            return new StationLinkTokenResponse(
                credential,
                DateTimeOffset.UnixEpoch,
                m_settings.Capabilities!);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The broker rejected the station-link token request " +
                $"with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        byte[] payload = await ReadBoundedResponseAsync(
            response.Content,
            cancellationToken);
        StationLinkTokenResponse? token;
        try
        {
            token = JsonSerializer.Deserialize<StationLinkTokenResponse>(
                payload,
                StationProtocol.JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The broker returned a malformed station-link token.",
                exception);
        }
        if (token is null ||
            string.IsNullOrWhiteSpace(token.AccessToken) ||
            token.AccessToken.Length is < 32 or > 512 ||
            token.AccessToken.Any(char.IsControl) ||
            token.ExpiresAt < DateTimeOffset.UnixEpoch ||
            !StationProtocolValidator.CapabilitiesMatch(
                token.Capabilities,
                m_settings.Capabilities!))
        {
            throw new InvalidDataException(
                "The broker returned an invalid station-link token.");
        }
        logger.LogInformation(
            "Obtained a short-lived station-link token expiring at {ExpiresAt}",
            token.ExpiresAt);
        return token;
    }

    public static Uri BuildTokenUri(string brokerUrl)
    {
        Uri brokerUri = new(brokerUrl, UriKind.Absolute);
        UriBuilder builder = new(brokerUri)
        {
            Scheme = brokerUri.Scheme == "wss"
                ? Uri.UriSchemeHttps
                : Uri.UriSchemeHttp,
            Port = brokerUri.IsDefaultPort ? -1 : brokerUri.Port,
            Path = brokerUri.AbsolutePath.TrimEnd('/') + "/token",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 8 * 1024;
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException(
                "The broker station-link token response is too large.");
        }
        await using Stream stream = await content.ReadAsStreamAsync(
            cancellationToken);
        using MemoryStream payload = new();
        byte[] buffer = new byte[1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (payload.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    "The broker station-link token response is too large.");
            }
            await payload.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
        return payload.ToArray();
    }

    private async Task SendInventoryAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendGate,
        long sequence,
        CancellationToken cancellationToken)
    {
        StationInventoryMessage message = new(
            StationMessageTypes.Inventory,
            sequence,
            inventory.GetSnapshot());
        string? error =
            StationProtocolValidator.ValidateInventory(message);
        if (error is not null)
        {
            throw new InvalidDataException(error);
        }
        await SendJsonAsync(
            socket,
            sendGate,
            message,
            cancellationToken);
    }

    private static async Task<BrokerWelcomeMessage> ReceiveWelcomeAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        string? json = await ReceiveTextAsync(
            socket,
            cancellationToken);
        if (json is null)
        {
            throw new InvalidDataException(
                "The broker closed before sending a welcome.");
        }
        BrokerWelcomeMessage? welcome;
        try
        {
            welcome = JsonSerializer.Deserialize<BrokerWelcomeMessage>(
                json,
                StationProtocol.JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The broker welcome message is malformed.",
                exception);
        }
        if (welcome is null ||
            welcome.Type != StationMessageTypes.Welcome ||
            welcome.ProtocolVersion != StationProtocol.Version ||
            !StationProtocolValidator.IsIdentifier(
                welcome.ConnectionId,
                64) ||
            welcome.HeartbeatSeconds is < 5 or > 60 ||
            welcome.MaximumMessageBytes !=
            StationProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "The broker welcome message is not supported.");
        }
        return welcome;
    }

    private async Task ReceiveBrokerMessagesAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               socket.State == WebSocketState.Open)
        {
            string? json = await ReceiveTextAsync(
                socket,
                cancellationToken);
            if (json is null)
            {
                return;
            }
            using JsonDocument document = JsonDocument.Parse(json);
            string? type =
                StationProtocolValidator.ReadMessageType(
                    document.RootElement);
            if (type == StationMessageTypes.Error)
            {
                BrokerErrorMessage? error =
                    document.RootElement.Deserialize<BrokerErrorMessage>(
                        StationProtocol.JsonOptions);
                throw new InvalidDataException(
                    error?.Message ?? "The broker rejected the station link.");
            }
            if (type == StationMessageTypes.OpenReceiveSession)
            {
                BrokerOpenReceiveSessionMessage? request =
                    document.RootElement
                        .Deserialize<BrokerOpenReceiveSessionMessage>(
                            StationProtocol.JsonOptions);
                string? validation =
                    StationProtocolValidator.ValidateOpenReceiveSession(
                        request);
                if (validation is not null || request is null)
                {
                    throw new InvalidDataException(validation);
                }
                try
                {
                    StationReceiveSessionOpenedMessage opened =
                        await receiveSessions.OpenAsync(
                            request,
                            m_settings.StationId,
                            cancellationToken);
                    await SendJsonAsync(
                        socket,
                        sendGate,
                        opened,
                        cancellationToken);
                }
                catch (StationReceiveSessionException exception)
                {
                    await SendJsonAsync(
                        socket,
                        sendGate,
                        new StationReceiveSessionErrorMessage(
                            StationMessageTypes.ReceiveSessionError,
                            request.SessionId,
                            exception.Code,
                            exception.Message),
                        cancellationToken);
                }
                continue;
            }
            if (type == StationMessageTypes.ReleaseServiceControl)
            {
                BrokerReleaseServiceControlMessage? request =
                    document.RootElement
                        .Deserialize<BrokerReleaseServiceControlMessage>(
                            StationProtocol.JsonOptions);
                string? validation =
                    StationProtocolValidator.ValidateReleaseServiceControl(
                        request);
                if (validation is not null || request is null)
                {
                    throw new InvalidDataException(validation);
                }
                StationReleaseServiceControlResultMessage result =
                    await releaseServiceControl.ExecuteAsync(
                        request,
                        cancellationToken);
                await SendJsonAsync(
                    socket,
                    sendGate,
                    result,
                    cancellationToken);
                continue;
            }
            if (type == StationMessageTypes.CloseReceiveSession)
            {
                BrokerCloseReceiveSessionMessage? request =
                    document.RootElement
                        .Deserialize<BrokerCloseReceiveSessionMessage>(
                            StationProtocol.JsonOptions);
                string? validation =
                    StationProtocolValidator.ValidateCloseReceiveSession(
                        request);
                if (validation is not null || request is null)
                {
                    throw new InvalidDataException(validation);
                }
                await receiveSessions.CloseAsync(
                    request.SessionId,
                    cancellationToken);
                await SendJsonAsync(
                    socket,
                    sendGate,
                    new StationReceiveSessionClosedMessage(
                        StationMessageTypes.ReceiveSessionClosed,
                        request.SessionId,
                        "Closed by the broker."),
                    cancellationToken);
                continue;
            }
            if (type == StationMessageTypes.SendReceiveText)
            {
                BrokerReceiveTextMessage? request =
                    document.RootElement
                        .Deserialize<BrokerReceiveTextMessage>(
                            StationProtocol.JsonOptions);
                string? validation =
                    StationProtocolValidator.ValidateBrokerReceiveText(
                        request);
                if (validation is not null || request is null)
                {
                    throw new InvalidDataException(validation);
                }
                try
                {
                    await receiveSessions.ForwardTextAsync(
                        request,
                        cancellationToken);
                }
                catch (StationReceiveSessionException exception)
                {
                    await SendJsonAsync(
                        socket,
                        sendGate,
                        new StationReceiveSessionErrorMessage(
                            StationMessageTypes.ReceiveSessionError,
                            request.SessionId,
                            exception.Code,
                            exception.Message),
                        cancellationToken);
                }
                continue;
            }
            throw new InvalidDataException(
                "The broker sent an unsupported message.");
        }
    }

    public static async Task SendJsonAsync<T>(
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
            WebSocketState state;
            try
            {
                state = socket.State;
            }
            catch (ObjectDisposedException exception)
            {
                throw new IOException(
                    "The station link closed before the message could be sent.",
                    exception);
            }
            if (state != WebSocketState.Open)
            {
                throw new IOException(
                    $"The station link cannot send while the socket is {state}.");
            }

            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
            catch (Exception exception)
                when (exception is WebSocketException or
                      InvalidOperationException or
                      ObjectDisposedException)
            {
                throw new IOException(
                    "The station link closed while the message was being sent.",
                    exception);
            }
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(
        ClientWebSocket socket,
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
                throw new InvalidDataException(
                    "The broker sent a binary phase-1 message.");
            }
            if (message.Length + result.Count >
                StationProtocol.MaximumMessageBytes)
            {
                throw new InvalidDataException(
                    "The broker message exceeds the protocol boundary.");
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
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException(
                        "The broker message is not valid UTF-8.",
                        exception);
                }
            }
        }
    }

    private static string ReadCredentialFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The station credential file does not exist.",
                fullPath);
        }
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(fullPath);
            UnixFileMode forbidden =
                UnixFileMode.GroupRead |
                UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute;
            if ((mode & forbidden) != 0)
            {
                throw new InvalidDataException(
                    "The station credential file must be owner-readable only.");
            }
        }

        string credential = File.ReadAllText(fullPath).Trim();
        if (credential.Length is < 32 or > 512 ||
            credential.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "The station credential is outside the supported boundary.");
        }
        return credential;
    }

    private static TimeSpan ReconnectDelay(int consecutiveFailures)
    {
        int exponent = Math.Min(Math.Max(consecutiveFailures - 1, 0), 5);
        double seconds = Math.Min(Math.Pow(2, exponent), 30);
        double jitter = Random.Shared.NextDouble();
        return TimeSpan.FromSeconds(seconds + jitter);
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
                "Station agent stopping.",
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
