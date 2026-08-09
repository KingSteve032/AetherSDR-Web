using System.Globalization;
using System.Security.Claims;

namespace AetherSDR.Web.Auth.Identity;

internal static class AetherIdentityClaimTypes
{
    internal const string SessionId = "aether:session_id";
    internal const string AuthorityVersion = "aether:authority_version";
    internal const string AuthenticationMethod = "aether:authentication_method";
    internal const string ProviderId = "aether:provider_id";
}

internal sealed record AetherExternalIdentityKey(
    string ProviderId,
    string Issuer,
    string Subject);

internal sealed record AetherExternalIdentityEvidence(
    AetherExternalIdentityKey Key,
    string? DisplayName,
    string? Email,
    DateTimeOffset? AuthenticatedAtUtc);

internal static class AetherExternalIdentityEvidenceReader
{
    internal static AetherExternalIdentityEvidence Read(
        AetherExternalProviderDescriptor provider,
        ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException(
                "External identity evidence must be authenticated.");
        }

        string issuer = RequiredSingleClaim(principal, "iss", 500);
        ValidateIssuer(issuer);
        string subject = RequiredSingleClaim(principal, "sub", 500);
        string? displayName = OptionalSingleClaim(principal, "name", 200);
        string? email = OptionalSingleClaim(
            principal,
            "preferred_username",
            320) ?? OptionalSingleClaim(principal, ClaimTypes.Email, 320);
        DateTimeOffset? authenticatedAt = ReadAuthenticationTime(principal);

        return new(
            new(provider.ProviderId, issuer, subject),
            displayName,
            email,
            authenticatedAt);
    }

    private static DateTimeOffset? ReadAuthenticationTime(
        ClaimsPrincipal principal)
    {
        string? value = OptionalSingleClaim(principal, "auth_time", 20);
        if (value is null)
        {
            return null;
        }
        if (!long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long seconds) ||
            seconds < 0)
        {
            throw new InvalidOperationException(
                "External auth_time must be a non-negative Unix timestamp.");
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException(
                "External auth_time is outside the supported range.",
                exception);
        }
    }

    private static string RequiredSingleClaim(
        ClaimsPrincipal principal,
        string claimType,
        int maximumLength) =>
        OptionalSingleClaim(principal, claimType, maximumLength) ??
        throw new InvalidOperationException(
            $"External identity evidence requires exactly one '{claimType}' claim.");

    private static string? OptionalSingleClaim(
        ClaimsPrincipal principal,
        string claimType,
        int maximumLength)
    {
        Claim[] claims = principal.FindAll(claimType).ToArray();
        if (claims.Length == 0)
        {
            return null;
        }
        if (claims.Length != 1)
        {
            throw new InvalidOperationException(
                $"External identity evidence permits at most one '{claimType}' claim.");
        }

        string value = claims[0].Value;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"External claim '{claimType}' is not a bounded exact value.");
        }
        return value;
    }

    private static void ValidateIssuer(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? issuer) ||
            !string.Equals(
                issuer.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(issuer.Host) ||
            !string.IsNullOrEmpty(issuer.UserInfo) ||
            !string.IsNullOrEmpty(issuer.Query) ||
            !string.IsNullOrEmpty(issuer.Fragment))
        {
            throw new InvalidOperationException(
                "External issuer evidence must be an absolute HTTPS URI " +
                "without credentials, query, or fragment.");
        }
    }
}

internal static class AetherCanonicalPrincipalFactory
{
    internal const string AuthenticationType = "AetherIdentity";

