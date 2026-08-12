using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class AetherAuthenticationCompositionTests
{
    [Fact]
    public async Task ExternalCompositionUsesCanonicalEventsAndAbsoluteCookie()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
        AuthSettings settings = ExternalSettings();
        AetherAuthenticationTopology topology =
            AetherAuthenticationConfiguration.Validate(
                settings,
                isDevelopmentEnvironment: false);

        AetherAuthenticationComposition.Configure(
            builder,
            settings,
            topology);
        await using WebApplication application = builder.Build();

        AuthenticationOptions authentication =
            application.Services
                .GetRequiredService<IOptions<AuthenticationOptions>>()
                .Value;
        CookieAuthenticationOptions cookie =
            application.Services
                .GetRequiredService<
                    IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        OpenIdConnectOptions oidc =
            application.Services
                .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(OpenIdConnectDefaults.AuthenticationScheme);

        Assert.Equal(
            CookieAuthenticationDefaults.AuthenticationScheme,
            authentication.DefaultAuthenticateScheme);
        Assert.Equal(
            OpenIdConnectDefaults.AuthenticationScheme,
            authentication.DefaultChallengeScheme);
        Assert.Equal("__Host-AetherSdrWeb", cookie.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.Equal("/login", cookie.LoginPath);
        Assert.Equal("returnUrl", cookie.ReturnUrlParameter);
        Assert.False(cookie.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(8), cookie.ExpireTimeSpan);
        Assert.Equal(
            typeof(AetherCookieAuthenticationEvents),
            cookie.EventsType);

        Assert.Equal("https://identity.example/tenant", oidc.Authority);
        Assert.Equal("aethersdr-web", oidc.ClientId);
        Assert.True(oidc.UsePkce);
        Assert.False(oidc.SaveTokens);
        Assert.False(oidc.UseTokenLifetime);
        Assert.False(oidc.MapInboundClaims);
        Assert.True(oidc.RequireHttpsMetadata);
        Assert.Equal(
            typeof(AetherOpenIdConnectEvents),
            oidc.EventsType);
        Assert.Equal(
            "aether:external-role-not-authority",
            oidc.TokenValidationParameters.RoleClaimType);
    }

    [Fact]
    public async Task DevelopmentCompositionRemainsDevelopmentOnly()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
        AuthSettings settings = new() { Mode = "Development" };
        AetherAuthenticationTopology topology =
            AetherAuthenticationConfiguration.Validate(
                settings,
                isDevelopmentEnvironment: true);

        AetherAuthenticationComposition.Configure(
            builder,
            settings,
            topology);
        await using WebApplication application = builder.Build();

        AuthenticationOptions authentication =
            application.Services
                .GetRequiredService<IOptions<AuthenticationOptions>>()
                .Value;
        Assert.Equal(
            DevelopmentAuthenticationDefaults.Scheme,
            authentication.DefaultAuthenticateScheme);
        Assert.Equal(
            DevelopmentAuthenticationDefaults.Scheme,
            authentication.DefaultChallengeScheme);
    }

    [Fact]
    public async Task ServiceBoundaryHasOneFailClosedScheme()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
        AetherAuthenticationTopology topology =
            AetherAuthenticationConfiguration.CreateServiceBoundary();

        AetherAuthenticationComposition.Configure(
            builder,
            new AuthSettings(),
            topology);
        await using WebApplication application = builder.Build();

        AuthenticationOptions authentication =
            application.Services
                .GetRequiredService<IOptions<AuthenticationOptions>>()
                .Value;
        IAuthenticationSchemeProvider schemes =
            application.Services
                .GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Equal(
            ServiceBoundaryAuthenticationDefaults.Scheme,
            authentication.DefaultAuthenticateScheme);
        Assert.Equal(
            ServiceBoundaryAuthenticationDefaults.Scheme,
            authentication.DefaultChallengeScheme);
        AuthenticationScheme scheme = Assert.IsType<AuthenticationScheme>(
            await schemes.GetSchemeAsync(
                ServiceBoundaryAuthenticationDefaults.Scheme));
        Assert.Equal(
            typeof(ServiceBoundaryAuthenticationHandler),
            scheme.HandlerType);
        Assert.Null(
            await schemes.GetSchemeAsync(
                CookieAuthenticationDefaults.AuthenticationScheme));
        Assert.Null(
            await schemes.GetSchemeAsync(
                OpenIdConnectDefaults.AuthenticationScheme));
    }

    [Fact]
    public async Task LocalCompositionUsesCanonicalCookieWithoutOidcScheme()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
        AuthSettings settings = new() { Mode = "Local" };
        AetherAuthenticationTopology topology =
            AetherAuthenticationConfiguration.Validate(
                settings,
                isDevelopmentEnvironment: false);

        AetherAuthenticationComposition.Configure(
            builder,
            settings,
            topology);
        await using WebApplication application = builder.Build();

        AuthenticationOptions authentication =
            application.Services
                .GetRequiredService<IOptions<AuthenticationOptions>>()
                .Value;
        IAuthenticationSchemeProvider schemes =
            application.Services
                .GetRequiredService<IAuthenticationSchemeProvider>();
        CookieAuthenticationOptions cookie =
            application.Services
                .GetRequiredService<
                    IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(
            CookieAuthenticationDefaults.AuthenticationScheme,
            authentication.DefaultAuthenticateScheme);
        Assert.Equal(
            CookieAuthenticationDefaults.AuthenticationScheme,
            authentication.DefaultChallengeScheme);
        Assert.NotNull(
            await schemes.GetSchemeAsync(
                CookieAuthenticationDefaults.AuthenticationScheme));
        Assert.Null(
            await schemes.GetSchemeAsync(
                OpenIdConnectDefaults.AuthenticationScheme));
        Assert.Equal("__Host-AetherSdrWeb", cookie.Cookie.Name);
        Assert.Equal("/login", cookie.LoginPath);
        Assert.False(cookie.SlidingExpiration);
    }

    [Fact]
    public async Task CombinedCompositionOffersOidcButChallengesToLoginChoice()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
        AuthSettings settings = new()
        {
            Mode = "Combined",
            ProviderType = "OpenIdConnect",
            ProviderId = "club-oidc",
            Authority = "https://identity.example/tenant",
            ClientId = "aethersdr-web",
            ClientSecret = "test-secret",
            CallbackPath = "/signin-oidc",
            SignedOutCallbackPath = "/signout-callback-oidc"
        };
        AetherAuthenticationTopology topology =
            AetherAuthenticationConfiguration.Validate(
                settings,
                isDevelopmentEnvironment: false);

        AetherAuthenticationComposition.Configure(
            builder,
            settings,
            topology);
        await using WebApplication application = builder.Build();

        AuthenticationOptions authentication =
            application.Services
                .GetRequiredService<IOptions<AuthenticationOptions>>()
                .Value;
        IAuthenticationSchemeProvider schemes =
            application.Services
                .GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Equal(
            CookieAuthenticationDefaults.AuthenticationScheme,
            authentication.DefaultChallengeScheme);
        Assert.NotNull(
            await schemes.GetSchemeAsync(
                CookieAuthenticationDefaults.AuthenticationScheme));
        Assert.NotNull(
            await schemes.GetSchemeAsync(
                OpenIdConnectDefaults.AuthenticationScheme));
    }

    private static AuthSettings ExternalSettings() =>
        new()
        {
            Mode = "OpenIdConnect",
            ProviderId = "club-oidc",
            Authority = "https://identity.example/tenant",
            ClientId = "aethersdr-web",
            ClientSecret = "test-secret",
            CallbackPath = "/signin-oidc",
            SignedOutCallbackPath = "/signout-callback-oidc"
        };
}
