using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public sealed class ReleaseManifestTrustSettings
{
    public const string SectionName = "ReleaseManifestTrust";

    public bool VerificationEnabled { get; set; }
    public ReleaseManifestTrustKeySettings[] Keys { get; set; } = [];
}

public sealed class ReleaseManifestTrustKeySettings
{
    public string KeyId { get; set; } = string.Empty;
    public ReleaseManifestSignatureAlgorithm Algorithm { get; set; }
    public string PublicKeyPath { get; set; } = string.Empty;
}

public sealed record ReleaseManifestTrustedKeyDiagnostics(
    string KeyId,
    ReleaseManifestSignatureAlgorithm Algorithm,
    string Fingerprint);

public sealed record ReleaseManifestTrustDiagnostics(
    bool VerificationEnabled,
    bool SignatureVerificationAvailable,
    int TrustedKeyCount,
    IReadOnlyList<ReleaseManifestTrustedKeyDiagnostics> TrustedKeys,
    string Reason);

public sealed record SignedReleaseManifestVerificationServiceDiagnostics(
    bool Registered,
    bool LocalVerificationAvailable,
    bool NetworkDownloadRegistered,
    bool InstallationRegistered,
    bool ActivationRegistered,
    string Reason);

/// <summary>
/// Immutable local registry of reviewed public keys used only to verify signed
/// release manifests. It owns no private key, signer, network client, installer,
/// extractor, active-release switch, service control, migration, backup, radio,
/// watchdog, command, or transmit authority.
/// </summary>
public sealed class ReleaseManifestTrustRegistry
{
    internal const int MaximumTrustedKeys = 8;
    internal const int MaximumPublicKeyFileBytes = 4096;
    internal const int MaximumPublicKeyPathLength = 1024;

    private const UnixFileMode ForbiddenWritableUnixModes =
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;

    private readonly ReleaseManifestVerificationKey[] m_verificationKeys;

    public ReleaseManifestTrustRegistry(
        IOptions<ReleaseManifestTrustSettings> options,
        ILogger<ReleaseManifestTrustRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        ReleaseManifestTrustSettings settings = options.Value ??
            new ReleaseManifestTrustSettings();
        LoadedTrustedKey[] keys = LoadKeys(settings);
        m_verificationKeys = keys
            .Select(key => key.VerificationKey)
            .ToArray();
        Snapshot = CreateSnapshot(settings.VerificationEnabled, keys);

        logger.LogInformation(
            "Signed release manifest verification {State} with {KeyCount} " +
            "trusted public keys; network download, installation, activation, " +
            "service control, radio, watchdog, command, and TX callers remain absent",
            Snapshot.SignatureVerificationAvailable ? "ready" : Snapshot.Reason,
            Snapshot.TrustedKeyCount);
    }

    public ReleaseManifestTrustDiagnostics Snapshot { get; }

    internal IReadOnlyCollection<ReleaseManifestVerificationKey>
        VerificationKeys => m_verificationKeys;

    private static LoadedTrustedKey[] LoadKeys(
        ReleaseManifestTrustSettings settings)
    {
        ReleaseManifestTrustKeySettings[] configured = settings.Keys ?? [];
        if (configured.Length > MaximumTrustedKeys)
        {
            throw new InvalidOperationException(
                $"{ReleaseManifestTrustSettings.SectionName}:Keys supports at " +
                $"most {MaximumTrustedKeys} trusted public keys.");
        }
        if (settings.VerificationEnabled && configured.Length == 0)
        {
            throw new InvalidOperationException(
                $"{ReleaseManifestTrustSettings.SectionName}:Keys must contain " +
                "at least one trusted public key when verification is enabled.");
        }

        HashSet<string> keyIds = new(StringComparer.Ordinal);
        HashSet<string> paths = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        List<LoadedTrustedKey> loaded = [];

        foreach (ReleaseManifestTrustKeySettings? configuredKey in configured)
        {
            if (configuredKey is null)
            {
                throw new InvalidOperationException(
                    $"{ReleaseManifestTrustSettings.SectionName}:Keys contains " +
                    "a null entry.");
            }

            string keyId = ValidateKeyId(configuredKey.KeyId);
            if (!keyIds.Add(keyId))
            {
                throw new InvalidOperationException(
                    $"Duplicate signed release manifest trust key ID '{keyId}'.");
            }

            ReleaseManifestSignatureAlgorithm algorithm =
                ValidateAlgorithm(configuredKey.Algorithm, keyId);
            string path = ValidatePath(configuredKey.PublicKeyPath, keyId);
            if (!paths.Add(path))
            {
                throw new InvalidOperationException(
                    "Each signed release manifest trust key must use a distinct " +
                    "public-key file.");
            }

            byte[] subjectPublicKeyInfo = ReadPublicKey(path, keyId);
            try
            {
                ValidatePublicKey(subjectPublicKeyInfo, algorithm, keyId);
                loaded.Add(new LoadedTrustedKey(
                    new ReleaseManifestVerificationKey(
                        keyId,
                        algorithm,
                        subjectPublicKeyInfo),
                    Convert.ToHexString(
                        SHA256.HashData(subjectPublicKeyInfo).AsSpan(0, 12))));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(subjectPublicKeyInfo);
            }
        }

        return [.. loaded];
    }

