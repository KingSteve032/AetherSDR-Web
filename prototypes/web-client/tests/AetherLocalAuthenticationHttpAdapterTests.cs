using System.Text;
using System.Text.Json;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.RateLimiting;

namespace AetherSDR.Web.Tests;

public sealed class AetherLocalAuthenticationHttpAdapterTests
{
    private const string CsrfCookieName = "__Host-AetherSdrWeb-Csrf";
    private const string SessionCookieName = "__Host-AetherSdrWeb";
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task LocalModeMapsOptionsAndOnlyRateLimitsCredentialMutations()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        RouteEndpoint[] endpoints =
            ((IEndpointRouteBuilder)host.Application).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(endpoint =>
                    endpoint.RoutePattern.RawText?.StartsWith(
                        "/api/auth/",
                        StringComparison.Ordinal) == true)
                .ToArray();

        Assert.Equal(
            [
                AetherLocalAuthenticationHttpAdapter.MfaPath,
                AetherLocalAuthenticationHttpAdapter.PasswordPath,
                AetherLocalAuthenticationHttpAdapter.OptionsPath
            ],
            host.Report.EndpointPaths.Order(StringComparer.Ordinal));
        Assert.Equal(3, endpoints.Length);

        RouteEndpoint options = Assert.Single(
            endpoints,
            endpoint =>
                endpoint.RoutePattern.RawText ==
                AetherLocalAuthenticationHttpAdapter.OptionsPath);
        Assert.Null(
            options.Metadata.GetMetadata<EnableRateLimitingAttribute>());

