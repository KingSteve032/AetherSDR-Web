using AetherSDR.Web.Auth;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationRuntimeStartupGateTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledDefaultDoesNotResolvePathsOrReadState()
    {
        bool resolverCalled = false;

        InstallationRuntimeReadinessReport? report =
            await InstallationRuntimeStartupGate.RequireReadyAsync(
                new InstallationRuntimeSettings(),
                () =>
                {
                    resolverCalled = true;
                    throw new InvalidOperationException("must not run");
                });

        Assert.Null(report);
        Assert.False(resolverCalled);
    }

    [Fact]
    public async Task DisabledPartialBindingFailsBeforePathResolution()
    {
        InstallationRuntimeSettings[] invalid =
        [
            new() { SetupRevision = 0 },
            new()
            {
                RuntimeRole = InstallationRuntimeRole.RemoteStationNode
            },
            new() { Topology = InstallationTopologyKind.HybridGateway },
            new() { CanonicalPublicUrl = " " },
            new() { CanonicalPublicUrl = "https://radio.example.org" },
            new() { InstallTransmitSupport = true }
        ];

        foreach (InstallationRuntimeSettings settings in invalid)
        {
            bool resolverCalled = false;
            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => InstallationRuntimeStartupGate.RequireReadyAsync(
                        settings,
                        () =>
                        {
                            resolverCalled = true;
                            throw new InvalidOperationException("must not run");
                        }));

            Assert.Contains("Disabled installation runtime", exception.Message);
            Assert.False(resolverCalled);
        }
    }

    [Fact]
    public async Task ExactCompletedGatewayBindingStartsReadyAndNonMutating()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationPaths paths = CreatePaths(temporary.Path);
        InstallationSetupStore store = new(paths.SetupStatePath, time);
        InstallationSetupState completed = await CompleteSetupAsync(
            store,
            time,
            paths,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        InstallationRuntimeSettings settings = CreateSettings(completed);
        string before = await File.ReadAllTextAsync(store.StatePath);
        int resolverCalls = 0;

        InstallationRuntimeReadinessReport? report =
            await InstallationRuntimeStartupGate.RequireReadyAsync(
                settings,
                () =>
                {
                    resolverCalls++;
                    return paths;
                });
        string after = await File.ReadAllTextAsync(store.StatePath);

        Assert.NotNull(report);
        Assert.True(report.Ready);
        Assert.Empty(report.BlockingReasons);
        Assert.Equal(completed.Revision, report.SetupRevision);
        Assert.Equal(1, resolverCalls);
        Assert.Equal(before, after);
        Assert.Equal(completed, await store.LoadAsync());
    }

    [Fact]
    public async Task EnabledIncompleteSetupBlocksNormalRuntimeWithoutMutation()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationPaths paths = CreatePaths(temporary.Path);
        InstallationSetupStore store = new(paths.SetupStatePath, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationRuntimeSettings settings = new()
        {
            Enabled = true,
            SetupRevision = initial.Revision,
            RuntimeRole = InstallationRuntimeRole.Gateway,
            Topology = InstallationTopologyKind.PersonalSingleStation,
            CanonicalPublicUrl = "https://radio.example.org",
            InstallTransmitSupport = false
        };
        string before = await File.ReadAllTextAsync(store.StatePath);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallationRuntimeStartupGate.RequireReadyAsync(
                    settings,
                    () => paths));
        string after = await File.ReadAllTextAsync(store.StatePath);

        Assert.Contains("setup is not complete", exception.Message);
        Assert.Equal(before, after);
        Assert.Equal(initial, await store.LoadAsync());
    }

    [Fact]
    public async Task WebStartupRejectsRemoteNodeRoleBeforePathResolution()
    {
        bool resolverCalled = false;
        InstallationRuntimeSettings settings = new()
        {
            Enabled = true,
            SetupRevision = 10,
            RuntimeRole = InstallationRuntimeRole.RemoteStationNode,
            Topology = InstallationTopologyKind.RemoteStationNode,
            CanonicalPublicUrl = "https://radio.example.org"
        };

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallationRuntimeStartupGate.RequireReadyAsync(
                    settings,
                    () =>
                    {
                        resolverCalled = true;
                        throw new InvalidOperationException("must not run");
                    }));

        Assert.Contains("web process supports only", exception.Message);
        Assert.False(resolverCalled);
    }

    [Fact]
    public async Task WebStartupRejectsTopologyWithoutGatewayBeforePathResolution()
    {
        bool resolverCalled = false;
        InstallationRuntimeSettings settings = new()
        {
            Enabled = true,
            SetupRevision = 10,
            RuntimeRole = InstallationRuntimeRole.Gateway,
            Topology = InstallationTopologyKind.RemoteStationNode,
            CanonicalPublicUrl = "https://radio.example.org"
        };

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallationRuntimeStartupGate.RequireReadyAsync(
                    settings,
                    () =>
                    {
                        resolverCalled = true;
                        throw new InvalidOperationException("must not run");
                    }));

        Assert.Contains("does not run the web gateway", exception.Message);
        Assert.False(resolverCalled);
    }

    [Fact]
    public async Task StaleConfiguredRevisionBlocksStartupWithoutMutation()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationPaths paths = CreatePaths(temporary.Path);
        InstallationSetupStore store = new(paths.SetupStatePath, time);
        InstallationSetupState completed = await CompleteSetupAsync(
            store,
            time,
            paths,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: true);
        InstallationRuntimeSettings settings = CreateSettings(completed) with
        {
            SetupRevision = completed.Revision - 1
        };
        string before = await File.ReadAllTextAsync(store.StatePath);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallationRuntimeStartupGate.RequireReadyAsync(
                    settings,
                    () => paths));
        string after = await File.ReadAllTextAsync(store.StatePath);

        Assert.Contains("targets setup revision", exception.Message);
        Assert.Equal(before, after);
        Assert.Equal(completed, await store.LoadAsync());
    }

    private static InstallationRuntimeSettings CreateSettings(
        InstallationSetupState completed) =>
        new()
        {
            Enabled = true,
            SetupRevision = completed.Revision,
            RuntimeRole = InstallationRuntimeRole.Gateway,
            Topology = completed.Topology ?? throw new InvalidOperationException(),
            CanonicalPublicUrl = completed.CanonicalPublicUrl,
            InstallTransmitSupport = completed.InstallTransmitSupport
        };

    private static async Task<InstallationSetupState> CompleteSetupAsync(
        InstallationSetupStore store,
        ManualTimeProvider time,
        InstallationPaths paths,
        InstallationTopologyKind topology,
        bool installTransmitSupport)
    {
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationBootstrapTokenIssue issue =
            await tokenService.IssueAsync(initial.Revision);
        InstallationSetupState claimed = await tokenService.ClaimAsync(
            issue.State.Revision,
            issue.Token);
        InstallationSetupWorkflow workflow = new(store);
        InstallationSetupState configuredTopology =
            await workflow.ConfigureTopologyAsync(claimed.Revision, topology);
        InstallationSetupState publicUrl =
            await workflow.ConfigurePublicUrlAsync(
                configuredTopology.Revision,
                "https://radio.example.org");
        InstallationSetupState configuredPaths =
            await workflow.ConfigurePathsAsync(publicUrl.Revision, paths);
        InstallationSetupState channel =
            await workflow.ConfigureUpdateChannelAsync(
                configuredPaths.Revision,
                InstallationUpdateChannel.Stable);
        InstallationSetupState backup =
            await workflow.ConfirmBackupLocationAsync(channel.Revision);
        InstallationSetupState transmit =
            await workflow.ConfigureTransmitSupportAsync(
                backup.Revision,
                installTransmitSupport);
        return await new InstallationFirstAdministratorHandoff(store, time)
            .CompleteAsync(
                transmit.Revision,
                new Verifier(request =>
                    Task.FromResult(
                        new InstallationFirstAdministratorEvidence(
                            request.SetupSchemaVersion,
                            request.SetupRevision,
                            request.SetupCreatedAt,
                            request.Topology,
                            request.CanonicalPublicUrl,
                            "local-admin-1",
                            request.SetupCreatedAt,
                            IsEnabled: true,
                            [AetherRoles.Admin, AetherRoles.Observe]))));
    }

    private static InstallationPaths CreatePaths(string root) =>
        new(
            Path.Combine(root, "config"),
            Path.Combine(root, "state"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "releases"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "logs"));

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
                $"aethersdr-runtime-startup-gate-tests-{Guid.NewGuid():N}");
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
