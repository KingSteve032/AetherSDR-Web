using System.Net;
using System.Text;
using AetherRemote.Agent;

namespace AetherRemote.Tests;

public sealed class FlexDiscoveryParserTests
{
    [Fact]
    public void ParsesSanitizedFlexAdvertisement()
    {
        byte[] packet = Encoding.UTF8.GetBytes(
            "model=FLEX-6700 serial=1234-5678 " +
            "nickname=Remote\u007fRadio status=Available " +
            "ip=192.168.50.20 port=4992 " +
            "available_clients=1 licensed_clients=2");

        LocalRadioAdvertisement? result =
            FlexDiscoveryParser.TryParse(
                packet,
                IPAddress.Parse("192.168.50.99"),
                DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("flex:1234-5678", result.Advertisement.RadioId);
        Assert.Equal("Remote Radio", result.Advertisement.Nickname);
        Assert.Equal("available", result.Advertisement.Status);
        Assert.Equal("192.168.50.20", result.Host);
        Assert.Equal(1, result.Advertisement.AvailableClients);
        Assert.Equal(2, result.Advertisement.LicensedClients);
    }

    [Fact]
    public void RejectsAdvertisementWithoutStableIdentity()
    {
        byte[] packet = Encoding.UTF8.GetBytes(
            "model=FLEX-6700 nickname=Missing\u007fSerial");

        LocalRadioAdvertisement? result =
            FlexDiscoveryParser.TryParse(
                packet,
                IPAddress.Loopback,
                DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void InUseFlagWinsOverAdvertisedAvailability()
    {
        byte[] packet = Encoding.UTF8.GetBytes(
            "model=FLEX-6700 serial=1234 " +
            "status=Available inuse=1");

        LocalRadioAdvertisement? result =
            FlexDiscoveryParser.TryParse(
                packet,
                IPAddress.Parse("10.0.0.10"),
                DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("in-use", result.Advertisement.Status);
    }
}
