namespace AetherSDR.Web.Setup;

public enum InstallationTopologyKind
{
    PersonalSingleStation = 1,
    LocalStationGateway = 2,
    RemoteStationGateway = 3,
    HybridGateway = 4,
    RemoteStationNode = 5
}

public sealed record InstallationTopologyProfile(
    InstallationTopologyKind Kind,
    bool GatewayRunsHere,
    bool BrokerRunsHere,
    bool StationEngineRunsHere,
    bool AgentRunsHere,
    bool AcceptsRemoteStations,
    bool IntendedForOnePersonalStation)
{
    public static InstallationTopologyProfile For(
        InstallationTopologyKind kind) =>
        kind switch
        {
            InstallationTopologyKind.PersonalSingleStation => new(
                kind,
                GatewayRunsHere: true,
                BrokerRunsHere: true,
                StationEngineRunsHere: true,
                AgentRunsHere: false,
                AcceptsRemoteStations: false,
                IntendedForOnePersonalStation: true),
            InstallationTopologyKind.LocalStationGateway => new(
                kind,
                GatewayRunsHere: true,
                BrokerRunsHere: true,
                StationEngineRunsHere: true,
                AgentRunsHere: false,
                AcceptsRemoteStations: false,
                IntendedForOnePersonalStation: false),
            InstallationTopologyKind.RemoteStationGateway => new(
                kind,
                GatewayRunsHere: true,
                BrokerRunsHere: true,
                StationEngineRunsHere: false,
                AgentRunsHere: false,
                AcceptsRemoteStations: true,
                IntendedForOnePersonalStation: false),
            InstallationTopologyKind.HybridGateway => new(
                kind,
                GatewayRunsHere: true,
                BrokerRunsHere: true,
                StationEngineRunsHere: true,
                AgentRunsHere: false,
                AcceptsRemoteStations: true,
                IntendedForOnePersonalStation: false),
            InstallationTopologyKind.RemoteStationNode => new(
                kind,
                GatewayRunsHere: false,
                BrokerRunsHere: false,
                StationEngineRunsHere: true,
                AgentRunsHere: true,
                AcceptsRemoteStations: false,
                IntendedForOnePersonalStation: false),
            _ => throw new InvalidOperationException(
                $"Unsupported installation topology '{kind}'.")
        };
}
