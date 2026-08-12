using System.Globalization;
using System.Text;

namespace AetherSDR.Web.Setup;

public enum InstallationInstallerAuthenticationMode
{
    None = 1,
    Local = 2,
    MicrosoftEntraId = 3,
    OpenIdConnect = 4,
    CombinedMicrosoftEntraId = 5,
    CombinedOpenIdConnect = 6
}

public sealed record InstallationInstallerAuthenticationSelection(
    InstallationInstallerAuthenticationMode Mode,
    string ProviderId = "",
    string Authority = "",
    string ClientId = "")
{
    public static InstallationInstallerAuthenticationSelection Local { get; } =
        new(InstallationInstallerAuthenticationMode.Local);

    public bool UsesExternalProvider =>
        Mode is not
            (InstallationInstallerAuthenticationMode.None or
             InstallationInstallerAuthenticationMode.Local);
}

internal sealed record InstallationInstallerGatewayConfiguration(
    long SetupRevision,
    InstallationTopologyKind Topology,
    string CanonicalPublicUrl,
    bool InstallTransmitSupport,
    InstallationReverseProxyMode ReverseProxyMode,
    InstallationInstallerAuthenticationSelection Authentication);

internal sealed record InstallationInstallerGatewayConfigurationPlan(
    string EnvironmentTargetPath,
    string EnvironmentMarkerPath,
    string RenderedEnvironment,
    bool RequiresClientSecret,
    string ClientSecretTargetPath);

internal static class InstallationInstallerGatewayConfigurationPlanComposer
{
    internal const string EnvironmentTargetPath = "/etc/aethersdr/environment";
    internal const string EnvironmentMarkerPath =
        "/var/lib/aethersdr-installer/gateway-environment.sha256";
    internal const string ClientSecretTargetPath =
        "/var/lib/aethersdr/secrets/auth-client-secret";

    internal static InstallationInstallerGatewayConfigurationPlan Compose(
        InstallationInstallerUbuntuMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InstallationInstallerGatewayConfiguration configuration =
            request.GatewayConfiguration ??
            throw new InvalidOperationException(
                "The gateway configuration binding is unavailable.");
        InstallationInstallerAuthenticationSelection authentication =
            NormalizeAndValidate(configuration.Authentication);

        CanonicalPublicUrl canonical =
            CanonicalPublicUrl.Parse(configuration.CanonicalPublicUrl);
        if (configuration.SetupRevision < 0 ||
            !Enum.IsDefined(configuration.Topology) ||
            !InstallationTopologyProfile.For(configuration.Topology)
                .GatewayRunsHere ||
            configuration.ReverseProxyMode == InstallationReverseProxyMode.None)
        {
            throw new InvalidOperationException(
                "The gateway runtime configuration binding is invalid.");
        }

        List<KeyValuePair<string, string>> values =
        [
            new("AllowedHosts",
                $"{canonical.Uri.IdnHost};localhost;127.0.0.1"),
            new("AllowedOrigins__0", canonical.Value),
            new("ReverseProxy__Enabled", "true"),
            new("ReverseProxy__KnownProxies__0", "127.0.0.1"),
            new("InstallationSetupOnly__Enabled", "false"),
            new("InstallationSetupOnly__CanonicalAccessUrl", ""),
            new("InstallationRuntime__Enabled", "true"),
            new("InstallationRuntime__SetupRevision",
                configuration.SetupRevision.ToString(
                    CultureInfo.InvariantCulture)),
            new("InstallationRuntime__RuntimeRole", "Gateway"),
            new("InstallationRuntime__Topology",
                configuration.Topology.ToString()),
            new("InstallationRuntime__CanonicalPublicUrl", canonical.Value),
            new("InstallationRuntime__InstallTransmitSupport",
                configuration.InstallTransmitSupport ? "true" : "false"),
            new("Radio__Mode", "Simulation"),
            new("Radio__AllowTransmit", "false"),
            new("Radio__BrowserTxLeaseEnabled", "false"),
            new("StationTxProductionActivation__Enabled", "false"),
            new("Auth__Mode", RuntimeMode(authentication.Mode))
        ];

        if (authentication.UsesExternalProvider)
        {
            values.Add(new("Auth__ProviderId", authentication.ProviderId));
            if (authentication.Mode is
                InstallationInstallerAuthenticationMode
                    .CombinedMicrosoftEntraId or
                InstallationInstallerAuthenticationMode
                    .CombinedOpenIdConnect)
            {
                values.Add(new(
                    "Auth__ProviderType",
                    authentication.Mode ==
                        InstallationInstallerAuthenticationMode
                            .CombinedMicrosoftEntraId
                        ? "EntraId"
                        : "OpenIdConnect"));
            }
            values.Add(new("Auth__Authority", authentication.Authority));
            values.Add(new("Auth__ClientId", authentication.ClientId));
            values.Add(new(
                "Auth__ClientSecretFile",
                ClientSecretTargetPath));
            values.Add(new("Auth__CallbackPath", "/signin-oidc"));
            values.Add(new(
                "Auth__SignedOutCallbackPath",
                "/signout-callback-oidc"));
        }

        StringBuilder rendered = new();
        foreach ((string key, string value) in values)
        {
            rendered.Append(key);
            rendered.Append('=');
            rendered.Append(QuoteEnvironmentValue(value));
            rendered.Append('\n');
        }

        return new(
            EnvironmentTargetPath,
            EnvironmentMarkerPath,
            rendered.ToString(),
            authentication.UsesExternalProvider,
            ClientSecretTargetPath);
    }

