namespace AetherSDR.Web.Setup;

public enum InstallationSetupCenterMutationKind
{
    Topology = 1,
    PublicUrl = 2,
    Paths = 3,
    UpdateChannel = 4,
    BackupConfirmation = 5,
    TransmitSupport = 6
}

public abstract class InstallationSetupCenterMutation
{
    private protected InstallationSetupCenterMutation(
        InstallationSetupCenterMutationKind kind,
        long expectedRevision)
    {
        Kind = kind;
        ExpectedRevision = expectedRevision;
    }

    public InstallationSetupCenterMutationKind Kind { get; }

    public long ExpectedRevision { get; }

    public override string ToString() =>
        $"{GetType().Name} {{ Kind = {Kind}, " +
        $"ExpectedRevision = {ExpectedRevision} }}";
}

public sealed class InstallationSetupCenterTopologyMutation
    : InstallationSetupCenterMutation
{
    public InstallationSetupCenterTopologyMutation(
        long expectedRevision,
        InstallationTopologyKind topology)
        : base(InstallationSetupCenterMutationKind.Topology, expectedRevision)
    {
        Topology = topology;
    }

    public InstallationTopologyKind Topology { get; }
}

public sealed class InstallationSetupCenterPublicUrlMutation
    : InstallationSetupCenterMutation
{
    public InstallationSetupCenterPublicUrlMutation(
        long expectedRevision,
        string canonicalPublicUrl)
        : base(InstallationSetupCenterMutationKind.PublicUrl, expectedRevision)
    {
        CanonicalPublicUrl = canonicalPublicUrl ?? string.Empty;
    }

    public string CanonicalPublicUrl { get; }
}

public sealed class InstallationSetupCenterPathsMutation
    : InstallationSetupCenterMutation
{
    public InstallationSetupCenterPathsMutation(
        long expectedRevision,
        InstallationPaths paths)
        : base(InstallationSetupCenterMutationKind.Paths, expectedRevision)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public InstallationPaths Paths { get; }
}

public sealed class InstallationSetupCenterUpdateChannelMutation
    : InstallationSetupCenterMutation
{
    public InstallationSetupCenterUpdateChannelMutation(
        long expectedRevision,
        InstallationUpdateChannel updateChannel,
        string? pinnedRelease = null)
        : base(InstallationSetupCenterMutationKind.UpdateChannel, expectedRevision)
    {
        UpdateChannel = updateChannel;
        PinnedRelease = pinnedRelease;
    }

    public InstallationUpdateChannel UpdateChannel { get; }

    public string? PinnedRelease { get; }
}

public sealed class InstallationSetupCenterBackupConfirmationMutation
    : InstallationSetupCenterMutation
{
    public InstallationSetupCenterBackupConfirmationMutation(long expectedRevision)
        : base(
            InstallationSetupCenterMutationKind.BackupConfirmation,
            expectedRevision)
    {
    }
}

public sealed class InstallationSetupCenterTransmitSupportMutation
    : InstallationSetupCenterMutation
{
    public InstallationSetupCenterTransmitSupportMutation(
        long expectedRevision,
        bool installTransmitSupport)
        : base(
            InstallationSetupCenterMutationKind.TransmitSupport,
            expectedRevision)
    {
        InstallTransmitSupport = installTransmitSupport;
    }

    public bool InstallTransmitSupport { get; }
}

public sealed record InstallationSetupCenterPageResult(
    InstallationSetupStatusReport Status,
    InstallationSetupHttpCsrfIssue Csrf,
    InstallationSetupHttpSecurityContract SecurityContract);

public sealed record InstallationSetupCenterClaimResult(
    InstallationSetupStatusReport Status,
    InstallationSetupClaimSessionIssue Session,
    InstallationSetupHttpCsrfIssue Csrf);

public sealed record InstallationSetupCenterSessionResult(
    InstallationSetupStatusReport Status,
    InstallationSetupClaimSessionContext Session);

public sealed record InstallationSetupCenterPreflightResult(
    InstallationSetupStatusReport Status,
    InstallationSetupClaimSessionContext Session,
    InstallationSetupPreflightReport Preflight);

public sealed record InstallationSetupCenterMutationResult(
    InstallationSetupStatusReport Status,
    InstallationSetupClaimSessionIssue Session,
    InstallationSetupHttpCsrfIssue Csrf,
    InstallationSetupCenterMutationKind MutationKind);

public sealed record InstallationSetupCenterAdministratorEnrollmentResult(
    InstallationSetupStatusReport Status,
    InstallationSetupClaimSessionContext Session,
    InstallationFirstLocalAdministratorEnrollmentIssue Enrollment);

