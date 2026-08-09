namespace AetherSDR.Web.Auth;

internal enum AetherAuthenticationMode
{
    Development = 1,
    Local = 2,
    MicrosoftEntraId = 3,
    OpenIdConnect = 4,
    Combined = 5
}

internal enum AetherExternalProviderKind
{
    MicrosoftEntraId = 1,
    OpenIdConnect = 2
}

internal sealed record AetherExternalProviderDescriptor(
    string ProviderId,
    AetherExternalProviderKind Kind,
    Uri Authority,
    string ClientId,
    string CallbackPath,
    string SignedOutCallbackPath);

internal sealed record AetherAuthenticationTopology(
    AetherAuthenticationMode Mode,
    bool LocalAccountsEnabled,
    AetherExternalProviderDescriptor? ExternalProvider,
    TimeSpan SessionAbsoluteLifetime);

internal static class AetherAuthenticationConfiguration
{
    internal static AetherAuthenticationTopology Validate(
        AuthSettings settings,
        bool isDevelopmentEnvironment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AetherAuthenticationMode mode = ParseMode(settings.Mode);
        TimeSpan sessionLifetime = ValidateSessionLifetime(settings.Session);

        if (mode == AetherAuthenticationMode.Development)
        {
            if (!isDevelopmentEnvironment)
            {
                throw new InvalidOperationException(
                    "Development authentication is forbidden outside the " +
                    "Development environment.");
            }
            ValidateDevelopmentUser(settings.DevelopmentUser);
            RejectDisabledExternalConfiguration(settings);
            return new(
                mode,
                LocalAccountsEnabled: false,
                ExternalProvider: null,
                sessionLifetime);
        }

        if (mode == AetherAuthenticationMode.Local)
        {
            RejectDisabledExternalConfiguration(settings);
            return new(
                mode,
                LocalAccountsEnabled: true,
                ExternalProvider: null,
                sessionLifetime);
        }

        if (mode != AetherAuthenticationMode.Combined &&
            !string.IsNullOrWhiteSpace(settings.ProviderType))
        {
            throw new InvalidOperationException(
                "Auth:ProviderType is valid only with Combined authentication.");
        }

        AetherExternalProviderKind providerKind = mode switch
        {
            AetherAuthenticationMode.MicrosoftEntraId =>
                AetherExternalProviderKind.MicrosoftEntraId,
            AetherAuthenticationMode.OpenIdConnect =>
                AetherExternalProviderKind.OpenIdConnect,
            AetherAuthenticationMode.Combined =>
                ParseProviderKind(settings.ProviderType),
            _ => throw new InvalidOperationException(
                "The configured authentication mode is unsupported.")
        };
        AetherExternalProviderDescriptor provider =
            ValidateExternalProvider(settings, providerKind);
        return new(
            mode,
            LocalAccountsEnabled: mode == AetherAuthenticationMode.Combined,
            provider,
            sessionLifetime);
    }