    internal static InstallationInstallerAuthenticationSelection
        NormalizeAndValidate(
            InstallationInstallerAuthenticationSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(selection.Mode))
        {
            throw new InvalidOperationException(
                "The installer authentication mode is unsupported.");
        }

        bool external = selection.UsesExternalProvider;
        if (!external)
        {
            if (!string.IsNullOrEmpty(selection.ProviderId) ||
                !string.IsNullOrEmpty(selection.Authority) ||
                !string.IsNullOrEmpty(selection.ClientId))
            {
                throw new InvalidOperationException(
                    "Provider settings must be absent when external authentication is disabled.");
            }
            return selection;
        }

        string providerId = Exact(
            selection.ProviderId,
            "authentication provider ID",
            100);
        if (!IsProviderId(providerId) ||
            providerId is "local" or "development")
        {
            throw new InvalidOperationException(
                "The authentication provider ID is not canonical.");
        }

        string authorityText = Exact(
            selection.Authority,
            "authentication authority",
            500);
        if (!Uri.TryCreate(
                authorityText,
                UriKind.Absolute,
                out Uri? authority) ||
            authority.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(authority.Host) ||
            !string.IsNullOrEmpty(authority.UserInfo) ||
            !string.IsNullOrEmpty(authority.Query) ||
            !string.IsNullOrEmpty(authority.Fragment))
        {
            throw new InvalidOperationException(
                "The authentication authority must be an absolute HTTPS issuer without credentials, query, or fragment.");
        }

        string clientId = Exact(
            selection.ClientId,
            "authentication client ID",
            200);
        return selection with
        {
            ProviderId = providerId,
            Authority = authority.AbsoluteUri,
            ClientId = clientId
        };
    }

    private static string RuntimeMode(
        InstallationInstallerAuthenticationMode mode) =>
        mode switch
        {
            InstallationInstallerAuthenticationMode.Local => "Local",
            InstallationInstallerAuthenticationMode.MicrosoftEntraId =>
                "EntraId",
            InstallationInstallerAuthenticationMode.OpenIdConnect =>
                "OpenIdConnect",
            InstallationInstallerAuthenticationMode.CombinedMicrosoftEntraId or
            InstallationInstallerAuthenticationMode.CombinedOpenIdConnect =>
                "Combined",
            _ => throw new InvalidOperationException(
                "A gateway requires a production authentication mode.")
        };

    private static string QuoteEnvironmentValue(string value)
    {
        if (value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "Installer environment values cannot contain control characters.");
        }
        return "\"" +
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) +
            "\"";
    }

    private static string Exact(
        string value,
        string name,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"The {name} must be one exact bounded value.");
        }
        return value;
    }

    private static bool IsProviderId(string value) =>
        value.Length > 0 &&
        LowerLetterOrDigit(value[0]) &&
        value.All(character =>
            LowerLetterOrDigit(character) ||
            character is '.' or '_' or '-');

    private static bool LowerLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
