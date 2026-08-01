using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed class StationTxCommandTrustSettings
{
    public const string SectionName = "StationTxCommandTrust";

    public bool VerificationEnabled { get; set; }
    public StationTxCommandTrustKeySettings[] Keys { get; set; } = [];
}

public sealed class StationTxCommandTrustKeySettings
{
    public string KeyId { get; set; } = string.Empty;
    public string PublicKeyPath { get; set; } = string.Empty;
}

public sealed record StationTxCommandTrustedKeyDiagnostics(
    string KeyId,
    string Fingerprint);

public sealed record StationTxCommandTrustDiagnostics(
    bool VerificationEnabled,
    bool SignatureVerificationAvailable,
    int TrustedKeyCount,
    IReadOnlyList<StationTxCommandTrustedKeyDiagnostics> TrustedKeys,
    string Reason);

/// <summary>
/// Station-scoped immutable trust anchor registry for signed TX command
/// envelopes. It owns public verification keys only. It does not expose a
/// signer, command ingress, command adapter, arming operation, or radio
/// transport.
/// </summary>
public sealed class StationTxCommandTrustRegistry : IDisposable
{
    internal const int MaximumTrustedKeys = 4;
    internal const int MaximumPublicKeyFileBytes = 4096;
    internal const int MaximumPublicKeyPathLength = 1024;

    private const UnixFileMode ForbiddenWritableUnixModes =
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;

    private readonly StationTxCommandKeyRingSignatureVerifier m_verifier;
    private int m_disposed;

    public StationTxCommandTrustRegistry(
        IOptions<StationTxCommandTrustSettings> options,
        ILogger<StationTxCommandTrustRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        StationTxCommandTrustSettings settings = options.Value ??
            new StationTxCommandTrustSettings();
        LoadedTrustedKey[] keys = LoadKeys(settings);
        try
        {
            m_verifier = new StationTxCommandKeyRingSignatureVerifier(
                settings.VerificationEnabled,
                keys);
        }
        catch
        {
            DisposeKeys(keys);
            throw;
        }

        Snapshot = CreateSnapshot(settings.VerificationEnabled, keys);
        logger.LogInformation(
            "Station TX command signature verification {State} with {KeyCount} " +
            "trusted public keys; command boundary and adapter remain disabled",
            Snapshot.SignatureVerificationAvailable ? "ready" : Snapshot.Reason,
            Snapshot.TrustedKeyCount);
    }

    public StationTxCommandTrustDiagnostics Snapshot { get; }

