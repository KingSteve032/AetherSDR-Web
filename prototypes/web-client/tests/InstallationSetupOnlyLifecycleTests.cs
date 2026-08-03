using AetherSDR.Web.Auth;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupOnlyLifecycleTests
{
    private const string CanonicalUrl = "https://radio.example.org";
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActiveSetupAllowsMonotonicProgressAndLocalRecoveryToken()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationSetupOnlyLifecycleEvaluator evaluator =
            CreateEvaluator(store, initial);

        InstallationSetupOnlyLifecycleDecision first =
            await evaluator.EvaluateAsync();
        InstallationBootstrapTokenIssue issued =
            await new InstallationBootstrapTokenService(store, time)
                .IssueAsync(initial.Revision);
        InstallationSetupState claimed =
            await new InstallationBootstrapTokenService(store, time)
                .ClaimAsync(issued.State.Revision, issued.Token);
        InstallationSetupOnlyLifecycleDecision progressed =
            await evaluator.EvaluateAsync();
        InstallationBootstrapTokenIssue recovery =
            await new InstallationBootstrapTokenService(store, time)
                .IssueAsync(claimed.Revision);
        InstallationSetupOnlyLifecycleDecision recoverable =
            await evaluator.EvaluateAsync();

        Assert.False(first.ShouldStop);
        Assert.False(progressed.ShouldStop);
        Assert.False(recoverable.ShouldStop);
        Assert.Equal(recovery.State.Revision, recoverable.SetupRevision);
        Assert.Equal(
            InstallationSetupLockMode.BootstrapRequired,
            recoverable.LockMode);
        Assert.Equal(
            InstallationSetupStep.BootstrapClaim,
            recoverable.LastCompletedStep);
    }

    [Fact]
    public async Task CompletedSetupStopsLifecycleMonitorImmediately()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationSetupOnlyLifecycleEvaluator evaluator =
            CreateEvaluator(store, initial);
        InstallationSetupState configured =
            await ConfigureThroughTransmitAsync(store, time, temporary.Path);
        _ = await CompleteAsync(store, time, configured);
        FakeLifetime lifetime = new();
        InstallationSetupOnlyLifecycleMonitor monitor = new(
            evaluator,
            lifetime,
            time,
            NullLogger<InstallationSetupOnlyLifecycleMonitor>.Instance);

        await monitor.StartAsync(CancellationToken.None);
        InstallationSetupOnlyLifecycleDecision decision =
            await evaluator.EvaluateAsync();
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.StopAsync(CancellationToken.None);

        Assert.True(decision.ShouldStop);
        Assert.Equal(
            InstallationSetupOnlyLifecycleStopReason.SetupComplete,
            decision.StopReason);
        Assert.True(lifetime.StopRequested);
    }

    [Fact]
    public async Task RemoteNodeTopologyStopsSetupOnlyHost()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationSetupOnlyLifecycleEvaluator evaluator =
            CreateEvaluator(store, initial);
        InstallationBootstrapTokenIssue issued =
            await new InstallationBootstrapTokenService(store, time)
                .IssueAsync(initial.Revision);
        InstallationSetupState claimed =
            await new InstallationBootstrapTokenService(store, time)
                .ClaimAsync(issued.State.Revision, issued.Token);
        InstallationSetupState remote =
            await new InstallationSetupWorkflow(store).ConfigureTopologyAsync(
                claimed.Revision,
                InstallationTopologyKind.RemoteStationNode);

        InstallationSetupOnlyLifecycleDecision decision =
            await evaluator.EvaluateAsync();

        Assert.True(decision.ShouldStop);
        Assert.Equal(
            InstallationSetupOnlyLifecycleStopReason.GatewayNoLongerRunsHere,
            decision.StopReason);
        Assert.Equal(remote.Revision, decision.SetupRevision);
    }

    [Fact]
    public async Task MissingAndMalformedStateStopFailClosed()
    {
        using TemporaryDirectory missingDirectory = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore missingStore = CreateStore(missingDirectory, time);
        InstallationSetupState initial = await missingStore.LoadOrCreateAsync();
        InstallationSetupOnlyLifecycleEvaluator missingEvaluator =
            CreateEvaluator(missingStore, initial);
        File.Delete(missingStore.StatePath);

        InstallationSetupOnlyLifecycleDecision missing =
            await missingEvaluator.EvaluateAsync();

        using TemporaryDirectory malformedDirectory = new();
        InstallationSetupStore malformedStore = CreateStore(malformedDirectory, time);
        InstallationSetupState malformedInitial =
            await malformedStore.LoadOrCreateAsync();
        InstallationSetupOnlyLifecycleEvaluator malformedEvaluator =
            CreateEvaluator(malformedStore, malformedInitial);
        await File.WriteAllTextAsync(malformedStore.StatePath, "{}");

        InstallationSetupOnlyLifecycleDecision malformed =
            await malformedEvaluator.EvaluateAsync();

        Assert.Equal(
            InstallationSetupOnlyLifecycleStopReason.StateUnavailable,
            missing.StopReason);
        Assert.Equal(
            InstallationSetupOnlyLifecycleStopReason.StateInvalid,
            malformed.StopReason);
    }

    [Fact]
    public async Task IdentityReplacementAndRevisionRollbackStopFailClosed()
    {
        using TemporaryDirectory identityDirectory = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore identityStore = CreateStore(identityDirectory, time);
        InstallationSetupState identityInitial =
            await identityStore.LoadOrCreateAsync();
        InstallationSetupOnlyLifecycleEvaluator identityEvaluator =
            CreateEvaluator(identityStore, identityInitial);
        using TemporaryDirectory replacementDirectory = new();
        InstallationSetupStore replacementStore = new(
            CreatePaths(replacementDirectory.Path).SetupStatePath,
            new ManualTimeProvider(Start.AddMinutes(1)));
        _ = await replacementStore.LoadOrCreateAsync();
        File.Copy(
            replacementStore.StatePath,
            identityStore.StatePath,
            overwrite: true);

        InstallationSetupOnlyLifecycleDecision replaced =
            await identityEvaluator.EvaluateAsync();

        using TemporaryDirectory revisionDirectory = new();
        InstallationSetupStore revisionStore = CreateStore(revisionDirectory, time);
        InstallationSetupState revisionInitial =
            await revisionStore.LoadOrCreateAsync();
        string original = await File.ReadAllTextAsync(revisionStore.StatePath);
        InstallationSetupOnlyLifecycleEvaluator revisionEvaluator =
            CreateEvaluator(revisionStore, revisionInitial);
        InstallationBootstrapTokenIssue advanced =
            await new InstallationBootstrapTokenService(revisionStore, time)
                .IssueAsync(revisionInitial.Revision);
        InstallationSetupOnlyLifecycleDecision observed =
            await revisionEvaluator.EvaluateAsync();
        await File.WriteAllTextAsync(revisionStore.StatePath, original);

        InstallationSetupOnlyLifecycleDecision regressed =
            await revisionEvaluator.EvaluateAsync();

        Assert.Equal(
            InstallationSetupOnlyLifecycleStopReason.SetupIdentityChanged,
            replaced.StopReason);
        Assert.False(observed.ShouldStop);
        Assert.Equal(advanced.State.Revision, observed.SetupRevision);
        Assert.Equal(
            InstallationSetupOnlyLifecycleStopReason.RevisionRegressed,
            regressed.StopReason);
    }

    private static InstallationSetupOnlyLifecycleEvaluator CreateEvaluator(
        InstallationSetupStore store,
        InstallationSetupState state) =>
        new(
            store,
            new InstallationSetupOnlyIdentity(
                state.SchemaVersion,
                state.CreatedAt,
                state.Revision));

    private static async Task<InstallationSetupState> ConfigureThroughTransmitAsync(
        InstallationSetupStore store,
        ManualTimeProvider time,
        string root)
    {
        InstallationSetupState initial = await store.LoadAsync();
        InstallationBootstrapTokenIssue issued =
            await new InstallationBootstrapTokenService(store, time)
                .IssueAsync(initial.Revision);
        InstallationSetupState claimed =
            await new InstallationBootstrapTokenService(store, time)
                .ClaimAsync(issued.State.Revision, issued.Token);
        InstallationSetupWorkflow workflow = new(store);
        InstallationSetupState topology =
            await workflow.ConfigureTopologyAsync(
                claimed.Revision,
                InstallationTopologyKind.PersonalSingleStation);
        InstallationSetupState publicUrl =
            await workflow.ConfigurePublicUrlAsync(topology.Revision, CanonicalUrl);
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
        return await workflow.ConfigureTransmitSupportAsync(
            backup.Revision,
            installTransmitSupport: false);
    }

    private static Task<InstallationSetupState> CompleteAsync(
        InstallationSetupStore store,
        ManualTimeProvider time,
        InstallationSetupState configured) =>
        new InstallationFirstAdministratorHandoff(store, time).CompleteAsync(
            configured.Revision,
            new Verifier(request =>
                Task.FromResult(
                    new InstallationFirstAdministratorEvidence(
                        request.SetupSchemaVersion,
                        request.SetupRevision,
                        request.SetupCreatedAt,
                        request.Topology,
                        request.CanonicalPublicUrl,
                        "local-admin",
                        time.GetUtcNow(),
                        IsEnabled: true,
                        Roles: [AetherRoles.Admin]))));

    private static InstallationSetupStore CreateStore(
        TemporaryDirectory temporary,
        TimeProvider time) =>
        new(CreatePaths(temporary.Path).SetupStatePath, time);

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

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource m_started = new();
        private readonly CancellationTokenSource m_stopping = new();
        private readonly CancellationTokenSource m_stopped = new();

        public CancellationToken ApplicationStarted => m_started.Token;
        public CancellationToken ApplicationStopping => m_stopping.Token;
        public CancellationToken ApplicationStopped => m_stopped.Token;
        public TaskCompletionSource Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StopRequested { get; private set; }

        public void StopApplication()
        {
            StopRequested = true;
            m_stopping.Cancel();
            m_stopped.Cancel();
            Stopped.TrySetResult();
        }
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
                "aethersdr-setup-lifecycle-tests",
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
