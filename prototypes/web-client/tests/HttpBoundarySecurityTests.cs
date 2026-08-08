using System.Net;
using System.Security.Claims;
using AetherSDR.Web.Auth;
using Microsoft.AspNetCore.Http;

namespace AetherSDR.Web.Tests;

public sealed class HttpBoundarySecurityTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("relative", "/")]
    [InlineData("https://attacker.example", "/")]
    [InlineData("//attacker.example", "/")]
    [InlineData("/\\attacker.example", "/")]
    [InlineData("/%5cattacker.example", "/")]
    [InlineData("/%2fattacker.example", "/")]
    [InlineData("/%250d%250aLocation:%20https://attacker.example", "/")]
    [InlineData("/%25252fattacker.example", "/")]
    [InlineData("/radio?session=one", "/radio?session=one")]
    public void LoginReturnUrlFailsClosedToOneLocalPath(
        string? candidate,
        string expected)
    {
        Assert.Equal(expected, LocalReturnUrl.Normalize(candidate));
    }

    [Fact]
    public void WebSocketPartitionsAuthenticatedUsersIndependently()
    {
        DefaultHttpContext first = CreateContext(
            IPAddress.Parse("192.0.2.10"),
            "operator-one");
        DefaultHttpContext second = CreateContext(
            IPAddress.Parse("192.0.2.10"),
            "operator-two");

        Assert.Equal(
            "user:operator-one",
            RequestRateLimitPartitionKey
                .ForAuthenticatedUserOrAddress(first));
        Assert.Equal(
            "user:operator-two",
            RequestRateLimitPartitionKey
                .ForAuthenticatedUserOrAddress(second));
    }

    [Fact]
    public void AnonymousPartitionsUseNormalizedClientAddresses()
    {
        DefaultHttpContext ipv4 = CreateContext(
            IPAddress.Parse("192.0.2.25"));
        DefaultHttpContext mapped = CreateContext(
            IPAddress.Parse("::ffff:192.0.2.25"));
        DefaultHttpContext other = CreateContext(
            IPAddress.Parse("192.0.2.26"));

        Assert.Equal(
            RequestRateLimitPartitionKey.ForAddress(ipv4),
            RequestRateLimitPartitionKey.ForAddress(mapped));
        Assert.NotEqual(
            RequestRateLimitPartitionKey.ForAddress(ipv4),
            RequestRateLimitPartitionKey.ForAddress(other));
    }

    private static DefaultHttpContext CreateContext(
        IPAddress address,
        string? subject = null)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = address;
        if (subject is not null)
        {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim("oid", subject)],
                    authenticationType: "test"));
        }
        return context;
    }
}
