using System.Text;
using System.Text.Json;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupOnlyHttpAdapterTests
{
    private const string CanonicalUrl = "https://radio.example.org";
    private const string CanonicalHost = "radio.example.org";
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MapPublishesOnlyThirteenRateLimitedSetupRoutes()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();

        InstallationSetupOnlyHttpAdapterReport report = host.AdapterReport;
        RouteEndpoint[] endpoints =
            ((IEndpointRouteBuilder)host.Application).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .ToArray();

        Assert.Equal(13, report.EndpointPaths.Count);
        Assert.Equal(4, report.RateLimitPolicies.Count);
        Assert.Equal(13, endpoints.Length);
        Assert.Equal(
            report.EndpointPaths.Order(StringComparer.Ordinal),
            endpoints
                .Select(endpoint => endpoint.RoutePattern.RawText!)
                .Order(StringComparer.Ordinal));
        Assert.All(
            endpoints,
            endpoint =>
            {
                EnableRateLimitingAttribute metadata =
                    endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()!;
                Assert.NotNull(metadata);
                Assert.Contains(metadata.PolicyName, report.RateLimitPolicies);
            });
        Assert.DoesNotContain(
            endpoints,
            endpoint =>
                endpoint.RoutePattern.RawText is "/healthz" or "/ws" or "/admin");
    }

    [Fact]
    public async Task PageReturnsRedactedStatusStrictCsrfCookieAndSecurityHeaders()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();

        CapturedResponse response = await host.InvokeAsync(PageRequest());

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("no-store, max-age=0", response.Header("Cache-Control"));
        Assert.Equal("no-referrer", response.Header("Referrer-Policy"));
        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
        Assert.Contains("default-src 'none'", response.Header("Content-Security-Policy"));
        string csrfCookie = response.Cookie(
            InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        Assert.Equal(43, csrfCookie.Length);
        string csrfSetCookie = response.SetCookie(
            InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        Assert.Contains("secure", csrfSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", csrfSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", csrfSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("httponly", csrfSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(host.Bootstrap.Token, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("bootstrapTokenHash", response.Body, StringComparison.OrdinalIgnoreCase);

        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal(
            host.Bootstrap.State.Revision,
            json.RootElement.GetProperty("status").GetProperty("revision").GetInt64());
        Assert.True(
            json.RootElement
                .GetProperty("status")
                .GetProperty("bootstrapTokenPresent")
                .GetBoolean());
    }

    [Fact]
    public async Task ClaimConsumesBootstrapAndWritesOnlyStrictCookies()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        CapturedResponse page = await host.InvokeAsync(PageRequest());
        string csrf = page.Cookie(InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        string json = JsonSerializer.Serialize(
            new
            {
                expectedRevision = host.Bootstrap.State.Revision,
                bootstrapToken = host.Bootstrap.Token
            });

        CapturedResponse claim = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.ClaimPath,
                json,
                csrfCookie: csrf,
                csrfHeader: csrf));

        Assert.Equal(StatusCodes.Status200OK, claim.StatusCode);
        string session = claim.Cookie(
            InstallationSetupHttpSecurityPolicy.SessionCookieName);
        string rotatedCsrf = claim.Cookie(
            InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        Assert.Equal(43, session.Length);
        Assert.Equal(43, rotatedCsrf.Length);
        Assert.NotEqual(csrf, rotatedCsrf);
        string sessionSetCookie = claim.SetCookie(
            InstallationSetupHttpSecurityPolicy.SessionCookieName);
        Assert.Contains("secure", sessionSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", sessionSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", sessionSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", sessionSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(host.Bootstrap.Token, claim.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(session, claim.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedCsrf, claim.Body, StringComparison.Ordinal);

        using JsonDocument responseJson = JsonDocument.Parse(claim.Body);
        long revision = responseJson.RootElement
            .GetProperty("session")
            .GetProperty("setupRevision")
            .GetInt64();
        Assert.Equal(host.Bootstrap.State.Revision + 1, revision);
        Assert.Equal(
            InstallationSetupLockMode.Claimed,
            (await host.Store.LoadAsync()).Lock.Mode);
    }

    [Fact]
    public async Task MutationRotatesSessionAndOldAuthorityFailsClosed()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        ClaimedCookies claimed = await host.ClaimAsync();
        string body = JsonSerializer.Serialize(
            new
            {
                expectedRevision = claimed.Revision,
                topology = "personalSingleStation"
            });

        CapturedResponse mutation = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.TopologyPath,
                body,
                claimed.Session,
                claimed.Csrf,
                claimed.Csrf));

        Assert.Equal(StatusCodes.Status200OK, mutation.StatusCode);
        string nextSession = mutation.Cookie(
            InstallationSetupHttpSecurityPolicy.SessionCookieName);
        string nextCsrf = mutation.Cookie(
            InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        Assert.NotEqual(claimed.Session, nextSession);
        Assert.NotEqual(claimed.Csrf, nextCsrf);
        Assert.DoesNotContain(nextSession, mutation.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(nextCsrf, mutation.Body, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(mutation.Body);
        long nextRevision = json.RootElement
            .GetProperty("session")
            .GetProperty("setupRevision")
            .GetInt64();
        Assert.Equal(claimed.Revision + 1, nextRevision);
        Assert.Equal(
            "topology",
            json.RootElement.GetProperty("mutationKind").GetString());

        CapturedResponse stale = await host.InvokeAsync(
            SessionRequest(claimed.Session, claimed.Revision));
        Assert.Equal(StatusCodes.Status401Unauthorized, stale.StatusCode);

        CapturedResponse current = await host.InvokeAsync(
            SessionRequest(nextSession, nextRevision));
        Assert.Equal(StatusCodes.Status200OK, current.StatusCode);
    }

    [Fact]
    public async Task AdministratorEnrollmentRequiresExactAuthorityReturnsSecretsOnceAndFreezesChoices()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        ClaimedCookies ready = await host.ConfigureReadyAsync();
        string password = "correct horse battery staple";
        string body = JsonSerializer.Serialize(
            new
            {
                expectedRevision = ready.Revision,
                userName = "administrator",
                displayName = "Station Administrator",
                email = "operator@example.org",
                password
            });
        string before = await File.ReadAllTextAsync(host.Store.StatePath);

        CapturedResponse rejected = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.AdministratorEnrollmentPath,
                body,
                ready.Session,
                ready.Csrf,
                "wrong-csrf-token"));

        Assert.Equal(StatusCodes.Status403Forbidden, rejected.StatusCode);
        Assert.Equal(0, host.Administrator.BeginCalls);
        Assert.Equal(0, host.Administrator.HasIdentityCalls);
        Assert.Equal(before, await File.ReadAllTextAsync(host.Store.StatePath));

        CapturedResponse enrolled = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.AdministratorEnrollmentPath,
                body,
                ready.Session,
                ready.Csrf,
                ready.Csrf));

        Assert.Equal(StatusCodes.Status200OK, enrolled.StatusCode);
        Assert.Equal("no-store, max-age=0", enrolled.Header("Cache-Control"));
        Assert.Equal(string.Empty, enrolled.Header("Set-Cookie"));
        Assert.DoesNotContain(password, enrolled.Body, StringComparison.Ordinal);
        using (JsonDocument json = JsonDocument.Parse(enrolled.Body))
        {
            Assert.Equal(
                ready.Revision,
                json.RootElement
                    .GetProperty("session")
                    .GetProperty("setupRevision")
                    .GetInt64());
            Assert.Equal(
                TestAdministratorProvisioningExecutor.SharedSecret,
                json.RootElement
                    .GetProperty("sharedSecretBase32")
                    .GetString());
            Assert.Equal(
                TestAdministratorProvisioningExecutor.RecoveryCodes,
                json.RootElement
                    .GetProperty("recoveryCodes")
                    .EnumerateArray()
                    .Select(value => value.GetString()!)
                    .ToArray());
            Assert.False(json.RootElement.GetProperty("rotated").GetBoolean());
        }
        Assert.Equal(1, host.Administrator.BeginCalls);
        Assert.True(host.Administrator.HasIdentity);
        Assert.Equal("administrator", host.Administrator.LastEnrollment?.UserName);
        Assert.Equal(
            "Station Administrator",
            host.Administrator.LastEnrollment?.DisplayName);
        Assert.Equal(
            "operator@example.org",
            host.Administrator.LastEnrollment?.Email);
        Assert.Equal(password, host.Administrator.LastEnrollment?.Password);
        Assert.StartsWith(
            "setup-administrator-enroll-",
            host.Administrator.LastEnrollment?.CorrelationId,
            StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(host.Store.StatePath));

        string mutationBody = JsonSerializer.Serialize(
            new
            {
                expectedRevision = ready.Revision,
                topology = "personalSingleStation"
            });
        CapturedResponse frozen = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.TopologyPath,
                mutationBody,
                ready.Session,
                ready.Csrf,
                ready.Csrf));

        Assert.Equal(StatusCodes.Status409Conflict, frozen.StatusCode);
        Assert.Contains("setupUnavailable", frozen.Body, StringComparison.Ordinal);
        Assert.Equal(string.Empty, frozen.Header("Set-Cookie"));
        Assert.Equal(1, host.Administrator.HasIdentityCalls);
        Assert.Equal(before, await File.ReadAllTextAsync(host.Store.StatePath));
    }

    [Fact]
    public async Task AdministratorConfirmationRejectsWrongTotpThenCompletesAndRevokesSetup()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        ClaimedCookies ready = await host.ConfigureReadyAsync();
        string enrollmentBody = JsonSerializer.Serialize(
            new
            {
                expectedRevision = ready.Revision,
                userName = "administrator",
                displayName = "Station Administrator",
                email = (string?)null,
                password = "correct horse battery staple"
            });
        CapturedResponse enrolled = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.AdministratorEnrollmentPath,
                enrollmentBody,
                ready.Session,
                ready.Csrf,
                ready.Csrf));
        Assert.Equal(StatusCodes.Status200OK, enrolled.StatusCode);
        string beforeConfirmation =
            await File.ReadAllTextAsync(host.Store.StatePath);

        string wrongBody = JsonSerializer.Serialize(
            new
            {
                expectedRevision = ready.Revision,
                totpCode = "000000"
            });
        CapturedResponse rejected = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.AdministratorConfirmationPath,
                wrongBody,
                ready.Session,
                ready.Csrf,
                ready.Csrf));

        Assert.Equal(StatusCodes.Status401Unauthorized, rejected.StatusCode);
        Assert.Contains(
            "first-local-administrator-confirmation-rejected",
            rejected.Body,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, rejected.Header("Set-Cookie"));
        Assert.Equal(
            beforeConfirmation,
            await File.ReadAllTextAsync(host.Store.StatePath));

        string correctBody = JsonSerializer.Serialize(
            new
            {
                expectedRevision = ready.Revision,
                totpCode = "123456"
            });
        CapturedResponse completed = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.AdministratorConfirmationPath,
                correctBody,
                ready.Session,
                ready.Csrf,
                ready.Csrf));

        Assert.Equal(StatusCodes.Status200OK, completed.StatusCode);
        Assert.Contains(
            "expires=Thu, 01 Jan 1970",
            completed.SetCookie(
                InstallationSetupHttpSecurityPolicy.SessionCookieName),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "expires=Thu, 01 Jan 1970",
            completed.SetCookie(
                InstallationSetupHttpSecurityPolicy.CsrfCookieName),
            StringComparison.OrdinalIgnoreCase);
        using (JsonDocument json = JsonDocument.Parse(completed.Body))
        {
            Assert.True(json.RootElement.GetProperty("completed").GetBoolean());
            Assert.Equal(
                "first-local-administrator-confirmed",
                json.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                ready.Revision + 1,
                json.RootElement
                    .GetProperty("status")
                    .GetProperty("revision")
                    .GetInt64());
            Assert.Equal(
                "administrator",
                json.RootElement
                    .GetProperty("status")
                    .GetProperty("lastCompletedStep")
                    .GetString());
        }
        InstallationSetupState state = await host.Store.LoadAsync();
        Assert.Equal(InstallationSetupLockMode.Complete, state.Lock.Mode);
        Assert.Equal(InstallationSetupStep.Administrator, state.LastCompletedStep);
        Assert.Equal(2, host.Administrator.ConfirmationCalls);
        Assert.StartsWith(
            "setup-administrator-confirm-",
            host.Administrator.LastConfirmationCorrelationId,
            StringComparison.Ordinal);

        CapturedResponse replay = await host.InvokeAsync(
            SessionRequest(ready.Session, ready.Revision));
        Assert.Equal(StatusCodes.Status401Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task RevokeRequiresExactRevisionDeletesCookiesAndInvalidatesSession()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        ClaimedCookies claimed = await host.ClaimAsync();
        string staleBody = JsonSerializer.Serialize(
            new { expectedRevision = claimed.Revision - 1 });

        CapturedResponse stale = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.RevokePath,
                staleBody,
                claimed.Session,
                claimed.Csrf,
                claimed.Csrf));
        Assert.Equal(StatusCodes.Status401Unauthorized, stale.StatusCode);

        string body = JsonSerializer.Serialize(
            new { expectedRevision = claimed.Revision });
        CapturedResponse revoked = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.RevokePath,
                body,
                claimed.Session,
                claimed.Csrf,
                claimed.Csrf));

        Assert.Equal(StatusCodes.Status204NoContent, revoked.StatusCode);
        Assert.Contains(
            "expires=Thu, 01 Jan 1970",
            revoked.SetCookie(
                InstallationSetupHttpSecurityPolicy.SessionCookieName),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "expires=Thu, 01 Jan 1970",
            revoked.SetCookie(
                InstallationSetupHttpSecurityPolicy.CsrfCookieName),
            StringComparison.OrdinalIgnoreCase);
        CapturedResponse invalid = await host.InvokeAsync(
            SessionRequest(claimed.Session, claimed.Revision));
        Assert.Equal(StatusCodes.Status401Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task OversizedClaimIsRejectedBeforeBodyRead()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        CapturedResponse page = await host.InvokeAsync(PageRequest());
        string csrf = page.Cookie(InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        TestRequest request = JsonRequest(
            "POST",
            InstallationSetupOnlyHttpAdapter.ClaimPath,
            "{}",
            csrfCookie: csrf,
            csrfHeader: csrf) with
        {
            ContentLength = 4097,
            Body = new ThrowOnReadStream()
        };
        string before = await File.ReadAllTextAsync(host.Store.StatePath);

        CapturedResponse response = await host.InvokeAsync(request);

        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        Assert.Contains("requestBodyTooLarge", response.Body, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(host.Store.StatePath));
    }

    [Fact]
    public async Task UnknownJsonFieldIsRejectedWithoutConsumingBootstrap()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        CapturedResponse page = await host.InvokeAsync(PageRequest());
        string csrf = page.Cookie(InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        string before = await File.ReadAllTextAsync(host.Store.StatePath);
        string body = JsonSerializer.Serialize(
            new
            {
                expectedRevision = host.Bootstrap.State.Revision,
                bootstrapToken = host.Bootstrap.Token,
                unexpected = true
            });

        CapturedResponse response = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.ClaimPath,
                body,
                csrfCookie: csrf,
                csrfHeader: csrf));

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("invalidRequest", response.Body, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(host.Store.StatePath));
        Assert.Equal(
            InstallationSetupLockMode.BootstrapRequired,
            (await host.Store.LoadAsync()).Lock.Mode);
    }

    [Fact]
    public async Task ForeignOriginFailsBeforeBootstrapConsumption()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        CapturedResponse page = await host.InvokeAsync(PageRequest());
        string csrf = page.Cookie(InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        string body = JsonSerializer.Serialize(
            new
            {
                expectedRevision = host.Bootstrap.State.Revision,
                bootstrapToken = host.Bootstrap.Token
            });
        string before = await File.ReadAllTextAsync(host.Store.StatePath);
        TestRequest request = JsonRequest(
            "POST",
            InstallationSetupOnlyHttpAdapter.ClaimPath,
            body,
            csrfCookie: csrf,
            csrfHeader: csrf) with
        {
            Origin = "https://attacker.example"
        };

        CapturedResponse response = await host.InvokeAsync(request);

        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        Assert.Contains("canonicalOriginRequired", response.Body, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(host.Store.StatePath));
    }

    [Fact]
    public async Task DuplicateJsonPropertyIsRejectedWithoutConsumingBootstrap()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        CapturedResponse page = await host.InvokeAsync(PageRequest());
        string csrf = page.Cookie(InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        string before = await File.ReadAllTextAsync(host.Store.StatePath);
        string body =
            $"{{\"expectedRevision\":{host.Bootstrap.State.Revision}," +
            $"\"expectedRevision\":{host.Bootstrap.State.Revision}," +
            $"\"bootstrapToken\":\"{host.Bootstrap.Token}\"}}";

        CapturedResponse response = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.ClaimPath,
                body,
                csrfCookie: csrf,
                csrfHeader: csrf));

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("invalidRequest", response.Body, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(host.Store.StatePath));
    }

    [Fact]
    public async Task IntegerEnumEncodingIsRejectedWithoutMutatingState()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        ClaimedCookies claimed = await host.ClaimAsync();
        string before = await File.ReadAllTextAsync(host.Store.StatePath);
        string body =
            $"{{\"expectedRevision\":{claimed.Revision},\"topology\":1}}";

        CapturedResponse response = await host.InvokeAsync(
            JsonRequest(
                "POST",
                InstallationSetupOnlyHttpAdapter.TopologyPath,
                body,
                claimed.Session,
                claimed.Csrf,
                claimed.Csrf));

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(before, await File.ReadAllTextAsync(host.Store.StatePath));
        CapturedResponse stillValid = await host.InvokeAsync(
            SessionRequest(claimed.Session, claimed.Revision));
        Assert.Equal(StatusCodes.Status200OK, stillValid.StatusCode);
    }

    [Fact]
    public async Task DuplicateSessionCookieIsRejected()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        ClaimedCookies claimed = await host.ClaimAsync();
        TestRequest request = SessionRequest(claimed.Session, claimed.Revision) with
        {
            CookieHeader =
                $"{InstallationSetupHttpSecurityPolicy.SessionCookieName}=" +
                $"{claimed.Session}; " +
                $"{InstallationSetupHttpSecurityPolicy.SessionCookieName}=" +
                claimed.Session
        };

        CapturedResponse response = await host.InvokeAsync(request);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("invalidRequest", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageRateLimitRejectsThirtyFirstRequestWithoutQueueing()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();

        for (int request = 0; request < 30; request++)
        {
            CapturedResponse allowed = await host.InvokeAsync(PageRequest());
            Assert.Equal(StatusCodes.Status200OK, allowed.StatusCode);
        }
        CapturedResponse rejected = await host.InvokeAsync(PageRequest());

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.StatusCode);
        Assert.Equal("no-store, max-age=0", rejected.Header("Cache-Control"));
    }

    [Fact]
    public async Task UnknownPathStillReceivesNoStoreSecurityHeaders()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();
        CapturedResponse response = await host.InvokeAsync(
            new TestRequest("GET", "/not-mapped"));

        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        Assert.Equal("no-store, max-age=0", response.Header("Cache-Control"));
        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
    }

    private static TestRequest PageRequest() =>
        new("GET", InstallationSetupOnlyHttpAdapter.PagePath)
        {
            SecFetchSite = "none",
            SecFetchMode = "navigate"
        };

    private static TestRequest SessionRequest(string session, long revision) =>
        new("GET", InstallationSetupOnlyHttpAdapter.SessionPath)
        {
            Origin = CanonicalUrl,
            SecFetchSite = "same-origin",
            SecFetchMode = "cors",
            SessionCookie = session,
            Revision = revision
        };

    private static TestRequest JsonRequest(
        string method,
        string path,
        string json,
        string? sessionCookie = null,
        string? csrfCookie = null,
        string? csrfHeader = null) =>
        new(method, path)
        {
            Origin = CanonicalUrl,
            SecFetchSite = "same-origin",
            SecFetchMode = "cors",
            ContentType = "application/json; charset=utf-8",
            ContentLength = Encoding.UTF8.GetByteCount(json),
            Body = new MemoryStream(Encoding.UTF8.GetBytes(json)),
            SessionCookie = sessionCookie,
            CsrfCookie = csrfCookie,
            CsrfHeader = csrfHeader
        };

    private sealed class AdapterHost : IAsyncDisposable
    {
        private readonly RequestDelegate m_pipeline;
        private readonly TemporaryDirectory m_temporary;

        private AdapterHost(
            WebApplication application,
            RequestDelegate pipeline,
            InstallationSetupOnlyHttpAdapterReport adapterReport,
            InstallationSetupStore store,
            InstallationBootstrapTokenIssue bootstrap,
            TestAdministratorProvisioningExecutor administrator,
            TemporaryDirectory temporary)
        {
            Application = application;
            m_pipeline = pipeline;
            AdapterReport = adapterReport;
            Store = store;
            Bootstrap = bootstrap;
            Administrator = administrator;
            m_temporary = temporary;
        }

        public WebApplication Application { get; }

        public InstallationSetupOnlyHttpAdapterReport AdapterReport { get; }

        public InstallationSetupStore Store { get; }

        public InstallationBootstrapTokenIssue Bootstrap { get; }

        public TestAdministratorProvisioningExecutor Administrator { get; }

        public static async Task<AdapterHost> CreateAsync()
        {
            TemporaryDirectory temporary = new();
            try
            {
                ManualTimeProvider time = new(Start);
                InstallationPaths paths = CreatePaths(temporary.Path);
                InstallationSetupStore store = new(paths.SetupStatePath, time);
                InstallationSetupState initial = await store.LoadOrCreateAsync();
                InstallationBootstrapTokenIssue bootstrap =
                    await new InstallationBootstrapTokenService(store, time)
                        .IssueAsync(initial.Revision);
                InstallationHostStartupPlan plan =
                    await InstallationHostStartupPlanner.CreateAsync(
                        new InstallationSetupOnlySettings
                        {
                            Enabled = true,
                            CanonicalAccessUrl = CanonicalUrl
                        },
                        new InstallationRuntimeSettings(),
                        () => paths);
                WebApplicationBuilder builder = WebApplication.CreateBuilder(
                    new WebApplicationOptions
                    {
                        Args = [],
                        EnvironmentName = Environments.Development,
                        ContentRootPath = temporary.Path
                    });
                builder.Logging.ClearProviders();
                _ = InstallationSetupOnlyProgramComposition.Configure(
                    builder,
                    plan,
                    new InstallationSetupHttpSecuritySettings(),
                    time);
                TestAdministratorProvisioningExecutor administrator = new();
                builder.Services.AddSingleton<
                    IInstallationFirstLocalAdministratorProvisioningExecutor>(
                        administrator);
                WebApplication application = builder.Build();
                InstallationSetupOnlyHttpAdapterReport adapterReport =
                    InstallationSetupOnlyHttpAdapter.Map(application);
                RequestDelegate pipeline =
                    ((IApplicationBuilder)application).Build();
                return new AdapterHost(
                    application,
                    pipeline,
                    adapterReport,
                    store,
                    bootstrap,
                    administrator,
                    temporary);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        public async Task<ClaimedCookies> ClaimAsync()
        {
            CapturedResponse page = await InvokeAsync(PageRequest());
            string csrf = page.Cookie(
                InstallationSetupHttpSecurityPolicy.CsrfCookieName);
            string body = JsonSerializer.Serialize(
                new
                {
                    expectedRevision = Bootstrap.State.Revision,
                    bootstrapToken = Bootstrap.Token
                });
            CapturedResponse claim = await InvokeAsync(
                JsonRequest(
                    "POST",
                    InstallationSetupOnlyHttpAdapter.ClaimPath,
                    body,
                    csrfCookie: csrf,
                    csrfHeader: csrf));
            Assert.Equal(StatusCodes.Status200OK, claim.StatusCode);
            using JsonDocument json = JsonDocument.Parse(claim.Body);
            return new ClaimedCookies(
                claim.Cookie(InstallationSetupHttpSecurityPolicy.SessionCookieName),
                claim.Cookie(InstallationSetupHttpSecurityPolicy.CsrfCookieName),
                json.RootElement
                    .GetProperty("session")
                    .GetProperty("setupRevision")
                    .GetInt64());
        }

        public async Task<ClaimedCookies> ConfigureReadyAsync()
        {
            ClaimedCookies current = await ClaimAsync();
            current = await MutateAsync(
                InstallationSetupOnlyHttpAdapter.TopologyPath,
                new
                {
                    expectedRevision = current.Revision,
                    topology = "personalSingleStation"
                },
                current);
            current = await MutateAsync(
                InstallationSetupOnlyHttpAdapter.PublicUrlPath,
                new
                {
                    expectedRevision = current.Revision,
                    canonicalPublicUrl = CanonicalUrl
                },
                current);
            current = await MutateAsync(
                InstallationSetupOnlyHttpAdapter.PathsPath,
                new
                {
                    expectedRevision = current.Revision,
                    configurationDirectory = Path.Combine(
                        m_temporary.Path,
                        "installed-config"),
                    stateDirectory = Path.Combine(
                        m_temporary.Path,
                        "installed-state"),
                    secretDirectory = Path.Combine(
                        m_temporary.Path,
                        "installed-secrets"),
                    releaseDirectory = Path.Combine(
                        m_temporary.Path,
                        "installed-releases"),
                    backupDirectory = Path.Combine(
                        m_temporary.Path,
                        "installed-backups"),
                    logDirectory = Path.Combine(
                        m_temporary.Path,
                        "installed-logs")
                },
                current);
            current = await MutateAsync(
                InstallationSetupOnlyHttpAdapter.UpdateChannelPath,
                new
                {
                    expectedRevision = current.Revision,
                    updateChannel = "stable",
                    pinnedRelease = (string?)null
                },
                current);
            current = await MutateAsync(
                InstallationSetupOnlyHttpAdapter.BackupPath,
                new
                {
                    expectedRevision = current.Revision,
                    confirmed = true
                },
                current);
            return await MutateAsync(
                InstallationSetupOnlyHttpAdapter.TransmitSupportPath,
                new
                {
                    expectedRevision = current.Revision,
                    installTransmitSupport = false,
                    acknowledgedInstallationDoesNotEnableTransmit = true
                },
                current);
        }

        private async Task<ClaimedCookies> MutateAsync(
            string path,
            object body,
            ClaimedCookies current)
        {
            CapturedResponse response = await InvokeAsync(
                JsonRequest(
                    "POST",
                    path,
                    JsonSerializer.Serialize(body),
                    current.Session,
                    current.Csrf,
                    current.Csrf));
            Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(response.Body);
            return new(
                response.Cookie(
                    InstallationSetupHttpSecurityPolicy.SessionCookieName),
                response.Cookie(
                    InstallationSetupHttpSecurityPolicy.CsrfCookieName),
                json.RootElement
                    .GetProperty("session")
                    .GetProperty("setupRevision")
                    .GetInt64());
        }

        public async Task<CapturedResponse> InvokeAsync(TestRequest request)
        {
            DefaultHttpContext context = new();
            context.RequestServices = Application.Services;
            context.Request.Method = request.Method;
            context.Request.Path = request.Path;
            context.Request.Scheme = "https";
            context.Request.Host = new HostString(CanonicalHost);
            context.Request.Headers["Sec-Fetch-Site"] = request.SecFetchSite;
            context.Request.Headers["Sec-Fetch-Mode"] = request.SecFetchMode;
            if (request.Origin is not null)
            {
                context.Request.Headers.Origin = request.Origin;
            }
            if (request.Revision is long revision)
            {
                context.Request.Headers[
                    InstallationSetupOnlyHttpAdapter.RevisionHeaderName] =
                    revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            List<string> cookies = [];
            if (request.SessionCookie is not null)
            {
                cookies.Add(
                    $"{InstallationSetupHttpSecurityPolicy.SessionCookieName}=" +
                    request.SessionCookie);
            }
            if (request.CsrfCookie is not null)
            {
                cookies.Add(
                    $"{InstallationSetupHttpSecurityPolicy.CsrfCookieName}=" +
                    request.CsrfCookie);
            }
            if (request.CookieHeader is not null)
            {
                context.Request.Headers.Cookie = request.CookieHeader;
            }
            else if (cookies.Count > 0)
            {
                context.Request.Headers.Cookie = string.Join("; ", cookies);
            }
            if (request.CsrfHeader is not null)
            {
                context.Request.Headers[
                    InstallationSetupHttpSecurityPolicy.CsrfHeaderName] =
                    request.CsrfHeader;
            }
            context.Request.ContentType = request.ContentType;
            context.Request.ContentLength = request.ContentLength;
            context.Request.Body = request.Body ?? Stream.Null;
            context.Response.Body = new MemoryStream();

            await m_pipeline(context);

            context.Response.Body.Position = 0;
            string body = await new StreamReader(
                context.Response.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true).ReadToEndAsync();
            return new CapturedResponse(
                context.Response.StatusCode,
                context.Response.Headers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value
                        .Select(value => value ?? string.Empty)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                body);
        }

        public async ValueTask DisposeAsync()
        {
            await Application.DisposeAsync();
            m_temporary.Dispose();
        }
    }

    private sealed class TestAdministratorProvisioningExecutor
        : IInstallationFirstLocalAdministratorProvisioningExecutor,
          IInstallationFirstAdministratorVerifier
    {
        internal static readonly Guid UserId =
            Guid.Parse("83a74f35-9613-4659-bdf9-7372fc72a12f");
        internal const string SharedSecret =
            "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
        internal static readonly IReadOnlyList<string> RecoveryCodes =
            ["ALPHA-BRAVO-CHARLIE", "DELTA-ECHO-FOXTROT"];

        private InstallationFirstAdministratorVerificationRequest? m_setup;

        public bool HasIdentity { get; private set; }

        public int BeginCalls { get; private set; }

        public int ConfirmationCalls { get; private set; }

        public int HasIdentityCalls { get; private set; }

        public InstallationFirstLocalAdministratorEnrollment? LastEnrollment
        {
            get;
            private set;
        }

        public string? LastConfirmationCorrelationId { get; private set; }

        public Task<InstallationFirstLocalAdministratorEnrollmentIssue> BeginAsync(
            InstallationFirstAdministratorVerificationRequest setup,
            InstallationFirstLocalAdministratorEnrollment enrollment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            HasIdentity = true;
            m_setup = setup;
            LastEnrollment = enrollment;
            return Task.FromResult(
                new InstallationFirstLocalAdministratorEnrollmentIssue(
                    UserId,
                    Start,
                    SharedSecret,
                    RecoveryCodes,
                    rotated: BeginCalls > 1));
        }

        public async Task<InstallationFirstLocalAdministratorCompletionResult>
            ConfirmAndCompleteAsync(
                InstallationFirstAdministratorHandoff handoff,
                long expectedRevision,
                InstallationFirstAdministratorVerificationRequest setup,
                string? totpCode,
                string correlationId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmationCalls++;
            LastConfirmationCorrelationId = correlationId;
            if (!HasIdentity || m_setup != setup || totpCode != "123456")
            {
                return new(
                    new InstallationFirstLocalAdministratorConfirmationResult(
                        Succeeded: false,
                        "first-local-administrator-confirmation-rejected",
                        UserId: null,
                        MutationAttempted: false),
                    CompletedState: null);
            }

            InstallationSetupState completed = await handoff.CompleteAsync(
                expectedRevision,
                this,
                cancellationToken);
            return new(
                new InstallationFirstLocalAdministratorConfirmationResult(
                    Succeeded: true,
                    "first-local-administrator-confirmed",
                    UserId,
                    MutationAttempted: true),
                completed);
        }

        public Task<bool> HasIdentityAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HasIdentityCalls++;
            return Task.FromResult(HasIdentity);
        }

        public Task<InstallationFirstAdministratorEvidence> VerifyAsync(
            InstallationFirstAdministratorVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new InstallationFirstAdministratorEvidence(
                    request.SetupSchemaVersion,
                    request.SetupRevision,
                    request.SetupCreatedAt,
                    request.Topology,
                    request.CanonicalPublicUrl,
                    $"local:{UserId:D}",
                    Start,
                    IsEnabled: true,
                    Roles: [AetherRoles.Observe, AetherRoles.Admin]));
        }
    }

    private sealed record TestRequest(string Method, string Path)
    {
        public string? Origin { get; init; }
        public string? SecFetchSite { get; init; }
        public string? SecFetchMode { get; init; }
        public string? ContentType { get; init; }
        public long? ContentLength { get; init; }
        public Stream? Body { get; init; }
        public string? SessionCookie { get; init; }
        public string? CsrfCookie { get; init; }
        public string? CsrfHeader { get; init; }
        public string? CookieHeader { get; init; }
        public long? Revision { get; init; }
    }

    private sealed record ClaimedCookies(
        string Session,
        string Csrf,
        long Revision);

    private sealed record CapturedResponse(
        int StatusCode,
        IReadOnlyDictionary<string, string[]> Headers,
        string Body)
    {
        public string Header(string name) =>
            Headers.TryGetValue(name, out string[]? values)
                ? string.Join(",", values)
                : string.Empty;

        public string SetCookie(string name) =>
            Headers.TryGetValue("Set-Cookie", out string[]? values)
                ? values.First(value =>
                    value.StartsWith(name + "=", StringComparison.Ordinal))
                : throw new InvalidOperationException(
                    $"Cookie '{name}' was not written.");

        public string Cookie(string name)
        {
            string value = SetCookie(name);
            int start = name.Length + 1;
            int end = value.IndexOf(';', start);
            return end < 0
                ? value[start..]
                : value[start..end];
        }
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The rejected body was read.");
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(
                new InvalidOperationException("The rejected body was read."));
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static InstallationPaths CreatePaths(string root) =>
        new(
            Path.Combine(root, "config"),
            Path.Combine(root, "state"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "releases"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "logs"));

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aethersdr-setup-http-adapter-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
