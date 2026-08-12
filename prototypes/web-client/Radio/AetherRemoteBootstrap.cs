using System.Security.Cryptography;
using System.Text;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed record AetherRemoteBootstrapSettings
{
    public const string SectionName = "AetherRemoteBootstrap";

    public bool Enabled { get; init; }
}

public sealed record AetherRemoteBootstrapVerificationKey(
    string KeyId,
    string Algorithm,
    string Sha256,
    string SubjectPublicKeyInfoBase64);

public sealed record AetherRemoteBootstrapArchitecture(
    string Architecture,
    string ManifestUrl,
    string AgentPackageUrl,
    string StationEnginePackageUrl);

public sealed record AetherRemoteBootstrapDocument(
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
    AetherRemoteBootstrapVerificationKey ReleaseVerificationKey,
    IReadOnlyList<AetherRemoteBootstrapArchitecture> Architectures);

public sealed record AetherRemoteBootstrapAdminGuide(
    bool Enabled,
    bool Ready,
    string ReleaseIdentity,
    string ReleaseVersion,
    string GatewayUrl,
    string InstallerUrl,
    string InstallerSha256,
    string ReleaseKeySha256,
    string InstallCommand,
    IReadOnlyList<string> Architectures,
    string Message);

public sealed record AetherRemoteBootstrapAsset(
    string Path,
    string ContentType,
    string DownloadName);

/// <summary>
/// Exposes only the exact locally persisted release bundle selected by the
/// gateway's active release. Every served manifest/package is re-verified
/// against the local release trust registry before its path is returned. This
/// service has no network client, enrollment secret, station credential,
/// installer mutation, radio, command, lease, watchdog, or TX authority.
/// </summary>
public sealed class AetherRemoteBootstrapService
{
    public const int DocumentSchemaVersion = 1;
    public const string InstallerRoute = "/aetherremote/install";
    public const string WellKnownRoute = "/.well-known/aethersdr";

    private const string InstallerRelativePath =
        "bootstrap/aetherremote-install.sh";
    private const int MaximumInstallerBytes = 256 * 1024;
    private const int ApplicationReleaseProtocolVersion = 2;

    private readonly AetherRemoteBootstrapSettings m_settings;
    private readonly InstallationRuntimeSettings m_runtimeSettings;
    private readonly InstallationPaths m_paths;
    private readonly ReleaseInstallationStatusReader m_statusReader;
    private readonly LocalOfflineReleaseBundleVerificationService
        m_bundleVerifier;
    private readonly ReleaseManifestTrustRegistry m_trustRegistry;
    private readonly string m_contentRoot;

    public AetherRemoteBootstrapService(
        IOptions<AetherRemoteBootstrapSettings> settings,
        IOptions<InstallationRuntimeSettings> runtimeSettings,
        InstallationPaths paths,
        ReleaseInstallationStatusReader statusReader,
        LocalOfflineReleaseBundleVerificationService bundleVerifier,
        ReleaseManifestTrustRegistry trustRegistry,
        IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        m_settings = settings.Value ?? new AetherRemoteBootstrapSettings();
        m_runtimeSettings = runtimeSettings.Value ?? new InstallationRuntimeSettings();
        m_paths = paths ?? throw new ArgumentNullException(nameof(paths));
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_bundleVerifier = bundleVerifier ??
            throw new ArgumentNullException(nameof(bundleVerifier));
        m_trustRegistry = trustRegistry ??
            throw new ArgumentNullException(nameof(trustRegistry));
        ArgumentNullException.ThrowIfNull(environment);
        m_contentRoot = Path.GetFullPath(environment.ContentRootPath);
        InstallationPaths.Validate(m_paths);
        ValidateConfiguration();
    }

    public bool Enabled => m_settings.Enabled;

    public async Task<AetherRemoteBootstrapDocument> GetDocumentAsync(
        CancellationToken cancellationToken = default)
    {
        BootstrapSnapshot snapshot = await LoadSnapshotAsync(cancellationToken);
        return snapshot.Document;
    }

