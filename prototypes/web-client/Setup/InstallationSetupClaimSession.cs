using System.Security.Cryptography;
using System.Text;

namespace AetherSDR.Web.Setup;

public sealed class InstallationSetupClaimSessionIssue
{
    public InstallationSetupClaimSessionIssue(
        string token,
        DateTimeOffset expiresAt,
        int setupSchemaVersion,
        long setupRevision,
        DateTimeOffset setupCreatedAt,
        DateTimeOffset claimedAt)
    {
        Token = token;
        ExpiresAt = expiresAt;
        SetupSchemaVersion = setupSchemaVersion;
        SetupRevision = setupRevision;
        SetupCreatedAt = setupCreatedAt;
        ClaimedAt = claimedAt;
    }

    public string Token { get; }

    public DateTimeOffset ExpiresAt { get; }

    public int SetupSchemaVersion { get; }

    public long SetupRevision { get; }

    public DateTimeOffset SetupCreatedAt { get; }

    public DateTimeOffset ClaimedAt { get; }

    public override string ToString() =>
        $"{nameof(InstallationSetupClaimSessionIssue)} " +
        $"{{ Token = [redacted], ExpiresAt = {ExpiresAt:O}, " +
        $"SetupSchemaVersion = {SetupSchemaVersion}, " +
        $"SetupRevision = {SetupRevision} }}";
}

public sealed record InstallationSetupClaimSessionContext(
    int SetupSchemaVersion,
    long SetupRevision,
    DateTimeOffset SetupCreatedAt,
    DateTimeOffset ClaimedAt,
    DateTimeOffset ExpiresAt,
    InstallationSetupStep LastCompletedStep);

