using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed class StationTxCommandSigningSettings
{
    public const string SectionName = "StationTxCommandSigning";

    public bool SigningEnabled { get; set; }
    public string KeyId { get; set; } = string.Empty;
    public string PrivateKeyPath { get; set; } = string.Empty;
}

public sealed record StationTxCommandSigningDiagnostics(
    bool SigningEnabled,
    bool SigningAvailable,
    bool KeyConfigured,
    string? KeyId,
    string? PublicKeyFingerprint,
    string Reason);

internal sealed record StationTxCommandSigningRequest(
    string StationId,
    string RadioId,
    string SessionId,
    string BrowserClientId,
    string LeaseId,
    string GatewayInstanceId,
    string EngineInstanceId,
    uint ClientHandle,
    StationTxCommandAction Action,
    bool Enabled);

internal interface IStationTxCommandSigner
{
    bool IsAvailable { get; }

    StationTxCommandEnvelope CreateEnvelope(
        StationTxCommandSigningRequest request);
}

/// <summary>
/// Station-scoped private-key authority for constructing signed TX command
/// envelopes. The authority has no command submission, adapter, arming, radio,
/// browser, HTTP, WebSocket, AetherRemote, watchdog, or timer entry point.
/// </summary>
public sealed class StationTxCommandSigningAuthority : IDisposable
{
    internal const int MaximumPrivateKeyFileBytes = 8192;
    internal const int MaximumPrivateKeyPathLength = 1024;

    private const UnixFileMode AllowedPrivateKeyUnixModes =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite;
    private const UnixFileMode ForbiddenDirectoryWritableUnixModes =
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;

    private readonly StationTxEcdsaCommandSigner m_signer;
    private int m_disposed;

    public StationTxCommandSigningAuthority(
        IOptions<StationTxCommandSigningSettings> options,
        ILogger<StationTxCommandSigningAuthority> logger)
        : this(options, logger, TimeProvider.System)
    {
    }

    internal StationTxCommandSigningAuthority(
        IOptions<StationTxCommandSigningSettings> options,
        ILogger<StationTxCommandSigningAuthority> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        StationTxCommandSigningSettings settings = options.Value ??
            new StationTxCommandSigningSettings();
        LoadedSigningKey? loaded = LoadKey(settings);
        try
        {
            m_signer = new StationTxEcdsaCommandSigner(
                settings.SigningEnabled,
                loaded,
                timeProvider);
        }
        catch
        {
            loaded?.Key.Dispose();
            throw;
        }

        Snapshot = CreateSnapshot(settings.SigningEnabled, loaded);
        logger.LogInformation(
            "Station TX command signing authority {State}; command ingress, " +
            "boundary enablement, adapter, arming, and transmit remain disabled",
            Snapshot.SigningAvailable ? "ready" : Snapshot.Reason);
    }

    public StationTxCommandSigningDiagnostics Snapshot { get; }

