namespace AetherRemote.Broker;

public sealed class StationLinkSettings
{
    public const string SectionName = "StationLink";

    public bool Enabled { get; set; }
    public bool RequireForwardedHttps { get; set; } = true;
    public int HeartbeatSeconds { get; set; } = 10;
    public int DegradedAfterSeconds { get; set; } = 25;
    public int DisconnectAfterSeconds { get; set; } = 45;
    public int LinkTokenSeconds { get; set; } = 60;
    public int EnrollmentCodeMinutes { get; set; } = 10;
    public string EnrollmentRegistryPath { get; set; } = string.Empty;
    public string RuntimeCredentialSha256 { get; set; } = string.Empty;
    public string AdministrationCredentialSha256 { get; set; } = string.Empty;
    public StationCredentialSettings[] Stations { get; set; } = [];
}

public sealed class StationCredentialSettings
{
    public string StationId { get; set; } = string.Empty;
    public string CredentialSha256 { get; set; } = string.Empty;
}
