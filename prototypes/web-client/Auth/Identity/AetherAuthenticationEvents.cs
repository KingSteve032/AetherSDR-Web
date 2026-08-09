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
    AetherAuthenticationTopology topology)
    : OpenIdConnectEvents
{
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

        AetherExternalAuthenticationResult result =
            await externalAuthentication.AuthenticateAsync(
                provider,
                externalPrincipal,
                context.HttpContext.TraceIdentifier,
                topology.SessionAbsoluteLifetime,
                context.HttpContext.RequestAborted);
        if (!result.Succeeded ||
            result.Principal is null ||
            result.AbsoluteExpiresAtUtc is null)
        {
            context.Fail(
                "The external identity is not linked to an enabled Aether account.");
            return;
        }

        context.Principal = result.Principal;
        properties.IsPersistent = false;
        properties.AllowRefresh = false;
        properties.ExpiresUtc = result.AbsoluteExpiresAtUtc;
    }
}