    public async Task<AetherRemoteBootstrapAdminGuide> GetAdminGuideAsync(
        string? stationId,
        CancellationToken cancellationToken = default)
    {
        if (!m_settings.Enabled)
        {
            return new AetherRemoteBootstrapAdminGuide(
                false,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                InstallerRoute,
                string.Empty,
                string.Empty,
                string.Empty,
                [],
                "AetherRemote bootstrap publication is disabled.");
        }

        try
        {
            BootstrapSnapshot snapshot = await LoadSnapshotAsync(cancellationToken);
            string command = string.Empty;
            if (!string.IsNullOrEmpty(stationId))
            {
                RemoteStationManagementValidator.ValidateStationId(stationId);
                command = BuildInstallCommand(
                    snapshot.Document,
                    CanonicalGatewayUrl(),
                    stationId);
            }
            return new AetherRemoteBootstrapAdminGuide(
                true,
                true,
                snapshot.Document.ReleaseIdentity,
                snapshot.Document.ReleaseVersion,
                CanonicalGatewayUrl(),
                snapshot.Document.InstallerUrl,
                snapshot.Document.InstallerSha256,
                snapshot.Document.ReleaseVerificationKey.Sha256,
                command,
                snapshot.Document.Architectures
                    .Select(entry => entry.Architecture)
                    .ToArray(),
                "The active signed release is ready for guided AetherRemote bootstrap.");
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or IOException or
                  InvalidDataException or UnauthorizedAccessException or
                  System.Security.SecurityException)
        {
            return new AetherRemoteBootstrapAdminGuide(
                true,
                false,
                string.Empty,
                string.Empty,
                CanonicalGatewayUrl(),
                AbsoluteUrl(InstallerRoute),
                string.Empty,
                string.Empty,
                string.Empty,
                [],
                exception.Message);
        }
    }

    public async Task<AetherRemoteBootstrapAsset?> ResolveReleaseAssetAsync(
        string releaseIdentity,
        string architecture,
        string asset,
        CancellationToken cancellationToken = default)
    {
        BootstrapSnapshot snapshot = await LoadSnapshotAsync(cancellationToken);
        if (!string.Equals(
                releaseIdentity,
                snapshot.Document.ReleaseIdentity,
                StringComparison.Ordinal))
        {
            return null;
        }
        VerifiedBootstrapBundle? bundle = snapshot.Bundles.FirstOrDefault(
            candidate => string.Equals(
                candidate.Architecture,
                architecture,
                StringComparison.Ordinal));
        if (bundle is null)
        {
            return null;
        }

        return asset switch
        {
            "manifest" => new AetherRemoteBootstrapAsset(
                bundle.ManifestPath,
                "application/json",
                Path.GetFileName(bundle.ManifestPath)),
            "agent" => new AetherRemoteBootstrapAsset(
                bundle.AgentPackagePath,
                "application/gzip",
                Path.GetFileName(bundle.AgentPackagePath)),
            "station-engine" => new AetherRemoteBootstrapAsset(
                bundle.StationEnginePackagePath,
                "application/gzip",
                Path.GetFileName(bundle.StationEnginePackagePath)),
            _ => null
        };
    }

    public AetherRemoteBootstrapAsset ResolveInstallerAsset()
    {
        EnsureEnabled();
        string path = InstallerPath();
        ValidateRegularFile(path, MaximumInstallerBytes, "bootstrap installer");
        return new AetherRemoteBootstrapAsset(
            path,
            "text/x-shellscript; charset=utf-8",
            "aetherremote-install.sh");
    }

    private async Task<BootstrapSnapshot> LoadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ReleaseStatusReadResult status =
            await m_statusReader.ReadAsync(cancellationToken);
        if (!status.Succeeded ||
            !status.SetupComplete ||
            !status.CurrentPointerPresent ||
            string.IsNullOrEmpty(status.ActiveReleaseIdentity))
        {
            throw new InvalidOperationException(
                "AetherRemote bootstrap requires one complete active gateway release.");
        }

        string releaseIdentity = InstallationReleaseIdentity.Parse(
            status.ActiveReleaseIdentity);
        ValidateRunningGatewayContentRoot(releaseIdentity);
        List<VerifiedBootstrapBundle> bundles = [];
        foreach ((string token, ReleaseManifestArchitecture architecture) in
                 SupportedArchitectures())
        {
            VerifiedBootstrapBundle? bundle = VerifyBundle(
                releaseIdentity,
                token,
                architecture,
                status);
            if (bundle is not null)
            {
                bundles.Add(bundle);
            }
        }
        if (bundles.Count == 0)
        {
            throw new InvalidOperationException(
                "No locally verified AetherRemote architecture bundle is available for the active release.");
        }
        if (bundles.Select(bundle => bundle.Version)
                .Distinct(StringComparer.Ordinal).Count() != 1 ||
            bundles.Select(bundle => bundle.KeyId)
                .Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new InvalidDataException(
                "The hosted AetherRemote architecture bundles do not describe one exact signed release.");
        }

