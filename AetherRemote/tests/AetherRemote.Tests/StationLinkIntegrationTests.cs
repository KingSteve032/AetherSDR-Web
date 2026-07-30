using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using AetherRemote.Broker;
using AetherRemote.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace AetherRemote.Tests;

public sealed class StationLinkIntegrationTests
{
    private const string StationCredential =
        "integration-station-credential-at-least-thirty-two-characters";
    private const string RuntimeCredential =
        "integration-runtime-credential-at-least-thirty-two-characters";
    private const string AdministrationCredential =
        "integration-administration-credential-at-least-thirty-two-characters";

    [Fact]
    public async Task AuthenticatedStationAppearsInManagementInventory()
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        await using WebApplicationFactory<Program> factory =
            CreateFactory();
        WebSocketClient webSocketClient =
            await CreateStationWebSocketClientAsync(
                factory,
                [],
                timeout.Token);

        using WebSocket socket = await webSocketClient.ConnectAsync(
            new Uri("ws://localhost/station/v1"),
            timeout.Token);
        await SendAsync(
            socket,
            new StationHelloMessage(
                StationMessageTypes.Hello,
                StationProtocol.Version,
                "station-integration",
                "instance-integration",
                "0.1.0",
                []),
            timeout.Token);
        BrokerWelcomeMessage? welcome =
            JsonSerializer.Deserialize<BrokerWelcomeMessage>(
                await ReceiveAsync(
                    socket,
                    timeout.Token),
                StationProtocol.JsonOptions);
        Assert.NotNull(welcome);
        Assert.Equal(StationMessageTypes.Welcome, welcome.Type);

        await SendAsync(
            socket,
            new StationInventoryMessage(
                StationMessageTypes.Inventory,
                1,
                [
                    new StationRadioAdvertisement(
                        "flex:integration",
                        "flex",
                        "FLEX-6700",
                        "integration",
                        "Integration Radio",
                        "available",
                        1,
                        2,
                        string.Empty)
                ]),
            timeout.Token);