    private static string ValidateKeyId(string? value)
    {
        string keyId = value?.Trim() ?? string.Empty;
        if (keyId.Length is 0 or > 64 ||
            !string.Equals(value, keyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Each signed release manifest trust key requires a canonical " +
                "key ID from 1 through 64 characters.");
        }

        foreach (char character in keyId)
        {
            if (!(character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or '-' or '_' or '.'))
            {
                throw new InvalidOperationException(
                    "A signed release manifest trust key ID contains an " +
                    "unsupported character.");
            }
        }

        return keyId;
    }

    private static ReleaseManifestSignatureAlgorithm ValidateAlgorithm(
        ReleaseManifestSignatureAlgorithm algorithm,
        string keyId)
    {
        if (algorithm != ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256)
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' uses an " +
                "unsupported verification algorithm.");
        }

        return algorithm;
    }

    private static string ValidatePath(string? value, string keyId)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length is 0 or > MaximumPublicKeyPathLength ||
            !string.Equals(value, path, StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' requires an " +
                "absolute canonical PublicKeyPath.");
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
                    $"Signed release manifest trust key '{keyId}' PublicKeyPath " +
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
                $"Signed release manifest trust key '{keyId}' has an invalid " +
                "PublicKeyPath.",
                exception);
        }
    }

    private static byte[] ReadPublicKey(string path, string keyId)
    {
        try
        {
            FileInfo before = new(path);
            before.Refresh();
            ValidateKeyFile(before, keyId);
            ValidateContainingDirectory(before, keyId);

            long expectedLength = before.Length;
            DateTime expectedLastWriteUtc = before.LastWriteTimeUtc;
            byte[] fileBytes = new byte[checked((int)expectedLength)];
            try
            {
                using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);
                if (stream.Length != expectedLength)
                {
                    throw ChangedWhileReading(keyId);
                }
                stream.ReadExactly(fileBytes);
                if (stream.ReadByte() != -1)
                {
                    throw ChangedWhileReading(keyId);
                }

                FileInfo after = new(path);
                after.Refresh();
                ValidateKeyFile(after, keyId);
                if (after.Length != expectedLength ||
                    after.LastWriteTimeUtc != expectedLastWriteUtc)
                {
                    throw ChangedWhileReading(keyId);
                }

                string pem = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(fileBytes);
                return DecodeExactPublicKeyPem(pem, keyId);
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
                CryptographicException or DecoderFallbackException or
                OverflowException)
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' could not be loaded.",
                exception);
        }
    }

    private static void ValidateKeyFile(FileInfo info, string keyId)
    {
        if (!info.Exists)
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' does not exist.");
        }
        if ((info.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            info.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' must be a regular, " +
                "non-symlink file.");
        }
        if (info.Length is <= 0 or > MaximumPublicKeyFileBytes)
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' must be from 1 " +
                $"through {MaximumPublicKeyFileBytes} bytes.");
        }
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(info.FullName) & ForbiddenWritableUnixModes) != 0)
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' must not be " +
                "writable by group or other users.");
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
                $"Signed release manifest trust key '{keyId}' has no containing " +
                "directory.");
        }

        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' must be stored in " +
                "a regular, non-symlink directory.");
        }
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(directory.FullName) &
                ForbiddenWritableUnixModes) != 0)
        {
            throw new InvalidOperationException(
                "The directory containing signed release manifest trust key " +
                $"'{keyId}' must not be writable by group or other users.");
        }
    }

    private static byte[] DecodeExactPublicKeyPem(string pem, string keyId)
    {
        ReadOnlySpan<char> span = pem.AsSpan();
        if (!PemEncoding.TryFind(span, out PemFields fields))
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' is not PEM encoded.");
        }

        (int locationOffset, int locationLength) =
            fields.Location.GetOffsetAndLength(span.Length);
        if (!span[..locationOffset].Trim().IsEmpty ||
            !span[(locationOffset + locationLength)..].Trim().IsEmpty)
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' must contain " +
                "exactly one PEM block and no other data.");
        }
        if (!span[fields.Label].SequenceEqual("PUBLIC KEY"))
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' must use a PUBLIC " +
                "KEY PEM block; private keys are forbidden.");
        }

        try
        {
            return Convert.FromBase64String(span[fields.Base64Data].ToString());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' has invalid PEM " +
                "data.",
                exception);
        }
    }

    private static void ValidatePublicKey(
        ReadOnlySpan<byte> subjectPublicKeyInfo,
        ReleaseManifestSignatureAlgorithm algorithm,
        string keyId)
    {
        try
        {
            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(
                subjectPublicKeyInfo,
                out int bytesRead);
            ECParameters parameters = verifier.ExportParameters(false);
            if (algorithm != ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256 ||
                bytesRead != subjectPublicKeyInfo.Length ||
                verifier.KeySize != 256 ||
                !string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Signed release manifest trust key '{keyId}' is not a valid " +
                    "ECDSA P-256 public key.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Signed release manifest trust key '{keyId}' is not a valid " +
                "ECDSA P-256 public key.",
                exception);
        }
    }

    private static InvalidOperationException ChangedWhileReading(string keyId) =>
        new(
            $"Signed release manifest trust key '{keyId}' changed while it was " +
            "being read.");

    private static ReleaseManifestTrustDiagnostics CreateSnapshot(
        bool verificationEnabled,
        IReadOnlyList<LoadedTrustedKey> keys)
    {
        bool available = verificationEnabled && keys.Count > 0;
        return new ReleaseManifestTrustDiagnostics(
            verificationEnabled,
            available,
            keys.Count,
            keys
                .Select(key => new ReleaseManifestTrustedKeyDiagnostics(
                    key.VerificationKey.KeyId,
                    key.VerificationKey.Algorithm,
                    key.Fingerprint))
                .ToArray(),
            available
                ? "ready"
                : verificationEnabled
                    ? "no-trusted-keys"
                    : "disabled");
    }

    private sealed record LoadedTrustedKey(
        ReleaseManifestVerificationKey VerificationKey,
        string Fingerprint);
}

