using System.Security.Cryptography;
using System.Text;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupClaimSessionTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidBootstrapClaimCreatesRedactedProcessLocalSession()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService bootstrap = new(store, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue bootstrapIssue =
            await bootstrap.IssueAsync(initial.Revision);
        using InstallationSetupClaimSessionService sessions =
            new(store, bootstrap, time);

        InstallationSetupClaimSessionIssue issue = await sessions.ClaimAsync(
            bootstrapIssue.State.Revision,
            bootstrapIssue.Token,
            TimeSpan.FromMinutes(10));
        InstallationSetupClaimSessionContext context =
            await sessions.ValidateAsync(issue.Token, issue.SetupRevision);
        InstallationSetupState claimed = await store.LoadAsync();
        string persisted = await File.ReadAllTextAsync(store.StatePath);
        string sessionDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(issue.Token)));

        Assert.Equal(43, issue.Token.Length);
        Assert.Equal(Start + TimeSpan.FromMinutes(10), issue.ExpiresAt);
        Assert.Equal(claimed.SchemaVersion, issue.SetupSchemaVersion);
        Assert.Equal(claimed.Revision, issue.SetupRevision);
        Assert.Equal(claimed.CreatedAt, issue.SetupCreatedAt);
        Assert.Equal(claimed.Lock.ClaimedAt, issue.ClaimedAt);
        Assert.Equal(InstallationSetupLockMode.Claimed, claimed.Lock.Mode);
        Assert.Equal(
            InstallationSetupStep.BootstrapClaim,
            context.LastCompletedStep);
        Assert.Equal(issue.SetupRevision, context.SetupRevision);
        Assert.DoesNotContain(issue.Token, issue.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", issue.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(issue.Token, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionDigest, persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidBootstrapTokenNeverCreatesSessionOrMutatesClaimState()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService bootstrap = new(store, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue bootstrapIssue =
            await bootstrap.IssueAsync(initial.Revision);
        using InstallationSetupClaimSessionService sessions =
            new(store, bootstrap, time);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.ClaimAsync(
                bootstrapIssue.State.Revision,
                new string('x', 43)));
        InstallationSetupState unchanged = await store.LoadAsync();

        Assert.Equal(bootstrapIssue.State, unchanged);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.ValidateAsync(new string('y', 43), unchanged.Revision));
    }

    [Fact]
    public async Task InvalidLifetimeFailsBeforeBootstrapTokenConsumption()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService bootstrap = new(store, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue bootstrapIssue =
            await bootstrap.IssueAsync(initial.Revision);
        using InstallationSetupClaimSessionService sessions =
            new(store, bootstrap, time);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sessions.ClaimAsync(
                bootstrapIssue.State.Revision,
                bootstrapIssue.Token,
                InstallationSetupClaimSessionService.MaximumLifetime +
                    TimeSpan.FromSeconds(1)));
        InstallationSetupClaimSessionIssue issue = await sessions.ClaimAsync(
            bootstrapIssue.State.Revision,
            bootstrapIssue.Token);

        Assert.Equal(
            bootstrapIssue.State.Revision + 1,
            issue.SetupRevision);
    }

    [Fact]
    public async Task SessionExpiresAtAbsoluteBoundaryAndCannotBeRevived()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService bootstrap = new(store, time);
        using ClaimedSession issue =
            await ClaimAsync(store, bootstrap, time, TimeSpan.FromMinutes(5));
        InstallationSetupClaimSessionService sessions = issue.Service;

        time.Advance(TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.ValidateAsync(issue.Token, issue.SetupRevision));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.ValidateAsync(issue.Token, issue.SetupRevision));
        Assert.Equal(
            InstallationSetupLockMode.Claimed,
            (await store.LoadAsync()).Lock.Mode);
    }

    [Fact]
    public async Task ExactOneRevisionAdvanceRotatesTokenWithoutSlidingExpiry()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService bootstrap = new(store, time);
        using ClaimedSession claimed =
            await ClaimAsync(store, bootstrap, time, TimeSpan.FromMinutes(10));
        InstallationSetupClaimSessionService sessions = claimed.Service;
        InstallationSetupWorkflow workflow = new(store);

        InstallationSetupState topology =
            await workflow.ConfigureTopologyAsync(
                claimed.SetupRevision,
                InstallationTopologyKind.PersonalSingleStation);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.ValidateAsync(claimed.Token, claimed.SetupRevision));

        InstallationSetupClaimSessionIssue advanced =
            await sessions.AdvanceAsync(
                claimed.Token,
                claimed.SetupRevision,
                topology.Revision);
        InstallationSetupClaimSessionContext context =
            await sessions.ValidateAsync(advanced.Token, topology.Revision);

        Assert.NotEqual(claimed.Token, advanced.Token);
        Assert.Equal(claimed.ExpiresAt, advanced.ExpiresAt);
        Assert.Equal(topology.Revision, advanced.SetupRevision);
        Assert.Equal(InstallationSetupStep.Topology, context.LastCompletedStep);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.ValidateAsync(claimed.Token, topology.Revision));
    }

    [Fact]
    public async Task SkippedOrConcurrentRevisionCannotAdvanceSession()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService bootstrap = new(store, time);
        using ClaimedSession claimed =
            await ClaimAsync(store, bootstrap, time);
        InstallationSetupClaimSessionService sessions = claimed.Service;
        InstallationSetupWorkflow workflow = new(store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sessions.AdvanceAsync(
                claimed.Token,
                claimed.SetupRevision,
                claimed.SetupRevision + 2));
        InstallationSetupState topology =
            await workflow.ConfigureTopologyAsync(
                claimed.SetupRevision,
                InstallationTopologyKind.PersonalSingleStation);
        InstallationSetupState revised =
            await workflow.ConfigureTopologyAsync(
                topology.Revision,
                InstallationTopologyKind.HybridGateway);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.AdvanceAsync(
                claimed.Token,
                claimed.SetupRevision,
                topology.Revision));
        Assert.Equal(revised, await store.LoadAsync());
    }

    [Fact]
    public async Task NewBootstrapClaimReplacesPriorProcessSession()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService bootstrap = new(store, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue firstBootstrap =
            await bootstrap.IssueAsync(initial.Revision);
        using InstallationSetupClaimSessionService sessions =
            new(store, bootstrap, time);
        InstallationSetupClaimSessionIssue first = await sessions.ClaimAsync(
            firstBootstrap.State.Revision,
            firstBootstrap.Token);

        InstallationBootstrapTokenIssue secondBootstrap =
            await bootstrap.IssueAsync(first.SetupRevision);
        InstallationSetupClaimSessionIssue second = await sessions.ClaimAsync(
            secondBootstrap.State.Revision,
            secondBootstrap.Token);

        Assert.NotEqual(first.Token, second.Token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.ValidateAsync(first.Token, first.SetupRevision));
        Assert.Equal(
            second.SetupRevision,
            (await sessions.ValidateAsync(second.Token, second.SetupRevision))
                .SetupRevision);
    }

    [Fact]
    public async Task ProcessRestartDoesNotRecoverSessionBearer()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService bootstrap = new(store, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue bootstrapIssue =
            await bootstrap.IssueAsync(initial.Revision);
        string token;
        long revision;
        using (InstallationSetupClaimSessionService first =
               new(store, bootstrap, time))
        {
            InstallationSetupClaimSessionIssue issue = await first.ClaimAsync(
                bootstrapIssue.State.Revision,
                bootstrapIssue.Token);
            token = issue.Token;
            revision = issue.SetupRevision;
        }

        using InstallationSetupClaimSessionService restarted =
            new(store, bootstrap, time);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => restarted.ValidateAsync(token, revision));
        Assert.Equal(
            InstallationSetupLockMode.Claimed,
            (await store.LoadAsync()).Lock.Mode);
    }

    [Fact]
    public async Task CompletedSetupInvalidatesOutstandingClaimSession()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService bootstrap = new(store, time);
        using ClaimedSession session =
            await ClaimAsync(store, bootstrap, time);
        InstallationSetupClaimSessionService sessions = session.Service;
        InstallationSetupState completed = await CompleteSetupAsync(
            store,
            time,
            session);

        Assert.Equal(InstallationSetupLockMode.Complete, completed.Lock.Mode);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.ValidateAsync(session.Token, session.SetupRevision));
    }

    [Fact]
    public async Task RevokeRequiresExactBearerAndLeavesSetupStateUnchanged()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService bootstrap = new(store, time);
        using ClaimedSession issue =
            await ClaimAsync(store, bootstrap, time);
        InstallationSetupClaimSessionService sessions = issue.Service;
        InstallationSetupState before = await store.LoadAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.RevokeAsync(new string('z', 43)));
        await sessions.RevokeAsync(issue.Token);

        Assert.Equal(before, await store.LoadAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sessions.ValidateAsync(issue.Token, issue.SetupRevision));
    }

    private static async Task<ClaimedSession> ClaimAsync(
        InstallationSetupStore store,
        InstallationBootstrapTokenService bootstrap,
        ManualTimeProvider time,
        TimeSpan? lifetime = null)
    {
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue bootstrapIssue =
            await bootstrap.IssueAsync(initial.Revision);
        InstallationSetupClaimSessionService service =
            new(store, bootstrap, time);
        InstallationSetupClaimSessionIssue issue = await service.ClaimAsync(
            bootstrapIssue.State.Revision,
            bootstrapIssue.Token,
            lifetime);
        return new ClaimedSession(issue, service);
    }

    private static async Task<InstallationSetupState> CompleteSetupAsync(
        InstallationSetupStore store,
        ManualTimeProvider time,
        ClaimedSession session)
    {
        InstallationSetupWorkflow workflow = new(store);
        InstallationSetupState topology =
            await workflow.ConfigureTopologyAsync(
                session.SetupRevision,
                InstallationTopologyKind.PersonalSingleStation);
        InstallationSetupState publicUrl =
            await workflow.ConfigurePublicUrlAsync(
                topology.Revision,
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
                installTransmitSupport: false);
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

    private sealed class ClaimedSession : IDisposable
    {
        private readonly InstallationSetupClaimSessionIssue m_issue;

        public ClaimedSession(
            InstallationSetupClaimSessionIssue issue,
            InstallationSetupClaimSessionService service)
        {
            m_issue = issue;
            Service = service;
        }

        public InstallationSetupClaimSessionService Service { get; }

        public string Token => m_issue.Token;

        public DateTimeOffset ExpiresAt => m_issue.ExpiresAt;

        public int SetupSchemaVersion => m_issue.SetupSchemaVersion;

        public long SetupRevision => m_issue.SetupRevision;

        public DateTimeOffset SetupCreatedAt => m_issue.SetupCreatedAt;

        public DateTimeOffset ClaimedAt => m_issue.ClaimedAt;

        public void Dispose() => Service.Dispose();
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
                $"aethersdr-setup-session-tests-{Guid.NewGuid():N}");
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
