using System.Security.Claims;
using System.Threading.RateLimiting;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AetherSDR.Web.Tests;

public sealed class AetherIdentityAdministrationHttpAdapterTests
{
    [Fact]
    public async Task LocalModeMapsOnlyBoundedAdminRateLimitedEndpoints()
    {
        await using AdapterApplication host =
            await AdapterApplication.CreateAsync(LocalTopology());

        Assert.Equal(
            [
                AetherIdentityAdministrationHttpAdapter.AccountsPath,
                AetherIdentityAdministrationHttpAdapter.EnrollmentsPath,
                AetherIdentityAdministrationHttpAdapter.AccountsPath +
                    "/{userId:guid}/enrollment-confirmation",
                AetherIdentityAdministrationHttpAdapter.AccountsPath +
                    "/{userId:guid}/password-reset",
                AetherIdentityAdministrationHttpAdapter.AccountsPath +
                    "/{userId:guid}/roles",
                AetherIdentityAdministrationHttpAdapter
                    .LocalMfaReauthenticationPath,
                AetherIdentityAdministrationHttpAdapter
                    .LocalPasswordReauthenticationPath
            ],
            host.Report.EndpointPaths.Order(StringComparer.Ordinal));

        RouteEndpoint[] endpoints = host.Endpoints();
        Assert.Equal(7, endpoints.Length);
        foreach (RouteEndpoint endpoint in endpoints)
        {
            IAuthorizeData authorization = Assert.Single(
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(AetherPolicies.Admin, authorization.Policy);
            EnableRateLimitingAttribute rateLimit = Assert.IsType<
                EnableRateLimitingAttribute>(
                    endpoint.Metadata.GetMetadata<
                        EnableRateLimitingAttribute>());
            Assert.Equal(
                AetherIdentityAdministrationDefaults.RateLimitPolicy,
                rateLimit.PolicyName);
        }

        RouteEndpoint listing = Assert.Single(
            endpoints,
            endpoint =>
                endpoint.RoutePattern.RawText ==
                AetherIdentityAdministrationHttpAdapter.AccountsPath &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains(
                        HttpMethods.Get,
                        StringComparer.Ordinal));
        Assert.NotNull(listing);
        Assert.All(
            endpoints.Except([listing]),
            endpoint => Assert.DoesNotContain(
                HttpMethods.Get,
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods));
    }

    [Fact]
    public async Task DevelopmentMapsNothingAndCombinedAddsExternalReauthentication()
    {
        AetherAuthenticationTopology development =
            AetherAuthenticationConfiguration.Validate(
                new AuthSettings { Mode = "Development" },
                isDevelopmentEnvironment: true);
        await using AdapterApplication developmentHost =
            await AdapterApplication.CreateAsync(development);
        await using AdapterApplication combinedHost =
            await AdapterApplication.CreateAsync(CombinedTopology());

        Assert.Empty(developmentHost.Report.EndpointPaths);
        Assert.Empty(developmentHost.Endpoints());
        Assert.Contains(
            AetherIdentityAdministrationHttpAdapter
                .ExternalReauthenticationPath,
            combinedHost.Report.EndpointPaths);
        Assert.Contains(
            AetherIdentityAdministrationHttpAdapter.ExternalIdentityLinkPath,
            combinedHost.Report.EndpointPaths);
        Assert.Contains(
            AetherIdentityAdministrationHttpAdapter
                .ExternalIdentityProviderPath,
            combinedHost.Report.EndpointPaths);
        Assert.Equal(10, combinedHost.Endpoints().Length);
    }

    [Fact]
    public async Task ExternalOnlyMapsRoleAndProviderAdministrationWithoutLocalCredentials()
    {
        await using AdapterApplication host =
            await AdapterApplication.CreateAsync(ExternalTopology());

        Assert.Equal(
            [
                AetherIdentityAdministrationHttpAdapter.AccountsPath,
                AetherIdentityAdministrationHttpAdapter
                    .ExternalIdentityLinkPath,
                AetherIdentityAdministrationHttpAdapter
                    .ExternalIdentityProviderPath,
                AetherIdentityAdministrationHttpAdapter.AccountsPath +
                    "/{userId:guid}/roles",
                AetherIdentityAdministrationHttpAdapter
                    .ExternalReauthenticationPath
            ],
            host.Report.EndpointPaths.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(
            AetherIdentityAdministrationHttpAdapter
                .LocalPasswordReauthenticationPath,
            host.Report.EndpointPaths);
        Assert.DoesNotContain(
            AetherIdentityAdministrationHttpAdapter
                .LocalMfaReauthenticationPath,
            host.Report.EndpointPaths);
    }

    [Fact]
    public async Task ExternalReauthenticationMutationRequiresAntiforgery()
    {
        await using AdapterApplication host =
            await AdapterApplication.CreateAsync(
                CombinedTopology(),
                buildPipeline: true);
        DefaultHttpContext context = new()
        {
            RequestServices = host.Application.Services,
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [
                        new(
                            ClaimTypes.NameIdentifier,
                            Guid.NewGuid().ToString("D")),
                        new(
                            ClaimTypes.Role,
                            AetherRoles.Admin)
                    ],
                    authenticationType:
                        AetherCanonicalPrincipalFactory.AuthenticationType))
        };
        context.Request.Scheme = "https";
        context.Request.Method = HttpMethods.Post;
        context.Request.Path =
            AetherIdentityAdministrationHttpAdapter
                .ExternalReauthenticationPath;
        byte[] body = System.Text.Encoding.UTF8.GetBytes(
            """{"returnUrl":"/admin.html"}""");
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();

        await host.Pipeline!(context);

        Assert.Equal(
            StatusCodes.Status400BadRequest,
            context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using StreamReader reader = new(context.Response.Body);
        string response = await reader.ReadToEndAsync();
        Assert.Contains(
            AetherAntiforgery.FailureMessage,
            response,
            StringComparison.Ordinal);
    }

    private static AetherAuthenticationTopology LocalTopology() =>
        AetherAuthenticationConfiguration.Validate(
            new AuthSettings { Mode = "Local" },
            isDevelopmentEnvironment: false);

    private static AetherAuthenticationTopology ExternalTopology() =>
        AetherAuthenticationConfiguration.Validate(
            new AuthSettings
            {
                Mode = "OpenIdConnect",
                ProviderId = "club-oidc",
                Authority = "https://identity.example/tenant",
                ClientId = "aethersdr-web",
                ClientSecret = "test-secret"
            },
            isDevelopmentEnvironment: false);

    private static AetherAuthenticationTopology CombinedTopology() =>
        AetherAuthenticationConfiguration.Validate(
            new AuthSettings
            {
                Mode = "Combined",
                ProviderId = "club-oidc",
                ProviderType = "OpenIdConnect",
                Authority = "https://identity.example/tenant",
                ClientId = "aethersdr-web",
                ClientSecret = "test-secret"
            },
            isDevelopmentEnvironment: false);

    private sealed class AdapterApplication : IAsyncDisposable
    {
        private AdapterApplication(
            WebApplication application,
            AetherIdentityAdministrationHttpAdapterReport report,
            RequestDelegate? pipeline)
        {
            Application = application;
            Report = report;
            Pipeline = pipeline;
        }

        internal WebApplication Application { get; }

        internal AetherIdentityAdministrationHttpAdapterReport Report { get; }

        internal RequestDelegate? Pipeline { get; }

        internal static Task<AdapterApplication> CreateAsync(
            AetherAuthenticationTopology topology,
            bool buildPipeline = false)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    Args = [],
                    EnvironmentName = Environments.Production
                });
            builder.Logging.ClearProviders();
            builder.Services.AddRouting();
            builder.Services.AddAntiforgery(options =>
            {
                options.HeaderName = AetherAntiforgery.HeaderName;
                options.Cookie.Name = "__Host-AetherSdrWeb-Csrf";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy =
                    CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
            });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(
                    AetherPolicies.Admin,
                    policy => policy.RequireRole(AetherRoles.Admin));
            });
            builder.Services.AddRateLimiter(options =>
            {
                options.AddPolicy(
                    AetherIdentityAdministrationDefaults.RateLimitPolicy,
                    _ => RateLimitPartition.GetFixedWindowLimiter(
                        "test-client",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 100,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        }));
            });

            WebApplication application = builder.Build();
            application.UseRouting();
            application.UseAuthorization();
            AetherIdentityAdministrationHttpAdapterReport report =
                AetherIdentityAdministrationHttpAdapter.Map(
                    application,
                    topology);
            application.UseRateLimiter();
            application.UseEndpoints(_ => { });
            RequestDelegate? pipeline = buildPipeline
                ? ((IApplicationBuilder)application).Build()
                : null;
            return Task.FromResult(
                new AdapterApplication(
                    application,
                    report,
                    pipeline));
        }

        internal RouteEndpoint[] Endpoints() =>
            ((IEndpointRouteBuilder)Application).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(endpoint =>
                    endpoint.RoutePattern.RawText?.StartsWith(
                        "/api/admin/identity",
                        StringComparison.Ordinal) == true)
                .ToArray();

        public async ValueTask DisposeAsync()
        {
            await Application.DisposeAsync();
        }
    }
}