/// <summary>
/// Typed local-only composition of the signed release manifest verifier and the
/// production public-key trust registry. It has no network, filesystem mutation,
/// installation, extraction, activation, service, migration, backup, radio,
/// watchdog, command, or transmit method.
/// </summary>
public sealed class SignedReleaseManifestVerificationService
{
    private readonly ReleaseManifestTrustRegistry m_trustRegistry;
    private readonly SignedReleaseManifestVerifier m_verifier;

    public SignedReleaseManifestVerificationService(
        ReleaseManifestTrustRegistry trustRegistry,
        SignedReleaseManifestVerifier verifier)
    {
        m_trustRegistry = trustRegistry ??
            throw new ArgumentNullException(nameof(trustRegistry));
        m_verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));

        ReleaseManifestTrustDiagnostics trust = m_trustRegistry.Snapshot;
        Snapshot = new SignedReleaseManifestVerificationServiceDiagnostics(
            Registered: true,
            LocalVerificationAvailable: trust.SignatureVerificationAvailable,
            NetworkDownloadRegistered: false,
            InstallationRegistered: false,
            ActivationRegistered: false,
            Reason: trust.SignatureVerificationAvailable
                ? "ready"
                : trust.Reason);
    }

    public SignedReleaseManifestVerificationServiceDiagnostics Snapshot { get; }

    public ReleaseManifestVerificationReport VerifyLocal(
        ReadOnlyMemory<byte> manifestUtf8,
        IReadOnlyCollection<LocalImmutableReleasePackage> localPackages,
        ReleaseManifestVerificationContext context) =>
        VerifyLocalDetailed(manifestUtf8, localPackages, context).Report;

    internal SignedReleaseManifestVerificationResult VerifyLocalDetailed(
        ReadOnlyMemory<byte> manifestUtf8,
        IReadOnlyCollection<LocalImmutableReleasePackage> localPackages,
        ReleaseManifestVerificationContext context)
    {
        ReleaseManifestTrustDiagnostics trust = m_trustRegistry.Snapshot;
        if (!trust.VerificationEnabled)
        {
            return SignedReleaseManifestVerificationResult.Failure(
                ReleaseManifestVerificationReport.Failure(
                    ReleaseManifestFailureCode.VerificationTrustDisabled,
                    "Signed release manifest verification trust is disabled."));
        }
        if (!trust.SignatureVerificationAvailable)
        {
            return SignedReleaseManifestVerificationResult.Failure(
                ReleaseManifestVerificationReport.Failure(
                    ReleaseManifestFailureCode.VerificationTrustUnavailable,
                    "Signed release manifest verification trust is unavailable."));
        }

        return m_verifier.VerifyDetailed(
            manifestUtf8,
            localPackages,
            context,
            m_trustRegistry.VerificationKeys);
    }
}