    internal IStationTxCommandSignatureVerifier Verifier => m_verifier;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) == 0)
        {
            m_verifier.Dispose();
        }
    }

    private static LoadedTrustedKey[] LoadKeys(
        StationTxCommandTrustSettings settings)
    {
        StationTxCommandTrustKeySettings[] configured = settings.Keys ?? [];
        if (configured.Length > MaximumTrustedKeys)
        {
            throw new InvalidOperationException(
                $"{StationTxCommandTrustSettings.SectionName}:Keys supports at " +
                $"most {MaximumTrustedKeys} trusted public keys.");
        }
        if (settings.VerificationEnabled && configured.Length == 0)
        {
            throw new InvalidOperationException(
                $"{StationTxCommandTrustSettings.SectionName}:Keys must contain " +
                "at least one trusted public key when verification is enabled.");
        }

        HashSet<string> keyIds = new(StringComparer.Ordinal);
        HashSet<string> paths = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        List<LoadedTrustedKey> loaded = [];
        try
        {
            foreach (StationTxCommandTrustKeySettings? configuredKey in configured)
            {
                if (configuredKey is null)
                {
                    throw new InvalidOperationException(
                        $"{StationTxCommandTrustSettings.SectionName}:Keys " +
                        "contains a null entry.");
                }

                string keyId = ValidateKeyId(configuredKey.KeyId);
                if (!keyIds.Add(keyId))
                {
                    throw new InvalidOperationException(
                        $"Duplicate station TX command trust key ID '{keyId}'.");
                }

                string path = ValidatePath(configuredKey.PublicKeyPath, keyId);
                if (!paths.Add(path))
                {
                    throw new InvalidOperationException(
                        "Each station TX command trust key must use a distinct " +
                        "public-key file.");
                }

                byte[] subjectPublicKeyInfo = ReadPublicKey(path, keyId);
                try
                {
                    StationTxEcdsaCommandSignatureVerifier verifier = new(
                        keyId,
                        subjectPublicKeyInfo);
                    string fingerprint = Convert.ToHexString(
                        SHA256.HashData(subjectPublicKeyInfo).AsSpan(0, 12));
                    loaded.Add(new LoadedTrustedKey(
                        keyId,
                        fingerprint,
                        verifier));
                }
                catch (Exception exception)
                    when (exception is CryptographicException or
                        ArgumentException)
                {
                    throw new InvalidOperationException(
                        $"Station TX command trust key '{keyId}' is not a valid " +
                        "ECDSA P-256 public key.",
                        exception);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(subjectPublicKeyInfo);
                }
            }

            return [.. loaded];
        }
        catch
        {
            DisposeKeys([.. loaded]);
            throw;
        }
    }

    private static string ValidateKeyId(string? value)
    {
        string keyId = value?.Trim() ?? string.Empty;
        if (keyId.Length is 0 or > 64 ||
            !string.Equals(value, keyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Each station TX command trust key requires a canonical key ID " +
                "from 1 through 64 characters.");
        }

        foreach (char character in keyId)
        {
            if (!(character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or '-' or '_' or '.'))
            {
                throw new InvalidOperationException(
                    "A station TX command trust key ID contains an " +
                    "unsupported character.");
            }
        }
        return keyId;
    }

    private static string ValidatePath(string? value, string keyId)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length is 0 or > MaximumPublicKeyPathLength ||
            !string.Equals(value, path, StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                $"Station TX command trust key '{keyId}' requires an absolute " +
                "canonical PublicKeyPath.");
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(path, fullPath, comparison))
            {
                throw new InvalidOperationException(
                    $"Station TX command trust key '{keyId}' PublicKeyPath " +
                    "must not contain relative path segments.");
            }
            return fullPath;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            throw new InvalidOperationException(
                $"Station TX command trust key '{keyId}' has an invalid " +
                "PublicKeyPath.",
                exception);
        }
    }

    private static byte[] ReadPublicKey(string path, string keyId)
    {
        try
        {
            FileInfo info = new(path);
            info.Refresh();
            if (!info.Exists)
            {
                throw new InvalidOperationException(
                    $"Station TX command trust key '{keyId}' does not exist.");
            }
            if ((info.Attributes &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                info.LinkTarget is not null)
            {
                throw new InvalidOperationException(
                    $"Station TX command trust key '{keyId}' must be a regular, " +
                    "non-symlink file.");
            }
            if (info.Length is <= 0 or > MaximumPublicKeyFileBytes)
            {
                throw new InvalidOperationException(
                    $"Station TX command trust key '{keyId}' must be from 1 " +
                    $"through {MaximumPublicKeyFileBytes} bytes.");
            }

            ValidateContainingDirectory(info, keyId);
            if (!OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(path) & ForbiddenWritableUnixModes) != 0)
            {
                throw new InvalidOperationException(
                    $"Station TX command trust key '{keyId}' must not be writable " +
                    "by group or other users.");
            }

            byte[] fileBytes = File.ReadAllBytes(path);
            if (fileBytes.Length != info.Length ||
                fileBytes.Length is <= 0 or > MaximumPublicKeyFileBytes)
            {
                CryptographicOperations.ZeroMemory(fileBytes);
                throw new InvalidOperationException(
                    $"Station TX command trust key '{keyId}' changed while it " +
                    "was being read.");
            }

            string pem;
            try
            {
                pem = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(fileBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileBytes);
            }

            return DecodeExactPublicKeyPem(pem, keyId);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                CryptographicException or DecoderFallbackException)
        {
            throw new InvalidOperationException(
                $"Station TX command trust key '{keyId}' could not be loaded.",
                exception);
        }
    }

    private static void ValidateContainingDirectory(
        FileInfo keyFile,
        string keyId)
    {
        DirectoryInfo? directory = keyFile.Directory;
        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Station TX command trust key '{keyId}' has no containing " +
                "directory.");
        }

        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"Station TX command trust key '{keyId}' must be stored in a " +
                "regular, non-symlink directory.");
        }
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(directory.FullName) &
                ForbiddenWritableUnixModes) != 0)
        {
            throw new InvalidOperationException(
                $"The directory containing station TX command trust key " +
                $"'{keyId}' must not be writable by group or other users.");
        }
    }

    private static byte[] DecodeExactPublicKeyPem(string pem, string keyId)
    {
        ReadOnlySpan<char> span = pem.AsSpan();
        if (!PemEncoding.TryFind(span, out PemFields fields))
        {
            throw new InvalidOperationException(
                $"Station TX command trust key '{keyId}' is not PEM encoded.");
        }

        (int locationOffset, int locationLength) =
            fields.Location.GetOffsetAndLength(span.Length);
        if (!span[..locationOffset].Trim().IsEmpty ||
            !span[(locationOffset + locationLength)..].Trim().IsEmpty)
        {
            throw new InvalidOperationException(
                $"Station TX command trust key '{keyId}' must contain exactly " +
                "one PEM block and no other data.");
        }
        if (!span[fields.Label].SequenceEqual("PUBLIC KEY"))
        {
            throw new InvalidOperationException(
                $"Station TX command trust key '{keyId}' must use a PUBLIC KEY " +
                "PEM block; private keys are forbidden.");
        }

        try
        {
            return Convert.FromBase64String(span[fields.Base64Data].ToString());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"Station TX command trust key '{keyId}' has invalid PEM data.",
                exception);
        }
    }

    private static StationTxCommandTrustDiagnostics CreateSnapshot(
        bool verificationEnabled,
        IReadOnlyList<LoadedTrustedKey> keys)
    {
        bool available = verificationEnabled && keys.Count > 0;
        return new StationTxCommandTrustDiagnostics(
            verificationEnabled,
            available,
            keys.Count,
            keys
                .Select(key => new StationTxCommandTrustedKeyDiagnostics(
                    key.KeyId,
                    key.Fingerprint))
                .ToArray(),
            available
                ? "ready"
                : verificationEnabled
                    ? "no-trusted-keys"
                    : "disabled");
    }

    private static void DisposeKeys(IEnumerable<LoadedTrustedKey> keys)
    {
        foreach (LoadedTrustedKey key in keys)
        {
            key.Verifier.Dispose();
        }
    }

    internal sealed record LoadedTrustedKey(
        string KeyId,
        string Fingerprint,
        StationTxEcdsaCommandSignatureVerifier Verifier);
}

internal sealed class StationTxCommandKeyRingSignatureVerifier :
    IStationTxCommandSignatureVerifier,
    IDisposable
{
    private readonly bool m_enabled;
    private readonly IReadOnlyDictionary<
        string,
        StationTxCommandTrustRegistry.LoadedTrustedKey> m_keys;
    private int m_disposed;

    public StationTxCommandKeyRingSignatureVerifier(
        bool enabled,
        IEnumerable<StationTxCommandTrustRegistry.LoadedTrustedKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        m_enabled = enabled;
        m_keys = keys.ToDictionary(key => key.KeyId, StringComparer.Ordinal);
    }

    public bool IsAvailable =>
        m_enabled &&
        m_keys.Count > 0 &&
        Volatile.Read(ref m_disposed) == 0;

    public bool Verify(
        string keyId,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature) =>
        IsAvailable &&
        m_keys.TryGetValue(keyId, out var key) &&
        key.Verifier.Verify(keyId, payload, signature);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
        {
            return;
        }

        foreach (StationTxCommandTrustRegistry.LoadedTrustedKey key in
            m_keys.Values)
        {
            key.Verifier.Dispose();
        }
    }
}
