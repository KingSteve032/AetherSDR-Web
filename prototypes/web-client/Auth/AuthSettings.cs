namespace AetherSDR.Web.Auth;

public sealed class AuthSettings
{
    public const string SectionName = "Auth";

    public string Mode { get; init; } = "OpenIdConnect";
    public string ProviderId { get; init; } = "primary";
    public string ProviderType { get; init; } = string.Empty;
    public string Authority { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string ClientSecretFile { get; init; } = string.Empty;
    public string CallbackPath { get; init; } = "/signin-oidc";
    public string SignedOutCallbackPath { get; init; } = "/signout-callback-oidc";
    public AuthenticationSessionSettings Session { get; init; } = new();
    public DevelopmentUserSettings DevelopmentUser { get; init; } = new();
}

public sealed class AuthenticationSessionSettings
{
    public int AbsoluteLifetimeMinutes { get; init; } = 480;
}

public sealed class DevelopmentUserSettings
{
    public string ObjectId { get; init; } = "local-operator";
    public string Name { get; init; } = "Local Operator";
    public string Email { get; init; } = "operator@localhost";
    public string[] Roles { get; init; } =
        [AetherRoles.Observe, AetherRoles.Control];
}
