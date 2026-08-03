using System.Security.Cryptography;
using System.Text;

namespace AetherSDR.Web.Setup;

public sealed record InstallationBootstrapTokenIssue(
    string Token,
    DateTimeOffset ExpiresAt,
    InstallationSetupState State);

public sealed class InstallationBootstrapTokenService
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(1);

    private readonly InstallationSetupStore m_store;
    private readonly TimeProvider m_timeProvider;

    public InstallationBootstrapTokenService(
        InstallationSetupStore store,
        TimeProvider? timeProvider = null)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<InstallationBootstrapTokenIssue> IssueAsync(
        long expectedRevision,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan tokenLifetime = lifetime ?? DefaultLifetime;
        if (tokenLifetime <= TimeSpan.Zero ||
            tokenLifetime > MaximumLifetime)
        {
            throw new InvalidOperationException(
                "The bootstrap token lifetime must be positive and no longer than one hour.");
        }

        string token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string tokenHash = HashToken(token);
        DateTimeOffset expiresAt = default;
        InstallationSetupState updated = await m_store.UpdateAsync(
            expectedRevision,
            state =>
            {
                if (state.Lock.Mode == InstallationSetupLockMode.Complete)
                {
                    throw new InvalidOperationException(
                        "Bootstrap tokens cannot be issued after setup is complete.");
                }

                DateTimeOffset issuedAt = m_timeProvider.GetUtcNow();
                expiresAt = issuedAt + tokenLifetime;
                return state with
                {
                    Lock = new InstallationSetupLock
                    {
                        Mode = InstallationSetupLockMode.BootstrapRequired,
                        BootstrapTokenHash = tokenHash,
                        BootstrapTokenIssuedAt = issuedAt,
                        BootstrapTokenExpiresAt = expiresAt
                    }
                };
            },
            cancellationToken);
        return new InstallationBootstrapTokenIssue(
            token,
            expiresAt,
            updated);
    }

    public Task<InstallationSetupState> ClaimAsync(
        long expectedRevision,
        string token,
        CancellationToken cancellationToken = default)
    {
        string candidate = token?.Trim() ?? string.Empty;
        if (candidate.Length is < 32 or > 256)
        {
            throw InvalidToken();
        }

        return m_store.UpdateAsync(
            expectedRevision,
            state =>
            {
                DateTimeOffset claimedAt = m_timeProvider.GetUtcNow();
                InstallationSetupLock setupLock = state.Lock;
                if (setupLock.Mode !=
                        InstallationSetupLockMode.BootstrapRequired ||
                    string.IsNullOrWhiteSpace(setupLock.BootstrapTokenHash) ||
                    setupLock.BootstrapTokenExpiresAt is null ||
                    claimedAt >= setupLock.BootstrapTokenExpiresAt)
                {
                    throw InvalidToken();
                }

                byte[] expectedHash;
                try
                {
                    expectedHash = Convert.FromHexString(
                        setupLock.BootstrapTokenHash);
                }
                catch (FormatException)
                {
                    throw InvalidToken();
                }
                byte[] actualHash = SHA256.HashData(
                    Encoding.UTF8.GetBytes(candidate));
                if (!CryptographicOperations.FixedTimeEquals(
                        expectedHash,
                        actualHash))
                {
                    throw InvalidToken();
                }

                InstallationSetupStep completedStep =
                    state.LastCompletedStep <
                        InstallationSetupStep.BootstrapClaim
                        ? InstallationSetupStep.BootstrapClaim
                        : state.LastCompletedStep;
                return state with
                {
                    LastCompletedStep = completedStep,
                    Lock = new InstallationSetupLock
                    {
                        Mode = InstallationSetupLockMode.Claimed,
                        ClaimedAt = claimedAt
                    }
                };
            },
            cancellationToken);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static UnauthorizedAccessException InvalidToken() =>
        new("The bootstrap token is invalid or expired.");
}
