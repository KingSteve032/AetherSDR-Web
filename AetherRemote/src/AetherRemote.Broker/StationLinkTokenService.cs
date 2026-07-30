using System.Security.Cryptography;
using System.Text;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Broker;

public sealed record StationLinkTokenGrant(
    string StationId,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ExpiresAt);

public sealed class StationLinkTokenException(
    string code,
    string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class StationLinkTokenService
{
    private const int MaximumOutstandingTokens = 1024;
    private readonly object m_gate = new();
    private readonly Dictionary<string, TokenState> m_tokens =
        new(StringComparer.Ordinal);
    private readonly TimeProvider m_timeProvider;
    private readonly TimeSpan m_lifetime;

    public StationLinkTokenService(
        IOptions<StationLinkSettings> settings)
        : this(settings, TimeProvider.System)
    {
    }

    public StationLinkTokenService(
        IOptions<StationLinkSettings> settings,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        m_timeProvider = timeProvider;
        m_lifetime = TimeSpan.FromSeconds(settings.Value.LinkTokenSeconds);
    }

    public StationLinkTokenResponse Issue(
        string stationId,
        IReadOnlyList<string> capabilities)
    {
        StationLinkTokenRequest request = new(stationId, capabilities);
        string? validation =
            StationProtocolValidator.ValidateLinkTokenRequest(
                request,
                stationId);
        if (validation is not null)
        {
            throw new StationLinkTokenException(
                "invalid_token_request",
                validation);
        }

        string[] authorized = capabilities
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray();
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now + m_lifetime;
        string accessToken = Base64UrlEncode(
            RandomNumberGenerator.GetBytes(32));
        string tokenHash = HashToken(accessToken);

        lock (m_gate)
        {
            PruneExpired(now);
            foreach (string existing in m_tokens
                .Where(pair => string.Equals(
                    pair.Value.StationId,
                    stationId,
                    StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray())
            {
                m_tokens.Remove(existing);
            }
            if (m_tokens.Count >= MaximumOutstandingTokens)
            {
                throw new StationLinkTokenException(
                    "token_capacity",
                    "The station-link token service is temporarily full.");
            }
            m_tokens[tokenHash] = new TokenState(
                stationId,
                authorized,
                expiresAt);
        }

        return new StationLinkTokenResponse(
            accessToken,
            expiresAt,
            authorized);
    }

    public bool TryConsume(
        string stationId,
        string accessToken,
        out StationLinkTokenGrant? grant)
    {
        grant = null;
        if (!StationProtocolValidator.IsIdentifier(
                stationId,
                StationProtocol.MaximumStationIdLength) ||
            string.IsNullOrWhiteSpace(accessToken) ||
            accessToken.Length is < 32 or > 512 ||
            accessToken.Any(char.IsControl))
        {
            return false;
        }

        string tokenHash = HashToken(accessToken);
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        lock (m_gate)
        {
            PruneExpired(now);
            if (!m_tokens.TryGetValue(tokenHash, out TokenState? state) ||
                !string.Equals(
                    state.StationId,
                    stationId,
                    StringComparison.Ordinal))
            {
                return false;
            }
            m_tokens.Remove(tokenHash);
            grant = new StationLinkTokenGrant(
                state.StationId,
                state.Capabilities,
                state.ExpiresAt);
            return true;
        }
    }

    public void RevokeStation(string stationId)
    {
        lock (m_gate)
        {
            foreach (string tokenHash in m_tokens
                .Where(pair => string.Equals(
                    pair.Value.StationId,
                    stationId,
                    StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray())
            {
                m_tokens.Remove(tokenHash);
            }
        }
    }

    public int OutstandingCount
    {
        get
        {
            lock (m_gate)
            {
                PruneExpired(m_timeProvider.GetUtcNow());
                return m_tokens.Count;
            }
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (string tokenHash in m_tokens
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(pair => pair.Key)
            .ToArray())
        {
            m_tokens.Remove(tokenHash);
        }
    }

    private static string HashToken(string accessToken)
    {
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(accessToken));
        return Convert.ToHexStringLower(digest);
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record TokenState(
        string StationId,
        IReadOnlyList<string> Capabilities,
        DateTimeOffset ExpiresAt);
}
