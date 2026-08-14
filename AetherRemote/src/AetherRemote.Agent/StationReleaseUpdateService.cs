using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Agent;

public sealed record StationReleaseUpdateExecution(
    StationReleaseUpdateResultMessage Result,
    bool RestartAgent);

/// <summary>
/// Executes one exact signed release request at the station. The gateway may
/// name only a release identity. This service derives every URL from the pinned
/// HTTPS gateway, re-verifies the gateway's pinned release key, verifies the
/// signed manifest and package hashes locally, writes only to a fixed staging
/// root, then asks the root updater to apply that exact staged identity. It has
/// no arbitrary-command or arbitrary-path input.
/// </summary>
public sealed partial class StationReleaseUpdateService
{
    private const int MaximumBootstrapBytes = 256 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 1024L * 1024 * 1024;
    private const string StagingRoot = "/var/lib/aetherremote/release-staging";
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions BootstrapJson =
        CreateBootstrapJson();

    private readonly AgentSettings m_settings;
    private readonly IStationReleaseUpdateLocalClient m_localUpdater;
    private readonly ILogger<StationReleaseUpdateService> m_logger;

    public StationReleaseUpdateService(
        IOptions<AgentSettings> settings,
        IStationReleaseUpdateLocalClient localUpdater,
        ILogger<StationReleaseUpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        m_settings = settings.Value;
        m_localUpdater = localUpdater ??
            throw new ArgumentNullException(nameof(localUpdater));
        m_logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StationReleaseUpdateResultMessage?> ConfirmStartupAsync(
        CancellationToken cancellationToken)
    {
        if (!m_settings.ReleaseUpdateEnabled)
        {
            return null;
        }
        string startupCorrelation = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(16));
        LocalStationReleaseUpdateRequest request = new(
            StationLocalUpdaterMessageTypes.Request,
            startupCorrelation,
            m_settings.ReleaseIdentity,
            StationLocalUpdaterActions.Confirm);
        LocalStationReleaseUpdateResult result =
            await m_localUpdater.ExecuteAsync(request, cancellationToken);
        if (!result.Succeeded ||
            !string.Equals(
                result.ActiveReleaseIdentity,
                m_settings.ReleaseIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The root station updater did not confirm this Agent release as active.");
        }
        if (string.Equals(result.Outcome, "current", StringComparison.Ordinal) &&
            string.Equals(
                result.CorrelationId,
                startupCorrelation,
                StringComparison.Ordinal) &&
            string.IsNullOrEmpty(result.CompletedReleaseIdentity))
        {
            return null;
        }
        if (string.IsNullOrEmpty(result.CompletedReleaseIdentity))
        {
            throw new InvalidDataException(
                "The root station updater returned completion evidence without the requested release identity.");
        }
        return new StationReleaseUpdateResultMessage(
            StationMessageTypes.ReleaseUpdateResult,
            result.CorrelationId,
            result.CompletedReleaseIdentity,
            !result.RolledBack,
            result.RolledBack ? "startup-rollback" : "confirmed",
            result.ActiveReleaseIdentity,
            result.RolledBack);
    }

    public async Task AcknowledgeStartupAsync(
        StationReleaseUpdateResultMessage completion,
        BrokerReleaseUpdateAcknowledgementMessage acknowledgement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (StationProtocolValidator.ValidateReleaseUpdateResult(completion)
                is not null ||
            StationProtocolValidator.ValidateReleaseUpdateAcknowledgement(
                acknowledgement) is not null ||
            !string.Equals(
                completion.CorrelationId,
                acknowledgement.CorrelationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                completion.ReleaseIdentity,
                acknowledgement.ReleaseIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The broker release-update acknowledgement does not match the durable startup completion.");
        }

        LocalStationReleaseUpdateResult result =
            await m_localUpdater.ExecuteAsync(
                new LocalStationReleaseUpdateRequest(
                    StationLocalUpdaterMessageTypes.Request,
                    completion.CorrelationId,
                    m_settings.ReleaseIdentity,
                    StationLocalUpdaterActions.Acknowledge),
                cancellationToken);
        if (!result.Succeeded ||
            !string.Equals(
                result.CorrelationId,
                completion.CorrelationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                result.CompletedReleaseIdentity,
                completion.ReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                result.ActiveReleaseIdentity,
                m_settings.ReleaseIdentity,
                StringComparison.Ordinal) ||
            result.RolledBack != completion.RolledBack)
        {
            throw new InvalidOperationException(
                "The root station updater did not acknowledge the broker-confirmed release completion.");
        }
    }

    public async Task<StationReleaseUpdateExecution> ExecuteAsync(
        BrokerReleaseUpdateMessage request,
        CancellationToken cancellationToken)
    {
        string? validation = StationProtocolValidator.ValidateReleaseUpdate(request);
        if (validation is not null)
        {
            throw new InvalidDataException(validation);
        }
        if (!m_settings.ReleaseUpdateEnabled ||
            m_settings.Capabilities?.Contains(
                StationCapabilities.ReleaseUpdateV1,
                StringComparer.Ordinal) != true)
        {
            return Failure(
                request,
                "update-disabled",
                m_settings.ReleaseIdentity,
                rolledBack: false);
        }
        if (string.Equals(
                request.ReleaseIdentity,
                m_settings.ReleaseIdentity,
                StringComparison.Ordinal))
        {
            return new StationReleaseUpdateExecution(
                new StationReleaseUpdateResultMessage(
                    StationMessageTypes.ReleaseUpdateResult,
                    request.CorrelationId,
                    request.ReleaseIdentity,
                    true,
                    "already-current",
                    m_settings.ReleaseIdentity,
                    false),
                RestartAgent: false);
        }

        string stagingDirectory = FixedStagingDirectory(request.CorrelationId);
        try
        {
            EnsureEmptyStagingDirectory(stagingDirectory);
            VerifiedStationRelease release = await DownloadAndVerifyAsync(
                request.ReleaseIdentity,
                stagingDirectory,
                cancellationToken);
            LocalStationReleaseUpdateResult applied =
                await m_localUpdater.ExecuteAsync(
                    new LocalStationReleaseUpdateRequest(
                        StationLocalUpdaterMessageTypes.Request,
                        request.CorrelationId,
                        request.ReleaseIdentity,
                        StationLocalUpdaterActions.Apply),
                    cancellationToken);
            if (!applied.Succeeded)
            {
                return Failure(
                    request,
                    applied.Outcome,
                    applied.ActiveReleaseIdentity,
                    rolledBack: false);
            }

            bool healthy = await WaitForStationEngineHealthAsync(cancellationToken);
            if (!healthy)
            {
                LocalStationReleaseUpdateResult rollback =
                    await m_localUpdater.ExecuteAsync(
                        new LocalStationReleaseUpdateRequest(
                            StationLocalUpdaterMessageTypes.Request,
                            request.CorrelationId,
                            request.ReleaseIdentity,
                            StationLocalUpdaterActions.Rollback),
                        cancellationToken);
                return Failure(
                    request,
                    rollback.Succeeded
                        ? "health-rollback"
                        : "rollback-failed",
                    rollback.ActiveReleaseIdentity,
                    rollback.Succeeded);
            }

            m_logger.LogInformation(
                "Station release {ReleaseIdentity} verified and applied; Agent restart is required",
                release.ReleaseIdentity);
            return new StationReleaseUpdateExecution(
                new StationReleaseUpdateResultMessage(
                    StationMessageTypes.ReleaseUpdateResult,
                    request.CorrelationId,
                    request.ReleaseIdentity,
                    true,
                    "applied",
                    request.ReleaseIdentity,
                    false),
                RestartAgent: true);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException or
                  InvalidDataException or InvalidOperationException or
                  CryptographicException or UnauthorizedAccessException or
                  System.Security.SecurityException)
        {
            m_logger.LogWarning(
                exception,
                "Station signed release update {ReleaseIdentity} failed closed",
                request.ReleaseIdentity);
            return Failure(
                request,
                "verification-failed",
                m_settings.ReleaseIdentity,
                rolledBack: false);
        }
    }

    private async Task<VerifiedStationRelease> DownloadAndVerifyAsync(
        string requestedReleaseIdentity,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Uri gateway = ValidateGatewayUrl(m_settings.GatewayUrl);
        byte[] pinnedKey = ReadPinnedKey();
        string pinnedFingerprint = ReadPinnedKeyFingerprint();
        string actualFingerprint = Convert.ToHexStringLower(
            SHA256.HashData(pinnedKey));
        if (!string.Equals(
                actualFingerprint,
                pinnedFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The locally pinned release verification key fingerprint is inconsistent.");
        }

        using HttpClient http = CreateHttpClient();
        byte[] bootstrapBytes = await DownloadBytesAsync(
            http,
            new Uri(gateway, "/.well-known/aethersdr"),
            MaximumBootstrapBytes,
            cancellationToken);
        EnsureNoDuplicateProperties(bootstrapBytes);
        BootstrapDocument bootstrap =
            JsonSerializer.Deserialize<BootstrapDocument>(
                bootstrapBytes,
                BootstrapJson) ??
            throw new InvalidDataException(
                "The gateway bootstrap document is empty.");
        if (bootstrap.SchemaVersion != 1 ||
            !string.Equals(
                bootstrap.ReleaseIdentity,
                requestedReleaseIdentity,
                StringComparison.Ordinal) ||
            bootstrap.ReleaseVerificationKey is null ||
            !string.Equals(
                bootstrap.ReleaseVerificationKey.Algorithm,
                "ecdsa-p256-sha256",
                StringComparison.Ordinal) ||
            !string.Equals(
                bootstrap.ReleaseVerificationKey.Sha256,
                pinnedFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The gateway bootstrap release or verification key does not match the station request.");
        }
        byte[] bootstrapKey;
        try
        {
            bootstrapKey = Convert.FromBase64String(
                bootstrap.ReleaseVerificationKey.SubjectPublicKeyInfoBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The gateway bootstrap release key is malformed.",
                exception);
        }
        if (!CryptographicOperations.FixedTimeEquals(
                bootstrapKey,
                pinnedKey))
        {
            throw new InvalidDataException(
                "The gateway bootstrap key differs from the station-pinned release key.");
        }

        string architecture = CurrentArchitectureToken();
        BootstrapArchitecture[] matches = bootstrap.Architectures
            .Where(item => string.Equals(
                item.Architecture,
                architecture,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                "The gateway does not publish one exact release for this station architecture.");
        }
        BootstrapArchitecture selected = matches[0];
        Uri manifestUri = ValidateSameGatewayUri(
            selected.ManifestUrl,
            gateway,
            $"/aetherremote/releases/{requestedReleaseIdentity}/{architecture}/manifest");
        Uri agentUri = ValidateSameGatewayUri(
            selected.AgentPackageUrl,
            gateway,
            $"/aetherremote/releases/{requestedReleaseIdentity}/{architecture}/agent");
        Uri engineUri = ValidateSameGatewayUri(
            selected.StationEnginePackageUrl,
            gateway,
            $"/aetherremote/releases/{requestedReleaseIdentity}/{architecture}/station-engine");

        string manifestPath = Path.Combine(stagingDirectory, "release-manifest.json");
        string agentPath = Path.Combine(stagingDirectory, "agent.tar.gz");
        string enginePath = Path.Combine(stagingDirectory, "station-engine.tar.gz");
        await DownloadFileAsync(
            http,
            manifestUri,
            manifestPath,
            MaximumManifestBytes,
            cancellationToken);
        await DownloadFileAsync(
            http,
            agentUri,
            agentPath,
            MaximumPackageBytes,
            cancellationToken);
        await DownloadFileAsync(
            http,
            engineUri,
            enginePath,
            MaximumPackageBytes,
            cancellationToken);

        return VerifyManifestAndPackages(
            File.ReadAllBytes(manifestPath),
            pinnedKey,
            requestedReleaseIdentity,
            architecture,
            agentPath,
            enginePath);
    }

    private static VerifiedStationRelease VerifyManifestAndPackages(
        byte[] manifestBytes,
        byte[] pinnedKey,
        string releaseIdentity,
        string architecture,
        string agentPath,
        string enginePath)
    {
        if (manifestBytes.Length is < 2 or > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                "The station release manifest size is invalid.");
        }
        EnsureNoDuplicateProperties(manifestBytes);
        using JsonDocument document = JsonDocument.Parse(manifestBytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("payload", out JsonElement payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("signature", out JsonElement signature) ||
            signature.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The station release manifest shape is invalid.");
        }
        string signedRelease = RequiredString(payload, "releaseIdentity", 96);
        string version = RequiredString(payload, "version", 96);
        string signedArchitecture = RequiredString(payload, "architecture", 32);
        string expectedArchitecture = architecture switch
        {
            "linux-x64" => "linuxX64",
            "linux-arm64" => "linuxArm64",
            _ => throw new InvalidDataException(
                "The station architecture is unsupported.")
        };
        if (!string.Equals(signedRelease, releaseIdentity, StringComparison.Ordinal) ||
            !string.Equals(
                signedArchitecture,
                expectedArchitecture,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The signed station release identity or architecture is mismatched.");
        }
        if (!payload.TryGetProperty("txSupport", out JsonElement txSupport) ||
            !txSupport.TryGetProperty("enablesTransmit", out JsonElement enablesTx) ||
            enablesTx.ValueKind != JsonValueKind.False)
        {
            throw new InvalidDataException(
                "AetherRemote station update refuses a release that declares transmit enabled.");
        }

        string algorithm = RequiredString(signature, "algorithm", 64);
        _ = RequiredString(signature, "keyId", 64);
        string signatureValue = RequiredString(signature, "value", 512);
        if (!string.Equals(
                algorithm,
                "ecdsaP256Sha256",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The signed station release uses an unsupported signature algorithm.");
        }
        byte[] signatureBytes = DecodeBase64Url(signatureValue);
        if (signatureBytes.Length != 64)
        {
            throw new InvalidDataException(
                "The signed station release ECDSA signature length is invalid.");
        }
        byte[] suffix = Encoding.UTF8.GetBytes(
            $",\"value\":\"{signatureValue}\"}}}}");
        if (!manifestBytes.AsSpan().EndsWith(suffix))
        {
            throw new InvalidDataException(
                "The station release manifest does not use the canonical signer representation.");
        }
        byte[] signingBytes = new byte[
            manifestBytes.Length - suffix.Length + 2];
        manifestBytes.AsSpan(0, manifestBytes.Length - suffix.Length)
            .CopyTo(signingBytes);
        signingBytes[^2] = (byte)'}';
        signingBytes[^1] = (byte)'}';
        using ECDsa verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(pinnedKey, out int bytesRead);
        if (bytesRead != pinnedKey.Length ||
            !verifier.VerifyData(
                signingBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new InvalidDataException(
                "The station release manifest signature is invalid.");
        }
        CryptographicOperations.ZeroMemory(signingBytes);

        if (!payload.TryGetProperty("packages", out JsonElement packages) ||
            packages.ValueKind != JsonValueKind.Array ||
            packages.GetArrayLength() != 4)
        {
            throw new InvalidDataException(
                "The station release manifest package inventory is invalid.");
        }
        Dictionary<string, SignedPackage> roles = new(StringComparer.Ordinal);
        foreach (JsonElement package in packages.EnumerateArray())
        {
            string packageIdentity = RequiredString(
                package,
                "packageIdentity",
                96);
            string role = RequiredString(package, "role", 64);
            string fileName = RequiredString(package, "fileName", 160);
            string sha256 = RequiredString(package, "sha256", 64).ToLowerInvariant();
            long length = RequiredInt64(package, "length");
            (string ExpectedIdentity, string ExpectedFileName)? expected =
                ExpectedPackageDeclaration(role, architecture);
            if (expected is null ||
                !string.Equals(
                    packageIdentity,
                    expected.Value.ExpectedIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    fileName,
                    expected.Value.ExpectedFileName,
                    StringComparison.Ordinal) ||
                !Sha256Pattern().IsMatch(sha256) ||
                length is <= 0 or > MaximumPackageBytes ||
                !roles.TryAdd(
                    role,
                    new SignedPackage(fileName, sha256, length)))
            {
                throw new InvalidDataException(
                    "The station release manifest contains an invalid package declaration.");
            }
        }
        SignedPackage agent = RequireRole(roles, "aetherRemoteAgent");
        SignedPackage engine = RequireRole(roles, "stationEngine");
        _ = RequireRole(roles, "gatewayWeb");
        _ = RequireRole(roles, "broker");
        VerifyPackageFile(agentPath, agent);
        VerifyPackageFile(enginePath, engine);
        return new VerifiedStationRelease(
            releaseIdentity,
            version,
            architecture,
            agent,
            engine);
    }

    private async Task<bool> WaitForStationEngineHealthAsync(
        CancellationToken cancellationToken)
    {
        Uri engine = new(m_settings.LocalEngineUrl, UriKind.Absolute);
        Uri health = new(engine, "/healthz");
        using HttpClient http = CreateHttpClient();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(HealthTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using HttpResponseMessage response =
                    await http.GetAsync(health, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        return false;
    }

    private byte[] ReadPinnedKey()
    {
        string path = ExactAbsoluteFile(
            m_settings.ReleaseVerificationKeyPath,
            "Agent:ReleaseVerificationKeyPath");
        FileInfo info = new(path);
        info.Refresh();
        if (!info.Exists || info.Length is < 64 or > 1024 ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.LinkTarget is not null)
        {
            throw new InvalidDataException(
                "The station release verification key file is unsafe.");
        }
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Station release updates require Linux.");
        }
        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidDataException(
                "The station release verification key file is writable by an untrusted identity.");
        }
        return File.ReadAllBytes(path);
    }

    private string ReadPinnedKeyFingerprint()
    {
        string path = ExactAbsoluteFile(
            m_settings.ReleaseVerificationKeySha256File,
            "Agent:ReleaseVerificationKeySha256File");
        string value = File.ReadAllText(path).Trim().ToLowerInvariant();
        if (!Sha256Pattern().IsMatch(value))
        {
            throw new InvalidDataException(
                "The station release verification key fingerprint is invalid.");
        }
        return value;
    }

    private static string FixedStagingDirectory(string correlationId)
    {
        if (!StationProtocolValidator.IsIdentifier(correlationId, 64) ||
            correlationId.Length != 32)
        {
            throw new InvalidDataException(
                "The release-update correlation ID is invalid for staging.");
        }
        string root = Path.GetFullPath(StagingRoot);
        string path = Path.GetFullPath(Path.Combine(root, correlationId));
        if (!string.Equals(
                Path.GetDirectoryName(path),
                root,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The release-update staging path escaped its fixed root.");
        }
        return path;
    }

    private static void EnsureEmptyStagingDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Station release updates require Linux.");
        }
        Directory.CreateDirectory(StagingRoot);
        File.SetUnixFileMode(
            StagingRoot,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new InvalidOperationException(
                "A release-update staging transaction already exists and requires reconciliation.");
        }
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
    }

    private static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseCookies = false
        };
        HttpClient client = new(handler, disposeHandler: true)
        {
            Timeout = NetworkTimeout
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("AetherRemote-Agent", "1"));
        return client;
    }

    private static async Task<byte[]> DownloadBytesAsync(
        HttpClient http,
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long declared &&
            (declared <= 0 || declared > maximumBytes))
        {
            throw new InvalidDataException(
                "A station update HTTP response declared an invalid length.");
        }
        await using Stream input =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream output = new();
        byte[] buffer = new byte[64 * 1024];
        int total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException(
                    "A station update HTTP response exceeded its byte bound.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static async Task DownloadFileAsync(
        HttpClient http,
        Uri uri,
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long declared &&
            (declared <= 0 || declared > maximumBytes))
        {
            throw new InvalidDataException(
                "A station update package declared an invalid length.");
        }
        await using Stream input =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream output = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException(
                    "A station update package exceeded its byte bound.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Station release updates require Linux.");
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static Uri ValidateGatewayUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/"))
        {
            throw new InvalidOperationException(
                "Agent:GatewayUrl must be one exact HTTPS origin.");
        }
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static Uri ValidateSameGatewayUri(
        string value,
        Uri gateway,
        string expectedPath)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Authority, gateway.Authority, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(
                "A station release asset URL escaped its fixed gateway origin/path.");
        }
        return uri;
    }

    private static string CurrentArchitectureToken() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => throw new PlatformNotSupportedException(
                "Station release updates support only x64 and arm64 Linux.")
        };

    private static void EnsureNoDuplicateProperties(ReadOnlySpan<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        ValidateElement(document.RootElement);

        static void ValidateElement(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException(
                            "A station update JSON document contains duplicate properties.");
                    }
                    ValidateElement(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    ValidateElement(child);
                }
            }
        }
    }

    private static string RequiredString(
        JsonElement element,
        string property,
        int maximumLength)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"The signed station release requires {property}.");
        }
        string text = value.GetString() ?? string.Empty;
        if (text.Length is < 1 || text.Length > maximumLength ||
            text.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"The signed station release {property} is invalid.");
        }
        return text;
    }

    private static long RequiredInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long number))
        {
            throw new InvalidDataException(
                $"The signed station release requires numeric {property}.");
        }
        return number;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        if (!Base64UrlPattern().IsMatch(value))
        {
            throw new InvalidDataException(
                "The signed station release signature encoding is invalid.");
        }
        string normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The signed station release signature encoding is invalid.",
                exception);
        }
    }

    private static (string ExpectedIdentity, string ExpectedFileName)?
        ExpectedPackageDeclaration(string role, string architecture) =>
        role switch
        {
            "gatewayWeb" => (
                "gateway-web",
                $"packages/aethersdr-gateway-{architecture}.tar.gz"),
            "broker" => (
                "broker",
                $"packages/aethersdr-broker-{architecture}.tar.gz"),
            "aetherRemoteAgent" => (
                "aetherremote-agent",
                $"packages/aetherremote-agent-{architecture}.tar.gz"),
            "stationEngine" => (
                "station-engine",
                $"packages/aethersdr-station-engine-{architecture}.tar.gz"),
            _ => null
        };

    private static SignedPackage RequireRole(
        IReadOnlyDictionary<string, SignedPackage> packages,
        string role) =>
        packages.TryGetValue(role, out SignedPackage? package)
            ? package
            : throw new InvalidDataException(
                $"The signed station release is missing {role}.");

    private static void VerifyPackageFile(string path, SignedPackage package)
    {
        FileInfo info = new(path);
        info.Refresh();
        if (!info.Exists || info.Length != package.Length)
        {
            throw new InvalidDataException(
                "A station release package length differs from its signed manifest.");
        }
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        string digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(digest, package.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A station release package digest differs from its signed manifest.");
        }
    }

    private static string ExactAbsoluteFile(string value, string label)
    {
        if (string.IsNullOrEmpty(value) ||
            !Path.IsPathFullyQualified(value) ||
            !string.Equals(Path.GetFullPath(value), value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label} must be one exact absolute path.");
        }
        return value;
    }

    private static StationReleaseUpdateExecution Failure(
        BrokerReleaseUpdateMessage request,
        string outcome,
        string activeReleaseIdentity,
        bool rolledBack) =>
        new(
            new StationReleaseUpdateResultMessage(
                StationMessageTypes.ReleaseUpdateResult,
                request.CorrelationId,
                request.ReleaseIdentity,
                false,
                NormalizeOutcome(outcome),
                string.IsNullOrEmpty(activeReleaseIdentity)
                    ? request.ReleaseIdentity
                    : activeReleaseIdentity,
                rolledBack),
            RestartAgent: false);

    private static string NormalizeOutcome(string outcome) =>
        OutcomePattern().IsMatch(outcome)
            ? outcome
            : "update-failed";

    private static JsonSerializerOptions CreateBootstrapJson() =>
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{40,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64UrlPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex OutcomePattern();

    private sealed record BootstrapDocument(
        int SchemaVersion,
        string GatewayVersion,
        string ReleaseIdentity,
        string ReleaseVersion,
        string MinimumCompatibleAgentVersion,
        string MaximumCompatibleAgentVersion,
        int MinimumStationProtocolVersion,
        int MaximumStationProtocolVersion,
        string BrokerWebSocketUrl,
        string BrokerTokenUrl,
        string EnrollmentUrl,
        string InstallerUrl,
        string InstallerSha256,
        BootstrapVerificationKey ReleaseVerificationKey,
        IReadOnlyList<BootstrapArchitecture> Architectures);

    private sealed record BootstrapVerificationKey(
        string KeyId,
        string Algorithm,
        string Sha256,
        string SubjectPublicKeyInfoBase64);

    private sealed record BootstrapArchitecture(
        string Architecture,
        string ManifestUrl,
        string AgentPackageUrl,
        string StationEnginePackageUrl);

    private sealed record SignedPackage(
        string FileName,
        string Sha256,
        long Length);

    private sealed record VerifiedStationRelease(
        string ReleaseIdentity,
        string Version,
        string Architecture,
        SignedPackage Agent,
        SignedPackage StationEngine);
}
