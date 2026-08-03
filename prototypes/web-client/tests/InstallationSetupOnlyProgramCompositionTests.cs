using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupOnlyProgramCompositionTests
{
    private const string CanonicalUrl = "https://radio.example.org";

    [Fact]
    public async Task ConfigureBuildsOnlySetupServicesAndNoEndpoints()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = CreatePaths(temporary.Path);
        InstallationSetupStore store = new(paths.SetupStatePath);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        string before = await File.ReadAllTextAsync(paths.SetupStatePath);
        InstallationHostStartupPlan plan =
            await InstallationHostStartupPlanner.CreateAsync(
                EnabledSetupOnly(),
                new InstallationRuntimeSettings(),
                () => paths);
        WebApplicationBuilder builder = CreateBuilder(temporary.Path);

        InstallationSetupOnlyProgramCompositionReport report =
            InstallationSetupOnlyProgramComposition.Configure(
                builder,
                plan,
                new InstallationSetupHttpSecuritySettings());

        Assert.Equal(CanonicalUrl, report.CanonicalAccessUrl);
        Assert.Equal(paths.SetupStatePath, report.SetupStatePath);
        Assert.Equal(initial.Revision, report.SetupRevision);
        Assert.DoesNotContain(builder.Services, IsNormalRuntimeService);

        await using WebApplication app = builder.Build();
        InstallationSetupCenterApplication application =
            app.Services.GetRequiredService<InstallationSetupCenterApplication>();
        InstallationSetupOnlyProgramCompositionReport resolvedReport =
            app.Services.GetRequiredService<
                InstallationSetupOnlyProgramCompositionReport>();
        InstallationSetupHttpSecurityPolicy security =
            app.Services.GetRequiredService<InstallationSetupHttpSecurityPolicy>();
        InstallationSetupOnlyIdentity identity =
            app.Services.GetRequiredService<InstallationSetupOnlyIdentity>();
        InstallationSetupOnlyLifecycleEvaluator lifecycle =
            app.Services.GetRequiredService<InstallationSetupOnlyLifecycleEvaluator>();
        IHostedService lifecycleMonitor = app.Services
            .GetServices<IHostedService>()
            .Single(service => service is InstallationSetupOnlyLifecycleMonitor);
        IReadOnlyList<Endpoint> endpoints =
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .ToArray();

        Assert.NotNull(application);
        Assert.Equal(report, resolvedReport);
        Assert.Equal(CanonicalUrl, security.Contract.CanonicalOrigin);
        Assert.Equal(plan.SetupOnlyIdentity, identity);
        Assert.NotNull(lifecycle);
        Assert.IsType<InstallationSetupOnlyLifecycleMonitor>(lifecycleMonitor);
        Assert.Empty(endpoints);
        Assert.Equal(before, await File.ReadAllTextAsync(paths.SetupStatePath));
        Assert.Equal(initial, await store.LoadAsync());
    }

    [Fact]
    public async Task ConfigureRejectsNormalRuntimePlan()
    {
        using TemporaryDirectory temporary = new();
        WebApplicationBuilder builder = CreateBuilder(temporary.Path);
        InstallationHostStartupPlan legacy =
            await InstallationHostStartupPlanner.CreateAsync(
                new InstallationSetupOnlySettings(),
                new InstallationRuntimeSettings(),
                () => throw new InvalidOperationException());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => InstallationSetupOnlyProgramComposition.Configure(
                builder,
                legacy,
                new InstallationSetupHttpSecuritySettings()));

        Assert.Contains("eligible setup-only startup plan", exception.Message);
    }

    [Fact]
    public async Task ConfigureRejectsPreexistingRadioService()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = CreatePaths(temporary.Path);
        _ = await new InstallationSetupStore(paths.SetupStatePath)
            .LoadOrCreateAsync();
        InstallationHostStartupPlan plan =
            await InstallationHostStartupPlanner.CreateAsync(
                EnabledSetupOnly(),
                new InstallationRuntimeSettings(),
                () => paths);
        WebApplicationBuilder builder = CreateBuilder(temporary.Path);
        builder.Services.AddSingleton(new RadioSettings());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => InstallationSetupOnlyProgramComposition.Configure(
                builder,
                plan,
                new InstallationSetupHttpSecuritySettings()));

        Assert.Contains("before authentication, radio", exception.Message);
    }

    [Fact]
    public async Task RealProgramMapsFailClosedSetupRouteWithoutReadingNormalSettings()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = CreatePaths(temporary.Path);
        _ = await new InstallationSetupStore(paths.SetupStatePath)
            .LoadOrCreateAsync();
        int port = ReserveLoopbackPort();
        string root = FindRepositoryRoot();
        string assemblyPath = Path.Combine(
            root,
            "prototypes",
            "web-client",
            "bin",
            "Release",
            "net10.0",
            "AetherSDR.Web.dll");
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = Path.Combine(root, "prototypes", "web-client"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["InstallationSetupOnly__Enabled"] = "true";
        startInfo.Environment["InstallationSetupOnly__CanonicalAccessUrl"] =
            CanonicalUrl;
        startInfo.Environment["InstallationRuntime__Enabled"] = "false";
        startInfo.Environment["InstallationPaths__ConfigurationDirectory"] =
            paths.ConfigurationDirectory;
        startInfo.Environment["InstallationPaths__StateDirectory"] =
            paths.StateDirectory;
        startInfo.Environment["InstallationPaths__SecretDirectory"] =
            paths.SecretDirectory;
        startInfo.Environment["InstallationPaths__ReleaseDirectory"] =
            paths.ReleaseDirectory;
        startInfo.Environment["InstallationPaths__BackupDirectory"] =
            paths.BackupDirectory;
        startInfo.Environment["InstallationPaths__LogDirectory"] =
            paths.LogDirectory;
        startInfo.Environment["Auth__Mode"] = "invalid-normal-auth";
        startInfo.Environment["Radio__Mode"] = "invalid-normal-radio";

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Setup-only process did not start.");
        try
        {
            using HttpClient client = new()
            {
                Timeout = TimeSpan.FromMilliseconds(500)
            };
            HttpResponseMessage? response = null;
            for (int attempt = 0; attempt < 50 && response is null; attempt++)
            {
                if (process.HasExited)
                {
                    break;
                }
                try
                {
                    using HttpRequestMessage request = new(
                        HttpMethod.Get,
                        $"http://127.0.0.1:{port}/setup");
                    request.Headers.TryAddWithoutValidation(
                        "Sec-Fetch-Site",
                        "none");
                    request.Headers.TryAddWithoutValidation(
                        "Sec-Fetch-Mode",
                        "navigate");
                    request.Headers.Host = "radio.example.org";
                    response = await client.SendAsync(request);
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(100);
                }
                catch (TaskCanceledException)
                {
                    await Task.Delay(100);
                }
            }

            if (response is null)
            {
                string failedOutput = await process.StandardOutput.ReadToEndAsync();
                string failedError = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"Setup-only process did not become reachable. {failedOutput} {failedError}");
            }
            using (response)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal(
                    "no-store, max-age=0",
                    response.Headers.CacheControl?.ToString());
                string body = await response.Content.ReadAsStringAsync();
                Assert.Contains("httpsRequired", body, StringComparison.Ordinal);
            }

            using HttpRequestMessage shellRequest = new(
                HttpMethod.Get,
                $"http://127.0.0.1:{port}" +
                InstallationSetupBrowserShell.PagePath);
            shellRequest.Headers.TryAddWithoutValidation(
                "Sec-Fetch-Site",
                "none");
            shellRequest.Headers.TryAddWithoutValidation(
                "Sec-Fetch-Mode",
                "navigate");
            shellRequest.Headers.Host = "radio.example.org";
            using HttpResponseMessage shellResponse =
                await client.SendAsync(shellRequest);
            Assert.Equal(HttpStatusCode.Forbidden, shellResponse.StatusCode);
            Assert.Equal(
                "no-store, max-age=0",
                shellResponse.Headers.CacheControl?.ToString());
            Assert.Contains(
                "setup page request was rejected",
                await shellResponse.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        Assert.DoesNotContain("invalid-normal-auth", output + error, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-normal-radio", output + error, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultConfigurationKeepsSetupOnlyDisabled()
    {
        string root = FindRepositoryRoot();
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(
                root,
                "prototypes",
                "web-client",
                "appsettings.json")));
        JsonElement setupOnly =
            document.RootElement.GetProperty("InstallationSetupOnly");
        JsonElement security =
            document.RootElement.GetProperty("InstallationSetupHttpSecurity");

        Assert.False(setupOnly.GetProperty("Enabled").GetBoolean());
        Assert.Equal(
            string.Empty,
            setupOnly.GetProperty("CanonicalAccessUrl").GetString());
        Assert.Equal(
            4096,
            security.GetProperty("BootstrapClaimMaximumBodyBytes").GetInt32());
        Assert.Equal(
            16384,
            security.GetProperty("SessionMutationMaximumBodyBytes").GetInt32());
    }

    private static int ReserveLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static WebApplicationBuilder CreateBuilder(string contentRoot)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                Args = [],
                EnvironmentName = Environments.Development,
                ContentRootPath = contentRoot
            });
        builder.Logging.ClearProviders();
        return builder;
    }

    private static InstallationSetupOnlySettings EnabledSetupOnly() =>
        new()
        {
            Enabled = true,
            CanonicalAccessUrl = CanonicalUrl
        };

    private static bool IsNormalRuntimeService(ServiceDescriptor descriptor)
    {
        string serviceNamespace = descriptor.ServiceType.Namespace ?? string.Empty;
        string implementationNamespace =
            descriptor.ImplementationType?.Namespace ?? string.Empty;
        return IsNormalRuntimeNamespace(serviceNamespace) ||
            IsNormalRuntimeNamespace(implementationNamespace);
    }

    private static bool IsNormalRuntimeNamespace(string value) =>
        value.StartsWith("AetherSDR.Web.Auth", StringComparison.Ordinal) ||
        value.StartsWith("AetherSDR.Web.Radio", StringComparison.Ordinal) ||
        value.StartsWith("AetherRemote", StringComparison.Ordinal) ||
        value.StartsWith("AetherSDR.TxWatchdog", StringComparison.Ordinal);

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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aethersdr-setup-only-composition-tests",
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
