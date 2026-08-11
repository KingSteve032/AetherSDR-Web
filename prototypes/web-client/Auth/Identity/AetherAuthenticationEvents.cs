using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace AetherSDR.Web.Auth.Identity;

internal sealed class AetherCookieAuthenticationEvents(
    AetherAuthenticationSessionService sessions)
    : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(
        CookieValidatePrincipalContext context)
    {
        ClaimsPrincipal? cookiePrincipal = context.Principal;
        if (cookiePrincipal is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }

        AetherAuthenticationSessionValidationResult result =
            await sessions.ValidateAsync(
                cookiePrincipal,
                context.HttpContext.RequestAborted);
        if (!result.Succeeded || result.Principal is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }

        context.ReplacePrincipal(result.Principal);
        context.ShouldRenew = false;
    }
}

internal sealed class AetherOpenIdConnectEvents(
    AetherExternalAuthenticationService externalAuthentication,
    AetherAuthenticationSessionService sessions,
    AetherAuthenticationTopology topology)
    : OpenIdConnectEvents
{
    internal const string FreshAuthenticationItem =
        ".aether.external-reauthentication";
    internal const string ExpectedUserIdItem =
        ".aether.external-reauthentication-user";
    internal const string RequiredValue = "required";

    public override async Task TokenValidated(
        TokenValidatedContext context)
    {
        AetherExternalProviderDescriptor provider =
            topology.ExternalProvider ??
            throw new InvalidOperationException(
                "OIDC token validation requires one configured external provider.");
        ClaimsPrincipal? externalPrincipal = context.Principal;
        if (externalPrincipal is null)
        {
            context.Fail("External identity evidence was unavailable.");
            return;
        }

        AuthenticationProperties? properties = context.Properties;
        if (properties is null)
        {
            context.Fail("Authentication properties were unavailable.");
            return;
        }

        bool requireFreshAuthentication = properties.Items.TryGetValue(
                FreshAuthenticationItem,
                out string? freshValue) &&
            string.Equals(
                freshValue,
                RequiredValue,
                StringComparison.Ordinal);
        Guid expectedUserId = Guid.Empty;
        if (requireFreshAuthentication &&
            (!properties.Items.TryGetValue(
                 ExpectedUserIdItem,
                 out string? expectedUserIdValue) ||
             !Guid.TryParseExact(
                 expectedUserIdValue,
                 "D",
                 out expectedUserId) ||
             expectedUserId == Guid.Empty))
        {
            context.Fail("External reauthentication binding was invalid.");
            return;
        }

        AetherExternalAuthenticationResult result =
            await externalAuthentication.AuthenticateAsync(
                provider,
                externalPrincipal,
                context.HttpContext.TraceIdentifier,
                topology.SessionAbsoluteLifetime,
                requireFreshAuthentication,
                context.HttpContext.RequestAborted);
        if (!result.Succeeded ||
            result.Principal is null ||
            result.AbsoluteExpiresAtUtc is null)
        {
            context.Fail(
                "The external identity is not linked to an enabled Aether account.");
            return;
        }

        if (requireFreshAuthentication &&
            (!AetherAuthenticationSessionService.TryReadCanonicalIdentity(
                 result.Principal,
                 out Guid reauthenticatedUserId,
                 out _,
                 out _) ||
             reauthenticatedUserId != expectedUserId))
        {
            _ = await sessions.RevokeAsync(
                result.Principal,
                "external-reauthentication-binding-rejected",
                context.HttpContext.RequestAborted);
            context.Fail(
                "External reauthentication did not match the current account.");
            return;
        }

        context.Principal = result.Principal;
        properties.IsPersistent = false;
        properties.AllowRefresh = false;
        properties.ExpiresUtc = result.AbsoluteExpiresAtUtc;
    }
}
