using AetherSDR.Web.Auth.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AetherSDR.Web.Auth;

internal static class AetherAuthenticationComposition
{
    internal static void Configure(
        WebApplicationBuilder builder,
        AuthSettings authSettings,
        AetherAuthenticationTopology authenticationTopology)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(authSettings);
        ArgumentNullException.ThrowIfNull(authenticationTopology);

        if (authenticationTopology.Mode ==
            AetherAuthenticationMode.Development)
        {
            builder.Services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme =
                        DevelopmentAuthenticationDefaults.Scheme;
                    options.DefaultChallengeScheme =
                        DevelopmentAuthenticationDefaults.Scheme;
                })
                .AddScheme<
                    AuthenticationSchemeOptions,
                    DevelopmentAuthenticationHandler>(
                    DevelopmentAuthenticationDefaults.Scheme,
                    _ => { });
            return;
        }

        AetherExternalProviderDescriptor provider =
            authenticationTopology.ExternalProvider ??
            throw new InvalidOperationException(
                "Production external authentication requires one provider.");
        string clientSecret = OidcClientSecretResolver.Resolve(authSettings);

        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme =
                    CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme =
                    CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme =
                    OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(
                CookieAuthenticationDefaults.AuthenticationScheme,
                options =>
                {
                    options.Cookie.Name = "__Host-AetherSdrWeb";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.IsEssential = true;
                    options.Cookie.SecurePolicy =
                        CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.Path = "/";
                    options.AccessDeniedPath = "/access-denied";
                    options.ExpireTimeSpan =
                        authenticationTopology.SessionAbsoluteLifetime;
                    options.SlidingExpiration = false;
                    options.EventsType =
                        typeof(AetherCookieAuthenticationEvents);
                })
            .AddOpenIdConnect(
                OpenIdConnectDefaults.AuthenticationScheme,
                options =>
                {
                    options.Authority =
                        provider.Authority.AbsoluteUri.TrimEnd('/');
                    options.ClientId = provider.ClientId;
                    options.ClientSecret = clientSecret;
                    options.CallbackPath = provider.CallbackPath;
                    options.SignedOutCallbackPath =
                        provider.SignedOutCallbackPath;
                    options.SignInScheme =
                        CookieAuthenticationDefaults.AuthenticationScheme;
                    options.ResponseType =
                        OpenIdConnectResponseType.Code;
                    options.UsePkce = true;
                    options.SaveTokens = false;
                    options.UseTokenLifetime = false;
                    options.GetClaimsFromUserInfoEndpoint = false;
                    options.MapInboundClaims = false;
                    options.RequireHttpsMetadata = true;
                    options.EventsType =
                        typeof(AetherOpenIdConnectEvents);
                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.Scope.Add("email");
                    options.TokenValidationParameters =
                        new TokenValidationParameters
                        {
                            NameClaimType = "name",
                            RoleClaimType =
                                "aether:external-role-not-authority",
                            ValidateIssuer = true
                        };
                });
    }
}
