using System.Security.Cryptography;

namespace AetherSDR.Web.Auth.Identity;

internal sealed record AetherLocalMfaChallenge(
    Guid UserId,
    long AuthorityVersion,
    string SecurityStamp,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal sealed record AetherLocalMfaChallengeIssue(
    bool Succeeded,
    string? Token);

internal sealed class AetherLocalMfaChallengeStore(
    AetherLocalAuthenticationPolicy policy,
    TimeProvider timeProvider)
{
    private readonly object gate = new();
    private readonly Dictionary<string, AetherLocalMfaChallenge> challenges =
        new(StringComparer.Ordinal);

    internal AetherLocalMfaChallengeIssue Issue(AetherIdentityUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Id == Guid.Empty ||
            user.AuthorityVersion <= 0 ||
            string.IsNullOrWhiteSpace(user.SecurityStamp) ||
            user.SecurityStamp.Length > 100 ||
            !string.Equals(
                user.SecurityStamp,
                user.SecurityStamp.Trim(),
                StringComparison.Ordinal) ||
            user.SecurityStamp.Any(char.IsControl))
        {
            return new(Succeeded: false, Token: null);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (gate)
        {
            RemoveExpired(now);
            if (challenges.Count >=
                policy.MaximumOutstandingMfaChallenges)
            {
                return new(Succeeded: false, Token: null);
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                string token = GenerateToken();
                string binding = Bind(token);
                if (challenges.TryAdd(
                        binding,
                        new(
                            user.Id,
                            user.AuthorityVersion,
                            user.SecurityStamp,
                            now,
                            now.Add(policy.MfaChallengeLifetime))))
                {
                    return new(Succeeded: true, token);
                }
            }
        }

        return new(Succeeded: false, Token: null);
    }

    internal bool TryConsume(
        string? token,
        out AetherLocalMfaChallenge? challenge)
    {
        challenge = null;
        if (!IsCanonicalToken(token))
        {
            return false;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (gate)
        {
            RemoveExpired(now);
            if (!challenges.Remove(Bind(token!), out challenge))
            {
                return false;
            }
        }

        if (challenge.ExpiresAtUtc <= now ||
            challenge.IssuedAtUtc > now ||
            challenge.ExpiresAtUtc <= challenge.IssuedAtUtc)
        {
            challenge = null;
            return false;
        }
        return true;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string key in challenges
                     .Where(entry => entry.Value.ExpiresAtUtc <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _ = challenges.Remove(key);
        }
    }

    private static string GenerateToken()
    {
        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToHexStringLower(tokenBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    private static string Bind(string token)
    {
        byte[] tokenBytes = Convert.FromHexString(token);
        try
        {
            return Convert.ToHexStringLower(
                SHA256.HashData(tokenBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    private static bool IsCanonicalToken(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