        ReleaseManifestVerificationKey key = GetVerificationKey(
            bundles[0].KeyId);
        string keyFingerprint = Convert.ToHexString(
            SHA256.HashData(key.SubjectPublicKeyInfo)).ToLowerInvariant();
        string installerPath = InstallerPath();
        ValidateRegularFile(
            installerPath,
            MaximumInstallerBytes,
            "bootstrap installer");
        string installerSha256 = HashFile(installerPath);
        string gatewayVersion =
            typeof(AetherRemoteBootstrapService).Assembly.GetName().Version?
                .ToString(3) ?? "0.0.0";

        AetherRemoteBootstrapDocument document = new(
            DocumentSchemaVersion,
            gatewayVersion,
            releaseIdentity,
            bundles[0].Version,
            bundles[0].Version,
            bundles[0].Version,
            bundles.Min(bundle => bundle.MinimumProtocolVersion),
            bundles.Max(bundle => bundle.MaximumProtocolVersion),
            BrokerWebSocketUrl(),
            AbsoluteUrl("/aetherremote/broker/station/v1/token"),
            AbsoluteUrl("/api/station-enrollment/redeem"),
            AbsoluteUrl(InstallerRoute),
            installerSha256,
            new AetherRemoteBootstrapVerificationKey(
                key.KeyId,
                key.Algorithm ==
                    ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256
                    ? "ecdsa-p256-sha256"
                    : throw new InvalidDataException(
                        "AetherRemote bootstrap requires the ECDSA P-256 release verification key used by station installers."),
                keyFingerprint,
                Convert.ToBase64String(key.SubjectPublicKeyInfo)),
            bundles.Select(bundle => new AetherRemoteBootstrapArchitecture(
                    bundle.Architecture,
                    ReleaseAssetUrl(releaseIdentity, bundle.Architecture, "manifest"),
                    ReleaseAssetUrl(releaseIdentity, bundle.Architecture, "agent"),
                    ReleaseAssetUrl(
                        releaseIdentity,
                        bundle.Architecture,
                        "station-engine")))
                .ToArray());
        return new BootstrapSnapshot(document, bundles);
    }

    private VerifiedBootstrapBundle? VerifyBundle(
        string releaseIdentity,
        string architectureToken,
        ReleaseManifestArchitecture architecture,
        ReleaseStatusReadResult status)
    {
        string root = Path.GetFullPath(m_paths.ReleaseDownloadDirectory);
        string bundleDirectory = Path.GetFullPath(
            Path.Combine(root, $"{releaseIdentity}-{architectureToken}"));
        if (!string.Equals(
                Path.GetDirectoryName(bundleDirectory),
                root,
                StringComparison.Ordinal) ||
            !Directory.Exists(bundleDirectory))
        {
            return null;
        }

        string manifestPath = Path.Combine(
            bundleDirectory,
            $"release-manifest-{architectureToken}.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }
        ValidateRegularFile(
            manifestPath,
            SignedReleaseManifestJson.MaximumManifestBytes,
            "release manifest");
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        SignedReleaseManifestDocument document =
            SignedReleaseManifestJson.Deserialize(manifestBytes) ??
            throw new InvalidDataException(
                "The hosted AetherRemote release manifest is invalid.");
        SignedReleaseManifestPayload payload = document.Payload ??
            throw new InvalidDataException(
                "The hosted AetherRemote release manifest has no payload.");
        if (!string.Equals(
                payload.ReleaseIdentity,
                releaseIdentity,
                StringComparison.Ordinal) ||
            payload.Architecture != architecture)
        {
            throw new InvalidDataException(
                "The hosted AetherRemote release manifest does not match its exact release path.");
        }

        int configurationSchema = payload.Migration?.Kind switch
        {
            ReleaseMigrationKind.Required =>
                payload.Migration.FromConfigurationSchemaVersion ?? 0,
            _ => payload.Configuration?.TargetSchemaVersion ?? 0
        };
        int protocolVersion = payload.Protocol?.MinimumVersion ?? 0;
        InstallationUpdateChannel channel = payload.Channel switch
        {
            ReleaseManifestChannel.Stable => InstallationUpdateChannel.Stable,
            ReleaseManifestChannel.Beta => InstallationUpdateChannel.Beta,
            ReleaseManifestChannel.Pinned => InstallationUpdateChannel.Pinned,
            _ => throw new InvalidDataException(
                "The hosted AetherRemote release channel is unsupported.")
        };
        ReleaseManifestVerificationContext context = new(
            architecture,
            channel,
            channel == InstallationUpdateChannel.Pinned
                ? releaseIdentity
                : string.Empty,
            InstalledVersion: string.Empty,
            configurationSchema,
            protocolVersion);
        LocalOfflineReleaseBundleVerificationReport verification =
            m_bundleVerifier.VerifyDirectory(bundleDirectory, context);
        if (!verification.Succeeded || verification.Verification is null)
        {
            throw new InvalidDataException(
                "The hosted AetherRemote release bundle no longer passes local signature and package verification.");
        }

        SignedReleasePackage agent = RequirePackage(
            payload,
            ReleasePackageRole.AetherRemoteAgent);
        SignedReleasePackage stationEngine = RequirePackage(
            payload,
            ReleasePackageRole.StationEngine);
        string agentPath = SafePackagePath(bundleDirectory, agent.FileName);
        string stationEnginePath = SafePackagePath(
            bundleDirectory,
            stationEngine.FileName);
        if (!string.Equals(
                HashFile(agentPath),
                agent.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                HashFile(stationEnginePath),
                stationEngine.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The hosted AetherRemote package digest changed after release verification.");
        }

        ReleaseProtocolCompatibility protocol = payload.Protocol ??
            throw new InvalidDataException(
                "The hosted AetherRemote release has no protocol compatibility declaration.");
        if (protocol.MinimumVersion > ApplicationReleaseProtocolVersion ||
            protocol.MaximumVersion < ApplicationReleaseProtocolVersion)
        {
            throw new InvalidDataException(
                "The active signed release is incompatible with this gateway's release protocol.");
        }
        if (status.UpdateChannel is InstallationUpdateChannel configured &&
            configured != channel &&
            configured != InstallationUpdateChannel.Pinned)
        {
            throw new InvalidDataException(
                "The active release download channel does not match the installed gateway update policy.");
        }

        return new VerifiedBootstrapBundle(
            architectureToken,
            payload.Version,
            protocol.MinimumVersion,
            protocol.MaximumVersion,
            document.Signature.KeyId,
            manifestPath,
            agentPath,
            stationEnginePath);
    }

    private static SignedReleasePackage RequirePackage(
        SignedReleaseManifestPayload payload,
        ReleasePackageRole role)
    {
        SignedReleasePackage[] matches = (payload.Packages ?? [])
            .Where(package => package.Role == role)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"The hosted release must contain exactly one {role} package.");
    }

    private ReleaseManifestVerificationKey GetVerificationKey(string keyId)
    {
        ReleaseManifestVerificationKey[] matches =
            m_trustRegistry.VerificationKeys
                .Where(key => string.Equals(
                    key.KeyId,
                    keyId,
                    StringComparison.Ordinal))
                .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                "The hosted release does not bind to one locally trusted verification key.");
    }

    private void ValidateRunningGatewayContentRoot(string releaseIdentity)
    {
        string releaseRoot = Path.GetFullPath(m_paths.ReleaseDirectory);
        string expectedPhysical = Path.GetFullPath(
            Path.Combine(releaseRoot, releaseIdentity, "gateway-web"));
        string? releaseParent = Path.GetDirectoryName(releaseRoot);
        if (string.IsNullOrEmpty(releaseParent))
        {
            throw new InvalidOperationException(
                "The release directory has no supported current-pointer parent.");
        }
        string expectedCurrent = Path.GetFullPath(
            Path.Combine(releaseParent, "current", "gateway-web"));
        if (!PathEquals(m_contentRoot, expectedPhysical) &&
            !PathEquals(m_contentRoot, expectedCurrent))
        {
            throw new InvalidOperationException(
                "AetherRemote bootstrap refuses to publish from content outside the active signed gateway release.");
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal);

    private string InstallerPath()
    {
        string path = Path.GetFullPath(
            Path.Combine(m_contentRoot, InstallerRelativePath));
        if (!path.StartsWith(
                m_contentRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The AetherRemote bootstrap installer escaped the application content root.");
        }
        return path;
    }

    private static void ValidateRegularFile(
        string path,
        long maximumBytes,
        string label)
    {
        FileInfo info = new(path);
        info.Refresh();
        if (!info.Exists ||
            info.Length is <= 0 ||
            info.Length > maximumBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.LinkTarget is not null)
        {
            throw new InvalidDataException(
                $"The {label} is unavailable or unsafe.");
        }
    }

    private static string SafePackagePath(string root, string relativePath)
    {
        if (!ReleasePackagePath.IsSafe(relativePath))
        {
            throw new InvalidDataException(
                "The hosted release contains an unsafe package path.");
        }
        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!path.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The hosted release package escaped its verified bundle.");
        }
        ValidateRegularFile(
            path,
            SignedReleaseManifestVerifier.MaximumDeclaredPackageLength,
            "release package");
        return path;
    }

    private static string HashFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private void ValidateConfiguration()
    {
        if (!m_settings.Enabled)
        {
            return;
        }
        if (!m_runtimeSettings.Enabled ||
            m_runtimeSettings.RuntimeRole != InstallationRuntimeRole.Gateway)
        {
            throw new InvalidOperationException(
                "AetherRemote bootstrap requires the completed gateway installation runtime.");
        }
        CanonicalPublicUrl canonical =
            CanonicalPublicUrl.Parse(m_runtimeSettings.CanonicalPublicUrl);
        if (!string.Equals(
                canonical.Value,
                m_runtimeSettings.CanonicalPublicUrl,
                StringComparison.Ordinal) ||
            !canonical.Value.StartsWith("https://", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AetherRemote bootstrap requires one exact canonical HTTPS gateway URL.");
        }
        if (!m_trustRegistry.Snapshot.SignatureVerificationAvailable)
        {
            throw new InvalidOperationException(
                "AetherRemote bootstrap requires configured signed-release verification trust.");
        }
    }

    private void EnsureEnabled()
    {
        if (!m_settings.Enabled)
        {
            throw new InvalidOperationException(
                "AetherRemote bootstrap publication is disabled.");
        }
    }

    private string CanonicalGatewayUrl() =>
        PathTrimSlash(CanonicalPublicUrl.Parse(
            m_runtimeSettings.CanonicalPublicUrl).Value);

    private string AbsoluteUrl(string path) =>
        $"{CanonicalGatewayUrl()}{path}";

    private string BrokerWebSocketUrl()
    {
        Uri gateway = new(CanonicalGatewayUrl(), UriKind.Absolute);
        UriBuilder builder = new(gateway)
        {
            Scheme = "wss",
            Port = gateway.IsDefaultPort ? -1 : gateway.Port,
            Path = "/aetherremote/broker/station/v1",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private string ReleaseAssetUrl(
        string releaseIdentity,
        string architecture,
        string asset) =>
        AbsoluteUrl(
            $"/aetherremote/releases/{Uri.EscapeDataString(releaseIdentity)}/" +
            $"{Uri.EscapeDataString(architecture)}/{asset}");

    internal static string BuildInstallCommand(
        AetherRemoteBootstrapDocument document,
        string canonicalGatewayUrl,
        string stationId)
    {
        ArgumentNullException.ThrowIfNull(document);
        RemoteStationManagementValidator.ValidateStationId(stationId);
        CanonicalPublicUrl canonical =
            CanonicalPublicUrl.Parse(canonicalGatewayUrl);
        if (!string.Equals(
                canonical.Value,
                canonicalGatewayUrl,
                StringComparison.Ordinal) ||
            !canonical.Value.StartsWith("https://", StringComparison.Ordinal) ||
            document.InstallerSha256 is not { Length: 64 } ||
            !document.InstallerSha256.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            document.ReleaseVerificationKey.Sha256 is not { Length: 64 } ||
            !document.ReleaseVerificationKey.Sha256.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidDataException(
                "The AetherRemote install command inputs are invalid.");
        }
        const string installer = "/tmp/aetherremote-install.sh";
        return
            $"curl --proto '=https' --tlsv1.2 --fail --silent --show-error " +
            $"'{document.InstallerUrl}' -o {installer} && " +
            $"printf '%s  %s\\n' '{document.InstallerSha256}' '{installer}' | " +
            "sha256sum --check --strict && " +
            $"sudo bash {installer} --gateway '{canonical.Value}' " +
            $"--station-id '{stationId}' --release-key-sha256 " +
            $"'{document.ReleaseVerificationKey.Sha256}'";
    }

    private static IEnumerable<(string Token, ReleaseManifestArchitecture Architecture)>
        SupportedArchitectures()
    {
        yield return ("linux-x64", ReleaseManifestArchitecture.LinuxX64);
        yield return ("linux-arm64", ReleaseManifestArchitecture.LinuxArm64);
    }

    private static string PathTrimSlash(string value) =>
        value.EndsWith("/", StringComparison.Ordinal)
            ? value[..^1]
            : value;

    private sealed record VerifiedBootstrapBundle(
        string Architecture,
        string Version,
        int MinimumProtocolVersion,
        int MaximumProtocolVersion,
        string KeyId,
        string ManifestPath,
        string AgentPackagePath,
        string StationEnginePackagePath);

    private sealed record BootstrapSnapshot(
        AetherRemoteBootstrapDocument Document,
        IReadOnlyList<VerifiedBootstrapBundle> Bundles);
}
