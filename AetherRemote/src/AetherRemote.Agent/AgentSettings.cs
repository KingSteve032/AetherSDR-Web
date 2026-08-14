using AetherRemote.Protocol;

namespace AetherRemote.Agent;

public sealed class AgentSettings
{
    public const string SectionName = "Agent";

    public string BrokerUrl { get; set; } = string.Empty;
    public string StationId { get; set; } = string.Empty;
    public string CredentialFile { get; set; } = string.Empty;
    public string GatewayUrl { get; set; } = string.Empty;
    public string ReleaseIdentity { get; set; } = string.Empty;
    public string StationEngineVersion { get; set; } = string.Empty;
    public string ReleaseVerificationKeyPath { get; set; } = string.Empty;
    public string ReleaseVerificationKeySha256File { get; set; } = string.Empty;
    public string SoftwareVersion =>
        typeof(AgentSettings).Assembly.GetName().Version?.ToString(3) ??
        "0.0.0";
    public bool DiscoveryEnabled { get; set; } = true;
    public int InventorySeconds { get; set; } = 5;
    public int RadioOfflineSeconds { get; set; } = 15;
    public string LocalEngineUrl { get; set; } =
        "http://127.0.0.1:5081";
    public string LocalEngineOrigin { get; set; } =
        "http://127.0.0.1:5081";
    public bool AllowInsecureDevelopmentTransport { get; set; }
    public bool ReleaseServiceControlEnabled { get; set; }
    public bool ReleaseUpdateEnabled { get; set; }
    public string[]? Capabilities { get; set; }
    public ConfiguredRadioSettings[] ConfiguredRadios { get; set; } = [];
}

internal static class AgentRunningReleaseMetadata
{
    private const string DefaultReleaseRoot = "/opt/aetherremote/releases";
    private const string DefaultAgentLink = "/opt/aetherremote/agent";
    private const string DefaultEngineLink = "/opt/aetherremote/station-engine";
    private const string ReleasePrefix = "aethersdr-";

    internal static void Reconcile(
        AgentSettings settings,
        string agentLink = DefaultAgentLink,
        string engineLink = DefaultEngineLink,
        string releaseRoot = DefaultReleaseRoot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.ReleaseUpdateEnabled || !OperatingSystem.IsLinux())
        {
            return;
        }

        string canonicalRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(releaseRoot));
        string identity = ReadReleaseIdentity(
            agentLink,
            canonicalRoot,
            "agent");
        string engineIdentity = ReadReleaseIdentity(
            engineLink,
            canonicalRoot,
            "station-engine");
        if (!string.Equals(identity, engineIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Agent and station-engine release links do not identify the same active release.");
        }
        if (!identity.StartsWith(ReleasePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The active Agent release identity is not canonical.");
        }
        string version = identity[ReleasePrefix.Length..];
        if (!StationProtocolValidator.IsIdentifier(identity, 96) ||
            !StationProtocolValidator.IsText(version, 64))
        {
            throw new InvalidOperationException(
                "The active Agent release metadata is invalid.");
        }

        settings.ReleaseIdentity = identity;
        settings.StationEngineVersion = version;
    }

    private static string ReadReleaseIdentity(
        string linkPath,
        string releaseRoot,
        string component)
    {
        DirectoryInfo link = new(Path.GetFullPath(linkPath));
        link.Refresh();
        string? linkTarget = link.LinkTarget;
        if (string.IsNullOrEmpty(linkTarget) ||
            !Path.IsPathFullyQualified(linkTarget))
        {
            throw new InvalidOperationException(
                $"The active {component} release link is unavailable or non-canonical.");
        }
        string target = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(linkTarget));
        if (!Directory.Exists(target))
        {
            throw new InvalidOperationException(
                $"The active {component} release target is unavailable.");
        }
        if (!string.Equals(
                Path.GetFileName(target),
                component,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The active {component} release link has an unexpected target shape.");
        }
        string releaseDirectory = Path.GetDirectoryName(target) ??
            throw new InvalidOperationException(
                $"The active {component} release target has no release directory.");
        string? parent = Path.GetDirectoryName(releaseDirectory);
        if (!string.Equals(parent, releaseRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The active {component} release link escaped the fixed release root.");
        }
        return Path.GetFileName(releaseDirectory);
    }
}

public static class AgentBrokerEndpointValidator
{
    public const string DirectStationPath = "/station/v1";
    public const string GatewayPrefixedStationPath =
        "/aetherremote/broker/station/v1";

    public static bool IsSupportedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        string normalized = path.EndsWith("/", StringComparison.Ordinal)
            ? path[..^1]
            : path;
        return string.Equals(
                normalized,
                DirectStationPath,
                StringComparison.Ordinal) ||
            string.Equals(
                normalized,
                GatewayPrefixedStationPath,
                StringComparison.Ordinal);
    }
}

public static class AgentCapabilityGrantValidator
{
    public static void Validate(IReadOnlyList<string>? capabilities)
    {
        if (capabilities is null)
        {
            throw new InvalidOperationException(
                "Agent:Capabilities is required. Use an explicit empty list " +
                "to grant no station capabilities.");
        }
        if (capabilities.Count > 16 ||
            capabilities.Any(
                capability =>
                    !StationProtocolValidator.IsIdentifier(capability, 64)) ||
            capabilities.Distinct(StringComparer.Ordinal).Count() !=
                capabilities.Count)
        {
            throw new InvalidOperationException(
                "Agent:Capabilities contains an invalid or duplicate grant.");
        }
        string? unknown = capabilities.FirstOrDefault(
            capability => !StationCapabilities.IsKnown(capability));
        if (unknown is not null)
        {
            throw new InvalidOperationException(
                $"Agent:Capabilities contains an unsupported grant: " +
                $"'{unknown}'.");
        }
    }
}

public sealed class ConfiguredRadioSettings
{
    public string RadioId { get; set; } = string.Empty;
    public string Family { get; set; } = "flex";
    public string Model { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public int AvailableClients { get; set; } = -1;
    public int LicensedClients { get; set; } = -1;
    public string CapabilityHash { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 4992;
}