        foreach (RouteEndpoint mutation in endpoints.Except([options]))
        {
            EnableRateLimitingAttribute rateLimit = Assert.IsType<
                EnableRateLimitingAttribute>(
                    mutation.Metadata.GetMetadata<
                        EnableRateLimitingAttribute>());
            Assert.Equal(
                AetherLocalAuthenticationDefaults.RateLimitPolicy,
                rateLimit.PolicyName);
        }
    }

    [Fact]
    public async Task OptionsIssuesStrictAntiforgeryAndNormalizesReturnUrl()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();

        CapturedResponse response = await host.InvokeAsync(
            "GET",
            AetherLocalAuthenticationHttpAdapter.OptionsPath +
            "?returnUrl=%2F%2Fevil.example");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains(
            "no-store",
            response.Header("Cache-Control"),
            StringComparison.Ordinal);
        string setCookie = response.SetCookie(CsrfCookieName);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "httponly",
            setCookie,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "samesite=strict",
            setCookie,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "domain=",
            setCookie,
            StringComparison.OrdinalIgnoreCase);

        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.True(
            json.RootElement
                .GetProperty("localAccountsEnabled")
                .GetBoolean());
        Assert.Equal(
            JsonValueKind.Null,
            json.RootElement.GetProperty("externalProvider").ValueKind);
        Assert.Equal(
            "/",
            json.RootElement.GetProperty("returnUrl").GetString());
        Assert.Equal(
            AetherAntiforgery.HeaderName,
            json.RootElement
                .GetProperty("antiforgery")
                .GetProperty("headerName")
                .GetString());
        Assert.NotEmpty(
            json.RootElement
                .GetProperty("antiforgery")
                .GetProperty("requestToken")
                .GetString()!);
    }

    [Fact]
    public async Task PasswordAndRecoveryCodeCreateDurableCanonicalCookieSession()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        SeededUser seeded = await host.SeedUserAsync();
        AntiforgeryEvidence csrf = await host.GetAntiforgeryAsync();

        CapturedResponse password = await host.InvokeJsonAsync(
            AetherLocalAuthenticationHttpAdapter.PasswordPath,
            JsonSerializer.Serialize(
                new
                {
                    userName = seeded.UserName,
                    password = Password
                }),
            csrf);
        Assert.Equal(StatusCodes.Status200OK, password.StatusCode);
        Assert.Equal(string.Empty, password.Header("Set-Cookie"));
        using JsonDocument passwordJson = JsonDocument.Parse(password.Body);
        string challenge = passwordJson.RootElement
            .GetProperty("challengeToken")
            .GetString()!;
        Assert.Equal(64, challenge.Length);
        Assert.DoesNotContain(Password, password.Body, StringComparison.Ordinal);

        CapturedResponse mfa = await host.InvokeJsonAsync(
            AetherLocalAuthenticationHttpAdapter.MfaPath,
            JsonSerializer.Serialize(
                new
                {
                    challengeToken = challenge,
                    verificationCode = seeded.RecoveryCode,
                    returnUrl = "/radios"
                }),
            csrf);
        Assert.Equal(StatusCodes.Status200OK, mfa.StatusCode);
        Assert.Contains(
            "no-store",
            mfa.Header("Cache-Control"),
            StringComparison.Ordinal);
        string sessionCookie = mfa.SetCookie(SessionCookieName);
        Assert.Contains(
            "secure",
            sessionCookie,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "httponly",
            sessionCookie,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "samesite=lax",
            sessionCookie,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "domain=",
            sessionCookie,
            StringComparison.OrdinalIgnoreCase);

        using (JsonDocument mfaJson = JsonDocument.Parse(mfa.Body))
        {
            Assert.Equal(
                "local-mfa-authenticated",
                mfaJson.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "/radios",
                mfaJson.RootElement.GetProperty("redirectUrl").GetString());
        }

        await using AsyncServiceScope scope =
            host.Application.Services.CreateAsyncScope();
        AetherIdentityDbContext database =
            scope.ServiceProvider.GetRequiredService<AetherIdentityDbContext>();
        AetherAuthenticationSession session =
            await database.AuthenticationSessions.SingleAsync(
                value => value.UserId == seeded.UserId);
        Assert.Equal(
            AetherAuthenticationMethod.LocalPasswordWithRecoveryCode,
            session.AuthenticationMethod);
        Assert.Null(session.ProviderId);
        Assert.Null(session.RevokedAtUtc);
        Assert.False(
            await database.Set<IdentityUserToken<Guid>>().AnyAsync(
                token =>
                    token.UserId == seeded.UserId &&
                    token.Name.StartsWith(
                        AetherLocalMfaCredentialProtector
                            .RecoveryCodeNamePrefix)));
    }

    [Fact]
    public async Task RejectionsAreGenericStrictAndChallengeCannotReplay()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        SeededUser seeded = await host.SeedUserAsync();
        AntiforgeryEvidence csrf = await host.GetAntiforgeryAsync();

        CapturedResponse unknown = await host.InvokeJsonAsync(
            AetherLocalAuthenticationHttpAdapter.PasswordPath,
            JsonSerializer.Serialize(
                new
                {
                    userName = "missing-user",
                    password = Password
                }),
            csrf);
        CapturedResponse wrong = await host.InvokeJsonAsync(
            AetherLocalAuthenticationHttpAdapter.PasswordPath,
            JsonSerializer.Serialize(
                new
                {
                    userName = seeded.UserName,
                    password = "this password is definitely wrong"
                }),
            csrf);
        Assert.Equal(StatusCodes.Status401Unauthorized, unknown.StatusCode);
        Assert.Equal(unknown.Body, wrong.Body);
        Assert.Contains(
            AetherLocalAuthenticationHttpAdapter.RejectedCode,
            unknown.Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("missing-user", unknown.Body, StringComparison.Ordinal);

        CapturedResponse invalid = await host.InvokeJsonAsync(
            AetherLocalAuthenticationHttpAdapter.PasswordPath,
            $$"""{"userName":"{{seeded.UserName}}","userName":"second","password":"{{Password}}"}""",
            csrf);
        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);

        CapturedResponse acceptedPassword = await host.InvokeJsonAsync(
            AetherLocalAuthenticationHttpAdapter.PasswordPath,
            JsonSerializer.Serialize(
                new
                {
                    userName = seeded.UserName,
                    password = Password
                }),
            csrf);
        using JsonDocument acceptedJson =
            JsonDocument.Parse(acceptedPassword.Body);
        string challenge = acceptedJson.RootElement
            .GetProperty("challengeToken")
            .GetString()!;

        string mfaBody = JsonSerializer.Serialize(
            new
            {
                challengeToken = challenge,
                verificationCode = seeded.RecoveryCode,
                returnUrl = "//evil.example"
            });
        CapturedResponse accepted = await host.InvokeJsonAsync(
            AetherLocalAuthenticationHttpAdapter.MfaPath,
            mfaBody,
            csrf);
        CapturedResponse replay = await host.InvokeJsonAsync(
            AetherLocalAuthenticationHttpAdapter.MfaPath,
            mfaBody,
            csrf);

        Assert.Equal(StatusCodes.Status200OK, accepted.StatusCode);
        using (JsonDocument acceptedMfa = JsonDocument.Parse(accepted.Body))
        {
            Assert.Equal(
                "/",
                acceptedMfa.RootElement
                    .GetProperty("redirectUrl")
                    .GetString());
        }
        Assert.Equal(StatusCodes.Status401Unauthorized, replay.StatusCode);
        Assert.Equal(string.Empty, replay.SetCookie(SessionCookieName));
        Assert.Contains(
            AetherLocalAuthenticationHttpAdapter.RejectedCode,
            replay.Body,
            StringComparison.Ordinal);
    }

    private sealed class AdapterHost : IAsyncDisposable
    {
        private readonly RequestDelegate pipeline;
        private readonly TemporaryDirectory temporary;

        private AdapterHost(
            WebApplication application,
            RequestDelegate pipeline,
            AetherLocalAuthenticationHttpAdapterReport report,
            AetherAuthenticationTopology topology,
            TemporaryDirectory temporary)
        {
            Application = application;
            this.pipeline = pipeline;
            Report = report;
            Topology = topology;
            this.temporary = temporary;
        }

        internal WebApplication Application { get; }

        internal AetherLocalAuthenticationHttpAdapterReport Report { get; }

        internal AetherAuthenticationTopology Topology { get; }

        internal static async Task<AdapterHost> CreateAsync()
        {
            TemporaryDirectory temporary = new();
            try
            {
                InstallationPaths paths = InstallationPaths.Resolve(
                    temporary.Path,
                    InstallationPathLayout.Development);
                AetherIdentityDatabaseReport plan =
                    await AetherIdentityDatabaseMigration.PlanAsync(paths);
                AetherIdentityDatabaseReport applied =
                    await AetherIdentityDatabaseMigration.ApplyAsync(
                        paths,
                        plan.PlanId);
                Assert.Equal("applied", applied.Outcome);

                AuthSettings settings = new() { Mode = "Local" };
                AetherAuthenticationTopology topology =
                    AetherAuthenticationConfiguration.Validate(
                        settings,
                        isDevelopmentEnvironment: false);
                WebApplicationBuilder builder = WebApplication.CreateBuilder(
                    new WebApplicationOptions
                    {
                        Args = [],
                        EnvironmentName = Environments.Production,
                        ContentRootPath = temporary.Path
                    });
                builder.Logging.ClearProviders();
                builder.Services.AddSingleton(topology);
                builder.Services.AddSingleton(TimeProvider.System);
                builder.Services.AddSingleton<IDataProtectionProvider>(
                    new EphemeralDataProtectionProvider());
                builder.Services.AddAetherIdentityPersistence(paths);
                builder.Services.AddAetherLocalAuthenticationFoundation(
                    topology.LocalPolicy);
                builder.Services.AddScoped<AetherAuthenticationSessionService>();
                builder.Services.AddScoped<AetherCookieAuthenticationEvents>();
                builder.Services.AddScoped<AetherOpenIdConnectEvents>();
                builder.Services.AddAntiforgery(options =>
                {
                    options.HeaderName = AetherAntiforgery.HeaderName;
                    options.Cookie.Name = CsrfCookieName;
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    options.Cookie.Path = "/";
                });
                builder.Services.AddAuthorization();
                builder.Services.AddRateLimiter(options =>
                {
                    options.RejectionStatusCode =
                        StatusCodes.Status429TooManyRequests;
                    options.AddPolicy(
                        AetherLocalAuthenticationDefaults.RateLimitPolicy,
                        _ => RateLimitPartition.GetFixedWindowLimiter(
                            "test-client",
                            _ => AetherLocalAuthenticationDefaults
                                .CreateRateLimiterOptions(
                                    topology.LocalPolicy)));
                });
                AetherAuthenticationComposition.Configure(
                    builder,
                    settings,
                    topology);

                WebApplication application = builder.Build();
                application.UseRouting();
                application.UseAuthentication();
                application.UseAuthorization();
                application.UseRateLimiter();
                AetherLocalAuthenticationHttpAdapterReport report =
                    AetherLocalAuthenticationHttpAdapter.Map(
                        application,
                        topology);
                application.UseEndpoints(_ => { });
                RequestDelegate pipeline =
                    ((IApplicationBuilder)application).Build();
                return new(
                    application,
                    pipeline,
                    report,
                    topology,
                    temporary);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        internal async Task<SeededUser> SeedUserAsync()
        {
            await using AsyncServiceScope scope =
                Application.Services.CreateAsyncScope();
            IServiceProvider services = scope.ServiceProvider;
            AetherIdentityDbContext database =
                services.GetRequiredService<AetherIdentityDbContext>();
            IPasswordHasher<AetherIdentityUser> hasher =
                services.GetRequiredService<
                    IPasswordHasher<AetherIdentityUser>>();
            AetherLocalMfaCredentialProtector protector =
                services.GetRequiredService<
                    AetherLocalMfaCredentialProtector>();

            AetherIdentityUser user = new()
            {
                Id = Guid.NewGuid(),
                UserName = $"operator-{Guid.NewGuid():N}",
                DisplayName = "Local Station Operator",
                Enabled = true,
                AuthorityVersion = 1,
                EmailConfirmed = true,
                TwoFactorEnabled = true,
                LockoutEnabled = true,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };
            user.NormalizedUserName = user.UserName.ToUpperInvariant();
            user.PasswordHash = hasher.HashPassword(user, Password);
            AetherLocalRecoveryCredential recovery =
                AetherLocalMfaCredentialProtector
                    .GenerateRecoveryCredential(user.Id);

            database.Users.Add(user);
            database.Set<IdentityUserToken<Guid>>().Add(
                protector.CreateTotpSecretToken(
                    user.Id,
                    Enumerable.Range(1, 20)
                        .Select(value => (byte)value)
                        .ToArray()));
            database.Set<IdentityUserToken<Guid>>().Add(recovery.Token);
            Guid roleId = await database.Roles
                .Where(role => role.Name == AetherRoles.Observe)
                .Select(role => role.Id)
                .SingleAsync();
            database.Set<IdentityUserRole<Guid>>().Add(
                new() { UserId = user.Id, RoleId = roleId });
            await database.SaveChangesAsync();
            return new(user.Id, user.UserName, recovery.Code);
        }

        internal async Task<AntiforgeryEvidence> GetAntiforgeryAsync()
        {
            CapturedResponse response = await InvokeAsync(
                "GET",
                AetherLocalAuthenticationHttpAdapter.OptionsPath +
                "?returnUrl=%2Fradios");
            using JsonDocument json = JsonDocument.Parse(response.Body);
            return new(
                response.Cookie(CsrfCookieName),
                json.RootElement
                    .GetProperty("antiforgery")
                    .GetProperty("requestToken")
                    .GetString()!);
        }

        internal Task<CapturedResponse> InvokeJsonAsync(
            string path,
            string body,
            AntiforgeryEvidence csrf) =>
            InvokeAsync(
                "POST",
                path,
                body,
                csrf);

        internal async Task<CapturedResponse> InvokeAsync(
            string method,
            string pathAndQuery,
            string? body = null,
            AntiforgeryEvidence? csrf = null)
        {
            await using AsyncServiceScope scope =
                Application.Services.CreateAsyncScope();
            DefaultHttpContext context = new();
            context.RequestServices = scope.ServiceProvider;
            context.Request.Method = method;
            int queryStart = pathAndQuery.IndexOf('?');
            context.Request.Path = queryStart < 0
                ? pathAndQuery
                : pathAndQuery[..queryStart];
            context.Request.QueryString = queryStart < 0
                ? QueryString.Empty
                : new QueryString(pathAndQuery[queryStart..]);
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("radio.example.org");
            context.Response.Body = new MemoryStream();

            if (csrf is not null)
            {
                context.Request.Headers.Cookie =
                    $"{CsrfCookieName}={csrf.Cookie}";
                context.Request.Headers[AetherAntiforgery.HeaderName] =
                    csrf.RequestToken;
            }
            if (body is not null)
            {
                byte[] payload = Encoding.UTF8.GetBytes(body);
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = payload.Length;
                context.Request.Body = new MemoryStream(payload);
            }

            await pipeline(context);
            context.Response.Body.Position = 0;
            string responseBody = await new StreamReader(
                context.Response.Body,
                Encoding.UTF8).ReadToEndAsync();
            return new(
                context.Response.StatusCode,
                responseBody,
                context.Response.Headers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value
                        .Select(value => value ?? string.Empty)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase));
        }

        public async ValueTask DisposeAsync()
        {
            await Application.DisposeAsync();
            temporary.Dispose();
        }
    }

    private sealed record SeededUser(
        Guid UserId,
        string UserName,
        string RecoveryCode);

    private sealed record AntiforgeryEvidence(
        string Cookie,
        string RequestToken);

    private sealed record CapturedResponse(
        int StatusCode,
        string Body,
        IReadOnlyDictionary<string, string[]> Headers)
    {
        internal string Header(string name) =>
            Headers.TryGetValue(name, out string[]? values)
                ? string.Join(",", values)
                : string.Empty;

        internal string SetCookie(string name) =>
            Headers.TryGetValue(
                "Set-Cookie",
                out string[]? values)
                ? values.FirstOrDefault(
                    value => value.StartsWith(
                        name + "=",
                        StringComparison.Ordinal)) ?? string.Empty
                : string.Empty;

        internal string Cookie(string name)
        {
            string setCookie = SetCookie(name);
            Assert.NotEmpty(setCookie);
            int valueStart = name.Length + 1;
            int valueEnd = setCookie.IndexOf(';', valueStart);
            return valueEnd < 0
                ? setCookie[valueStart..]
                : setCookie[valueStart..valueEnd];
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-local-http-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    Path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
        }

        internal string Path { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