    internal static ClaimsPrincipal Create(
        AetherIdentityUser user,
        AetherAuthenticationSession session,
        IEnumerable<string> roles,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(roles);

        string[] canonicalRoles = roles.ToArray();
        Validate(user, session, canonicalRoles, now);

        List<Claim> claims =
        [
            new(
                ClaimTypes.NameIdentifier,
                user.Id.ToString("D", CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.DisplayName),
            new(
                AetherIdentityClaimTypes.SessionId,
                session.Id.ToString("D", CultureInfo.InvariantCulture)),
            new(
                AetherIdentityClaimTypes.AuthorityVersion,
                user.AuthorityVersion.ToString(CultureInfo.InvariantCulture)),
            new(
                AetherIdentityClaimTypes.AuthenticationMethod,
                session.AuthenticationMethod.ToString()),
            new(
                "auth_time",
                (session.ReauthenticatedAtUtc ?? session.CreatedAtUtc)
                    .ToUnixTimeSeconds()
                    .ToString(CultureInfo.InvariantCulture))
        ];
        if (user.Email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }
        if (!string.IsNullOrWhiteSpace(session.ProviderId))
        {
            claims.Add(
                new Claim(
                    AetherIdentityClaimTypes.ProviderId,
                    session.ProviderId));
        }
        claims.AddRange(canonicalRoles.Select(
            role => new Claim(ClaimTypes.Role, role)));

        ClaimsIdentity identity = new(
            claims,
            AuthenticationType,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new(identity);
    }

    private static void Validate(
        AetherIdentityUser user,
        AetherAuthenticationSession session,
        string[] roles,
        DateTimeOffset now)
    {
        if (user.Id == Guid.Empty ||
            !user.Enabled ||
            user.AuthorityVersion <= 0 ||
            string.IsNullOrWhiteSpace(user.DisplayName) ||
            user.DisplayName.Length > 200 ||
            !string.Equals(
                user.DisplayName,
                user.DisplayName.Trim(),
                StringComparison.Ordinal) ||
            user.DisplayName.Any(char.IsControl) ||
            user.Email is not null &&
            (string.IsNullOrWhiteSpace(user.Email) ||
             user.Email.Length > 320 ||
             !string.Equals(
                 user.Email,
                 user.Email.Trim(),
                 StringComparison.Ordinal) ||
             user.Email.Any(char.IsControl)))
        {
            throw new InvalidOperationException(
                "Only an enabled bounded canonical user can become a principal.");
        }
        if (session.Id == Guid.Empty ||
            session.UserId != user.Id ||
            session.AuthorityVersion != user.AuthorityVersion ||
            session.RevokedAtUtc is not null ||
            session.CreatedAtUtc > now ||
            session.LastSeenAtUtc < session.CreatedAtUtc ||
            session.LastSeenAtUtc > now ||
            session.AbsoluteExpiresAtUtc <= now ||
            session.AbsoluteExpiresAtUtc <= session.CreatedAtUtc ||
            session.ReauthenticatedAtUtc is DateTimeOffset reauthenticatedAt &&
            (reauthenticatedAt < session.CreatedAtUtc ||
             reauthenticatedAt > now))
        {
            throw new InvalidOperationException(
                "The authentication session is not current canonical authority.");
        }

        if (!Enum.IsDefined(session.AuthenticationMethod))
        {
            throw new InvalidOperationException(
                "The authentication session method is unsupported.");
        }
        bool external =
            session.AuthenticationMethod ==
                AetherAuthenticationMethod.ExternalOpenIdConnect;
        if (external != !string.IsNullOrWhiteSpace(session.ProviderId) ||
            session.ProviderId is not null &&
            !IsCanonicalProviderId(session.ProviderId))
        {
            throw new InvalidOperationException(
                "The authentication session provider evidence is inconsistent.");
        }
        if (roles.Distinct(StringComparer.Ordinal).Count() != roles.Length ||
            roles.Any(role => !AetherRoles.All.Contains(role)))
        {
            throw new InvalidOperationException(
                "Canonical principals require distinct persisted Aether roles.");
        }
    }

    private static bool IsCanonicalProviderId(string value) =>
        value.Length is > 0 and <= 100 &&
        value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or
                '.' or '_' or '-');
}