public sealed record InstallationSetupCenterAdministratorConfirmationResult(
    InstallationSetupStatusReport Status,
    bool Completed,
    InstallationFirstLocalAdministratorConfirmationResult Confirmation);

public sealed class InstallationSetupCenterSecurityException
    : UnauthorizedAccessException
{
    public InstallationSetupCenterSecurityException(
        InstallationSetupHttpOperation expectedOperation,
        InstallationSetupHttpSecurityDecision? decision = null)
        : base("The installation setup HTTP request was rejected.")
    {
        ExpectedOperation = expectedOperation;
        Decision = decision;
    }

    public InstallationSetupHttpOperation ExpectedOperation { get; }

    public InstallationSetupHttpSecurityDecision? Decision { get; }
}

public sealed class InstallationSetupCenterApplication : IDisposable
{
    private readonly InstallationSetupStore m_store;
    private readonly InstallationSetupWorkflow m_workflow;
    private readonly InstallationSetupPreflight m_preflight;
    private readonly InstallationSetupClaimSessionService m_sessions;
    private readonly InstallationSetupHttpSecurityPolicy m_security;
    private readonly IInstallationFirstLocalAdministratorProvisioningExecutor?
        m_administrators;
    private readonly InstallationFirstAdministratorHandoff? m_handoff;
    private readonly SemaphoreSlim m_operationGate = new(1, 1);
    private bool m_disposed;

