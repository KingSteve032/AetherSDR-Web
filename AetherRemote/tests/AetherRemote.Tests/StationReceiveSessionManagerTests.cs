using System.Net;
using AetherRemote.Agent;
using AetherRemote.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherRemote.Tests;

public sealed class StationReceiveSessionManagerTests
{
    [Fact]
    public async Task ProjectionCannotOpenWithoutAuthenticatedBrokerLink()
    {
        StationReceiveSessionManager manager = CreateManager();

        StationReceiveSessionException exception =
            await Assert.ThrowsAsync<StationReceiveSessionException>(
                () => manager.OpenAsync(
                    ValidOpen(),
                    "test-station",
                    CancellationToken.None));

        Assert.Equal("station_offline", exception.Code);
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public void OnlyOneBrokerProjectionSenderCanBeAttached()
    {
        StationReceiveSessionManager manager = CreateManager();
        using IDisposable first = manager.AttachBrokerSender(
            (_, _) => Task.CompletedTask);

        Assert.Throws<InvalidOperationException>(
            () => manager.AttachBrokerSender(
                (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task UnknownProjectedSessionIsRejected()
    {
        StationReceiveSessionManager manager = CreateManager();

        StationReceiveSessionException exception =
            await Assert.ThrowsAsync<StationReceiveSessionException>(
                () => manager.ForwardTextAsync(
                    new BrokerReceiveTextMessage(
                        StationMessageTypes.SendReceiveText,
                        ValidOpen().SessionId,
                        """
                        {"id":3,"cmd":"intent","action":"slice.set","payload":{"sliceId":0,"frequencyHz":14074000}}
                        """),
                    CancellationToken.None));

        Assert.Equal("unknown_session", exception.Code);
    }

    private static StationReceiveSessionManager CreateManager() =>
        new(
            new FakeInventory(),
            Options.Create(
                new AgentSettings
                {
                    LocalEngineUrl =
                        "http://127.0.0.1:5081",
                    LocalEngineOrigin =
                        "http://127.0.0.1:5081"
                }),
            NullLogger<StationReceiveSessionManager>.Instance);

    private static BrokerOpenReceiveSessionMessage ValidOpen() =>
        new(
            StationMessageTypes.OpenReceiveSession,
            "0123456789abcdef0123456789abcdef",
            "flex:test",
            Guid.NewGuid().ToString(),
            false);

    private sealed class FakeInventory : IStationRadioInventoryProvider
    {
        private readonly StationRadioAdvertisement m_radio = new(
            "flex:test",
            "flex",
            "FLEX-TEST",
            "TEST-SERIAL",
            "Test",
            "available",
            2,
            2,
            string.Empty);

        public IReadOnlyList<StationRadioAdvertisement> GetSnapshot() =>
            [m_radio];

        public bool TryResolve(
            string radioId,
            out LocalRadioEndpoint? endpoint)
        {
            endpoint = string.Equals(
                radioId,
                m_radio.RadioId,
                StringComparison.Ordinal)
                ? new LocalRadioEndpoint(
                    m_radio,
                    IPAddress.Loopback.ToString(),
                    4992)
                : null;
            return endpoint is not null;
        }
    }
}
