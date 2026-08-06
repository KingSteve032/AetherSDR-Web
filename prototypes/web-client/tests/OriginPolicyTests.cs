using AetherSDR.Web.Radio;
using Microsoft.AspNetCore.Http;

namespace AetherSDR.Web.Tests;

public sealed class OriginPolicyTests
{
    [Fact]
    public void ExactSameOriginWebSocketIsAllowed()
    {
        DefaultHttpContext context = CreateContext(
            "https",
            "radio.example.test",
            "https://radio.example.test");

        Assert.True(
            OriginPolicy.IsAllowed(context, new OriginSettings()));
    }

    [Theory]
    [InlineData("radio.example.test:443", "https://radio.example.test")]
    [InlineData("radio.example.test", "https://radio.example.test:443")]
    public void EquivalentDefaultPortsAreNormalized(
        string requestHost,
        string origin)
    {
        DefaultHttpContext context = CreateContext(
            "https",
            requestHost,
            origin);

        Assert.True(
            OriginPolicy.IsAllowed(context, new OriginSettings()));
    }

    [Fact]
    public void SameAuthorityWithDifferentSchemeIsRejected()
    {
        DefaultHttpContext context = CreateContext(
            "https",
            "radio.example.test",
            "http://radio.example.test");

        Assert.False(
            OriginPolicy.IsAllowed(context, new OriginSettings()));
    }

    [Fact]
    public void SameHostWithDifferentEffectivePortIsRejected()
    {
        DefaultHttpContext context = CreateContext(
            "https",
            "radio.example.test:444",
            "https://radio.example.test");

        Assert.False(
            OriginPolicy.IsAllowed(context, new OriginSettings()));
    }

    [Fact]
    public void MissingMalformedOrForeignOriginIsRejected()
    {
        DefaultHttpContext missing = CreateContext(
            "https",
            "radio.example.test",
            origin: null);
        Assert.False(
            OriginPolicy.IsAllowed(missing, new OriginSettings()));

        DefaultHttpContext malformed = CreateContext(
            "https",
            "radio.example.test",
            "https://radio.example.test/path");
        Assert.False(
            OriginPolicy.IsAllowed(malformed, new OriginSettings()));

        DefaultHttpContext foreign = CreateContext(
            "https",
            "radio.example.test",
            "https://attacker.example");
        Assert.False(
            OriginPolicy.IsAllowed(foreign, new OriginSettings()));
    }

    [Fact]
    public void ExactConfiguredReverseProxyOriginIsAllowed()
    {
        DefaultHttpContext context = CreateContext(
            "http",
            "127.0.0.1:5080",
            "https://radio.example.test");

        Assert.True(
            OriginPolicy.IsAllowed(
                context,
                new OriginSettings
                {
                    Values = ["https://radio.example.test"]
                }));
    }

    [Fact]
    public void ConfiguredOriginsRequireHttpOriginOnlyValues()
    {
        DefaultHttpContext context = CreateContext(
            "http",
            "127.0.0.1:5080",
            "https://radio.example.test");

        Assert.False(
            OriginPolicy.IsAllowed(
                context,
                new OriginSettings
                {
                    Values =
                    [
                        "wss://radio.example.test",
                        "https://radio.example.test/path",
                        "https://radio.example.test?query=1"
                    ]
                }));
    }

    private static DefaultHttpContext CreateContext(
        string scheme,
        string host,
        string? origin)
    {
        DefaultHttpContext context = new();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }
        return context;
    }
}
