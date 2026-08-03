using System.Text;
using System.Text.Json;
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
    public async Task MapPublishesOnlyElevenRateLimitedSetupRoutes()
    {
        await using AdapterHost host = await AdapterHost.CreateAsync();

        InstallationSetupOnlyHttpAdapterReport report = host.AdapterReport;
        RouteEndpoint[] endpoints =
            ((IEndpointRouteBuilder)host.Application).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .ToArray();

        Assert.Equal(11, report.EndpointPaths.Count);
        Assert.Equal(4, report.RateLimitPolicies.Count);
        Assert.Equal(11, endpoints.Length);
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
            TemporaryDirectory temporary)
        {
            Application = application;
            m_pipeline = pipeline;
            AdapterReport = adapterReport;
            Store = store;
            Bootstrap = bootstrap;
            m_temporary = temporary;
        }

        public WebApplication Application { get; }

        public InstallationSetupOnlyHttpAdapterReport AdapterReport { get; }

        public InstallationSetupStore Store { get; }

        public InstallationBootstrapTokenIssue Bootstrap { get; }

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
