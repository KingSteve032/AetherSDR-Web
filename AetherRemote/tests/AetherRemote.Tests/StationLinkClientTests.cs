using System.Net.WebSockets;
using AetherRemote.Agent;
using AetherRemote.Protocol;

namespace AetherRemote.Tests;

public sealed class StationLinkClientTests
{
    [Theory]
    [InlineData(
        "wss://flexweb.w4car.org/station/v1",
        "https://flexweb.w4car.org/station/v1/token")]
    [InlineData(
        "ws://127.0.0.1:5090/station/v1",
        "http://127.0.0.1:5090/station/v1/token")]
    public void LinkTokenUriUsesTheMatchingHttpsEndpoint(
        string brokerUrl,
        string expected)
    {
        Assert.Equal(
            new Uri(expected),
            StationLinkClient.BuildTokenUri(brokerUrl));
    }

    [Fact]
    public async Task AbortedSocketWaitingForSendGateIsRejectedBeforeSend()
    {
        using RecordingWebSocket socket = new(WebSocketState.Open);
        using SemaphoreSlim sendGate = new(0, 1);

        Task send = StationLinkClient.SendJsonAsync(
            socket,
            sendGate,
            new StationHeartbeatMessage(
                StationMessageTypes.Heartbeat,
                1),
            CancellationToken.None);

        socket.SetState(WebSocketState.Aborted);
        sendGate.Release();

        IOException exception = await Assert.ThrowsAsync<IOException>(
            async () => await send);

        Assert.Contains("Aborted", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, socket.SendCalls);
        Assert.True(sendGate.Wait(0));
    }

    [Fact]
    public async Task SocketAbortDuringSendIsNormalizedToIoFailure()
    {
        using RecordingWebSocket socket = new(
            WebSocketState.Open,
            abortDuringSend: true);
        using SemaphoreSlim sendGate = new(1, 1);

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => StationLinkClient.SendJsonAsync(
                socket,
                sendGate,
                new StationHeartbeatMessage(
                    StationMessageTypes.Heartbeat,
                    2),
                CancellationToken.None));

        Assert.Equal(
            "The station link closed while the message was being sent.",
            exception.Message);
        Assert.IsType<WebSocketException>(exception.InnerException);
        Assert.Equal(1, socket.SendCalls);
        Assert.Equal(WebSocketState.Aborted, socket.State);
        Assert.True(sendGate.Wait(0));
    }

    private sealed class RecordingWebSocket(
        WebSocketState initialState,
        bool abortDuringSend = false)
        : WebSocket
    {
        private WebSocketState m_state = initialState;

        public int SendCalls { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => m_state;

        public override string? SubProtocol => null;

        public void SetState(WebSocketState state)
        {
            m_state = state;
        }

        public override void Abort()
        {
            m_state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            m_state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            m_state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            m_state = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            if (abortDuringSend)
            {
                m_state = WebSocketState.Aborted;
                throw new WebSocketException(
                    WebSocketError.ConnectionClosedPrematurely);
            }
            return Task.CompletedTask;
        }
    }
}
