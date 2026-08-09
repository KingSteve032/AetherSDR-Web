using System.Security.Claims;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;

namespace AetherSDR.Web.Tests;

public sealed class AuthenticationProviderConfigurationTests
{
    [Fact]
    public void SupportsBoundedProviderTopologies()
    {
        AetherAuthenticationTopology development =
            AetherAuthenticationConfiguration.Validate(
                new AuthSettings { Mode = "Development" },
                isDevelopmentEnvironment: true);
        AetherAuthenticationTopology local =
            AetherAuthenticationConfiguration.Validate(
                new AuthSettings { Mode = "Local" },
                isDevelopmentEnvironment: false);
        AetherAuthenticationTopology entra =
            AetherAuthenticationConfiguration.Validate(
                External("EntraId", "entra-primary"),
                isDevelopmentEnvironment: false);
        AetherAuthenticationTopology oidc =
            AetherAuthenticationConfiguration.Validate(
                External("Oidc", "club-oidc"),
                isDevelopmentEnvironment: false);
        AetherAuthenticationTopology combined =
            AetherAuthenticationConfiguration.Validate(
                External(
                    "Combined",
                    "combined-oidc",
                    providerType: "OpenIdConnect"),
                isDevelopmentEnvironment: false);

        Assert.Equal(
            AetherAuthenticationMode.Development,
            development.Mode);
        Assert.False(development.LocalAccountsEnabled);
        Assert.Null(development.ExternalProvider);

        Assert.Equal(AetherAuthenticationMode.Local, local.Mode);
        Assert.True(local.LocalAccountsEnabled);
        Assert.Null(local.ExternalProvider);

        Assert.Equal(
            AetherExternalProviderKind.MicrosoftEntraId,
            entra.ExternalProvider!.Kind);
        Assert.False(entra.LocalAccountsEnabled);
        Assert.Equal("entra-primary", entra.ExternalProvider.ProviderId);

        Assert.Equal(
            AetherExternalProviderKind.OpenIdConnect,
            oidc.ExternalProvider!.Kind);
        Assert.False(oidc.LocalAccountsEnabled);

        Assert.Equal(AetherAuthenticationMode.Combined, combined.Mode);
        Assert.True(combined.LocalAccountsEnabled);
        Assert.Equal(
            AetherExternalProviderKind.OpenIdConnect,
            combined.ExternalProvider!.Kind);
    }

    [Fact]
    public void RejectsAmbiguousOrUnsafeProviderConfiguration()
    {
        Assert.Throws<InvalidOperationException>(
            () => AetherAuthenticationConfiguration.Validate(
                new AuthSettings { Mode = "Development" },
                isDevelopmentEnvironment: false));
        Assert.Throws<InvalidOperationException>(
            () => AetherAuthenticationConfiguration.Validate(
                new AuthSettings
                {
                    Mode = "Local",
                    ClientSecret = "dormant-secret"
                },
                isDevelopmentEnvironment: false));
        Assert.Throws<InvalidOperationException>(
            () => AetherAuthenticationConfiguration.Validate(
                External("Combined", "combined"),
                isDevelopmentEnvironment: false));
        Assert.Throws<InvalidOperationException>(
            () => AetherAuthenticationConfiguration.Validate(
                External(
                    "EntraId",
                    "entra",
                    providerType: "OpenIdConnect"),
                isDevelopmentEnvironment: false));
        Assert.Throws<InvalidOperationException>(
            () => AetherAuthenticationConfiguration.Validate(
                External(
                    "OpenIdConnect",
                    "Uppercase",
                    authority: "https://identity.example"),
                isDevelopmentEnvironment: false));
        Assert.Throws<InvalidOperationException>(
            () => AetherAuthenticationConfiguration.Validate(
                External(
                    "OpenIdConnect",
                    "generic",
                    authority: "http://identity.example"),
                isDevelopmentEnvironment: false));
        Assert.Throws<InvalidOperationException>(
            () => AetherAuthenticationConfiguration.Validate(
                External(
                    "OpenIdConnect",
                    "generic",
                    callbackPath: "//identity.example/callback"),
                isDevelopmentEnvironment: false));
        Assert.Throws<InvalidOperationException>(
            () => AetherAuthenticationConfiguration.Validate(
                new AuthSettings
                {
                    Mode = "OpenIdConnect",
                    ProviderId = "generic",
                    Authority = "https://identity.example",
                    ClientId = "aethersdr"
                },
                isDevelopmentEnvironment: false));
    }

