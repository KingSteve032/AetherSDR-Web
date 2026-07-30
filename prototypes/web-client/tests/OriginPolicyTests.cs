using AetherSDR.Web.Radio;
using Microsoft.AspNetCore.Http;

namespace AetherSDR.Web.Tests;

public sealed class OriginPolicyTests
{
    [Fact]
    public void SameOriginWebSocketIsAllowed()
    {
        DefaultHttpContext context = new();
        context.Request.Host = new HostString("radio.example.test");
        context.Request.Headers.Origin = "https://radio.example.test";

        Assert.True(
            OriginPolicy.IsAllowed(context, new OriginSettings()));
    }

    [Fact]
    public void MissingOrForeignOriginIsRejected()
    {
        DefaultHttpContext missing = new();
        missing.Request.Host = new HostString("radio.example.test");
        Assert.False(
            OriginPolicy.IsAllowed(missing, new OriginSettings()));

        DefaultHttpContext foreign = new();
        foreign.Request.Host = new HostString("radio.example.test");
        foreign.Request.Headers.Origin = "https://attacker.example";
        Assert.False(
            OriginPolicy.IsAllowed(foreign, new OriginSettings()));
    }

    [Fact]
    public void ExplicitReverseProxyOriginIsAllowed()
    {
        DefaultHttpContext context = new();
        context.Request.Host = new HostString("127.0.0.1:5080");
        context.Request.Headers.Origin = "https://radio.example.test";

        Assert.True(
            OriginPolicy.IsAllowed(
                context,
                new OriginSettings
                {
                    Values = ["https://radio.example.test"]
                }));
    }
}