        using HttpClient management = factory.CreateClient();
        management.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                RuntimeCredential);
        JsonDocument inventory = await WaitForInventoryAsync(
            management,
            timeout.Token);

        using (inventory)
        {
            JsonElement station = inventory.RootElement
                .GetProperty("stations")[0];
            Assert.Equal(
                "station-integration",
                station.GetProperty("stationId").GetString());
            Assert.Equal(
                "flex:integration",
                station.GetProperty("radios")[0]
                    .GetProperty("radioId")
                    .GetString());
        }

        await socket.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure,
            "Test complete.",
            timeout.Token);
    }

    [Fact]
    public async Task WrongDeviceCredentialCannotObtainLinkToken()
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        await using WebApplicationFactory<Program> factory =
            CreateFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                "wrong-credential-with-at-least-thirty-two-characters");
        client.DefaultRequestHeaders.Add(
            "X-Aether-Station-Id",
            "station-integration");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/station/v1/token",
            new StationLinkTokenRequest(
                "station-integration",
                [StationCapabilities.ReceiveProjectionV1]),
            timeout.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReceiveProjectionRequiresExplicitStationCapability()
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        await using WebApplicationFactory<Program> factory =
            CreateFactory();
        WebSocketClient webSocketClient =
            await CreateStationWebSocketClientAsync(
                factory,
                [],
                timeout.Token);

        using WebSocket socket = await webSocketClient.ConnectAsync(
            new Uri("ws://localhost/station/v1"),
            timeout.Token);
        await SendAsync(
            socket,
            new StationHelloMessage(
                StationMessageTypes.Hello,
                StationProtocol.Version,
                "station-integration",
                "instance-no-projection",
                "0.2.0",
                []),
            timeout.Token);
        _ = await ReceiveAsync(socket, timeout.Token);

        using HttpClient runtime = factory.CreateClient();
        runtime.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                RuntimeCredential);
        using HttpResponseMessage response =
            await runtime.PostAsJsonAsync(
                "/api/receive-sessions",
                new OpenRemoteReceiveSessionRequest(
                    "station-integration",
                    "flex:integration",
                    Guid.NewGuid().ToString(),
                    false),
                timeout.Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument failure = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(
            "station_capability",
            failure.RootElement.GetProperty("code").GetString());

        using HttpResponseMessage sessionsResponse =
            await runtime.GetAsync(
                "/api/receive-sessions",
                timeout.Token);
        sessionsResponse.EnsureSuccessStatusCode();
        using JsonDocument sessions = JsonDocument.Parse(
            await sessionsResponse.Content.ReadAsStringAsync(timeout.Token));
        Assert.Empty(
            sessions.RootElement.GetProperty("sessions").EnumerateArray());

        await SendAsync(
            socket,
            new StationHeartbeatMessage(
                StationMessageTypes.Heartbeat,
                1),
            timeout.Token);
        using JsonDocument stationState =
            await WaitForHeartbeatAsync(runtime, 1, timeout.Token);
        Assert.Equal(
            "online",
            stationState.RootElement.GetProperty("stations")[0]
                .GetProperty("state")
                .GetString());
    }

    [Fact]
    public async Task ManagementReceiveSessionIsRoutedOnlyToItsStation()
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        await using WebApplicationFactory<Program> factory =
            CreateFactory();
        WebSocketClient webSocketClient =
            await CreateStationWebSocketClientAsync(
                factory,
                [StationCapabilities.ReceiveProjectionV1],
                timeout.Token);

        using WebSocket socket = await webSocketClient.ConnectAsync(
            new Uri("ws://localhost/station/v1"),
            timeout.Token);
        await SendAsync(
            socket,
            new StationHelloMessage(
                StationMessageTypes.Hello,
                StationProtocol.Version,
                "station-integration",
                "instance-projection",
                "0.2.0",
                [StationCapabilities.ReceiveProjectionV1]),
            timeout.Token);
        _ = await ReceiveAsync(socket, timeout.Token);

        using HttpClient management = factory.CreateClient();
        management.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                RuntimeCredential);
        string guiClientId = Guid.NewGuid().ToString();
        Task<HttpResponseMessage> openTask =
            management.PostAsJsonAsync(
                "/api/receive-sessions",
                new OpenRemoteReceiveSessionRequest(
                    "station-integration",
                    "flex:integration",
                    guiClientId,
                    true),
                timeout.Token);

        BrokerOpenReceiveSessionMessage? open =
            JsonSerializer.Deserialize<BrokerOpenReceiveSessionMessage>(
                await ReceiveAsync(socket, timeout.Token),
                StationProtocol.JsonOptions);
        Assert.NotNull(open);
        Assert.Equal(
            StationMessageTypes.OpenReceiveSession,
            open.Type);
        Assert.Equal("flex:integration", open.RadioId);
        Assert.Equal(guiClientId, open.GuiClientId);
        Assert.True(open.LowBandwidth);

        await SendAsync(
            socket,
            new StationReceiveSessionOpenedMessage(
                StationMessageTypes.ReceiveSessionOpened,
                open.SessionId,
                open.RadioId,
                "FLEX-6700",
                "integration",
                "1234abcd"),
            timeout.Token);
        using HttpResponseMessage openedResponse = await openTask;
        openedResponse.EnsureSuccessStatusCode();
        RemoteReceiveSessionSnapshot? opened =
            await openedResponse.Content
                .ReadFromJsonAsync<RemoteReceiveSessionSnapshot>(
                    cancellationToken: timeout.Token);
        Assert.NotNull(opened);
        Assert.Equal("admitted", opened.State);
        Assert.Equal("1234abcd", opened.ClientHandle);

        WebSocketClient projectionClient =
            factory.Server.CreateWebSocketClient();
        projectionClient.SubProtocols.Add(
            ReceiveProjectionWebSocketEndpoint.Subprotocol);
        projectionClient.ConfigureRequest = request =>
        {
            request.Headers.Authorization =
                $"Bearer {RuntimeCredential}";
        };
        using WebSocket projection =
            await projectionClient.ConnectAsync(
                new Uri(
                    $"ws://localhost/receive/v1?sessionId={opened.SessionId}"),
                timeout.Token);
        const string snapshot =
            """
            {"event":"snapshot","snapshot":{"connected":true,"canTransmit":false}}
            """;
        await SendAsync(
            socket,
            new StationReceiveTextMessage(
                StationMessageTypes.ReceiveText,
                opened.SessionId,
                snapshot),
            timeout.Token);
        Assert.Equal(
            snapshot,
            await ReceiveAsync(projection, timeout.Token));

        const string projectedIntent =
            """
            {"id":3,"cmd":"intent","action":"slice.set","payload":{"sliceId":0,"frequencyHz":14074000}}
            """;
        await SendTextAsync(
            projection,
            projectedIntent,
            timeout.Token);
        BrokerReceiveTextMessage? forwarded =
            JsonSerializer.Deserialize<BrokerReceiveTextMessage>(
                await ReceiveAsync(socket, timeout.Token),
                StationProtocol.JsonOptions);
        Assert.NotNull(forwarded);
        Assert.Equal(
            StationMessageTypes.SendReceiveText,
            forwarded.Type);
        Assert.Equal(opened.SessionId, forwarded.SessionId);
        Assert.Equal(projectedIntent, forwarded.Payload);

        byte[] spectrum = new byte[16];
        "AETF"u8.CopyTo(spectrum);
        await SendAsync(
            socket,
            new StationReceiveBinaryMessage(
                StationMessageTypes.ReceiveBinary,
                opened.SessionId,
                Convert.ToBase64String(spectrum)),
            timeout.Token);
        byte[] receivedSpectrum =
            await ReceiveBinaryAsync(
                projection,
                timeout.Token);
        Assert.Equal(spectrum, receivedSpectrum);

        await projection.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure,
            "Projection verified.",
            timeout.Token);
        using HttpResponseMessage closeResponse =
            await management.DeleteAsync(
                $"/api/receive-sessions/{opened.SessionId}",
                timeout.Token);
        Assert.Equal(HttpStatusCode.NoContent, closeResponse.StatusCode);
        BrokerCloseReceiveSessionMessage? close =
            JsonSerializer.Deserialize<BrokerCloseReceiveSessionMessage>(
                await ReceiveAsync(socket, timeout.Token),
                StationProtocol.JsonOptions);
        Assert.NotNull(close);
        Assert.Equal(opened.SessionId, close.SessionId);
        await SendAsync(
            socket,
            new StationReceiveBinaryMessage(
                StationMessageTypes.ReceiveBinary,
                opened.SessionId,
                Convert.ToBase64String(spectrum)),
            timeout.Token);
        await SendAsync(
            socket,
            new StationReceiveSessionClosedMessage(
                StationMessageTypes.ReceiveSessionClosed,
                opened.SessionId,
                "Closed by test."),
            timeout.Token);
        await SendAsync(
            socket,
            new StationHeartbeatMessage(
                StationMessageTypes.Heartbeat,
                1),
            timeout.Token);
        JsonDocument afterClose = await WaitForHeartbeatAsync(
            management,
            1,
            timeout.Token);
        using (afterClose)
        {
            JsonElement station =
                afterClose.RootElement.GetProperty("stations")[0];
            Assert.Equal(
                1,
                station.GetProperty("heartbeatSequence").GetInt64());
            Assert.Equal(
                "online",
                station.GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task CancelledAdmissionDoesNotDropTheStationLink()
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        await using WebApplicationFactory<Program> factory =
            CreateFactory();
        WebSocketClient webSocketClient =
            await CreateStationWebSocketClientAsync(
                factory,
                [StationCapabilities.ReceiveProjectionV1],
                timeout.Token);

        using WebSocket socket = await webSocketClient.ConnectAsync(
            new Uri("ws://localhost/station/v1"),
            timeout.Token);
        await SendAsync(
            socket,
            new StationHelloMessage(
                StationMessageTypes.Hello,
                StationProtocol.Version,
                "station-integration",
                "instance-cancelled-open",
                "0.3.1",
                [StationCapabilities.ReceiveProjectionV1]),
            timeout.Token);
        _ = await ReceiveAsync(socket, timeout.Token);

        using HttpClient management = factory.CreateClient();
        management.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                RuntimeCredential);
        using CancellationTokenSource requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<HttpResponseMessage> openTask =
            management.PostAsJsonAsync(
                "/api/receive-sessions",
                new OpenRemoteReceiveSessionRequest(
                    "station-integration",
                    "flex:integration",
                    Guid.NewGuid().ToString(),
                    true),
                requestCancellation.Token);
        BrokerOpenReceiveSessionMessage? open =
            JsonSerializer.Deserialize<BrokerOpenReceiveSessionMessage>(
                await ReceiveAsync(socket, timeout.Token),
                StationProtocol.JsonOptions);
        Assert.NotNull(open);

        requestCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await openTask);
        BrokerCloseReceiveSessionMessage? close =
            JsonSerializer.Deserialize<BrokerCloseReceiveSessionMessage>(
                await ReceiveAsync(socket, timeout.Token),
                StationProtocol.JsonOptions);
        Assert.NotNull(close);
        Assert.Equal(open.SessionId, close.SessionId);

        await SendAsync(
            socket,
            new StationReceiveSessionOpenedMessage(
                StationMessageTypes.ReceiveSessionOpened,
                open.SessionId,
                open.RadioId,
                "FLEX-6700",
                "integration",
                "1234abcd"),
            timeout.Token);
        await SendAsync(
            socket,
            new StationReceiveSessionClosedMessage(
                StationMessageTypes.ReceiveSessionClosed,
                open.SessionId,
                "Cancelled admission cleaned up."),
            timeout.Token);
        await SendAsync(
            socket,
            new StationHeartbeatMessage(
                StationMessageTypes.Heartbeat,
                1),
            timeout.Token);

        using JsonDocument stationState =
            await WaitForHeartbeatAsync(
                management,
                1,
                timeout.Token);
        JsonElement station =
            stationState.RootElement.GetProperty("stations")[0];
        Assert.Equal("online", station.GetProperty("state").GetString());
    }

    [Fact]
    public async Task LongLivedDeviceCredentialCannotUpgradeStationWebSocket()
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        await using WebApplicationFactory<Program> factory =
            CreateFactory();
        WebSocketClient client = CreateStationWebSocketClient(
            factory,
            StationCredential);

        Exception exception =
            await Assert.ThrowsAnyAsync<Exception>(
                () => client.ConnectAsync(
                    new Uri("ws://localhost/station/v1"),
                    timeout.Token));

        Assert.Contains(
            ((int)HttpStatusCode.Unauthorized).ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkTokenIsSingleUse()
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        await using WebApplicationFactory<Program> factory =
            CreateFactory();
        StationLinkTokenResponse token = await IssueStationLinkTokenAsync(
            factory,
            [StationCapabilities.ReceiveProjectionV1],
            timeout.Token);
        WebSocketClient firstClient = CreateStationWebSocketClient(
            factory,
            token.AccessToken);
        using WebSocket first = await firstClient.ConnectAsync(
            new Uri("ws://localhost/station/v1"),
            timeout.Token);
        await SendAsync(
            first,
            new StationHelloMessage(
                StationMessageTypes.Hello,
                StationProtocol.Version,
                "station-integration",
                "instance-single-use",
                "0.3.6",
                token.Capabilities),
            timeout.Token);
        _ = await ReceiveAsync(first, timeout.Token);

        WebSocketClient replayClient = CreateStationWebSocketClient(
            factory,
            token.AccessToken);
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(
            () => replayClient.ConnectAsync(
                new Uri("ws://localhost/station/v1"),
                timeout.Token));

        Assert.Contains(
            ((int)HttpStatusCode.Unauthorized).ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingStationRevokesOutstandingLinkToken()
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        await using WebApplicationFactory<Program> factory =
            CreateFactory();
        StationLinkTokenResponse token = await IssueStationLinkTokenAsync(
            factory,
            [StationCapabilities.ReceiveProjectionV1],
            timeout.Token);
        using HttpClient administration = factory.CreateClient();
        administration.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdministrationCredential);
        using HttpResponseMessage disabled = await administration.PostAsync(
            "/api/station-credentials/station-integration/disable",
            content: null,
            timeout.Token);
        disabled.EnsureSuccessStatusCode();

        WebSocketClient client = CreateStationWebSocketClient(
            factory,
            token.AccessToken);
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(
            () => client.ConnectAsync(
                new Uri("ws://localhost/station/v1"),
                timeout.Token));

        Assert.Contains(
            ((int)HttpStatusCode.Unauthorized).ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelloCapabilitiesMustMatchLinkToken()
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        await using WebApplicationFactory<Program> factory =
            CreateFactory();
        WebSocketClient client = await CreateStationWebSocketClientAsync(
            factory,
            [],
            timeout.Token);
        using WebSocket socket = await client.ConnectAsync(
            new Uri("ws://localhost/station/v1"),
            timeout.Token);

        await SendAsync(
            socket,
            new StationHelloMessage(
                StationMessageTypes.Hello,
                StationProtocol.Version,
                "station-integration",
                "instance-capability-mismatch",
                "0.3.6",
                [StationCapabilities.ReceiveProjectionV1]),
            timeout.Token);
        BrokerErrorMessage? error =
            JsonSerializer.Deserialize<BrokerErrorMessage>(
                await ReceiveAsync(socket, timeout.Token),
                StationProtocol.JsonOptions);

        Assert.NotNull(error);
        Assert.Equal("capability_mismatch", error.Code);
    }

    [Fact]
    public async Task RuntimeAndAdministrationCredentialsCannotCrossBoundaries()
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        await using WebApplicationFactory<Program> factory =
            CreateFactory();

        using HttpClient runtime = factory.CreateClient();
        runtime.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", RuntimeCredential);
        using HttpClient administration = factory.CreateClient();
        administration.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                AdministrationCredential);

        using HttpResponseMessage runtimeInventory =
            await runtime.GetAsync("/api/stations", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, runtimeInventory.StatusCode);
        using HttpResponseMessage adminInventory =
            await administration.GetAsync("/api/stations", timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, adminInventory.StatusCode);

        using HttpResponseMessage administrationCredentials =
            await administration.GetAsync(
                "/api/station-credentials",
                timeout.Token);
        Assert.Equal(
            HttpStatusCode.OK,
            administrationCredentials.StatusCode);
        using HttpResponseMessage runtimeCredentials =
            await runtime.GetAsync(
                "/api/station-credentials",
                timeout.Token);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            runtimeCredentials.StatusCode);

        using HttpResponseMessage runtimeEnrollment =
            await runtime.PostAsJsonAsync(
                "/api/enrollment-codes",
                new CreateStationEnrollmentRequest("station-integration"),
                timeout.Token);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            runtimeEnrollment.StatusCode);
        using HttpResponseMessage administrationEnrollment =
            await administration.PostAsJsonAsync(
                "/api/enrollment-codes",
                new CreateStationEnrollmentRequest("station-integration"),
                timeout.Token);
        Assert.Equal(
            HttpStatusCode.OK,
            administrationEnrollment.StatusCode);
    }

    private static async Task<WebSocketClient>
        CreateStationWebSocketClientAsync(
            WebApplicationFactory<Program> factory,
            IReadOnlyList<string> capabilities,
            CancellationToken cancellationToken)
    {
        StationLinkTokenResponse token = await IssueStationLinkTokenAsync(
            factory,
            capabilities,
            cancellationToken);
        return CreateStationWebSocketClient(factory, token.AccessToken);
    }

    private static async Task<StationLinkTokenResponse>
        IssueStationLinkTokenAsync(
            WebApplicationFactory<Program> factory,
            IReadOnlyList<string> capabilities,
            CancellationToken cancellationToken)
    {
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                StationCredential);
        client.DefaultRequestHeaders.Add(
            "X-Aether-Station-Id",
            "station-integration");
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/station/v1/token",
            new StationLinkTokenRequest(
                "station-integration",
                capabilities),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        StationLinkTokenResponse? token =
            await response.Content.ReadFromJsonAsync<
                StationLinkTokenResponse>(
                    cancellationToken: cancellationToken);
        Assert.NotNull(token);
        Assert.True(StationProtocolValidator.CapabilitiesMatch(
            token.Capabilities,
            capabilities));
        return token;
    }

    private static WebSocketClient CreateStationWebSocketClient(
        WebApplicationFactory<Program> factory,
        string accessToken)
    {
        WebSocketClient client = factory.Server.CreateWebSocketClient();
        client.SubProtocols.Add(StationProtocol.WebSocketSubprotocol);
        client.ConfigureRequest = request =>
        {
            request.Headers["X-Aether-Station-Id"] =
                "station-integration";
            request.Headers.Authorization = $"Bearer {accessToken}";
        };
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        Dictionary<string, string?> configuration = new()
        {
            ["StationLink:Enabled"] = "true",
            ["StationLink:RequireForwardedHttps"] = "false",
            ["StationLink:HeartbeatSeconds"] = "10",
            ["StationLink:DegradedAfterSeconds"] = "25",
            ["StationLink:DisconnectAfterSeconds"] = "45",
            ["StationLink:LinkTokenSeconds"] = "60",
            ["StationLink:RuntimeCredentialSha256"] =
                StationCredentialVerifier.HashCredential(
                    RuntimeCredential),
            ["StationLink:AdministrationCredentialSha256"] =
                StationCredentialVerifier.HashCredential(
                    AdministrationCredential),
            ["StationLink:Stations:0:StationId"] =
                "station-integration",
            ["StationLink:Stations:0:CredentialSha256"] =
                StationCredentialVerifier.HashCredential(
                    StationCredential)
        };
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration(
                    (_, config) =>
                        config.AddInMemoryCollection(configuration));
            });
    }

    private static async Task<JsonDocument> WaitForInventoryAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using HttpResponseMessage response =
                await client.GetAsync(
                    "/api/stations",
                    cancellationToken);
            response.EnsureSuccessStatusCode();
            JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(
                    cancellationToken));
            JsonElement stations =
                document.RootElement.GetProperty("stations");
            if (stations.GetArrayLength() == 1 &&
                stations[0].GetProperty("radios").GetArrayLength() == 1)
            {
                return document;
            }
            document.Dispose();
            await Task.Delay(
                TimeSpan.FromMilliseconds(25),
                cancellationToken);
        }
        throw new TimeoutException(
            "The station inventory did not reach the broker.");
    }

    private static async Task<JsonDocument> WaitForHeartbeatAsync(
        HttpClient client,
        long sequence,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using HttpResponseMessage response =
                await client.GetAsync(
                    "/api/stations",
                    cancellationToken);
            response.EnsureSuccessStatusCode();
            JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(
                    cancellationToken));
            JsonElement stations =
                document.RootElement.GetProperty("stations");
            if (stations.GetArrayLength() == 1 &&
                stations[0].GetProperty("heartbeatSequence").GetInt64() ==
                    sequence)
            {
                return document;
            }
            document.Dispose();
            await Task.Delay(
                TimeSpan.FromMilliseconds(25),
                cancellationToken);
        }
        throw new TimeoutException(
            "The station heartbeat did not reach the broker.");
    }

    private static Task SendAsync<T>(
        WebSocket socket,
        T message,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            message,
            StationProtocol.JsonOptions);
        return socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private static Task SendTextAsync(
        WebSocket socket,
        string payload,
        CancellationToken cancellationToken) =>
        socket.SendAsync(
            System.Text.Encoding.UTF8.GetBytes(payload),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

    private static async Task<string> ReceiveAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[StationProtocol.MaximumMessageBytes];
        WebSocketReceiveResult result =
            await socket.ReceiveAsync(buffer, cancellationToken);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return System.Text.Encoding.UTF8.GetString(
            buffer,
            0,
            result.Count);
    }

    private static async Task<byte[]> ReceiveBinaryAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer =
            new byte[StationProtocol.MaximumProjectionBinaryBytes];
        WebSocketReceiveResult result =
            await socket.ReceiveAsync(buffer, cancellationToken);
        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.True(result.EndOfMessage);
        return buffer[..result.Count];
    }
}
