using AetherRemote.Agent;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;
using System.Net;

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
        !string.Equals(
            brokerUri.AbsolutePath.TrimEnd('/'),
            "/station/v1",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "Agent:BrokerUrl must use the /station/v1 wss:// endpoint in production.");
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