    private static TimeSpan ValidateSessionLifetime(
        AuthenticationSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.AbsoluteLifetimeMinutes is < 5 or > 1440)
        {
            throw new InvalidOperationException(
                "Auth:Session:AbsoluteLifetimeMinutes must be between 5 and " +
                "1440 minutes.");
        }
        return TimeSpan.FromMinutes(settings.AbsoluteLifetimeMinutes);
    }

    private static AetherAuthenticationMode ParseMode(string value)
    {
        string normalized = RequireExactValue(value, "Auth:Mode", 50);
        if (normalized.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            return AetherAuthenticationMode.Development;
        }
        if (normalized.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            return AetherAuthenticationMode.Local;
        }
        if (normalized.Equals("EntraId", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(
                "MicrosoftEntraId",
                StringComparison.OrdinalIgnoreCase))
        {
            return AetherAuthenticationMode.MicrosoftEntraId;
        }
        if (normalized.Equals("Oidc", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(
                "OpenIdConnect",
                StringComparison.OrdinalIgnoreCase))
        {
            return AetherAuthenticationMode.OpenIdConnect;
        }
        if (normalized.Equals("Combined", StringComparison.OrdinalIgnoreCase))
        {
            return AetherAuthenticationMode.Combined;
        }

        throw new InvalidOperationException(
            "Auth:Mode must be Development, Local, EntraId, " +
            "OpenIdConnect, or Combined.");
    }

    private static AetherExternalProviderKind ParseProviderKind(string value)
    {
        string normalized = RequireExactValue(
            value,
            "Auth:ProviderType",
            50);
        if (normalized.Equals("EntraId", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(
                "MicrosoftEntraId",
                StringComparison.OrdinalIgnoreCase))
        {
            return AetherExternalProviderKind.MicrosoftEntraId;
        }
        if (normalized.Equals("Oidc", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(
                "OpenIdConnect",
                StringComparison.OrdinalIgnoreCase))
        {
            return AetherExternalProviderKind.OpenIdConnect;
        }

        throw new InvalidOperationException(
            "Combined authentication requires Auth:ProviderType set to " +
            "EntraId or OpenIdConnect.");
    }

    private static AetherExternalProviderDescriptor ValidateExternalProvider(
        AuthSettings settings,
        AetherExternalProviderKind kind)
    {
        string providerId = RequireExactValue(
            settings.ProviderId,
            "Auth:ProviderId",
            100);
        if (!IsProviderId(providerId) ||
            providerId.Equals("local", StringComparison.Ordinal) ||
            providerId.Equals("development", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Auth:ProviderId must be a lowercase stable identifier using " +
                "only letters, digits, '.', '_', or '-'; local and " +
                "development are reserved.");
        }

        Uri authority = ParseAuthority(settings.Authority);
        string clientId = RequireExactValue(
            settings.ClientId,
            "Auth:ClientId",
            200);
        string callbackPath = ValidateCallbackPath(
            settings.CallbackPath,
            "Auth:CallbackPath");
        string signedOutCallbackPath = ValidateCallbackPath(
            settings.SignedOutCallbackPath,
            "Auth:SignedOutCallbackPath");
        if (string.Equals(
                callbackPath,
                signedOutCallbackPath,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "OIDC callback and signed-out callback paths must differ.");
        }

        string clientSecret = OidcClientSecretResolver.Resolve(settings);
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "External authentication requires either Auth:ClientSecret " +
                "or Auth:ClientSecretFile.");
        }

        return new(
            providerId,
            kind,
            authority,
            clientId,
            callbackPath,
            signedOutCallbackPath);
    }

    private static Uri ParseAuthority(string value)
    {
        string exact = RequireExactValue(value, "Auth:Authority", 500);
        if (!Uri.TryCreate(exact, UriKind.Absolute, out Uri? authority) ||
            !string.Equals(
                authority.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(authority.Host) ||
            !string.IsNullOrEmpty(authority.UserInfo) ||
            !string.IsNullOrEmpty(authority.Query) ||
            !string.IsNullOrEmpty(authority.Fragment))
        {
            throw new InvalidOperationException(
                "Auth:Authority must be an absolute HTTPS issuer authority " +
                "without credentials, query, or fragment.");
        }
        return authority;
    }

    private static string ValidateCallbackPath(string value, string name)
    {
        string path = RequireExactValue(value, name, 200);
        if (!path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('\\') ||
            path.Contains('?') ||
            path.Contains('#'))
        {
            throw new InvalidOperationException(
                $"{name} must be one local absolute-path reference.");
        }
        return path;
    }

    private static void RejectDisabledExternalConfiguration(
        AuthSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Authority) ||
            !string.IsNullOrWhiteSpace(settings.ClientId) ||
            !string.IsNullOrWhiteSpace(settings.ClientSecret) ||
            !string.IsNullOrWhiteSpace(settings.ClientSecretFile) ||
            !string.IsNullOrWhiteSpace(settings.ProviderType))
        {
            throw new InvalidOperationException(
                "External-provider configuration must be absent when the " +
                "selected authentication mode does not use it.");
        }
    }

    private static void ValidateDevelopmentUser(
        DevelopmentUserSettings user)
    {
        ArgumentNullException.ThrowIfNull(user);
        _ = RequireExactValue(
            user.ObjectId,
            "Auth:DevelopmentUser:ObjectId",
            200);
        _ = RequireExactValue(
            user.Name,
            "Auth:DevelopmentUser:Name",
            200);
        if (user.Roles.Length == 0 ||
            user.Roles.Distinct(StringComparer.Ordinal).Count() !=
                user.Roles.Length ||
            user.Roles.Any(role => !AetherRoles.All.Contains(role)))
        {
            throw new InvalidOperationException(
                "The development user must have distinct canonical Aether roles.");
        }
    }

    private static string RequireExactValue(
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
                $"{name} must be a non-empty exact value of at most " +
                $"{maximumLength} characters.");
        }
        return value;
    }

    private static bool IsProviderId(string value) =>
        value.Length > 0 &&
        IsLowerLetterOrDigit(value[0]) &&
        value.All(character =>
            IsLowerLetterOrDigit(character) ||
            character is '.' or '_' or '-');

    private static bool IsLowerLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
