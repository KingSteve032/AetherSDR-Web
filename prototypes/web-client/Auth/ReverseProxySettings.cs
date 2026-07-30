namespace AetherSDR.Web.Auth;

public sealed class ReverseProxySettings
{
    public const string SectionName = "ReverseProxy";

    public bool Enabled { get; init; }
    public string[] KnownProxies { get; init; } = [];
}