    internal IStationTxCommandSigner Signer => m_signer;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) == 0)
        {
            m_signer.Dispose();
        }
    }

    private static LoadedSigningKey? LoadKey(
        StationTxCommandSigningSettings settings)
    {
        bool keyIdConfigured = !string.IsNullOrEmpty(settings.KeyId);
        bool pathConfigured = !string.IsNullOrEmpty(settings.PrivateKeyPath);
        bool configured = keyIdConfigured || pathConfigured;
        if (!configured)
        {
            if (settings.SigningEnabled)
            {
                throw new InvalidOperationException(
                    $"{StationTxCommandSigningSettings.SectionName} requires a " +
                    "private signing key when signing is enabled.");
            }
            return null;
        }

        string keyId = ValidateKeyId(settings.KeyId);
        string path = ValidatePath(settings.PrivateKeyPath, keyId);
        byte[] privateKeyInfo = ReadPrivateKey(path, keyId);
        try
        {
            ECDsa key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(privateKeyInfo, out int bytesRead);
                if (bytesRead != privateKeyInfo.Length)
                {
                    throw new CryptographicException(
                        "The station command private key contains trailing data.");
                }

                ECParameters parameters = key.ExportParameters(
                    includePrivateParameters: false);
                if (!string.Equals(
                        parameters.Curve.Oid.Value,
                        "1.2.840.10045.3.1.7",
                        StringComparison.Ordinal) ||
                    parameters.Q.X?.Length != 32 ||
                    parameters.Q.Y?.Length != 32 ||
                    key.KeySize != 256)
                {
                    throw new CryptographicException(
                        "Station command signatures require an ECDSA P-256 key.");
                }

                byte[] subjectPublicKeyInfo = key.ExportSubjectPublicKeyInfo();
                try
                {
                    string fingerprint = Convert.ToHexString(
                        SHA256.HashData(subjectPublicKeyInfo).AsSpan(0, 12));
                    return new LoadedSigningKey(keyId, fingerprint, key);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(subjectPublicKeyInfo);
                }
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }
        catch (Exception exception)
            when (exception is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Station TX command signing key '{keyId}' is not a valid " +
                "unencrypted PKCS#8 ECDSA P-256 private key.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyInfo);
        }
    }

    private static string ValidateKeyId(string? value)
    {
        string keyId = value?.Trim() ?? string.Empty;
        if (keyId.Length is 0 or > 64 ||
            !string.Equals(value, keyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The station TX command signing key requires a canonical key ID " +
                "from 1 through 64 characters.");
        }

        foreach (char character in keyId)
        {
            if (!(character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or '-' or '_' or '.'))
            {
                throw new InvalidOperationException(
                    "The station TX command signing key ID contains an " +
                    "unsupported character.");
            }
        }
        return keyId;
    }

    private static string ValidatePath(string? value, string keyId)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length is 0 or > MaximumPrivateKeyPathLength ||
            !string.Equals(value, path, StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                $"Station TX command signing key '{keyId}' requires an absolute " +
                "canonical PrivateKeyPath.");
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
                    $"Station TX command signing key '{keyId}' PrivateKeyPath " +
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
                $"Station TX command signing key '{keyId}' has an invalid " +
                "PrivateKeyPath.",
                exception);
        }
    }

    private static byte[] ReadPrivateKey(string path, string keyId)
    {
        try
        {
            FileInfo info = new(path);
            ValidatePrivateKeyFile(info, keyId);
            long expectedLength = info.Length;

            byte[] fileBytes = File.ReadAllBytes(path);
            try
            {
                info.Refresh();
                ValidatePrivateKeyFile(info, keyId);
                if (fileBytes.Length != expectedLength ||
                    fileBytes.Length != info.Length)
                {
                    throw new InvalidOperationException(
                        $"Station TX command signing key '{keyId}' changed while " +
                        "it was being read.");
                }

                UTF8Encoding encoding = new(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true);
                char[] characters = new char[encoding.GetMaxCharCount(fileBytes.Length)];
                try
                {
                    int characterCount = encoding.GetChars(
                        fileBytes.AsSpan(),
                        characters.AsSpan());
                    return DecodeExactPrivateKeyPem(
                        characters.AsSpan(0, characterCount),
                        keyId);
                }
                finally
                {
                    Array.Clear(characters);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileBytes);
            }
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
                $"Station TX command signing key '{keyId}' could not be loaded.",
                exception);
        }
    }

    private static void ValidatePrivateKeyFile(FileInfo info, string keyId)
    {
        info.Refresh();
        if (!info.Exists)
        {
            throw new InvalidOperationException(
                $"Station TX command signing key '{keyId}' does not exist.");
        }
        if ((info.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            info.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"Station TX command signing key '{keyId}' must be a regular, " +
                "non-symlink file.");
        }
        if (info.Length is <= 0 or > MaximumPrivateKeyFileBytes)
        {
            throw new InvalidOperationException(
                $"Station TX command signing key '{keyId}' must be from 1 " +
                $"through {MaximumPrivateKeyFileBytes} bytes.");
        }

        ValidateContainingDirectory(info, keyId);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(info.FullName);
            if ((mode & UnixFileMode.UserRead) == 0 ||
                (mode & ~AllowedPrivateKeyUnixModes) != 0)
            {
                throw new InvalidOperationException(
                    $"Station TX command signing key '{keyId}' must use Unix " +
                    "mode 0400 or 0600.");
            }
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
                $"Station TX command signing key '{keyId}' has no containing " +
                "directory.");
        }

        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"Station TX command signing key '{keyId}' must be stored in a " +
                "regular, non-symlink directory.");
        }
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(directory.FullName) &
                ForbiddenDirectoryWritableUnixModes) != 0)
        {
            throw new InvalidOperationException(
                $"The directory containing station TX command signing key " +
                $"'{keyId}' must not be writable by group or other users.");
        }
    }

    private static byte[] DecodeExactPrivateKeyPem(
        ReadOnlySpan<char> pem,
        string keyId)
    {
        if (!PemEncoding.TryFind(pem, out PemFields fields))
        {
            throw new InvalidOperationException(
                $"Station TX command signing key '{keyId}' is not PEM encoded.");
        }

        (int locationOffset, int locationLength) =
            fields.Location.GetOffsetAndLength(pem.Length);
        if (!pem[..locationOffset].Trim().IsEmpty ||
            !pem[(locationOffset + locationLength)..].Trim().IsEmpty)
        {
            throw new InvalidOperationException(
                $"Station TX command signing key '{keyId}' must contain exactly " +
                "one PEM block and no other data.");
        }
        if (!pem[fields.Label].SequenceEqual("PRIVATE KEY"))
        {
            throw new InvalidOperationException(
                $"Station TX command signing key '{keyId}' must use one " +
                "unencrypted PKCS#8 PRIVATE KEY PEM block.");
        }

        ReadOnlySpan<char> base64 = pem[fields.Base64Data];
        byte[] decoded = new byte[((base64.Length + 3) / 4) * 3];
        if (!Convert.TryFromBase64Chars(base64, decoded, out int bytesWritten) ||
            bytesWritten <= 0)
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new InvalidOperationException(
                $"Station TX command signing key '{keyId}' has invalid PEM data.");
        }

        if (bytesWritten == decoded.Length)
        {
            return decoded;
        }

        byte[] exact = decoded.AsSpan(0, bytesWritten).ToArray();
        CryptographicOperations.ZeroMemory(decoded);
        return exact;
    }

    private static StationTxCommandSigningDiagnostics CreateSnapshot(
        bool signingEnabled,
        LoadedSigningKey? key)
    {
        bool available = signingEnabled && key is not null;
        return new StationTxCommandSigningDiagnostics(
            signingEnabled,
            available,
            KeyConfigured: key is not null,
            KeyId: key?.KeyId,
            PublicKeyFingerprint: key?.PublicKeyFingerprint,
            Reason: available
                ? "ready"
                : signingEnabled
                    ? "signing-key-unavailable"
                    : "disabled");
    }

    internal sealed record LoadedSigningKey(
        string KeyId,
        string PublicKeyFingerprint,
        ECDsa Key);
}

