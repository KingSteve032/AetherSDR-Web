using System.Net;
using AetherRemote.Broker;
using Microsoft.AspNetCore.Http;

namespace AetherRemote.Tests;

public sealed class BrokerNetworkBoundaryTests
{
    [Theory]
    [InlineData("/api/stations")]
    [InlineData("/api/receive-sessions")]
    [InlineData("/api/enrollments/redeem")]
    [InlineData("/receive/v1")]
    [InlineData("/receive/v1/extra")]
    public void PrivilegedBrokerPathsRequireLoopback(string path)
    {
        Assert.True(BrokerNetworkBoundary.RequiresLoopback(new PathString(path)));
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/station/v1")]
    [InlineData("/station/v1/token")]
    [InlineData("/station/v1/extra")]
    [InlineData("/apiary")]
    [InlineData("/receive")]
    public void StationAndHealthPathsRemainAvailable(string path)
    {
        Assert.False(BrokerNetworkBoundary.RequiresLoopback(new PathString(path)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.42.7.9")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void LoopbackAddressesAreAccepted(string value)
    {
        Assert.True(BrokerNetworkBoundary.IsLoopback(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("10.2.0.254")]
    [InlineData("10.2.0.162")]
    [InlineData("192.168.1.10")]
    [InlineData("::ffff:10.2.0.254")]
    [InlineData("2001:db8::1")]
    public void NonLoopbackAddressesAreRejected(string value)
    {
        Assert.False(BrokerNetworkBoundary.IsLoopback(IPAddress.Parse(value)));
    }

    [Fact]
    public void MissingRemoteAddressFailsClosed()
    {
        Assert.False(BrokerNetworkBoundary.IsLoopback(null));
    }
}
