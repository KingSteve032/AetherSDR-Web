using AetherSDR.Web.Auth;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupCenterApplicationTests
{
    private const string CanonicalUrl = "https://radio.example.org";
    private const string CanonicalHost = "radio.example.org";
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PageReadReturnsOnlyRedactedStatusAndFreshCsrf()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue bootstrap =
            await new InstallationBootstrapTokenService(store, time)
                .IssueAsync(initial.Revision);
        string before = await File.ReadAllTextAsync(store.StatePath);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);

        InstallationSetupCenterPageResult first =
            await application.ReadPageAsync(PageRequest());
        InstallationSetupCenterPageResult second =
            await application.ReadPageAsync(PageRequest());
        string after = await File.ReadAllTextAsync(store.StatePath);

        Assert.Equal(bootstrap.State.Revision, first.Status.Revision);
        Assert.True(first.Status.BootstrapTokenPresent);
        Assert.Equal(bootstrap.ExpiresAt, first.Status.BootstrapTokenExpiresAt);
        Assert.NotEqual(first.Csrf.Token, second.Csrf.Token);
        Assert.DoesNotContain(bootstrap.Token, first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(first.Csrf.Token, first.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", first.Csrf.ToString(), StringComparison.Ordinal);
        Assert.Equal(CanonicalUrl, first.SecurityContract.CanonicalOrigin);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task SecurityRejectionOccursBeforeBootstrapConsumption()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue bootstrap =
            await new InstallationBootstrapTokenService(store, time)
                .IssueAsync(initial.Revision);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);
        InstallationSetupHttpCsrfIssue csrf =
            InstallationSetupHttpSecurityPolicy.IssueCsrfToken();

        InstallationSetupCenterSecurityException exception =
            await Assert.ThrowsAsync<InstallationSetupCenterSecurityException>(
                () => application.ClaimAsync(
                    Request(
                        InstallationSetupHttpOperation.BootstrapClaim,
                        "POST",
                        host: "attacker.example",
                        origin: CanonicalUrl,
                        secFetchSite: "same-origin",
                        secFetchMode: "cors",
                        contentType: "application/json",
                        contentLength: 128,
                        sessionCookiePresent: false,
                        csrfCookie: csrf.Token,
                        csrfHeader: csrf.Token),
                    bootstrap.State.Revision,
                    bootstrap.Token));
        InstallationSetupState unchanged = await store.LoadAsync();

        Assert.NotNull(exception.Decision);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.CanonicalHostMismatch,
            exception.Decision!.Rejections);
        Assert.Equal(
            InstallationSetupLockMode.BootstrapRequired,
            unchanged.Lock.Mode);
        Assert.False(string.IsNullOrWhiteSpace(unchanged.Lock.BootstrapTokenHash));

        InstallationSetupCenterClaimResult claimed =
            await application.ClaimAsync(
                ClaimRequest(csrf.Token),
                bootstrap.State.Revision,
                bootstrap.Token);
        Assert.Equal(InstallationSetupLockMode.Claimed, claimed.Status.LockMode);
    }

    [Fact]
    public async Task ClaimCreatesSessionAndSessionReadIsNonMutating()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenIssue bootstrap = await IssueBootstrapAsync(store, time);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);
        InstallationSetupCenterPageResult page =
            await application.ReadPageAsync(PageRequest());

        InstallationSetupCenterClaimResult claim =
            await application.ClaimAsync(
                ClaimRequest(page.Csrf.Token),
                bootstrap.State.Revision,
                bootstrap.Token);
        string before = await File.ReadAllTextAsync(store.StatePath);
        InstallationSetupCenterSessionResult session =
            await application.ReadSessionAsync(
                SessionReadRequest(),
                claim.Session.Token,
                claim.Session.SetupRevision);
        string after = await File.ReadAllTextAsync(store.StatePath);

        Assert.Equal(InstallationSetupLockMode.Claimed, claim.Status.LockMode);
        Assert.Equal(InstallationSetupStep.BootstrapClaim, claim.Status.LastCompletedStep);
        Assert.Equal(claim.Session.SetupRevision, session.Session.SetupRevision);
        Assert.Equal(claim.Status, session.Status);
        Assert.Equal(before, after);
        Assert.DoesNotContain(claim.Session.Token, claim.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(claim.Csrf.Token, claim.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", claim.Session.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrderedWorkflowRotatesAuthorityAndProducesPreflight()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenIssue bootstrap = await IssueBootstrapAsync(store, time);
        InstallationPaths paths = CreatePaths(temporary.Path);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);
        InstallationSetupCenterPageResult page =
            await application.ReadPageAsync(PageRequest());
        InstallationSetupCenterClaimResult claim =
            await application.ClaimAsync(
                ClaimRequest(page.Csrf.Token),
                bootstrap.State.Revision,
                bootstrap.Token);
        string firstToken = claim.Session.Token;

        InstallationSetupCenterMutationResult topology =
            await MutateAsync(
                application,
                claim.Session,
                claim.Csrf,
                new InstallationSetupCenterTopologyMutation(
                    claim.Session.SetupRevision,
                    InstallationTopologyKind.PersonalSingleStation));
        await AssertInvalidSessionAsync(
            application,
            firstToken,
            claim.Session.SetupRevision);

        InstallationSetupCenterMutationResult publicUrl =
            await MutateAsync(
                application,
                topology.Session,
                topology.Csrf,
                new InstallationSetupCenterPublicUrlMutation(
                    topology.Session.SetupRevision,
                    CanonicalUrl));
        InstallationSetupCenterMutationResult configuredPaths =
            await MutateAsync(
                application,
                publicUrl.Session,
                publicUrl.Csrf,
                new InstallationSetupCenterPathsMutation(
                    publicUrl.Session.SetupRevision,
                    paths));
        InstallationSetupCenterMutationResult channel =
            await MutateAsync(
                application,
                configuredPaths.Session,
                configuredPaths.Csrf,
                new InstallationSetupCenterUpdateChannelMutation(
                    configuredPaths.Session.SetupRevision,
                    InstallationUpdateChannel.Stable));
        InstallationSetupCenterMutationResult backup =
            await MutateAsync(
                application,
                channel.Session,
                channel.Csrf,
                new InstallationSetupCenterBackupConfirmationMutation(
                    channel.Session.SetupRevision));
        InstallationSetupCenterMutationResult transmit =
            await MutateAsync(
                application,
                backup.Session,
                backup.Csrf,
                new InstallationSetupCenterTransmitSupportMutation(
                    backup.Session.SetupRevision,
                    installTransmitSupport: false));

        InstallationSetupCenterPreflightResult preflight =
            await application.ReadPreflightAsync(
                SessionReadRequest(),
                transmit.Session.Token,
                transmit.Session.SetupRevision);
        InstallationSetupState state = await store.LoadAsync();

        Assert.Equal(InstallationSetupStep.TransmitSupport, transmit.Status.LastCompletedStep);
        Assert.Equal(transmit.Session.SetupRevision, preflight.Preflight.StateRevision);
        Assert.True(preflight.Preflight.ReadyForInstallerReview);
        Assert.Equal(CanonicalUrl, preflight.Preflight.CanonicalPublicUrl);
        Assert.False(preflight.Preflight.InstallTransmitSupport);
        Assert.Equal(paths, state.Paths);
        Assert.Equal(transmit.Session.SetupRevision, state.Revision);
        Assert.NotEqual(backup.Session.Token, transmit.Session.Token);
        Assert.NotEqual(backup.Csrf.Token, transmit.Csrf.Token);
    }

    [Fact]
    public async Task CsrfMismatchRejectsMutationWithoutChangingState()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenIssue bootstrap = await IssueBootstrapAsync(store, time);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);
        InstallationSetupCenterPageResult page =
            await application.ReadPageAsync(PageRequest());
        InstallationSetupCenterClaimResult claim =
            await application.ClaimAsync(
                ClaimRequest(page.Csrf.Token),
                bootstrap.State.Revision,
                bootstrap.Token);
        string before = await File.ReadAllTextAsync(store.StatePath);
        string otherCsrf = InstallationSetupHttpSecurityPolicy.IssueCsrfToken().Token;

        InstallationSetupCenterSecurityException exception =
            await Assert.ThrowsAsync<InstallationSetupCenterSecurityException>(
                () => application.MutateAsync(
                    MutationRequest(claim.Csrf.Token, otherCsrf),
                    claim.Session.Token,
                    new InstallationSetupCenterTopologyMutation(
                        claim.Session.SetupRevision,
                        InstallationTopologyKind.PersonalSingleStation)));
        string after = await File.ReadAllTextAsync(store.StatePath);

        Assert.Contains(
            InstallationSetupHttpRejectionCode.CsrfTokenMismatch,
            exception.Decision!.Rejections);
        Assert.Equal(before, after);
        InstallationSetupCenterSessionResult stillValid =
            await application.ReadSessionAsync(
                SessionReadRequest(),
                claim.Session.Token,
                claim.Session.SetupRevision);
        Assert.Equal(claim.Session.SetupRevision, stillValid.Session.SetupRevision);
    }

    [Fact]
    public async Task RemoteNodeTopologyIsRejectedBeforeMutation()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenIssue bootstrap = await IssueBootstrapAsync(store, time);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);
        InstallationSetupCenterPageResult page =
            await application.ReadPageAsync(PageRequest());
        InstallationSetupCenterClaimResult claim =
            await application.ClaimAsync(
                ClaimRequest(page.Csrf.Token),
                bootstrap.State.Revision,
                bootstrap.Token);
        InstallationSetupState before = await store.LoadAsync();

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => application.MutateAsync(
                    MutationRequest(claim.Csrf.Token, claim.Csrf.Token),
                    claim.Session.Token,
                    new InstallationSetupCenterTopologyMutation(
                        claim.Session.SetupRevision,
                        InstallationTopologyKind.RemoteStationNode)));

        Assert.Contains("does not run the gateway", exception.Message);
        Assert.Equal(before, await store.LoadAsync());
    }

    [Fact]
    public async Task PublicUrlMustMatchExactStartupAccessOrigin()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenIssue bootstrap = await IssueBootstrapAsync(store, time);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);
        InstallationSetupCenterPageResult page =
            await application.ReadPageAsync(PageRequest());
        InstallationSetupCenterClaimResult claim =
            await application.ClaimAsync(
                ClaimRequest(page.Csrf.Token),
                bootstrap.State.Revision,
                bootstrap.Token);
        InstallationSetupCenterMutationResult topology =
            await MutateAsync(
                application,
                claim.Session,
                claim.Csrf,
                new InstallationSetupCenterTopologyMutation(
                    claim.Session.SetupRevision,
                    InstallationTopologyKind.PersonalSingleStation));
        InstallationSetupState before = await store.LoadAsync();

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => application.MutateAsync(
                    MutationRequest(topology.Csrf.Token, topology.Csrf.Token),
                    topology.Session.Token,
                    new InstallationSetupCenterPublicUrlMutation(
                        topology.Session.SetupRevision,
                        "https://other.example.org")));

        Assert.Contains("match its exact startup access URL", exception.Message);
        Assert.Equal(before, await store.LoadAsync());
        InstallationSetupCenterSessionResult stillValid =
            await application.ReadSessionAsync(
                SessionReadRequest(),
                topology.Session.Token,
                topology.Session.SetupRevision);
        Assert.Equal(topology.Session.SetupRevision, stillValid.Status.Revision);
    }

    [Fact]
    public async Task StaleRevisionAndReplacedTokenFailClosed()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenIssue bootstrap = await IssueBootstrapAsync(store, time);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);
        InstallationSetupCenterPageResult page =
            await application.ReadPageAsync(PageRequest());
        InstallationSetupCenterClaimResult claim =
            await application.ClaimAsync(
                ClaimRequest(page.Csrf.Token),
                bootstrap.State.Revision,
                bootstrap.Token);
        InstallationSetupCenterMutationResult topology =
            await MutateAsync(
                application,
                claim.Session,
                claim.Csrf,
                new InstallationSetupCenterTopologyMutation(
                    claim.Session.SetupRevision,
                    InstallationTopologyKind.PersonalSingleStation));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => application.MutateAsync(
                MutationRequest(claim.Csrf.Token, claim.Csrf.Token),
                claim.Session.Token,
                new InstallationSetupCenterPublicUrlMutation(
                    claim.Session.SetupRevision,
                    CanonicalUrl)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => application.ReadSessionAsync(
                SessionReadRequest(),
                topology.Session.Token,
                claim.Session.SetupRevision));
        Assert.Equal(topology.Session.SetupRevision, (await store.LoadAsync()).Revision);
    }

    [Fact]
    public async Task RevokeInvalidatesSessionWithoutMutatingState()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenIssue bootstrap = await IssueBootstrapAsync(store, time);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);
        InstallationSetupCenterPageResult page =
            await application.ReadPageAsync(PageRequest());
        InstallationSetupCenterClaimResult claim =
            await application.ClaimAsync(
                ClaimRequest(page.Csrf.Token),
                bootstrap.State.Revision,
                bootstrap.Token);
        string before = await File.ReadAllTextAsync(store.StatePath);

        await application.RevokeAsync(
            MutationRequest(claim.Csrf.Token, claim.Csrf.Token),
            claim.Session.Token);
        string after = await File.ReadAllTextAsync(store.StatePath);

        await AssertInvalidSessionAsync(
            application,
            claim.Session.Token,
            claim.Session.SetupRevision);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task CompletedSetupDisablesApplicationOperations()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState completed = await CompleteSetupAsync(store, time);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => application.ReadPageAsync(PageRequest()));

        Assert.Contains("unavailable after setup completes", exception.Message);
        Assert.Equal(completed, await store.LoadAsync());
    }

    [Fact]
    public async Task OperationMismatchFailsBeforeStateAccess()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);

        InstallationSetupCenterSecurityException exception =
            await Assert.ThrowsAsync<InstallationSetupCenterSecurityException>(
                () => application.ClaimAsync(
                    PageRequest(),
                    expectedRevision: 0,
                    bootstrapToken: "not-a-bootstrap-token"));

        Assert.Equal(
            InstallationSetupHttpOperation.BootstrapClaim,
            exception.ExpectedOperation);
        Assert.Null(exception.Decision);
        Assert.False(File.Exists(store.StatePath));
    }

    [Fact]
    public async Task ExternallySelectedRemoteNodeTopologyDisablesFacade()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenIssue bootstrap = await IssueBootstrapAsync(store, time);
        InstallationSetupState claimed =
            await new InstallationBootstrapTokenService(store, time)
                .ClaimAsync(bootstrap.State.Revision, bootstrap.Token);
        InstallationSetupState remoteNode =
            await new InstallationSetupWorkflow(store).ConfigureTopologyAsync(
                claimed.Revision,
                InstallationTopologyKind.RemoteStationNode);
        using InstallationSetupCenterApplication application =
            CreateApplication(store, time);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => application.ReadPageAsync(PageRequest()));

        Assert.Contains("does not run the browser setup center", exception.Message);
        Assert.Equal(remoteNode, await store.LoadAsync());
    }

    [Fact]
    public void ProgramConstructsApplicationOnlyInsideSetupOnlyBranch()
    {
        string programPath = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "web-client",
            "Program.cs");
        string source = File.ReadAllText(programPath);

        int setupBranch = source.IndexOf(
            "installationHostStartupPlan.Mode == InstallationHostStartupMode.SetupOnly",
            StringComparison.Ordinal);
        int application = source.IndexOf(
            "GetRequiredService<InstallationSetupCenterApplication>",
            StringComparison.Ordinal);
        int setupReturn = source.IndexOf(
            "await setupOnlyApplication.RunAsync();",
            StringComparison.Ordinal);
        int auth = source.IndexOf("AuthSettings authSettings", StringComparison.Ordinal);

        Assert.True(setupBranch >= 0);
        Assert.True(application > setupBranch);
        Assert.True(setupReturn > application);
        Assert.True(auth > setupReturn);
        Assert.DoesNotContain(
            "InstallationSetupCenterMutation",
            source,
            StringComparison.Ordinal);
    }

    private static async Task<InstallationSetupCenterMutationResult> MutateAsync(
        InstallationSetupCenterApplication application,
        InstallationSetupClaimSessionIssue session,
        InstallationSetupHttpCsrfIssue csrf,
        InstallationSetupCenterMutation mutation) =>
        await application.MutateAsync(
            MutationRequest(csrf.Token, csrf.Token),
            session.Token,
            mutation);

    private static async Task AssertInvalidSessionAsync(
        InstallationSetupCenterApplication application,
        string token,
        long revision) =>
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => application.ReadSessionAsync(
                SessionReadRequest(),
                token,
                revision));

    private static InstallationSetupCenterApplication CreateApplication(
        InstallationSetupStore store,
        ManualTimeProvider time) =>
        new(
            store,
            new InstallationSetupHttpSecurityPolicy(CanonicalUrl),
            time);

    private static InstallationSetupHttpRequest PageRequest() =>
        Request(
            InstallationSetupHttpOperation.PageRead,
            "GET",
            host: CanonicalHost,
            origin: null,
            secFetchSite: "none",
            secFetchMode: "navigate");

    private static InstallationSetupHttpRequest ClaimRequest(string csrf) =>
        Request(
            InstallationSetupHttpOperation.BootstrapClaim,
            "POST",
            host: CanonicalHost,
            origin: CanonicalUrl,
            secFetchSite: "same-origin",
            secFetchMode: "cors",
            contentType: "application/json; charset=utf-8",
            contentLength: 128,
            sessionCookiePresent: false,
            csrfCookie: csrf,
            csrfHeader: csrf);

    private static InstallationSetupHttpRequest SessionReadRequest() =>
        Request(
            InstallationSetupHttpOperation.SessionRead,
            "GET",
            host: CanonicalHost,
            origin: CanonicalUrl,
            secFetchSite: "same-origin",
            secFetchMode: "same-origin",
            sessionCookiePresent: true);

    private static InstallationSetupHttpRequest MutationRequest(
        string csrfCookie,
        string csrfHeader) =>
        Request(
            InstallationSetupHttpOperation.SessionMutation,
            "POST",
            host: CanonicalHost,
            origin: CanonicalUrl,
            secFetchSite: "same-origin",
            secFetchMode: "cors",
            contentType: "application/json",
            contentLength: 256,
            sessionCookiePresent: true,
            csrfCookie: csrfCookie,
            csrfHeader: csrfHeader);

    private static InstallationSetupHttpRequest Request(
        InstallationSetupHttpOperation operation,
        string method,
        string host,
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
            host,
            origin,
            secFetchSite,
            secFetchMode,
            contentType,
            contentLength,
            hasQueryString: false,
            sessionCookiePresent,
            csrfCookie,
            csrfHeader);

    private static async Task<InstallationBootstrapTokenIssue> IssueBootstrapAsync(
        InstallationSetupStore store,
        ManualTimeProvider time)
    {
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        return await new InstallationBootstrapTokenService(store, time)
            .IssueAsync(initial.Revision);
    }

    private static async Task<InstallationSetupState> CompleteSetupAsync(
        InstallationSetupStore store,
        ManualTimeProvider time)
    {
        InstallationBootstrapTokenIssue bootstrap = await IssueBootstrapAsync(store, time);
        InstallationSetupState claimed =
            await new InstallationBootstrapTokenService(store, time)
                .ClaimAsync(bootstrap.State.Revision, bootstrap.Token);
        InstallationSetupWorkflow workflow = new(store);
        InstallationSetupState topology =
            await workflow.ConfigureTopologyAsync(
                claimed.Revision,
                InstallationTopologyKind.PersonalSingleStation);
        InstallationSetupState publicUrl =
            await workflow.ConfigurePublicUrlAsync(topology.Revision, CanonicalUrl);
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
                $"aethersdr-setup-center-tests-{Guid.NewGuid():N}");
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