    public InstallationSetupCenterApplication(
        InstallationSetupStore store,
        InstallationSetupHttpSecurityPolicy security,
        TimeProvider? timeProvider = null,
        IInstallationFirstLocalAdministratorProvisioningExecutor? administrators = null)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_security = security ?? throw new ArgumentNullException(nameof(security));
        m_administrators = administrators;
        TimeProvider time = timeProvider ?? TimeProvider.System;
        InstallationBootstrapTokenService bootstrapTokens = new(store, time);
        m_sessions = new InstallationSetupClaimSessionService(
            store,
            bootstrapTokens,
            time);
        m_workflow = new InstallationSetupWorkflow(store);
        m_preflight = new InstallationSetupPreflight(store, time);
        m_handoff = administrators is null
            ? null
            : new InstallationFirstAdministratorHandoff(store, time);
    }

    public InstallationSetupHttpSecurityContract SecurityContract =>
        m_security.Contract;

    public async Task<InstallationSetupCenterPageResult> ReadPageAsync(
        InstallationSetupHttpRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireSecurity(request, InstallationSetupHttpOperation.PageRead);
        InstallationSetupState state =
            await LoadSetupOnlyStateAsync(cancellationToken);
        return new InstallationSetupCenterPageResult(
            InstallationSetupStatusReport.From(state),
            InstallationSetupHttpSecurityPolicy.IssueCsrfToken(),
            m_security.Contract);
    }

    public async Task<InstallationSetupCenterClaimResult> ClaimAsync(
        InstallationSetupHttpRequest request,
        long expectedRevision,
        string bootstrapToken,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireSecurity(request, InstallationSetupHttpOperation.BootstrapClaim);
        InstallationSetupState before =
            await LoadSetupOnlyStateAsync(cancellationToken);
        if (before.Revision != expectedRevision)
        {
            throw new InstallationSetupConcurrencyException(
                expectedRevision,
                before.Revision);
        }

        InstallationSetupClaimSessionIssue session =
            await m_sessions.ClaimAsync(
                expectedRevision,
                bootstrapToken,
                cancellationToken: cancellationToken);
        InstallationSetupState claimed =
            await LoadSetupOnlyStateAsync(cancellationToken);
        RequireSessionIssueMatchesState(session, claimed);
        return new InstallationSetupCenterClaimResult(
            InstallationSetupStatusReport.From(claimed),
            session,
            InstallationSetupHttpSecurityPolicy.IssueCsrfToken());
    }

    public async Task<InstallationSetupCenterSessionResult> ReadSessionAsync(
        InstallationSetupHttpRequest request,
        string sessionToken,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireSecurity(request, InstallationSetupHttpOperation.SessionRead);
        InstallationSetupClaimSessionContext session =
            await m_sessions.ValidateAsync(
                sessionToken,
                expectedRevision,
                cancellationToken);
        InstallationSetupState state =
            await LoadSetupOnlyStateAsync(cancellationToken);
        RequireSessionContextMatchesState(session, state);
        return new InstallationSetupCenterSessionResult(
            InstallationSetupStatusReport.From(state),
            session);
    }

    public async Task<InstallationSetupCenterPreflightResult> ReadPreflightAsync(
        InstallationSetupHttpRequest request,
        string sessionToken,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireSecurity(request, InstallationSetupHttpOperation.SessionRead);
        InstallationSetupClaimSessionContext session =
            await m_sessions.ValidateAsync(
                sessionToken,
                expectedRevision,
                cancellationToken);
        InstallationSetupState state =
            await LoadSetupOnlyStateAsync(cancellationToken);
        RequireSessionContextMatchesState(session, state);
        InstallationSetupPreflightReport preflight =
            await m_preflight.CreateAsync(cancellationToken);
        if (preflight.StateRevision != expectedRevision)
        {
            throw InvalidSession();
        }
        return new InstallationSetupCenterPreflightResult(
            InstallationSetupStatusReport.From(state),
            session,
            preflight);
    }

    public async Task<InstallationSetupCenterAdministratorEnrollmentResult>
        BeginAdministratorEnrollmentAsync(
            InstallationSetupHttpRequest request,
            string sessionToken,
            long expectedRevision,
            InstallationFirstLocalAdministratorEnrollment enrollment,
            CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(enrollment);
        RequireSecurity(
            request,
            InstallationSetupHttpOperation.SessionMutation);
        IInstallationFirstLocalAdministratorProvisioningExecutor administrators =
            RequireAdministratorProvisioner();

        await m_operationGate.WaitAsync(cancellationToken);
        try
        {
            InstallationSetupClaimSessionContext session =
                await m_sessions.ValidateAsync(
                    sessionToken,
                    expectedRevision,
                    cancellationToken);
            InstallationSetupState state =
                await LoadSetupOnlyStateAsync(cancellationToken);
            RequireSessionContextMatchesState(session, state);
            RequireReadyForAdministrator(state);

            InstallationFirstAdministratorVerificationRequest setup =
                CreateAdministratorVerificationRequest(state);
            InstallationFirstLocalAdministratorEnrollmentIssue issue =
                await administrators.BeginAsync(
                    setup,
                    enrollment,
                    cancellationToken);

            InstallationSetupState unchanged =
                await LoadSetupOnlyStateAsync(cancellationToken);
            if (unchanged != state)
            {
                throw new InstallationSetupConcurrencyException(
                    expectedRevision,
                    unchanged.Revision);
            }
            RequireSessionContextMatchesState(session, unchanged);
            return new(
                InstallationSetupStatusReport.From(unchanged),
                session,
                issue);
        }
        finally
        {
            m_operationGate.Release();
        }
    }

    public async Task<InstallationSetupCenterAdministratorConfirmationResult>
        ConfirmAdministratorAsync(
            InstallationSetupHttpRequest request,
            string sessionToken,
            long expectedRevision,
            string? totpCode,
            string correlationId,
            CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireSecurity(
            request,
            InstallationSetupHttpOperation.SessionMutation);
        IInstallationFirstLocalAdministratorProvisioningExecutor administrators =
            RequireAdministratorProvisioner();
        InstallationFirstAdministratorHandoff handoff =
            m_handoff ??
            throw new InvalidOperationException(
                "First-administrator handoff is unavailable.");

        await m_operationGate.WaitAsync(cancellationToken);
        try
        {
            InstallationSetupClaimSessionContext session =
                await m_sessions.ValidateAsync(
                    sessionToken,
                    expectedRevision,
                    cancellationToken);
            InstallationSetupState state =
                await LoadSetupOnlyStateAsync(cancellationToken);
            RequireSessionContextMatchesState(session, state);
            RequireReadyForAdministrator(state);

            InstallationFirstAdministratorVerificationRequest setup =
                CreateAdministratorVerificationRequest(state);
            InstallationFirstLocalAdministratorCompletionResult completion =
                await administrators.ConfirmAndCompleteAsync(
                    handoff,
                    expectedRevision,
                    setup,
                    totpCode,
                    correlationId,
                    cancellationToken);
            if (!completion.Completed)
            {
                return new(
                    InstallationSetupStatusReport.From(state),
                    Completed: false,
                    completion.Confirmation);
            }

            InstallationSetupState completed =
                completion.CompletedState ??
                throw new InvalidOperationException(
                    "First-administrator completion returned no setup state.");
            await m_sessions.RevokeAsync(
                sessionToken,
                CancellationToken.None);
            return new(
                InstallationSetupStatusReport.From(completed),
                Completed: true,
                completion.Confirmation);
        }
        finally
        {
            m_operationGate.Release();
        }
    }

    public async Task<InstallationSetupCenterMutationResult> MutateAsync(
        InstallationSetupHttpRequest request,
        string sessionToken,
        InstallationSetupCenterMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mutation);
        RequireSecurity(request, InstallationSetupHttpOperation.SessionMutation);
        if (mutation.ExpectedRevision < 0)
        {
            throw new InvalidOperationException(
                "A setup-center mutation requires a non-negative expected revision.");
        }
        ValidateMutationTarget(mutation);

        await m_operationGate.WaitAsync(cancellationToken);
        try
        {
            InstallationSetupClaimSessionContext context =
                await m_sessions.ValidateAsync(
                    sessionToken,
                    mutation.ExpectedRevision,
                    cancellationToken);
            InstallationSetupState before =
                await LoadSetupOnlyStateAsync(cancellationToken);
            RequireSessionContextMatchesState(context, before);
            if (before.LastCompletedStep >=
                    InstallationSetupStep.TransmitSupport &&
                m_administrators is not null &&
                await m_administrators.HasIdentityAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Setup choices are immutable after first-administrator " +
                    "enrollment begins.");
            }

            InstallationSetupState updated = await ApplyMutationAsync(
                mutation,
                cancellationToken);
            RequireSetupOnlyState(updated);
            if (updated.Revision != mutation.ExpectedRevision + 1)
            {
                throw new InvalidOperationException(
                    "A setup-center mutation must advance exactly one setup revision.");
            }

            InstallationSetupClaimSessionIssue rotated =
                await m_sessions.AdvanceAsync(
                    sessionToken,
                    mutation.ExpectedRevision,
                    updated.Revision,
                    CancellationToken.None);
            RequireSessionIssueMatchesState(rotated, updated);
            return new InstallationSetupCenterMutationResult(
                InstallationSetupStatusReport.From(updated),
                rotated,
                InstallationSetupHttpSecurityPolicy.IssueCsrfToken(),
                mutation.Kind);
        }
        finally
        {
            m_operationGate.Release();
        }
    }

    public async Task RevokeAsync(
        InstallationSetupHttpRequest request,
        string sessionToken,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireSecurity(request, InstallationSetupHttpOperation.SessionMutation);
        InstallationSetupClaimSessionContext session =
            await m_sessions.ValidateAsync(
                sessionToken,
                expectedRevision,
                cancellationToken);
        InstallationSetupState state =
            await LoadSetupOnlyStateAsync(cancellationToken);
        RequireSessionContextMatchesState(session, state);
        await m_sessions.RevokeAsync(sessionToken, cancellationToken);
    }

    public void Dispose()
    {
        if (m_disposed)
        {
            return;
        }
        m_disposed = true;
        m_sessions.Dispose();
        m_operationGate.Dispose();
    }

    private async Task<InstallationSetupState> ApplyMutationAsync(
        InstallationSetupCenterMutation mutation,
        CancellationToken cancellationToken) =>
        mutation switch
        {
            InstallationSetupCenterTopologyMutation topology =>
                await m_workflow.ConfigureTopologyAsync(
                    topology.ExpectedRevision,
                    topology.Topology,
                    cancellationToken),
            InstallationSetupCenterPublicUrlMutation publicUrl =>
                await m_workflow.ConfigurePublicUrlAsync(
                    publicUrl.ExpectedRevision,
                    publicUrl.CanonicalPublicUrl,
                    cancellationToken),
            InstallationSetupCenterPathsMutation paths =>
                await m_workflow.ConfigurePathsAsync(
                    paths.ExpectedRevision,
                    paths.Paths,
                    cancellationToken),
            InstallationSetupCenterUpdateChannelMutation updateChannel =>
                await m_workflow.ConfigureUpdateChannelAsync(
                    updateChannel.ExpectedRevision,
                    updateChannel.UpdateChannel,
                    updateChannel.PinnedRelease,
                    cancellationToken),
            InstallationSetupCenterBackupConfirmationMutation backup =>
                await m_workflow.ConfirmBackupLocationAsync(
                    backup.ExpectedRevision,
                    cancellationToken),
            InstallationSetupCenterTransmitSupportMutation transmit =>
                await m_workflow.ConfigureTransmitSupportAsync(
                    transmit.ExpectedRevision,
                    transmit.InstallTransmitSupport,
                    cancellationToken),
            _ => throw new InvalidOperationException(
                "An unsupported setup-center mutation was received.")
        };

    private IInstallationFirstLocalAdministratorProvisioningExecutor
        RequireAdministratorProvisioner() =>
        m_administrators ??
        throw new InvalidOperationException(
            "First-local-administrator provisioning is unavailable.");

    private static void RequireReadyForAdministrator(
        InstallationSetupState state)
    {
        if (state.Lock.Mode != InstallationSetupLockMode.Claimed ||
            state.LastCompletedStep < InstallationSetupStep.TransmitSupport)
        {
            throw new InvalidOperationException(
                "First-administrator enrollment requires a claimed setup " +
                "with every installation choice completed.");
        }
        _ = CreateAdministratorVerificationRequest(state);
    }

    private static InstallationFirstAdministratorVerificationRequest
        CreateAdministratorVerificationRequest(
            InstallationSetupState state)
    {
        InstallationTopologyKind topology = state.Topology ??
            throw new InvalidOperationException(
                "First-administrator enrollment requires a topology.");
        CanonicalPublicUrl publicUrl =
            CanonicalPublicUrl.Parse(state.CanonicalPublicUrl);
        return new(
            state.SchemaVersion,
            state.Revision,
            state.CreatedAt,
            topology,
            publicUrl.Value);
    }

    private void RequireSecurity(
        InstallationSetupHttpRequest request,
        InstallationSetupHttpOperation expectedOperation)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Operation != expectedOperation)
        {
            throw new InstallationSetupCenterSecurityException(expectedOperation);
        }
        InstallationSetupHttpSecurityDecision decision =
            m_security.Evaluate(request);
        if (!decision.Allowed)
        {
            throw new InstallationSetupCenterSecurityException(
                expectedOperation,
                decision);
        }
    }

    private async Task<InstallationSetupState> LoadSetupOnlyStateAsync(
        CancellationToken cancellationToken)
    {
        InstallationSetupState state =
            await m_store.LoadAsync(cancellationToken);
        RequireSetupOnlyState(state);
        return state;
    }

    private static void RequireSetupOnlyState(InstallationSetupState state)
    {
        InstallationSetupStateValidator.Validate(state);
        if (state.Lock.Mode == InstallationSetupLockMode.Complete ||
            state.LastCompletedStep == InstallationSetupStep.Administrator)
        {
            throw new InvalidOperationException(
                "The browser setup center is unavailable after setup completes.");
        }
        if (state.Topology is not null &&
            !InstallationTopologyProfile.For(state.Topology.Value).GatewayRunsHere)
        {
            throw new InvalidOperationException(
                "The selected installation topology does not run the browser setup " +
                "center on this host.");
        }
    }

    private void ValidateMutationTarget(
        InstallationSetupCenterMutation mutation)
    {
        if (!Enum.IsDefined(mutation.Kind))
        {
            throw new InvalidOperationException(
                "An unsupported setup-center mutation kind was received.");
        }
        if (mutation is InstallationSetupCenterTopologyMutation topology)
        {
            InstallationTopologyProfile profile =
                InstallationTopologyProfile.For(topology.Topology);
            if (!profile.GatewayRunsHere)
            {
                throw new InvalidOperationException(
                    "The browser setup center cannot select a topology that does not " +
                    "run the gateway on this host.");
            }
        }
        if (mutation is InstallationSetupCenterPublicUrlMutation publicUrl)
        {
            CanonicalPublicUrl requested =
                CanonicalPublicUrl.Parse(publicUrl.CanonicalPublicUrl);
            if (!string.Equals(
                    requested.Value,
                    m_security.Contract.CanonicalOrigin,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The browser setup center public URL must match its exact startup " +
                    "access URL.");
            }
        }
    }

    private static void RequireSessionContextMatchesState(
        InstallationSetupClaimSessionContext session,
        InstallationSetupState state)
    {
        if (session.SetupSchemaVersion != state.SchemaVersion ||
            session.SetupRevision != state.Revision ||
            session.SetupCreatedAt != state.CreatedAt ||
            session.ClaimedAt != state.Lock.ClaimedAt ||
            session.LastCompletedStep != state.LastCompletedStep)
        {
            throw InvalidSession();
        }
    }

    private static void RequireSessionIssueMatchesState(
        InstallationSetupClaimSessionIssue session,
        InstallationSetupState state)
    {
        if (session.SetupSchemaVersion != state.SchemaVersion ||
            session.SetupRevision != state.Revision ||
            session.SetupCreatedAt != state.CreatedAt ||
            session.ClaimedAt != state.Lock.ClaimedAt)
        {
            throw InvalidSession();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);
    }

    private static UnauthorizedAccessException InvalidSession() =>
        new("The setup claim session is invalid, expired, replaced, or stale.");
}
