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

internal sealed record AetherLocalAuthenticationPolicy(
    int PasswordHashIterationCount,
    int MinimumPasswordLength,
    int MaximumPasswordLength,
    int MaximumFailedAttempts,
    TimeSpan LockoutDuration,
    int RateLimitPermitCount,
    TimeSpan RateLimitWindow,
    TimeSpan MfaChallengeLifetime,
    int MaximumOutstandingMfaChallenges);

internal sealed record AetherAuthenticationTopology(
    AetherAuthenticationMode Mode,
    bool LocalAccountsEnabled,
    AetherExternalProviderDescriptor? ExternalProvider,
    TimeSpan SessionAbsoluteLifetime,
    AetherLocalAuthenticationPolicy LocalPolicy);

internal static class AetherAuthenticationConfiguration
{
    internal static AetherAuthenticationTopology Validate(
        AuthSettings settings,
        bool isDevelopmentEnvironment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AetherAuthenticationMode mode = ParseMode(settings.Mode);
        TimeSpan sessionLifetime = ValidateSessionLifetime(settings.Session);
        AetherLocalAuthenticationPolicy localPolicy =
            ValidateLocalPolicy(settings.Local);

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
                sessionLifetime,
                localPolicy);
        }

        if (mode == AetherAuthenticationMode.Local)
        {
            RejectDisabledExternalConfiguration(settings);
            return new(
                mode,
                LocalAccountsEnabled: true,
                ExternalProvider: null,
                sessionLifetime,
                localPolicy);
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
            sessionLifetime,
            localPolicy);
    }

    internal static AetherLocalAuthenticationPolicy
        CreateSetupLocalPolicy() =>
        ValidateLocalPolicy(new LocalAuthenticationSettings());

    private static AetherLocalAuthenticationPolicy ValidateLocalPolicy(
        LocalAuthenticationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.PasswordHashIterationCount is < 100_000 or > 1_000_000)
        {
            throw new InvalidOperationException(
                "Auth:Local:PasswordHashIterationCount must be between " +
                "100000 and 1000000.");
        }
        if (settings.MinimumPasswordLength is < 12 or > 128 ||
            settings.MaximumPasswordLength <
                settings.MinimumPasswordLength ||
            settings.MaximumPasswordLength > 1024)
        {
            throw new InvalidOperationException(
                "Auth:Local password lengths must require at least 12 " +
                "characters and have a bounded maximum no greater than 1024.");
        }
        if (settings.MaximumFailedAttempts is < 3 or > 10)
        {
            throw new InvalidOperationException(
                "Auth:Local:MaximumFailedAttempts must be between 3 and 10.");
        }
        if (settings.LockoutMinutes is < 1 or > 1440)
        {
            throw new InvalidOperationException(
                "Auth:Local:LockoutMinutes must be between 1 and 1440.");
        }
        if (settings.RateLimitPermitCount is < 1 or > 100 ||
            settings.RateLimitWindowSeconds is < 1 or > 3600)
        {
            throw new InvalidOperationException(
                "Auth:Local rate limiting must allow between 1 and 100 " +
                "attempts in a window between 1 and 3600 seconds.");
        }
        if (settings.MfaChallengeLifetimeMinutes is < 1 or > 10 ||
            settings.MaximumOutstandingMfaChallenges is < 128 or > 65_536)
        {
            throw new InvalidOperationException(
                "Auth:Local MFA challenges must expire between 1 and 10 " +
                "minutes and have a bounded capacity between 128 and 65536.");
        }

        return new(
            settings.PasswordHashIterationCount,
            settings.MinimumPasswordLength,
            settings.MaximumPasswordLength,
            settings.MaximumFailedAttempts,
            TimeSpan.FromMinutes(settings.LockoutMinutes),
            settings.RateLimitPermitCount,
            TimeSpan.FromSeconds(settings.RateLimitWindowSeconds),
            TimeSpan.FromMinutes(settings.MfaChallengeLifetimeMinutes),
            settings.MaximumOutstandingMfaChallenges);
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
