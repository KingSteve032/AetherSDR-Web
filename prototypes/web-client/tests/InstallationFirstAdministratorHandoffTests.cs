using AetherSDR.Web.Auth;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationFirstAdministratorHandoffTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task VerifiedAdministratorCompletesSetupAndPreservesChoices()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState configured =
            await ConfigureAsync(store, time);
        InstallationFirstAdministratorVerificationRequest? observed = null;
        time.Advance(TimeSpan.FromMinutes(2));
        DelegateVerifier verifier = new((request, _) =>
        {
            observed = request;
            return Task.FromResult(CreateEvidence(
                request,
                accountCreatedAt: Start + TimeSpan.FromMinutes(1)));
        });

        InstallationSetupState completed =
            await new InstallationFirstAdministratorHandoff(store, time)
                .CompleteAsync(configured.Revision, verifier);

        Assert.NotNull(observed);
        Assert.Equal(configured.Revision, observed.SetupRevision);
        Assert.Equal(configured.CreatedAt, observed.SetupCreatedAt);
        Assert.Equal(configured.Topology, observed.Topology);
        Assert.Equal(configured.CanonicalPublicUrl, observed.CanonicalPublicUrl);
        Assert.Equal(configured.Revision + 1, completed.Revision);
        Assert.Equal(
            InstallationSetupStep.Administrator,
            completed.LastCompletedStep);
        Assert.Equal(InstallationSetupLockMode.Complete, completed.Lock.Mode);
        Assert.Equal(configured.Lock.ClaimedAt, completed.Lock.ClaimedAt);
        Assert.Equal(time.GetUtcNow(), completed.Lock.CompletedAt);
        Assert.Equal(configured.Topology, completed.Topology);
        Assert.Equal(configured.CanonicalPublicUrl, completed.CanonicalPublicUrl);
        Assert.Equal(configured.Paths, completed.Paths);
        Assert.Equal(configured.UpdateChannel, completed.UpdateChannel);
        Assert.Equal(configured.InstallTransmitSupport, completed.InstallTransmitSupport);
        Assert.True(InstallationSetupStatusReport.From(completed).SetupComplete);

        InstallationBootstrapTokenService tokenService = new(store, time);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tokenService.IssueAsync(completed.Revision));
    }

    [Fact]
    public async Task IncompleteSetupNeverCallsAdministratorVerifier()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationSetupState claimed = await ClaimAsync(store, time, initial);
        int calls = 0;
        DelegateVerifier verifier = new((request, _) =>
        {
            calls++;
            return Task.FromResult(CreateEvidence(request, Start));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new InstallationFirstAdministratorHandoff(store, time)
                .CompleteAsync(claimed.Revision, verifier));

        Assert.Equal(0, calls);
        Assert.Equal(claimed, await store.LoadAsync());
    }

    [Fact]
    public async Task StaleRevisionNeverCallsAdministratorVerifier()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState configured = await ConfigureAsync(store, time);
        int calls = 0;
        DelegateVerifier verifier = new((request, _) =>
        {
            calls++;
            return Task.FromResult(CreateEvidence(request, Start));
        });

        await Assert.ThrowsAsync<InstallationSetupConcurrencyException>(
            () => new InstallationFirstAdministratorHandoff(store, time)
                .CompleteAsync(configured.Revision - 1, verifier));

        Assert.Equal(0, calls);
        Assert.Equal(configured, await store.LoadAsync());
    }

    [Fact]
    public async Task InvalidAdministratorEvidenceNeverCompletesSetup()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState configured = await ConfigureAsync(store, time);
        time.Advance(TimeSpan.FromMinutes(2));
        InstallationFirstAdministratorHandoff handoff = new(store, time);

        Func<InstallationFirstAdministratorEvidence,
            InstallationFirstAdministratorEvidence>[] invalidEvidence =
        [
            evidence => evidence with
            {
                SetupRevision = evidence.SetupRevision + 1
            },
            evidence => evidence with { IsEnabled = false },
            evidence => evidence with { SubjectId = " administrator " },
            evidence => evidence with
            {
                AccountCreatedAt = Start - TimeSpan.FromSeconds(1)
            },
            evidence => evidence with { Roles = [AetherRoles.Observe] },
            evidence => evidence with
            {
                Roles = [AetherRoles.Admin, AetherRoles.Admin]
            },
            evidence => evidence with
            {
                Roles = [AetherRoles.Admin, "Aether.Unknown"]
            }
        ];

        foreach (Func<InstallationFirstAdministratorEvidence,
                     InstallationFirstAdministratorEvidence> invalidate
                 in invalidEvidence)
        {
            DelegateVerifier verifier = new((request, _) =>
                Task.FromResult(
                    invalidate(
                        CreateEvidence(
                            request,
                            Start + TimeSpan.FromMinutes(1)))));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => handoff.CompleteAsync(configured.Revision, verifier));
            Assert.Equal(configured, await store.LoadAsync());
        }
    }

    [Fact]
    public async Task VerifierFailureLeavesSetupClaimedAndUnchanged()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState configured = await ConfigureAsync(store, time);
        DelegateVerifier verifier = new((_, _) =>
            throw new IOException("administrator store unavailable"));

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => new InstallationFirstAdministratorHandoff(store, time)
                .CompleteAsync(configured.Revision, verifier));

        Assert.Equal("administrator store unavailable", error.Message);
        Assert.Equal(configured, await store.LoadAsync());
    }

    [Fact]
    public async Task ConcurrentSetupChangeAfterVerificationFailsClosed()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState configured = await ConfigureAsync(store, time);
        InstallationSetupWorkflow workflow = new(store);
        time.Advance(TimeSpan.FromMinutes(2));
        DelegateVerifier verifier = new(async (request, cancellationToken) =>
        {
            _ = await workflow.ConfigureTopologyAsync(
                request.SetupRevision,
                InstallationTopologyKind.HybridGateway,
                cancellationToken);
            return CreateEvidence(
                request,
                Start + TimeSpan.FromMinutes(1));
        });

        await Assert.ThrowsAsync<InstallationSetupConcurrencyException>(
            () => new InstallationFirstAdministratorHandoff(store, time)
                .CompleteAsync(configured.Revision, verifier));

        InstallationSetupState current = await store.LoadAsync();
        Assert.Equal(configured.Revision + 1, current.Revision);
        Assert.Equal(
            InstallationTopologyKind.HybridGateway,
            current.Topology);
        Assert.Equal(InstallationSetupLockMode.Claimed, current.Lock.Mode);
        Assert.Equal(
            InstallationSetupStep.TransmitSupport,
            current.LastCompletedStep);
    }

    [Fact]
    public async Task ExistingVerifiedAdministratorCanCompleteAfterSafeRetry()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState configured = await ConfigureAsync(store, time);
        InstallationSetupWorkflow workflow = new(store);
        time.Advance(TimeSpan.FromMinutes(2));
        bool firstCall = true;
        DelegateVerifier verifier = new(async (request, cancellationToken) =>
        {
            if (firstCall)
            {
                firstCall = false;
                _ = await workflow.ConfigureTopologyAsync(
                    request.SetupRevision,
                    InstallationTopologyKind.LocalStationGateway,
                    cancellationToken);
            }
            return CreateEvidence(
                request,
                Start + TimeSpan.FromMinutes(1));
        });
        InstallationFirstAdministratorHandoff handoff = new(store, time);

        await Assert.ThrowsAsync<InstallationSetupConcurrencyException>(
            () => handoff.CompleteAsync(configured.Revision, verifier));
        InstallationSetupState revised = await store.LoadAsync();
        InstallationSetupState completed =
            await handoff.CompleteAsync(revised.Revision, verifier);

        Assert.Equal(InstallationSetupLockMode.Complete, completed.Lock.Mode);
        Assert.Equal(
            InstallationSetupStep.Administrator,
            completed.LastCompletedStep);
        Assert.Equal(
            InstallationTopologyKind.LocalStationGateway,
            completed.Topology);
    }

    private static InstallationFirstAdministratorEvidence CreateEvidence(
        InstallationFirstAdministratorVerificationRequest request,
        DateTimeOffset accountCreatedAt) =>
        new(
            request.SetupSchemaVersion,
            request.SetupRevision,
            request.SetupCreatedAt,
            request.Topology,
            request.CanonicalPublicUrl,
            "local:administrator",
            accountCreatedAt,
            IsEnabled: true,
            Roles: [AetherRoles.Observe, AetherRoles.Admin]);

    private static async Task<InstallationSetupState> ConfigureAsync(
        InstallationSetupStore store,
        ManualTimeProvider time)
    {
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationSetupState claimed = await ClaimAsync(store, time, initial);
        InstallationSetupWorkflow workflow = new(store);
        InstallationSetupState topology =
            await workflow.ConfigureTopologyAsync(
                claimed.Revision,
                InstallationTopologyKind.PersonalSingleStation);
        InstallationSetupState publicUrl =
            await workflow.ConfigurePublicUrlAsync(
                topology.Revision,
                "https://radio.example.org");
        string root =
            Path.GetDirectoryName(
                Path.GetDirectoryName(store.StatePath)!)!;
        InstallationSetupState paths =
            await workflow.ConfigurePathsAsync(
                publicUrl.Revision,
                new InstallationPaths(
                    Path.Combine(root, "config"),
                    Path.Combine(root, "state-data"),
                    Path.Combine(root, "secrets"),
                    Path.Combine(root, "releases"),
                    Path.Combine(root, "backups"),
                    Path.Combine(root, "logs")));
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

    private static async Task<InstallationSetupState> ClaimAsync(
        InstallationSetupStore store,
        ManualTimeProvider time,
        InstallationSetupState state)
    {
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationBootstrapTokenIssue issue =
            await tokenService.IssueAsync(state.Revision);
        return await tokenService.ClaimAsync(
            issue.State.Revision,
            issue.Token);
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

    private sealed class DelegateVerifier(
        Func<InstallationFirstAdministratorVerificationRequest,
            CancellationToken,
            Task<InstallationFirstAdministratorEvidence>> verify)
        : IInstallationFirstAdministratorVerifier
    {
        public Task<InstallationFirstAdministratorEvidence> VerifyAsync(
            InstallationFirstAdministratorVerificationRequest request,
            CancellationToken cancellationToken = default) =>
            verify(request, cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration) => m_now += duration;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-first-admin-tests-{Guid.NewGuid():N}");
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
