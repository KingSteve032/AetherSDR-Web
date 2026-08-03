using System.Text;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupBrowserShellTests
{
    private const string CanonicalUrl = "https://radio.example.org";
    private const string CanonicalHost = "radio.example.org";
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NavigationReturnsEncodedRedactedStateAndStrictCsrfCookie()
    {
        await using BrowserHost host = await BrowserHost.CreateAsync();
        string before = await File.ReadAllTextAsync(host.Store.StatePath);

        CapturedResponse response = await host.InvokeAsync(
            new TestRequest("GET", InstallationSetupBrowserShell.PagePath)
            {
                SecFetchSite = "none",
                SecFetchMode = "navigate"
            });
        string after = await File.ReadAllTextAsync(host.Store.StatePath);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.ContentType);
        Assert.Contains("data-setup-revision=\"1\"", response.Body);
        Assert.Contains("data-setup-lock-mode=\"bootstrapRequired\"", response.Body);
        Assert.Contains("data-bootstrap-token-present=\"true\"", response.Body);
        Assert.Contains("data-canonical-access-url=\"https://radio.example.org\"", response.Body);
        Assert.Contains("/setup/assets/setup.css", response.Body);
        Assert.Contains("/setup/assets/setup.js", response.Body);
        Assert.DoesNotContain(host.Bootstrap.Token, response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("BootstrapTokenHash", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", response.Body, StringComparison.Ordinal);
        string csrfCookie = response.SetCookie(
            InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        Assert.Contains("Secure", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpOnly", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store, max-age=0", response.Header("Cache-Control"));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task FixedAssetsRequireCanonicalHttpsAndSameOriginFetchMetadata()
    {
        await using BrowserHost host = await BrowserHost.CreateAsync();

        CapturedResponse stylesheet = await host.InvokeAsync(
            AssetRequest(InstallationSetupBrowserShell.StylePath));
        CapturedResponse script = await host.InvokeAsync(
            AssetRequest(InstallationSetupBrowserShell.ScriptPath));
        CapturedResponse cleartext = await host.InvokeAsync(
            AssetRequest(InstallationSetupBrowserShell.StylePath) with
            {
                Scheme = "http"
            });
        CapturedResponse foreignHost = await host.InvokeAsync(
            AssetRequest(InstallationSetupBrowserShell.ScriptPath) with
            {
                Host = "attacker.example"
            });
        CapturedResponse query = await host.InvokeAsync(
            AssetRequest(InstallationSetupBrowserShell.ScriptPath) with
            {
                QueryString = "?token=forbidden"
            });

        Assert.Equal(StatusCodes.Status200OK, stylesheet.StatusCode);
        Assert.Equal("text/css; charset=utf-8", stylesheet.ContentType);
        Assert.Contains(".setup-step", stylesheet.Body, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status200OK, script.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", script.ContentType);
        Assert.Contains("credentials: \"same-origin\"", script.Body, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status403Forbidden, cleartext.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, foreignHost.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, query.StatusCode);
    }

    [Fact]
    public async Task BrowserShellAddsThreeRoutesWithoutChangingJsonAdapterRoutes()
    {
        await using BrowserHost host = await BrowserHost.CreateAsync();
        IReadOnlyList<string> endpoints =
            ((IEndpointRouteBuilder)host.Application).DataSources
                .SelectMany(source => source.Endpoints)
                .Select(endpoint => endpoint.DisplayName ?? string.Empty)
                .ToArray();

        Assert.Equal(11, host.AdapterReport.EndpointPaths.Count);
        Assert.Equal(
            new[]
            {
                InstallationSetupBrowserShell.PagePath,
                InstallationSetupBrowserShell.StylePath,
                InstallationSetupBrowserShell.ScriptPath
            },
            host.ShellReport.EndpointPaths);
        Assert.Equal(14, endpoints.Count);
        Assert.Contains(
            endpoints,
            name => name.Contains(
                InstallationSetupOnlyHttpAdapter.ClaimPath,
                StringComparison.Ordinal));
        Assert.Contains(
            endpoints,
            name => name.Contains(
                InstallationSetupBrowserShell.PagePath,
                StringComparison.Ordinal));
    }

    [Fact]
    public void ProgramMapsShellBeforeNormalSettingsAndAssetsAvoidBrowserStorage()
    {
        string root = FindRepositoryRoot();
        string program = File.ReadAllText(Path.Combine(
            root,
            "prototypes",
            "web-client",
            "Program.cs"));
        string script = File.ReadAllText(Path.Combine(
            root,
            "prototypes",
            "web-client",
            "wwwroot",
            "setup.js"));
        int shell = program.IndexOf(
            "InstallationSetupBrowserShell.Map",
            StringComparison.Ordinal);
        int auth = program.IndexOf("AuthSettings authSettings", StringComparison.Ordinal);

        Assert.True(shell >= 0);
        Assert.True(auth > shell);
        Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("bootstrapToken=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionToken=", script, StringComparison.Ordinal);
    }

    private static TestRequest AssetRequest(string path) =>
        new("GET", path)
        {
            SecFetchSite = "same-origin",
            SecFetchMode = "no-cors"
        };

    private sealed class BrowserHost : IAsyncDisposable
    {
        private readonly RequestDelegate m_pipeline;
        private readonly TemporaryDirectory m_temporary;

        private BrowserHost(
            WebApplication application,
            RequestDelegate pipeline,
            InstallationSetupOnlyHttpAdapterReport adapterReport,
            InstallationSetupBrowserShellReport shellReport,
            InstallationSetupStore store,
            InstallationBootstrapTokenIssue bootstrap,
            TemporaryDirectory temporary)
        {
            Application = application;
            m_pipeline = pipeline;
            AdapterReport = adapterReport;
            ShellReport = shellReport;
            Store = store;
            Bootstrap = bootstrap;
            m_temporary = temporary;
        }

        public WebApplication Application { get; }
        public InstallationSetupOnlyHttpAdapterReport AdapterReport { get; }
        public InstallationSetupBrowserShellReport ShellReport { get; }
        public InstallationSetupStore Store { get; }
        public InstallationBootstrapTokenIssue Bootstrap { get; }

        public static async Task<BrowserHost> CreateAsync()
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
                string contentRoot = Path.Combine(
                    FindRepositoryRoot(),
                    "prototypes",
                    "web-client");
                WebApplicationBuilder builder = WebApplication.CreateBuilder(
                    new WebApplicationOptions
                    {
                        Args = [],
                        EnvironmentName = Environments.Development,
                        ContentRootPath = contentRoot,
                        WebRootPath = "wwwroot"
                    });
                builder.Logging.ClearProviders();
                _ = InstallationSetupOnlyProgramComposition.Configure(
                    builder,
                    plan,
                    new InstallationSetupHttpSecuritySettings(),
                    time);
                WebApplication application = builder.Build();
                InstallationSetupOnlyHttpAdapterReport adapter =
                    InstallationSetupOnlyHttpAdapter.Map(application);
                InstallationSetupBrowserShellReport shell =
                    InstallationSetupBrowserShell.Map(application);
                RequestDelegate pipeline =
                    ((IApplicationBuilder)application).Build();
                return new BrowserHost(
                    application,
                    pipeline,
                    adapter,
                    shell,
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

        public async Task<CapturedResponse> InvokeAsync(TestRequest request)
        {
            DefaultHttpContext context = new();
            context.RequestServices = Application.Services;
            context.Request.Method = request.Method;
            context.Request.Path = request.Path;
            context.Request.QueryString = new QueryString(request.QueryString ?? string.Empty);
            context.Request.Scheme = request.Scheme;
            context.Request.Host = new HostString(request.Host);
            context.Request.Headers["Sec-Fetch-Site"] = request.SecFetchSite;
            context.Request.Headers["Sec-Fetch-Mode"] = request.SecFetchMode;
            context.Request.Headers.Accept = request.Accept;
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
                context.Response.ContentType ?? string.Empty,
                context.Response.Headers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(value => value ?? string.Empty).ToArray(),
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
        public string Scheme { get; init; } = "https";
        public string Host { get; init; } = CanonicalHost;
        public string? QueryString { get; init; }
        public string SecFetchSite { get; init; } = "same-origin";
        public string SecFetchMode { get; init; } = "navigate";
        public string Accept { get; init; } = "text/html";
    }

    private sealed record CapturedResponse(
        int StatusCode,
        string ContentType,
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
    }

    private static InstallationPaths CreatePaths(string root) =>
        new(
            Path.Combine(root, "config"),
            Path.Combine(root, "state"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "releases"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "logs"));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }

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
                "aethersdr-setup-browser-shell-tests",
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
