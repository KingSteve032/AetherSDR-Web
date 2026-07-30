using AetherRemote.Broker;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Tests;

public sealed class StationLinkTokenServiceTests
{
    [Fact]
    public void TokenIsBoundToStationCapabilitiesAndSingleUse()
    {
        StationLinkTokenService service = CreateService();
        StationLinkTokenResponse issued = service.Issue(
            "station-one",
            [StationCapabilities.ReceiveProjectionV1]);

        Assert.Equal(1, service.OutstandingCount);
        Assert.False(service.TryConsume(
            "station-two",
            issued.AccessToken,
            out _));
        Assert.True(service.TryConsume(
            "station-one",
            issued.AccessToken,
            out StationLinkTokenGrant? grant));
        Assert.NotNull(grant);
        Assert.Equal("station-one", grant.StationId);
        Assert.Equal(
            [StationCapabilities.ReceiveProjectionV1],
            grant.Capabilities);
        Assert.Equal(0, service.OutstandingCount);
        Assert.False(service.TryConsume(
            "station-one",
            issued.AccessToken,
            out _));
    }

    [Fact]
    public void TokenExpiresAndCannotBeConsumed()
    {
        ManualTimeProvider time = new();
        StationLinkTokenService service = CreateService(time);
        StationLinkTokenResponse issued = service.Issue(
            "station-one",
            []);
        time.Advance(TimeSpan.FromSeconds(61));

        Assert.False(service.TryConsume(
            "station-one",
            issued.AccessToken,
            out _));
        Assert.Equal(0, service.OutstandingCount);
    }

    [Fact]
    public void NewTokenAndRevocationInvalidateOutstandingTokens()
    {
        StationLinkTokenService service = CreateService();
        StationLinkTokenResponse first = service.Issue(
            "station-one",
            []);
        StationLinkTokenResponse second = service.Issue(
            "station-one",
            [StationCapabilities.ReceiveProjectionV1]);

        Assert.False(service.TryConsume(
            "station-one",
            first.AccessToken,
            out _));
        service.RevokeStation("station-one");
        Assert.False(service.TryConsume(
            "station-one",
            second.AccessToken,
            out _));
        Assert.Equal(0, service.OutstandingCount);
    }

    [Fact]
    public void UnsupportedCapabilityCannotBeIssued()
    {
        StationLinkTokenService service = CreateService();

        StationLinkTokenException exception = Assert.Throws<
            StationLinkTokenException>(
                () => service.Issue(
                    "station-one",
                    ["future-transmit-capability"]));

        Assert.Equal("invalid_token_request", exception.Code);
        Assert.Equal(0, service.OutstandingCount);
    }

    private static StationLinkTokenService CreateService(
        TimeProvider? timeProvider = null) =>
        new(
            Options.Create(
                new StationLinkSettings
                {
                    LinkTokenSeconds = 60
                }),
            timeProvider ?? TimeProvider.System);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset m_now =
            new(2026, 7, 29, 22, 30, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan interval)
        {
            m_now += interval;
        }
    }
}