internal sealed class StationTxEcdsaCommandSigner :
    IStationTxCommandSigner,
    IDisposable
{
    internal static readonly TimeSpan EnvelopeLifetime =
        TimeSpan.FromSeconds(5);

    private readonly object m_gate = new();
    private readonly bool m_enabled;
    private readonly StationTxCommandSigningAuthority.LoadedSigningKey? m_key;
    private readonly TimeProvider m_timeProvider;
    private long m_sequence;
    private int m_disposed;

    public StationTxEcdsaCommandSigner(
        bool enabled,
        StationTxCommandSigningAuthority.LoadedSigningKey? key,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        m_enabled = enabled;
        m_key = key;
        m_timeProvider = timeProvider;
    }

    public bool IsAvailable =>
        m_enabled &&
        m_key is not null &&
        Volatile.Read(ref m_disposed) == 0;

    public StationTxCommandEnvelope CreateEnvelope(
        StationTxCommandSigningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        lock (m_gate)
        {
            if (!IsAvailable || m_key is null)
            {
                throw new InvalidOperationException(
                    "The station TX command signing authority is unavailable.");
            }

            long sequence = checked(++m_sequence);
            DateTimeOffset observedAt = m_timeProvider.GetUtcNow();
            DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                observedAt.ToUnixTimeMilliseconds());
            StationTxCommandEnvelope unsigned = new(
                StationTxCommandBoundary.ProtocolVersion,
                m_key.KeyId,
                Guid.NewGuid().ToString("N"),
                sequence,
                issuedAt,
                issuedAt + EnvelopeLifetime,
                request.StationId,
                request.RadioId,
                request.SessionId,
                request.BrowserClientId,
                request.LeaseId,
                request.GatewayInstanceId,
                request.EngineInstanceId,
                request.ClientHandle,
                request.Action,
                request.Enabled,
                Signature: string.Empty);

            byte[] payload = StationTxCommandBoundary.CreateSigningPayload(unsigned);
            try
            {
                byte[] signature = m_key.Key.SignData(
                    payload,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                try
                {
                    string encoded = Convert.ToBase64String(signature)
                        .TrimEnd('=')
                        .Replace('+', '-')
                        .Replace('/', '_');
                    return unsigned with { Signature = encoded };
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(signature);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    public void Dispose()
    {
        lock (m_gate)
        {
            if (Interlocked.Exchange(ref m_disposed, 1) == 0)
            {
                m_key?.Key.Dispose();
            }
        }
    }

    private static void ValidateRequest(StationTxCommandSigningRequest request)
    {
        if (!IsCanonicalIdentifier(request.StationId) ||
            !IsCanonicalIdentifier(request.RadioId) ||
            !IsCanonicalIdentifier(request.SessionId) ||
            !IsCanonicalIdentifier(request.BrowserClientId) ||
            !IsCanonicalIdentifier(request.LeaseId) ||
            !IsCanonicalIdentifier(request.GatewayInstanceId) ||
            !IsCanonicalIdentifier(request.EngineInstanceId))
        {
            throw new ArgumentException(
                "One or more station TX command signing identities are invalid.",
                nameof(request));
        }
        if (request.ClientHandle == 0)
        {
            throw new ArgumentException(
                "The protected FLEX client handle is required.",
                nameof(request));
        }
        if (request.Action != StationTxCommandAction.SetTransmit)
        {
            throw new ArgumentException(
                "The station TX command signing action is invalid.",
                nameof(request));
        }
    }

    private static bool IsCanonicalIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                return false;
            }
        }
        return true;
    }
}
