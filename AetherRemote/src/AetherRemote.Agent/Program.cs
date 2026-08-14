using AetherRemote.Agent;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;
using System.Net;

if (FlexDiscoveryConsole.IsRequested(args))
{
    Environment.ExitCode = await FlexDiscoveryConsole.ExecuteAsync(
        args,
        Console.Out,
        Console.Error);
    return;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

AgentSettings agentSettings =
    builder.Configuration
        .GetSection(AgentSettings.SectionName)
        .Get<AgentSettings>() ??
    new AgentSettings();
AgentRunningReleaseMetadata.Reconcile(agentSettings);
ValidateSettings(agentSettings);

builder.Services.AddSingleton(Options.Create(agentSettings));
builder.Services.AddSingleton<FlexDiscoveryService>();
builder.Services.AddSingleton<IStationRadioInventoryProvider>(
    services => services.GetRequiredService<FlexDiscoveryService>());
builder.Services.AddSingleton<IHostedService>(
    services => services.GetRequiredService<FlexDiscoveryService>());
builder.Services.AddSingleton<StationReceiveSessionManager>();
builder.Services.AddSingleton<IStationReleaseUpdaterClient,
    UnixSocketStationReleaseUpdaterClient>();
builder.Services.AddSingleton<StationReleaseServiceControlService>();
builder.Services.AddSingleton<IStationReleaseUpdateLocalClient,
    UnixSocketStationReleaseUpdateLocalClient>();
builder.Services.AddSingleton<StationReleaseUpdateService>();
builder.Services.AddHostedService<StationLinkClient>();

IHost host = builder.Build();
await host.RunAsync();

static void ValidateSettings(AgentSettings settings)
{
    if (!StationProtocolValidator.IsIdentifier(
            settings.StationId,
            StationProtocol.MaximumStationIdLength))
    {
        throw new InvalidOperationException(
            "Agent:StationId is invalid.");
    }
    if (!Uri.TryCreate(
            settings.BrokerUrl,
            UriKind.Absolute,
            out Uri? brokerUri) ||
        (brokerUri.Scheme != "wss" &&
         !(settings.AllowInsecureDevelopmentTransport &&
           brokerUri.Scheme == "ws")) ||
        !string.IsNullOrEmpty(brokerUri.UserInfo) ||
        !string.IsNullOrEmpty(brokerUri.Query) ||
        !string.IsNullOrEmpty(brokerUri.Fragment) ||
        !AgentBrokerEndpointValidator.IsSupportedPath(
            brokerUri.AbsolutePath))
    {
        throw new InvalidOperationException(
            "Agent:BrokerUrl must use either the direct /station/v1 endpoint " +
            "or the canonical /aetherremote/broker/station/v1 gateway endpoint.");
    }
    if (string.IsNullOrWhiteSpace(settings.CredentialFile))
    {
        throw new InvalidOperationException(
            "Agent:CredentialFile is required.");
    }
    AgentCapabilityGrantValidator.Validate(settings.Capabilities);
    if (!StationProtocolValidator.IsText(
            settings.SoftwareVersion,
            64))
    {
        throw new InvalidOperationException(
            "Agent:SoftwareVersion is invalid.");
    }
    bool releaseIdentityPresent =
        !string.IsNullOrEmpty(settings.ReleaseIdentity);
    bool stationEngineVersionPresent =
        !string.IsNullOrEmpty(settings.StationEngineVersion);
    if (releaseIdentityPresent != stationEngineVersionPresent ||
        releaseIdentityPresent &&
        (!StationProtocolValidator.IsIdentifier(settings.ReleaseIdentity, 96) ||
         !StationProtocolValidator.IsText(settings.StationEngineVersion, 64)))
    {
        throw new InvalidOperationException(
            "Agent release identity and station-engine version are invalid.");
    }
    if (settings.ReleaseServiceControlEnabled &&
        settings.Capabilities?.Contains(
            StationCapabilities.ReleaseServiceControlV1,
            StringComparer.Ordinal) != true)
    {
        throw new InvalidOperationException(
            "Agent release service control requires the " +
            "release-service-control-v1 capability grant.");
    }
    if (settings.ReleaseUpdateEnabled)
    {
        if (!releaseIdentityPresent ||
            settings.Capabilities?.Contains(
                StationCapabilities.ReleaseUpdateV1,
                StringComparer.Ordinal) != true)
        {
            throw new InvalidOperationException(
                "Agent release updates require exact release metadata and the " +
                "release-update-v1 capability grant.");
        }
        if (!OperatingSystem.IsLinux() ||
            !Uri.TryCreate(
                settings.GatewayUrl,
                UriKind.Absolute,
                out Uri? gatewayUri) ||
            gatewayUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(gatewayUri.Host) ||
            !string.IsNullOrEmpty(gatewayUri.UserInfo) ||
            !string.IsNullOrEmpty(gatewayUri.Query) ||
            !string.IsNullOrEmpty(gatewayUri.Fragment) ||
            gatewayUri.AbsolutePath is not ("" or "/") ||
            !IsExactAbsolutePath(settings.ReleaseVerificationKeyPath) ||
            !IsExactAbsolutePath(
                settings.ReleaseVerificationKeySha256File))
        {
            throw new InvalidOperationException(
                "Agent release updates require Linux, one exact HTTPS gateway " +
                "origin, and exact absolute release-trust file paths.");
        }
    }
    if (settings.InventorySeconds is < 2 or > 60 ||
        settings.RadioOfflineSeconds is < 5 or > 300)
    {
        throw new InvalidOperationException(
            "Agent inventory and radio-offline intervals are invalid.");
    }
    if (!Uri.TryCreate(
            settings.LocalEngineUrl,
            UriKind.Absolute,
            out Uri? localEngineUri) ||
        localEngineUri.Scheme != Uri.UriSchemeHttp ||
        !IPAddress.TryParse(
            localEngineUri.Host,
            out IPAddress? localEngineAddress) ||
        !IPAddress.IsLoopback(localEngineAddress) ||
        !string.IsNullOrEmpty(localEngineUri.UserInfo) ||
        !string.IsNullOrEmpty(localEngineUri.Query) ||
        !string.IsNullOrEmpty(localEngineUri.Fragment) ||
        !Uri.TryCreate(
            settings.LocalEngineOrigin,
            UriKind.Absolute,
            out Uri? localEngineOrigin) ||
        localEngineOrigin.Scheme != Uri.UriSchemeHttp ||
        !string.Equals(
            localEngineOrigin.Authority,
            localEngineUri.Authority,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "Agent local engine URLs must use the same loopback HTTP authority.");
    }

    StationInventoryMessage configuredInventory = new(
        StationMessageTypes.Inventory,
        1,
        settings.ConfiguredRadios
            .Select(radio => new StationRadioAdvertisement(
                radio.RadioId,
                radio.Family,
                radio.Model,
                radio.Serial,
                radio.Nickname,
                radio.Status,
                radio.AvailableClients,
                radio.LicensedClients,
                radio.CapabilityHash))
            .ToArray());
    string? inventoryError =
        StationProtocolValidator.ValidateInventory(configuredInventory);
    if (inventoryError is not null)
    {
        throw new InvalidOperationException(
            $"Agent:ConfiguredRadios is invalid: {inventoryError}");
    }
}

static bool IsExactAbsolutePath(string? path) =>
    !string.IsNullOrEmpty(path) &&
    Path.IsPathFullyQualified(path) &&
    string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal);
