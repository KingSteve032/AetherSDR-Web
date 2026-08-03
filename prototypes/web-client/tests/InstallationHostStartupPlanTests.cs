using System.Text.Json;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationHostStartupPlanTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactDisabledDefaultsRemainLegacyWithoutResolvingPaths()
    {
        bool resolved = false;

        InstallationHostStartupPlan plan =
            await InstallationHostStartupPlanner.CreateAsync(
                new InstallationSetupOnlySettings(),
                new InstallationRuntimeSettings(),
                () =>
                {
                    resolved = true;
                    throw new InvalidOperationException();
                });

        Assert.Equal(InstallationHostStartupMode.Legacy, plan.Mode);
        Assert.False(plan.SetupOnlyEligible);
        Assert.False(plan.NormalRuntimeReady);
        Assert.Null(plan.Paths);
        Assert.Null(plan.SetupStatus);
        Assert.Null(plan.RuntimeReadiness);
        Assert.Null(plan.SetupOnlyCanonicalAccessUrl);
        Assert.Null(plan.SetupOnlyIdentity);
        Assert.False(resolved);
    }

    [Fact]
    public async Task SetupOnlyAndNormalRuntimeAreMutuallyExclusive()
    {
        bool resolved = false;

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallationHostStartupPlanner.CreateAsync(
                    EnabledSetupOnly(),
                    new InstallationRuntimeSettings { Enabled = true },
                    () =>
                    {
                        resolved = true;
                        throw new InvalidOperationException();
                    }));

        Assert.Contains("cannot be enabled together", exception.Message);
        Assert.False(resolved);
    }

    [Fact]
    public async Task PartialDisabledRuntimeBindingIsRejectedBeforeSetupPathResolution()
    {
        bool resolved = false;
        InstallationRuntimeSettings partial = new()
        {
            CanonicalPublicUrl = "https://radio.example.org"
        };

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallationHostStartupPlanner.CreateAsync(
                    EnabledSetupOnly(),
                    partial,
                    () =>
                    {
                        resolved = true;
                        throw new InvalidOperationException();
                    }));

        Assert.Contains("exact empty default binding", exception.Message);
        Assert.False(resolved);
    }

    [Fact]
    public async Task SetupOnlyRequiresExistingStateAndNeverCreatesIt()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = CreatePaths(temporary.Path);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => InstallationHostStartupPlanner.CreateAsync(
                EnabledSetupOnly(),
                new InstallationRuntimeSettings(),
                () => paths));

        Assert.False(File.Exists(paths.SetupStatePath));
    }

    [Fact]
    public async Task InitialSetupStateProducesRedactedNonMutatingSetupOnlyPlan()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationPaths paths = CreatePaths(temporary.Path);
        InstallationSetupStore store = new(paths.SetupStatePath, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        string before = await File.ReadAllTextAsync(store.StatePath);

        InstallationHostStartupPlan plan =
            await InstallationHostStartupPlanner.CreateAsync(
                EnabledSetupOnly(),
                new InstallationRuntimeSettings(),
                () => paths);
        string after = await File.ReadAllTextAsync(store.StatePath);

        Assert.Equal(InstallationHostStartupMode.SetupOnly, plan.Mode);
        Assert.True(plan.SetupOnlyEligible);
        Assert.False(plan.NormalRuntimeReady);
        Assert.Equal(paths, plan.Paths);
        Assert.NotNull(plan.SetupStatus);
        Assert.Equal(initial.Revision, plan.SetupStatus!.Revision);
        Assert.Equal(InstallationSetupLockMode.BootstrapRequired, plan.SetupStatus.LockMode);
        Assert.False(plan.SetupStatus.BootstrapTokenPresent);
        Assert.Null(plan.RuntimeReadiness);
        Assert.Equal("https://radio.example.org", plan.SetupOnlyCanonicalAccessUrl);
        Assert.Equal(
            new InstallationSetupOnlyIdentity(
                initial.SchemaVersion,
                initial.CreatedAt,
                initial.Revision),
            plan.SetupOnlyIdentity);
        Assert.Equal(before, after);
        Assert.Equal(initial, await store.LoadAsync());
    }

    [Fact]
    public async Task BootstrapTokenValueAndDigestAreAbsentFromPlan()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationPaths paths = CreatePaths(temporary.Path);
        InstallationSetupStore store = new(paths.SetupStatePath, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue issue =
            await new InstallationBootstrapTokenService(store, time)
                .IssueAsync(initial.Revision);

        InstallationHostStartupPlan plan =
            await InstallationHostStartupPlanner.CreateAsync(
                EnabledSetupOnly(),
                new InstallationRuntimeSettings(),
                () => paths);
        string json = JsonSerializer.Serialize(plan);

        Assert.True(plan.SetupStatus!.BootstrapTokenPresent);
        Assert.DoesNotContain(issue.Token, json, StringComparison.Ordinal);
        Assert.DoesNotContain("BootstrapTokenHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            issue.State.Lock.BootstrapTokenHash!,
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClaimedIncompleteStateRemainsSetupOnlyEligible()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationPaths paths = CreatePaths(temporary.Path);
        InstallationSetupStore store = new(paths.SetupStatePath, time);
        InstallationSetupState claimed = await ClaimAsync(store, time);

        InstallationHostStartupPlan plan =
            await InstallationHostStartupPlanner.CreateAsync(
                EnabledSetupOnly(),
                new InstallationRuntimeSettings(),
                () => paths);

        Assert.Equal(InstallationHostStartupMode.SetupOnly, plan.Mode);
        Assert.Equal(claimed.Revision, plan.SetupStatus!.Revision);
        Assert.Equal(InstallationSetupLockMode.Claimed, plan.SetupStatus.LockMode);
        Assert.False(plan.SetupStatus.BootstrapTokenPresent);
    }

    [Fact]
    public async Task CompletedSetupCannotReenterSetupOnlyMode()
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

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallationHostStartupPlanner.CreateAsync(
                    EnabledSetupOnly(),
                    new InstallationRuntimeSettings(),
                    () => paths));

        Assert.Contains("forbidden after installation setup completes", exception.Message);
        Assert.Equal(completed, await store.LoadAsync());
    }

    [Fact]
    public async Task RemoteStationNodeTopologyCannotHostWebSetupCenter()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationPaths paths = CreatePaths(temporary.Path);
        InstallationSetupStore store = new(paths.SetupStatePath, time);
        InstallationSetupState claimed = await ClaimAsync(store, time);
        InstallationSetupState topology =
            await new InstallationSetupWorkflow(store).ConfigureTopologyAsync(
                claimed.Revision,
                InstallationTopologyKind.RemoteStationNode);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallationHostStartupPlanner.CreateAsync(
                    EnabledSetupOnly(),
                    new InstallationRuntimeSettings(),
                    () => paths));

        Assert.Contains("does not run the web setup center", exception.Message);
        Assert.Equal(topology, await store.LoadAsync());
    }

    [Fact]
    public async Task ExactCompletedRuntimeBindingProducesNormalRuntimePlan()
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
        InstallationRuntimeSettings runtime = new()
        {
            Enabled = true,
            SetupRevision = completed.Revision,
            RuntimeRole = InstallationRuntimeRole.Gateway,
            Topology = completed.Topology!.Value,
            CanonicalPublicUrl = completed.CanonicalPublicUrl,
            InstallTransmitSupport = completed.InstallTransmitSupport
        };

        InstallationHostStartupPlan plan =
            await InstallationHostStartupPlanner.CreateAsync(
                new InstallationSetupOnlySettings(),
                runtime,
                () => paths);

        Assert.Equal(InstallationHostStartupMode.NormalRuntime, plan.Mode);
        Assert.True(plan.NormalRuntimeReady);
        Assert.False(plan.SetupOnlyEligible);
        Assert.Null(plan.Paths);
        Assert.Null(plan.SetupStatus);
        Assert.NotNull(plan.RuntimeReadiness);
        Assert.True(plan.RuntimeReadiness!.Ready);
        Assert.Equal(completed.Revision, plan.RuntimeReadiness.SetupRevision);
        Assert.Null(plan.SetupOnlyCanonicalAccessUrl);
        Assert.Null(plan.SetupOnlyIdentity);
    }

    [Fact]
    public async Task DisabledSetupOnlyRejectsResidualAccessUrl()
    {
        bool resolved = false;
        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallationHostStartupPlanner.CreateAsync(
                    new InstallationSetupOnlySettings
                    {
                        CanonicalAccessUrl = "https://radio.example.org"
                    },
                    new InstallationRuntimeSettings(),
                    () =>
                    {
                        resolved = true;
                        throw new InvalidOperationException();
                    }));

        Assert.Contains("empty canonical access URL", exception.Message);
        Assert.False(resolved);
    }

    [Fact]
    public void PlannerIsWiredBeforeNormalProgramConfiguration()
    {
        string programPath = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "web-client",
            "Program.cs");
        string source = File.ReadAllText(programPath);

        int planner = source.IndexOf(
            "InstallationHostStartupPlanner.CreateAsync",
            StringComparison.Ordinal);
        int setupComposition = source.IndexOf(
            "InstallationSetupOnlyProgramComposition.Configure",
            StringComparison.Ordinal);
        int auth = source.IndexOf("AuthSettings authSettings", StringComparison.Ordinal);
        int radio = source.IndexOf("RadioSettings radioSettings", StringComparison.Ordinal);

        Assert.True(planner >= 0);
        Assert.True(setupComposition > planner);
        Assert.True(auth > setupComposition);
        Assert.True(radio > setupComposition);
    }

    private static InstallationSetupOnlySettings EnabledSetupOnly() =>
        new()
        {
            Enabled = true,
            CanonicalAccessUrl = "https://radio.example.org"
        };

    private static async Task<InstallationSetupState> ClaimAsync(
        InstallationSetupStore store,
        ManualTimeProvider time)
    {
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenService tokens = new(store, time);
        InstallationBootstrapTokenIssue issue =
            await tokens.IssueAsync(initial.Revision);
        return await tokens.ClaimAsync(issue.State.Revision, issue.Token);
    }

    private static async Task<InstallationSetupState> CompleteSetupAsync(
        InstallationSetupStore store,
        ManualTimeProvider time,
        InstallationPaths paths,
        InstallationTopologyKind topology,
        bool installTransmitSupport)
    {
        InstallationSetupState claimed = await ClaimAsync(store, time);
        InstallationSetupWorkflow workflow = new(store);
        InstallationSetupState configuredTopology =
            await workflow.ConfigureTopologyAsync(claimed.Revision, topology);
        InstallationSetupState publicUrl =
            await workflow.ConfigurePublicUrlAsync(
                configuredTopology.Revision,
                "https://radio.example.org");
        InstallationSetupState configuredPaths =
            await workflow.ConfigurePathsAsync(
                publicUrl.Revision,
                paths);
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
                $"aethersdr-host-startup-plan-tests-{Guid.NewGuid():N}");
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
