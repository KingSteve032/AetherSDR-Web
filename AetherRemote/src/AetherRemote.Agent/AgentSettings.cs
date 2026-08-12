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