    private static AuthSettings External(
        string mode,
        string providerId,
        string providerType = "",
        string authority = "https://identity.example/tenant",
        string callbackPath = "/signin-oidc") =>
        new()
        {
            Mode = mode,
            ProviderId = providerId,
            ProviderType = providerType,
            Authority = authority,
            ClientId = "aethersdr-web",
            ClientSecret = "test-secret",
            CallbackPath = callbackPath,
            SignedOutCallbackPath = "/signout-callback-oidc"
        };
}

public sealed class AetherPrincipalTests
{
    [Fact]
    public void ExternalEvidenceUsesOnlyExactProviderIssuerAndSubject()
    {
        AetherExternalProviderDescriptor provider = Provider();
        ClaimsPrincipal first = ExternalPrincipal(
            subject: "subject-one",
            email: "shared@example.test");
        ClaimsPrincipal second = ExternalPrincipal(
            subject: "subject-two",
            email: "shared@example.test");

        AetherExternalIdentityEvidence firstEvidence =
            AetherExternalIdentityEvidenceReader.Read(provider, first);
        AetherExternalIdentityEvidence secondEvidence =
            AetherExternalIdentityEvidenceReader.Read(provider, second);

        Assert.Equal(
            new AetherExternalIdentityKey(
                "club-oidc",
                "https://identity.example/tenant",
                "subject-one"),
            firstEvidence.Key);
        Assert.Equal("Operator One", firstEvidence.DisplayName);
        Assert.Equal("shared@example.test", firstEvidence.Email);
        Assert.NotEqual(firstEvidence.Key, secondEvidence.Key);
        Assert.DoesNotContain(
            first.Claims,
            claim => claim.Type == ClaimTypes.NameIdentifier &&
                     claim.Value == firstEvidence.Key.Subject);
    }

    [Fact]
    public void ExternalEvidenceNeverFallsBackToEmailOrObjectId()
    {
        AetherExternalProviderDescriptor provider = Provider();
        ClaimsIdentity missingSubject = new(
            [
                new("iss", "https://identity.example/tenant"),
                new("oid", "entra-object-id"),
                new(ClaimTypes.NameIdentifier, "mapped-name-id"),
                new(ClaimTypes.Email, "operator@example.test"),
                new(ClaimTypes.Role, AetherRoles.Admin)
            ],
            authenticationType: "oidc");
        ClaimsIdentity duplicateSubject = new(
            [
                new("iss", "https://identity.example/tenant"),
                new("sub", "one"),
                new("sub", "two")
            ],
            authenticationType: "oidc");

        Assert.Throws<InvalidOperationException>(
            () => AetherExternalIdentityEvidenceReader.Read(
                provider,
                new ClaimsPrincipal(missingSubject)));
        Assert.Throws<InvalidOperationException>(
            () => AetherExternalIdentityEvidenceReader.Read(
                provider,
                new ClaimsPrincipal(duplicateSubject)));
    }

