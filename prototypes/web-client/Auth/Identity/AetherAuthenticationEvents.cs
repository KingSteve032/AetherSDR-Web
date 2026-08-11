using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;

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
    AetherExternalIdentityAdministrationService externalIdentities,
    AetherAuthenticationSessionService sessions,
    AetherAuthenticationTopology topology)
    : OpenIdConnectEvents
{
    internal const string FreshAuthenticationItem =
        ".aether.external-reauthentication";
    internal const string ExpectedUserIdItem =
        ".aether.external-reauthentication-user";
    internal const string ExternalIdentityLinkItem =
        ".aether.external-identity-link";
    internal const string LinkActorUserIdItem =
        ".aether.external-identity-link-actor";
    internal const string LinkActorSessionIdItem =
        ".aether.external-identity-link-session";
    internal const string LinkTargetUserIdItem =
        ".aether.external-identity-link-target";
    internal const string LinkProviderIdItem =
        ".aether.external-identity-link-provider";
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
        bool linkExternalIdentity = properties.Items.TryGetValue(
                ExternalIdentityLinkItem,
                out string? linkValue) &&
            string.Equals(
                linkValue,
                RequiredValue,
                StringComparison.Ordinal);
        if (requireFreshAuthentication && linkExternalIdentity)
        {
            context.Fail("The external authentication operation was ambiguous.");
            return;
        }
        if (linkExternalIdentity)
        {
            await CompleteExternalIdentityLinkAsync(
                context,
                provider,
                externalPrincipal,
                properties);
            return;
        }

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

    private async Task CompleteExternalIdentityLinkAsync(
        TokenValidatedContext context,
        AetherExternalProviderDescriptor provider,
        ClaimsPrincipal externalPrincipal,
        AuthenticationProperties properties)
    {
        if (!TryReadLinkBinding(
                properties,
                provider,
                out Guid actorUserId,
                out Guid actorSessionId,
                out Guid targetUserId))
        {
            context.Fail("The external identity link binding was invalid.");
            return;
        }

        AuthenticateResult currentAdministrator =
            await context.HttpContext.AuthenticateAsync(
                CookieAuthenticationDefaults.AuthenticationScheme);
        if (!currentAdministrator.Succeeded ||
            currentAdministrator.Principal is null)
        {
            context.Fail(
                "A current administrator session is required to link an identity.");
            return;
        }

        AetherExternalIdentityMutationResult result;
        try
        {
            result = await externalIdentities.LinkAsync(
                currentAdministrator.Principal,
                actorUserId,
                actorSessionId,
                targetUserId,
                provider,
                externalPrincipal,
                context.HttpContext.TraceIdentifier,
                context.HttpContext.RequestAborted);
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            context.Fail(
                "Fresh administrator authority expired before identity linking.");
            return;
        }
        catch (InvalidOperationException)
        {
            context.Fail("External identity linking was rejected.");
            return;
        }

        string returnUrl = LocalReturnUrl.Normalize(properties.RedirectUri);
        string redirectUrl = QueryHelpers.AddQueryString(
            returnUrl,
            "externalIdentityLink",
            result.Code);
        context.HandleResponse();
        context.HttpContext.Response.Headers.CacheControl =
            "no-store, max-age=0";
        context.HttpContext.Response.Headers.Pragma = "no-cache";
        context.HttpContext.Response.Headers.Expires = "0";
        context.HttpContext.Response.Redirect(redirectUrl);
    }

    private static bool TryReadLinkBinding(
        AuthenticationProperties properties,
        AetherExternalProviderDescriptor provider,
        out Guid actorUserId,
        out Guid actorSessionId,
        out Guid targetUserId)
    {
        actorUserId = Guid.Empty;
        actorSessionId = Guid.Empty;
        targetUserId = Guid.Empty;
        return
            properties.Items.TryGetValue(
                LinkActorUserIdItem,
                out string? actorUserValue) &&
            Guid.TryParseExact(actorUserValue, "D", out actorUserId) &&
            actorUserId != Guid.Empty &&
            properties.Items.TryGetValue(
                LinkActorSessionIdItem,
                out string? actorSessionValue) &&
            Guid.TryParseExact(actorSessionValue, "D", out actorSessionId) &&
            actorSessionId != Guid.Empty &&
            properties.Items.TryGetValue(
                LinkTargetUserIdItem,
                out string? targetUserValue) &&
            Guid.TryParseExact(targetUserValue, "D", out targetUserId) &&
            targetUserId != Guid.Empty &&
            properties.Items.TryGetValue(
                LinkProviderIdItem,
                out string? providerId) &&
            string.Equals(
                providerId,
                provider.ProviderId,
                StringComparison.Ordinal);
    }
}
