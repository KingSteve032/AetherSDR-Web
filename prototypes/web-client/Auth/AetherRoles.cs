namespace AetherSDR.Web.Auth;

public static class AetherRoles
{
    public const string Observe = "Aether.Observe";
    public const string Control = "Aether.Control";
    public const string Transmit = "Aether.Transmit";
    public const string Admin = "Aether.Admin";

    public static readonly string[] All = [Observe, Control, Transmit, Admin];
}

public static class AetherPolicies
{
    public const string Observe = "aether.observe";
    public const string Control = "aether.control";
    public const string Transmit = "aether.transmit";
    public const string Admin = "aether.admin";
}
