using AetherSDR.Web.Auth;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationRuntimeReadinessTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactCompletedBindingIsReadyAndNonMutating()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState completed = await CompleteSetupAsync(
            store,
            time,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        InstallationRuntimeBinding binding = CreateBinding(
            completed,
            InstallationRuntimeRole.Gateway);
        string before = await File.ReadAllTextAsync(store.StatePath);

        InstallationRuntimeReadinessReport report =
            await new InstallationRuntimeReadiness(store)
                .RequireReadyAsync(binding);
        string after = await File.ReadAllTextAsync(store.StatePath);

        Assert.True(report.Ready);
        Assert.Empty(report.BlockingReasons);
        Assert.Equal(completed.SchemaVersion, report.SetupSchemaVersion);
        Assert.Equal(completed.Revision, report.SetupRevision);
        Assert.Equal(InstallationRuntimeRole.Gateway, report.RuntimeRole);
        Assert.Equal(completed.Topology, report.Topology);
        Assert.Equal(before, after);
        Assert.Equal(completed, await store.LoadAsync());
    }

    [Fact]
    public async Task IncompleteSetupIsBlockedWithoutMutation()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationPaths paths = CreatePaths(temporary.Path);
        InstallationRuntimeBinding binding = new(
            initial.Revision,
            InstallationRuntimeRole.Gateway,
            InstallationTopologyKind.PersonalSingleStation,
            "https://radio.example.org",
            paths,
            InstallTransmitSupport: false);
        string before = await File.ReadAllTextAsync(store.StatePath);

        InstallationRuntimeReadinessReport report =
            await new InstallationRuntimeReadiness(store)
                .EvaluateAsync(binding);
        string after = await File.ReadAllTextAsync(store.StatePath);

        Assert.False(report.Ready);
        Assert.Contains(
            report.BlockingReasons,
            value => value.Contains(
                "setup is not complete",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            report.BlockingReasons,
            value => value.Contains(
                "no installation topology",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, after);
        Assert.Equal(initial, await store.LoadAsync());
    }

    [Fact]
    public async Task EveryRuntimeBindingMismatchRemainsBlocked()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState completed = await CompleteSetupAsync(
            store,
            time,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        InstallationRuntimeReadiness readiness = new(store);
        InstallationRuntimeBinding exact = CreateBinding(
            completed,
            InstallationRuntimeRole.Gateway);

        await AssertBlockedAsync(
            readiness,
            exact with { SetupRevision = exact.SetupRevision - 1 },
            "targets setup revision");
        await AssertBlockedAsync(
            readiness,
            exact with { Topology = InstallationTopologyKind.HybridGateway },
            "does not match persisted topology");
        await AssertBlockedAsync(
            readiness,
            exact with { CanonicalPublicUrl = "https://other.example.org" },
            "canonical public URL");
        await AssertBlockedAsync(
            readiness,
            exact with
            {
                Paths = exact.Paths with
                {
                    LogDirectory = Path.Combine(temporary.Path, "other-logs")
                }
            },
            "installation paths");
        await AssertBlockedAsync(
            readiness,
            exact with { InstallTransmitSupport = true },
            "TX-support installation choice");
        await AssertBlockedAsync(
            readiness,
            exact with
            {
                RuntimeRole = InstallationRuntimeRole.RemoteStationNode
            },
            "not permitted");
    }

    [Fact]
    public async Task RemoteStationNodeBindingRequiresRemoteNodeTopology()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState completed = await CompleteSetupAsync(
            store,
            time,
            InstallationTopologyKind.RemoteStationNode,
            installTransmitSupport: true);
        InstallationRuntimeBinding binding = CreateBinding(
            completed,
            InstallationRuntimeRole.RemoteStationNode);

        InstallationRuntimeReadinessReport report =
            await new InstallationRuntimeReadiness(store)
                .RequireReadyAsync(binding);

        Assert.True(report.Ready);
        Assert.Equal(
            InstallationTopologyKind.RemoteStationNode,
            report.Topology);
        Assert.Equal(
            InstallationRuntimeRole.RemoteStationNode,
            report.RuntimeRole);
    }

    [Fact]
    public async Task RequireReadyIncludesBlockingDiagnostics()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState completed = await CompleteSetupAsync(
            store,
            time,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        InstallationRuntimeBinding mismatched = CreateBinding(
            completed,
            InstallationRuntimeRole.Gateway) with
        {
            SetupRevision = completed.Revision - 1,
            InstallTransmitSupport = true
        };

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new InstallationRuntimeReadiness(store)
                    .RequireReadyAsync(mismatched));

        Assert.Contains("targets setup revision", exception.Message);
        Assert.Contains("TX-support installation choice", exception.Message);
        Assert.Equal(completed, await store.LoadAsync());
    }

    [Fact]
    public async Task MalformedBindingFailsBeforeStateLoad()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationRuntimeBinding malformed = new(
            SetupRevision: 0,
            InstallationRuntimeRole.Gateway,
            InstallationTopologyKind.PersonalSingleStation,
            "https://Radio.Example.org:443/",
            CreatePaths(temporary.Path),
            InstallTransmitSupport: false);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new InstallationRuntimeReadiness(store)
                    .EvaluateAsync(malformed));

        Assert.Contains("canonical public URL", exception.Message);
        Assert.False(File.Exists(store.StatePath));
    }

    private static async Task AssertBlockedAsync(
        InstallationRuntimeReadiness readiness,
        InstallationRuntimeBinding binding,
        string expectedReason)
    {
        InstallationRuntimeReadinessReport report =
            await readiness.EvaluateAsync(binding);
        Assert.False(report.Ready);
        Assert.Contains(
            report.BlockingReasons,
            value => value.Contains(expectedReason, StringComparison.Ordinal));
    }

    private static InstallationRuntimeBinding CreateBinding(
        InstallationSetupState state,
        InstallationRuntimeRole role) =>
        new(
            state.Revision,
            role,
            state.Topology ?? throw new InvalidOperationException(),
            state.CanonicalPublicUrl,
            state.Paths ?? throw new InvalidOperationException(),
            state.InstallTransmitSupport);

    private static async Task<InstallationSetupState> CompleteSetupAsync(
        InstallationSetupStore store,
        ManualTimeProvider time,
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
        string root = Path.GetDirectoryName(
            Path.GetDirectoryName(store.StatePath)!)!;
        InstallationSetupState paths =
            await workflow.ConfigurePathsAsync(
                publicUrl.Revision,
                CreatePaths(root));
        InstallationSetupState channel =
            await workflow.ConfigureUpdateChannelAsync(
                paths.Revision,
                InstallationUpdateChannel.Stable);
        InstallationSetupState backup =
            await workflow.ConfirmBackupLocationAsync(channel.Revision);
        InstallationSetupState transmit =
            await workflow.ConfigureTransmitSupportAsync(
                backup.Revision,
                installTransmitSupport);
        InstallationFirstAdministratorHandoff handoff =
            new(store, time);
        return await handoff.CompleteAsync(
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

    private static InstallationSetupStore CreateStore(
        TemporaryDirectory temporary,
        ManualTimeProvider time) =>
        new(
            Path.Combine(
                temporary.Path,
                "state",
                "setup",
                "installation.json"),
            time);

    private static InstallationPaths CreatePaths(string root) =>
        new(
            Path.Combine(root, "config"),
            Path.Combine(root, "state-data"),
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
                $"aethersdr-runtime-readiness-tests-{Guid.NewGuid():N}");
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