    [Fact]
    public void CanonicalPrincipalContainsOnlyPersistedAetherAuthority()
    {
        DateTimeOffset now = DateTimeOffset.Parse(
            "2026-08-09T13:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        AetherIdentityUser user = User();
        AetherAuthenticationSession session = Session(user, now);

        ClaimsPrincipal principal = AetherCanonicalPrincipalFactory.Create(
            user,
            session,
            [AetherRoles.Observe, AetherRoles.Control],
            now);

        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.Equal(
            user.Id.ToString("D"),
            principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(
            session.Id.ToString("D"),
            principal.FindFirstValue(AetherIdentityClaimTypes.SessionId));
        Assert.Equal(
            user.AuthorityVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            principal.FindFirstValue(
                AetherIdentityClaimTypes.AuthorityVersion));
        Assert.Equal(
            "club-oidc",
            principal.FindFirstValue(AetherIdentityClaimTypes.ProviderId));
        Assert.True(principal.IsInRole(AetherRoles.Observe));
        Assert.True(principal.IsInRole(AetherRoles.Control));
        Assert.False(principal.IsInRole(AetherRoles.Admin));
        Assert.DoesNotContain(principal.Claims, claim => claim.Type == "oid");
        Assert.DoesNotContain(principal.Claims, claim => claim.Type == "roles");
    }

    [Fact]
    public void CanonicalPrincipalRejectsStaleRevokedOrUnknownAuthority()
    {
        DateTimeOffset now = DateTimeOffset.Parse(
            "2026-08-09T13:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        AetherIdentityUser user = User();
        AetherAuthenticationSession session = Session(user, now);

        Assert.Throws<InvalidOperationException>(
            () => AetherCanonicalPrincipalFactory.Create(
                user,
                session,
                ["Aether.Unknown"],
                now));

        session.RevokedAtUtc = now.AddMinutes(-1);
        Assert.Throws<InvalidOperationException>(
            () => AetherCanonicalPrincipalFactory.Create(
                user,
                session,
                [AetherRoles.Observe],
                now));

        session.RevokedAtUtc = null;
        session.AuthorityVersion = user.AuthorityVersion - 1;
        Assert.Throws<InvalidOperationException>(
            () => AetherCanonicalPrincipalFactory.Create(
                user,
                session,
                [AetherRoles.Observe],
                now));
    }

    private static AetherExternalProviderDescriptor Provider() =>
        new(
            "club-oidc",
            AetherExternalProviderKind.OpenIdConnect,
            new Uri("https://identity.example/tenant"),
            "aethersdr-web",
            "/signin-oidc",
            "/signout-callback-oidc");

    private static ClaimsPrincipal ExternalPrincipal(
        string subject,
        string email)
    {
        ClaimsIdentity identity = new(
            [
                new("iss", "https://identity.example/tenant"),
                new("sub", subject),
                new("oid", "not-a-link-key"),
                new("name", "Operator One"),
                new("preferred_username", email),
                new(ClaimTypes.Role, AetherRoles.Admin),
                new(
                    "auth_time",
                    DateTimeOffset.Parse(
                            "2026-08-09T12:55:00Z",
                            System.Globalization.CultureInfo.InvariantCulture)
                        .ToUnixTimeSeconds()
                        .ToString(
                            System.Globalization.CultureInfo.InvariantCulture))
            ],
            authenticationType: "oidc");
        return new(identity);
    }

    private static AetherIdentityUser User() =>
        new()
        {
            Id = Guid.Parse("113f75d6-f06c-4f5b-878a-e5368995bef7"),
            UserName = "operator",
            DisplayName = "Operator One",
            Email = "operator@example.test",
            Enabled = true,
            AuthorityVersion = 7
        };

    private static AetherAuthenticationSession Session(
        AetherIdentityUser user,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.Parse("d6420de5-829e-4125-bf76-09d6594b737c"),
            UserId = user.Id,
            User = user,
            AuthenticationMethod =
                AetherAuthenticationMethod.ExternalOpenIdConnect,
            ProviderId = "club-oidc",
            AuthorityVersion = user.AuthorityVersion,
            CreatedAtUtc = now.AddMinutes(-10),
            LastSeenAtUtc = now.AddMinutes(-1),
            AbsoluteExpiresAtUtc = now.AddHours(1),
            ReauthenticatedAtUtc = now.AddMinutes(-5)
        };
}
