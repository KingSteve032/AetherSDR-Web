using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupAcceptanceTests
{
    private const string CanonicalUrl = "https://radio.example.org";
    private const string CanonicalHost = "radio.example.org";
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshSetupRecoversAcrossRestartCompletesAndTransitionsToRuntime()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationPaths installationPaths = CreateInstallationPaths(temporary.Path);
        InstallationSetupStore store =
            new(installationPaths.SetupStatePath, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue bootstrap =
            await new InstallationBootstrapTokenService(store, time)
                .IssueAsync(initial.Revision);
        InstallationHostStartupPlan startupPlan =
            await InstallationHostStartupPlanner.CreateAsync(
                EnabledSetupOnly(),
                new InstallationRuntimeSettings(),
                () => installationPaths);
        InstallationSetupOnlyLifecycleEvaluator lifecycle = new(
            store,
            startupPlan.SetupOnlyIdentity ??
                throw new InvalidOperationException("Missing setup identity."));

        string oldSessionToken;
        long configuredRevision;
        using (InstallationSetupCenterApplication first =
            CreateApplication(store, time))
        {
            InstallationSetupCenterPageResult page =
                await first.ReadPageAsync(PageRequest());
            InstallationSetupCenterClaimResult claim =
                await first.ClaimAsync(
                    ClaimRequest(page.Csrf.Token),
                    bootstrap.State.Revision,
                    bootstrap.Token);
            InstallationSetupCenterMutationResult topology =
                await MutateAsync(
                    first,
                    claim.Session,
                    claim.Csrf,
                    new InstallationSetupCenterTopologyMutation(
                        claim.Session.SetupRevision,
                        InstallationTopologyKind.PersonalSingleStation));
            InstallationSetupCenterMutationResult publicUrl =
                await MutateAsync(
                    first,
                    topology.Session,
                    topology.Csrf,
                    new InstallationSetupCenterPublicUrlMutation(
                        topology.Session.SetupRevision,
                        CanonicalUrl));
            InstallationSetupCenterMutationResult paths =
                await MutateAsync(
                    first,
                    publicUrl.Session,
                    publicUrl.Csrf,
                    new InstallationSetupCenterPathsMutation(
                        publicUrl.Session.SetupRevision,
                        installationPaths));
            InstallationSetupCenterMutationResult channel =
                await MutateAsync(
                    first,
                    paths.Session,
                    paths.Csrf,
                    new InstallationSetupCenterUpdateChannelMutation(
                        paths.Session.SetupRevision,
                        InstallationUpdateChannel.Stable));
            InstallationSetupCenterMutationResult backup =
                await MutateAsync(
                    first,
                    channel.Session,
                    channel.Csrf,
                    new InstallationSetupCenterBackupConfirmationMutation(
                        channel.Session.SetupRevision));
            InstallationSetupCenterMutationResult transmit =
                await MutateAsync(
                    first,
                    backup.Session,
                    backup.Csrf,
                    new InstallationSetupCenterTransmitSupportMutation(
                        backup.Session.SetupRevision,
                        installTransmitSupport: false));
            InstallationSetupCenterPreflightResult preflight =
                await first.ReadPreflightAsync(
                    SessionReadRequest(),
                    transmit.Session.Token,
                    transmit.Session.SetupRevision);

            Assert.True(preflight.Preflight.ReadyForInstallerReview);
            Assert.Equal(
                InstallationSetupStep.TransmitSupport,
                preflight.Status.LastCompletedStep);
            oldSessionToken = transmit.Session.Token;
            configuredRevision = transmit.Session.SetupRevision;
        }

        using InstallationSetupCenterApplication restarted =
            CreateApplication(store, time);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => restarted.ReadSessionAsync(
                SessionReadRequest(),
                oldSessionToken,
                configuredRevision));

        InstallationBootstrapTokenIssue recovery =
            await new InstallationBootstrapTokenService(store, time)
                .IssueAsync(configuredRevision);
        Assert.Equal(
            InstallationSetupStep.TransmitSupport,
            recovery.State.LastCompletedStep);
        Assert.Equal(
            InstallationSetupLockMode.BootstrapRequired,
            recovery.State.Lock.Mode);
        InstallationSetupCenterPageResult recoveryPage =
            await restarted.ReadPageAsync(PageRequest());
        InstallationSetupCenterClaimResult reclaimed =
            await restarted.ClaimAsync(
                ClaimRequest(recoveryPage.Csrf.Token),
                recovery.State.Revision,
                recovery.Token);
        InstallationSetupCenterPreflightResult resumedPreflight =
            await restarted.ReadPreflightAsync(
                SessionReadRequest(),
                reclaimed.Session.Token,
                reclaimed.Session.SetupRevision);

        Assert.True(resumedPreflight.Preflight.ReadyForInstallerReview);
        Assert.Equal(
            InstallationSetupStep.TransmitSupport,
            reclaimed.Status.LastCompletedStep);

        InstallationSetupState completed =
            await new InstallationFirstAdministratorHandoff(store, time)
                .CompleteAsync(
                    reclaimed.Session.SetupRevision,
                    new Verifier(request =>
                        Task.FromResult(
                            new InstallationFirstAdministratorEvidence(
                                request.SetupSchemaVersion,
                                request.SetupRevision,
                                request.SetupCreatedAt,
                                request.Topology,
                                request.CanonicalPublicUrl,
                                "local-admin-acceptance",
                                time.GetUtcNow(),
                                IsEnabled: true,
                                Roles: [AetherRoles.Admin, AetherRoles.Observe]))));

        InstallationSetupOnlyLifecycleDecision stop =
            await lifecycle.EvaluateAsync();
        Assert.Equal(
            InstallationSetupOnlyLifecycleStopReason.SetupComplete,
            stop.StopReason);
        Assert.Equal(InstallationSetupLockMode.Complete, completed.Lock.Mode);
        Assert.Equal(
            InstallationSetupStep.Administrator,
            completed.LastCompletedStep);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InstallationHostStartupPlanner.CreateAsync(
                EnabledSetupOnly(),
                new InstallationRuntimeSettings(),
                () => installationPaths));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new InstallationBootstrapTokenService(store, time)
                .IssueAsync(completed.Revision));

        InstallationRuntimeSettings runtimeSettings = new()
        {
            Enabled = true,
            SetupRevision = completed.Revision,
            RuntimeRole = InstallationRuntimeRole.Gateway,
            Topology = completed.Topology ??
                throw new InvalidOperationException("Missing completed topology."),
            CanonicalPublicUrl = completed.CanonicalPublicUrl,
            InstallTransmitSupport = completed.InstallTransmitSupport
        };
        InstallationHostStartupPlan runtimePlan =
            await InstallationHostStartupPlanner.CreateAsync(
                new InstallationSetupOnlySettings(),
                runtimeSettings,
                () => installationPaths);

        Assert.Equal(InstallationHostStartupMode.NormalRuntime, runtimePlan.Mode);
        Assert.True(runtimePlan.NormalRuntimeReady);
        Assert.Equal(completed.Revision, runtimePlan.RuntimeReadiness!.SetupRevision);
    }

    [Fact]
    public async Task RealSetupOnlyProcessStopsAfterTrustedCompletion()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = CreateInstallationPaths(temporary.Path);
        InstallationSetupStore store = new(paths.SetupStatePath);
        InstallationSetupState configured =
            await ConfigureThroughTransmitAsync(store, paths);
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
            await WaitUntilReachableAsync(process, port);
            _ = await new InstallationFirstAdministratorHandoff(store)
                .CompleteAsync(
                    configured.Revision,
                    new Verifier(request =>
                        Task.FromResult(
                            new InstallationFirstAdministratorEvidence(
                                request.SetupSchemaVersion,
                                request.SetupRevision,
                                request.SetupCreatedAt,
                                request.Topology,
                                request.CanonicalPublicUrl,
                                "local-admin-process-acceptance",
                                request.SetupCreatedAt,
                                IsEnabled: true,
                                Roles: [AetherRoles.Admin]))));

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        Assert.Contains("SetupComplete", output + error, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-normal-auth", output + error, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-normal-radio", output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionInstallerPlanRejectsNoncanonicalLinuxPathsBeforeStateRead()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = CreateInstallationPaths(temporary.Path);
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
        startInfo.ArgumentList.Add("--installation-installer-plan");
        startInfo.ArgumentList.Add("--installation-architecture");
        startInfo.ArgumentList.Add("linux-x64");
        startInfo.ArgumentList.Add("--installation-reverse-proxy");
        startInfo.ArgumentList.Add("lan-internal-certificate");
        startInfo.ArgumentList.Add("--installation-release");
        startInfo.ArgumentList.Add("aethersdr-8.8.0-rc.2");
        startInfo.ArgumentList.Add("--installation-firewall");
        startInfo.ArgumentList.Add("guidance");
        startInfo.ArgumentList.Add("--installation-authentication");
        startInfo.ArgumentList.Add("local");
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
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

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Installer plan process did not start.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("\"outcome\":\"rejected\"", output, StringComparison.Ordinal);
        Assert.Contains(
            "\"code\":\"noncanonical-installation-paths\"",
            output,
            StringComparison.Ordinal);
        Assert.Contains("\"mutationAttempted\":false", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.SetupStatePath));
    }

    [Fact]
    public void CleanHostAcceptanceToolIsTlsStrictAndReadOnly()
    {
        string root = FindRepositoryRoot();
        string scriptPath = Path.Combine(
            root,
            "prototypes",
            "web-client",
            "deploy",
            "validate-setup-only-host.sh");
        string runbookPath = Path.Combine(
            root,
            "prototypes",
            "web-client",
            "deploy",
            "SETUP-ONLY-ACCEPTANCE.md");
        string projectPath = Path.Combine(
            root,
            "prototypes",
            "web-client",
            "AetherSDR.Web.csproj");
        string script = File.ReadAllText(scriptPath);
        string runbook = File.ReadAllText(runbookPath);
        string project = File.ReadAllText(projectPath);

        Assert.Contains("--proto '=https'", script, StringComparison.Ordinal);
        Assert.Contains("--tlsv1.2", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--insecure", script, StringComparison.Ordinal);
        Assert.DoesNotContain("curl -k", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--request POST", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-X POST", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/setup/api/claim", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/setup/api/revoke", script, StringComparison.Ordinal);
        Assert.Contains("sends only `GET` requests", runbook, StringComparison.Ordinal);
        Assert.Contains("does not claim setup", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("validate-setup-only-host.sh", project, StringComparison.Ordinal);
        Assert.Contains("SETUP-ONLY-ACCEPTANCE.md", project, StringComparison.Ordinal);
    }

    private static async Task<InstallationSetupState> ConfigureThroughTransmitAsync(
        InstallationSetupStore store,
        InstallationPaths paths)
    {
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue issue =
            await new InstallationBootstrapTokenService(store)
                .IssueAsync(initial.Revision);
        InstallationSetupState claimed =
            await new InstallationBootstrapTokenService(store)
                .ClaimAsync(issue.State.Revision, issue.Token);
        InstallationSetupWorkflow workflow = new(store);
        InstallationSetupState topology =
            await workflow.ConfigureTopologyAsync(
                claimed.Revision,
                InstallationTopologyKind.PersonalSingleStation);
        InstallationSetupState publicUrl =
            await workflow.ConfigurePublicUrlAsync(topology.Revision, CanonicalUrl);
        InstallationSetupState configuredPaths =
            await workflow.ConfigurePathsAsync(publicUrl.Revision, paths);
        InstallationSetupState channel =
            await workflow.ConfigureUpdateChannelAsync(
                configuredPaths.Revision,
                InstallationUpdateChannel.Stable);
        InstallationSetupState backup =
            await workflow.ConfirmBackupLocationAsync(channel.Revision);
        return await workflow.ConfigureTransmitSupportAsync(
            backup.Revision,
            installTransmitSupport: false);
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

    private static async Task WaitUntilReachableAsync(Process process, int port)
    {
        using HttpClient client = new()
        {
            Timeout = TimeSpan.FromMilliseconds(500)
        };
        for (int attempt = 0; attempt < 50; attempt++)
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
                request.Headers.Host = CanonicalHost;
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                using HttpResponseMessage response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                return;
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

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        throw new InvalidOperationException(
            $"Setup-only process did not become reachable. {output} {error}");
    }

    private static async Task<InstallationSetupCenterMutationResult> MutateAsync(
        InstallationSetupCenterApplication application,
        InstallationSetupClaimSessionIssue session,
        InstallationSetupHttpCsrfIssue csrf,
        InstallationSetupCenterMutation mutation) =>
        await application.MutateAsync(
            MutationRequest(csrf.Token),
            session.Token,
            mutation);

    private static InstallationSetupCenterApplication CreateApplication(
        InstallationSetupStore store,
        TimeProvider time) =>
        new(
            store,
            new InstallationSetupHttpSecurityPolicy(CanonicalUrl),
            time);

    private static InstallationSetupOnlySettings EnabledSetupOnly() =>
        new()
        {
            Enabled = true,
            CanonicalAccessUrl = CanonicalUrl
        };

    private static InstallationSetupHttpRequest PageRequest() =>
        Request(
            InstallationSetupHttpOperation.PageRead,
            "GET",
            origin: null,
            secFetchSite: "none",
            secFetchMode: "navigate");

    private static InstallationSetupHttpRequest ClaimRequest(string csrf) =>
        Request(
            InstallationSetupHttpOperation.BootstrapClaim,
            "POST",
            origin: CanonicalUrl,
            secFetchSite: "same-origin",
            secFetchMode: "cors",
            contentType: "application/json",
            contentLength: 128,
            csrfCookie: csrf,
            csrfHeader: csrf);

    private static InstallationSetupHttpRequest SessionReadRequest() =>
        Request(
            InstallationSetupHttpOperation.SessionRead,
            "GET",
            origin: CanonicalUrl,
            secFetchSite: "same-origin",
            secFetchMode: "same-origin",
            sessionCookiePresent: true);

    private static InstallationSetupHttpRequest MutationRequest(string csrf) =>
        Request(
            InstallationSetupHttpOperation.SessionMutation,
            "POST",
            origin: CanonicalUrl,
            secFetchSite: "same-origin",
            secFetchMode: "cors",
            contentType: "application/json",
            contentLength: 256,
            sessionCookiePresent: true,
            csrfCookie: csrf,
            csrfHeader: csrf);

    private static InstallationSetupHttpRequest Request(
        InstallationSetupHttpOperation operation,
        string method,
        string? origin,
        string? secFetchSite,
        string? secFetchMode,
        string? contentType = null,
        long? contentLength = null,
        bool sessionCookiePresent = false,
        string? csrfCookie = null,
        string? csrfHeader = null) =>
        new(
            operation,
            method,
            "https",
            CanonicalHost,
            origin,
            secFetchSite,
            secFetchMode,
            contentType,
            contentLength,
            hasQueryString: false,
            sessionCookiePresent,
            csrfCookie,
            csrfHeader);

    private static InstallationPaths CreateInstallationPaths(string root) =>
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

    private sealed class Verifier(
        Func<InstallationFirstAdministratorVerificationRequest,
            Task<InstallationFirstAdministratorEvidence>> verify)
        : IInstallationFirstAdministratorVerifier
    {
        public Task<InstallationFirstAdministratorEvidence> VerifyAsync(
            InstallationFirstAdministratorVerificationRequest request,
            CancellationToken cancellationToken = default) => verify(request);
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
                "aethersdr-setup-acceptance-tests",
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
