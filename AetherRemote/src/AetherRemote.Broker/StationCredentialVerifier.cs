using System.Security.Cryptography;
using System.Text;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Broker;

public sealed class StationCredentialVerifier
{
    private readonly StationEnrollmentRegistry m_enrollments;
    private readonly byte[]? m_runtimeVerifier;
    private readonly byte[]? m_administrationVerifier;

    public StationCredentialVerifier(
        IOptions<StationLinkSettings> settings,
        StationEnrollmentRegistry enrollments)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(enrollments);
        m_enrollments = enrollments;

        if (!settings.Value.Enabled)
        {
            return;
        }

        m_runtimeVerifier = DecodeRequiredVerifier(
            settings.Value.RuntimeCredentialSha256,
            "runtime");
        m_administrationVerifier = DecodeRequiredVerifier(
            settings.Value.AdministrationCredentialSha256,
            "administration");
        if (CryptographicOperations.FixedTimeEquals(
                m_runtimeVerifier,
                m_administrationVerifier))
        {
            throw new InvalidOperationException(
                "Runtime and administration credentials must be distinct.");
        }
    }

    public bool VerifyStation(string stationId, string credential) =>
        StationProtocolValidator.IsIdentifier(
            stationId,
            StationProtocol.MaximumStationIdLength) &&
        m_enrollments.TryGetVerifier(stationId, out byte[]? verifier) &&
        Verify(verifier, credential);

    public bool VerifyRuntime(string credential) =>
        Verify(m_runtimeVerifier, credential);

    public bool VerifyAdministration(string credential) =>
        Verify(m_administrationVerifier, credential);

    public static string HashCredential(string credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        return Convert.ToHexStringLower(digest);
    }

    private static byte[] DecodeRequiredVerifier(
        string? value,
        string purpose)
    {
        if (!TryDecodeVerifier(value, out byte[]? verifier) ||
            verifier is null)
        {
            throw new InvalidOperationException(
                $"The {purpose} credential verifier must be a " +
                "64-character SHA-256 value.");
        }
        return verifier;
    }

    private static bool Verify(byte[]? expected, string credential)
    {
        if (expected is null ||
            string.IsNullOrWhiteSpace(credential) ||
            credential.Length is < 32 or > 512 ||
            credential.Any(char.IsControl))
        {
            return false;
        }

        byte[] actual = SHA256.HashData(
            Encoding.UTF8.GetBytes(credential));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool TryDecodeVerifier(
        string? value,
        out byte[]? verifier)
    {
        verifier = null;
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length != 64)
        {
            return false;
        }
        try
        {
            byte[] decoded = Convert.FromHexString(normalized);
            if (decoded.Length != 32)
            {
                return false;
            }
            verifier = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