public sealed class InstallationSetupClaimSessionService : IDisposable
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(1);

    private readonly InstallationSetupStore m_store;
    private readonly InstallationBootstrapTokenService m_bootstrapTokenService;
    private readonly TimeProvider m_timeProvider;
    private readonly SemaphoreSlim m_gate = new(1, 1);
    private ActiveSession? m_activeSession;
    private bool m_disposed;

    public InstallationSetupClaimSessionService(
        InstallationSetupStore store,
        InstallationBootstrapTokenService bootstrapTokenService,
        TimeProvider? timeProvider = null)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_bootstrapTokenService = bootstrapTokenService ??
            throw new ArgumentNullException(nameof(bootstrapTokenService));
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<InstallationSetupClaimSessionIssue> ClaimAsync(
        long expectedRevision,
        string bootstrapToken,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TimeSpan sessionLifetime = ValidateLifetime(lifetime);
        InstallationSetupState claimed =
            await m_bootstrapTokenService.ClaimAsync(
                expectedRevision,
                bootstrapToken,
                cancellationToken);
        RequireClaimedSetup(claimed);

        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            InstallationSetupState current =
                await m_store.LoadAsync(cancellationToken);
            RequireSameClaimedState(claimed, current);

            DateTimeOffset issuedAt = m_timeProvider.GetUtcNow();
            if (issuedAt < current.Lock.ClaimedAt)
            {
                issuedAt = current.Lock.ClaimedAt.Value;
            }
            DateTimeOffset expiresAt = issuedAt + sessionLifetime;
            return ReplaceSession(current, expiresAt);
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<InstallationSetupClaimSessionContext> ValidateAsync(
        string token,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string candidate = ValidateToken(token);

        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            ActiveSession active = RequireActiveSession(
                candidate,
                expectedRevision,
                m_timeProvider.GetUtcNow());
            InstallationSetupState state =
                await m_store.LoadAsync(cancellationToken);
            RequireSessionState(active, state, expectedRevision);
            return CreateContext(active, state);
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<InstallationSetupClaimSessionIssue> AdvanceAsync(
        string token,
        long previousRevision,
        long nextRevision,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (previousRevision < 0 || nextRevision != previousRevision + 1)
        {
            throw new InvalidOperationException(
                "A setup claim session may advance only across one exact setup revision.");
        }
        string candidate = ValidateToken(token);

        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            ActiveSession active = RequireActiveSession(
                candidate,
                previousRevision,
                now);
            InstallationSetupState state =
                await m_store.LoadAsync(cancellationToken);
            RequireSessionState(active, state, nextRevision);
            return ReplaceSession(state, active.ExpiresAt);
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task RevokeAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string candidate = ValidateToken(token);

        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            ActiveSession? active = m_activeSession;
            if (active is null || !TokenMatches(active, candidate))
            {
                throw InvalidSession();
            }
            ClearActiveSession();
        }
        finally
        {
            m_gate.Release();
        }
    }

    public void Dispose()
    {
        if (m_disposed)
        {
            return;
        }
        m_disposed = true;
        ClearActiveSession();
        m_gate.Dispose();
    }

    private InstallationSetupClaimSessionIssue ReplaceSession(
        InstallationSetupState state,
        DateTimeOffset expiresAt)
    {
        string token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        byte[] digest = HashToken(token);
        ClearActiveSession();
        DateTimeOffset claimedAt = state.Lock.ClaimedAt ??
            throw new InvalidOperationException(
                "A setup claim session requires a claim timestamp.");
        m_activeSession = new ActiveSession(
            digest,
            expiresAt,
            state.SchemaVersion,
            state.Revision,
            state.CreatedAt,
            claimedAt);
        return new InstallationSetupClaimSessionIssue(
            token,
            expiresAt,
            state.SchemaVersion,
            state.Revision,
            state.CreatedAt,
            claimedAt);
    }

    private ActiveSession RequireActiveSession(
        string candidate,
        long expectedRevision,
        DateTimeOffset now)
    {
        ActiveSession? active = m_activeSession;
        if (active is null ||
            active.SetupRevision != expectedRevision ||
            now >= active.ExpiresAt ||
            !TokenMatches(active, candidate))
        {
            if (active is not null && now >= active.ExpiresAt)
            {
                ClearActiveSession();
            }
            throw InvalidSession();
        }
        return active;
    }

    private static InstallationSetupClaimSessionContext CreateContext(
        ActiveSession active,
        InstallationSetupState state) =>
        new(
            state.SchemaVersion,
            state.Revision,
            state.CreatedAt,
            active.ClaimedAt,
            active.ExpiresAt,
            state.LastCompletedStep);

    private static void RequireClaimedSetup(InstallationSetupState state)
    {
        InstallationSetupStateValidator.Validate(state);
        if (state.Lock.Mode != InstallationSetupLockMode.Claimed ||
            state.LastCompletedStep < InstallationSetupStep.BootstrapClaim)
        {
            throw new InvalidOperationException(
                "A setup claim session requires a successfully claimed first-run lock.");
        }
    }

    private static void RequireSameClaimedState(
        InstallationSetupState expected,
        InstallationSetupState actual)
    {
        RequireClaimedSetup(actual);
        if (actual.SchemaVersion != expected.SchemaVersion ||
            actual.Revision != expected.Revision ||
            actual.CreatedAt != expected.CreatedAt ||
            actual.Lock.ClaimedAt != expected.Lock.ClaimedAt)
        {
            throw new InstallationSetupConcurrencyException(
                expected.Revision,
                actual.Revision);
        }
    }

    private static void RequireSessionState(
        ActiveSession active,
        InstallationSetupState state,
        long expectedRevision)
    {
        if (state.Lock.Mode != InstallationSetupLockMode.Claimed ||
            state.LastCompletedStep < InstallationSetupStep.BootstrapClaim ||
            state.SchemaVersion != active.SetupSchemaVersion ||
            state.Revision != expectedRevision ||
            state.CreatedAt != active.SetupCreatedAt ||
            state.Lock.ClaimedAt != active.ClaimedAt)
        {
            throw InvalidSession();
        }
    }

    private static TimeSpan ValidateLifetime(TimeSpan? lifetime)
    {
        TimeSpan value = lifetime ?? DefaultLifetime;
        if (value <= TimeSpan.Zero || value > MaximumLifetime)
        {
            throw new InvalidOperationException(
                "The setup claim session lifetime must be positive and no longer than one hour.");
        }
        return value;
    }

    private static string ValidateToken(string token)
    {
        string candidate = token?.Trim() ?? string.Empty;
        if (candidate.Length is < 32 or > 256 ||
            !string.Equals(candidate, token, StringComparison.Ordinal))
        {
            throw InvalidSession();
        }
        return candidate;
    }

    private static bool TokenMatches(ActiveSession active, string candidate)
    {
        byte[] actual = HashToken(candidate);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                active.TokenDigest,
                actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private void ClearActiveSession()
    {
        ActiveSession? active = m_activeSession;
        m_activeSession = null;
        if (active is not null)
        {
            CryptographicOperations.ZeroMemory(active.TokenDigest);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);
    }

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static UnauthorizedAccessException InvalidSession() =>
        new("The setup claim session is invalid, expired, replaced, or stale.");

    private sealed record ActiveSession(
        byte[] TokenDigest,
        DateTimeOffset ExpiresAt,
        int SetupSchemaVersion,
        long SetupRevision,
        DateTimeOffset SetupCreatedAt,
        DateTimeOffset ClaimedAt);
}
