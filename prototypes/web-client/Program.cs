using System.Security.Claims;
using System.Net;
using System.Text.Json;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

const string ProductionTxPreflightSwitch =
    "--validate-production-tx-activation";
const string ProductionTxRadioIdSwitch =
    "--production-tx-radio-id";
InstallationSetupConsoleCommandLine installationSetupCommandLine =
    InstallationSetupConsoleCommandParser.Parse(args);
OfflineReleaseInstallPreflightCommandLine releaseInstallPreflightCommandLine =
    OfflineReleaseInstallPreflightCommandParser.Parse(
        installationSetupCommandLine.ApplicationArguments);
ReleaseUpdateConsoleCommandLine releaseUpdateCommandLine =
    ReleaseUpdateConsoleCommandParser.Parse(
        releaseInstallPreflightCommandLine.ApplicationArguments);
bool productionTxPreflightRequested = false;
string? productionTxPreflightRadioId = null;
List<string> applicationArguments = [];
for (int index = 0;
     index < releaseUpdateCommandLine.ApplicationArguments.Count;
     index++)
{
    string argument =
        releaseUpdateCommandLine.ApplicationArguments[index];
    if (string.Equals(
            argument,
            ProductionTxPreflightSwitch,
            StringComparison.Ordinal))
    {
        if (productionTxPreflightRequested)
        {
            throw new InvalidOperationException(
                "Production TX activation preflight was requested more than once.");
        }
        productionTxPreflightRequested = true;
        continue;
    }
    if (string.Equals(
            argument,
            ProductionTxRadioIdSwitch,
            StringComparison.Ordinal))
    {
        if (productionTxPreflightRadioId is not null ||
            index + 1 >= releaseUpdateCommandLine.ApplicationArguments.Count)
        {
            throw new InvalidOperationException(
                "Production TX activation preflight requires one exact radio ID.");
        }
        productionTxPreflightRadioId =
            releaseUpdateCommandLine.ApplicationArguments[++index];
        continue;
    }
    applicationArguments.Add(argument);
}
if (!productionTxPreflightRequested && productionTxPreflightRadioId is not null)
{
    throw new InvalidOperationException(
        "The production TX radio ID switch is valid only with activation preflight.");
}
if (productionTxPreflightRequested &&
    string.IsNullOrWhiteSpace(productionTxPreflightRadioId))
{
    throw new InvalidOperationException(
        "Production TX activation preflight requires --production-tx-radio-id.");
}
if (productionTxPreflightRequested &&
    installationSetupCommandLine.Command !=
        InstallationSetupConsoleCommandKind.None)
{
    throw new InvalidOperationException(
        "Installation setup commands cannot run with production TX preflight.");
}
if (releaseInstallPreflightCommandLine.Command !=
        OfflineReleaseInstallPreflightCommandKind.None &&
    installationSetupCommandLine.Command !=
        InstallationSetupConsoleCommandKind.None)
{
    throw new InvalidOperationException(
        "Release install preflight cannot run with installation setup commands.");
}
if (releaseUpdateCommandLine.Command !=
        ReleaseUpdateConsoleCommandKind.None &&
    installationSetupCommandLine.Command !=
        InstallationSetupConsoleCommandKind.None)
{
    throw new InvalidOperationException(
        "Release update commands cannot run with installation setup commands.");
}
if (releaseInstallPreflightCommandLine.Command !=
        OfflineReleaseInstallPreflightCommandKind.None &&
    releaseUpdateCommandLine.Command !=
        ReleaseUpdateConsoleCommandKind.None)
{
    throw new InvalidOperationException(
        "Release install preflight cannot run with another release command.");
}
if (productionTxPreflightRequested &&
    releaseInstallPreflightCommandLine.Command !=
        OfflineReleaseInstallPreflightCommandKind.None)
{
    throw new InvalidOperationException(
        "Release install preflight cannot run with production TX preflight.");
}
if (productionTxPreflightRequested &&
    releaseUpdateCommandLine.Command !=
        ReleaseUpdateConsoleCommandKind.None)
{
    throw new InvalidOperationException(
        "Release update commands cannot run with production TX preflight.");
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(
    [.. applicationArguments]);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

if (releaseInstallPreflightCommandLine.Command ==
    OfflineReleaseInstallPreflightCommandKind.Preflight)
{
    InstallationPathSettings releasePreflightPathSettings =
        builder.Configuration
            .GetSection(InstallationPathSettings.SectionName)
            .Get<InstallationPathSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationPathSettings();
    InstallationPathLayout releasePreflightPathLayout =
        builder.Environment.IsDevelopment()
            ? InstallationPathLayout.Development
            : OperatingSystem.IsLinux()
                ? InstallationPathLayout.LinuxSystem
                : throw new InvalidOperationException(
                    "Standalone production release install preflight requires Linux.");
    InstallationPaths releasePreflightPaths = InstallationPaths.Resolve(
        builder.Environment.ContentRootPath,
        releasePreflightPathLayout,
        releasePreflightPathSettings);
    ReleaseManifestTrustSettings releasePreflightTrustSettings =
        builder.Configuration
            .GetSection(ReleaseManifestTrustSettings.SectionName)
            .Get<ReleaseManifestTrustSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new ReleaseManifestTrustSettings();
    ReleaseManifestTrustRegistry releasePreflightTrustRegistry = new(
        Options.Create(releasePreflightTrustSettings),
        NullLogger<ReleaseManifestTrustRegistry>.Instance);
    SignedReleaseManifestVerificationService releasePreflightManifestService =
        new(
            releasePreflightTrustRegistry,
            new SignedReleaseManifestVerifier());
    LocalOfflineReleaseBundleVerificationService releasePreflightBundleService =
        new(releasePreflightManifestService);
    ReleaseInstallationStatusReader releasePreflightStatusReader = new(
        new InstallationSetupStore(releasePreflightPaths.SetupStatePath),
        releasePreflightPaths);
    OfflineReleaseInstallPreflightConsole releasePreflightConsole = new(
        new OfflineReleaseInstallPreflightPlanner(
            releasePreflightStatusReader,
            releasePreflightBundleService));
    Environment.ExitCode = await releasePreflightConsole.ExecuteAsync(
        releaseInstallPreflightCommandLine,
        Console.Out);
    return;
}

if (releaseUpdateCommandLine.Command ==
    ReleaseUpdateConsoleCommandKind.CheckOfflineBundle)
{
    ReleaseManifestTrustSettings releaseCheckTrustSettings =
        builder.Configuration
            .GetSection(ReleaseManifestTrustSettings.SectionName)
            .Get<ReleaseManifestTrustSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new ReleaseManifestTrustSettings();
    ReleaseManifestTrustRegistry releaseCheckTrustRegistry = new(
        Options.Create(releaseCheckTrustSettings),
        NullLogger<ReleaseManifestTrustRegistry>.Instance);
    SignedReleaseManifestVerificationService releaseCheckManifestService = new(
        releaseCheckTrustRegistry,
        new SignedReleaseManifestVerifier());
    LocalOfflineReleaseBundleVerificationService releaseCheckBundleService =
        new(releaseCheckManifestService);
    OfflineReleaseBundleCheckConsole releaseCheckConsole =
        new(releaseCheckBundleService);
    Environment.ExitCode = await releaseCheckConsole.ExecuteAsync(
        releaseUpdateCommandLine,
        Console.Out);
    return;
}
if (releaseUpdateCommandLine.Command ==
    ReleaseUpdateConsoleCommandKind.Status)
{
    InstallationPathSettings releaseStatusPathSettings =
        builder.Configuration
            .GetSection(InstallationPathSettings.SectionName)
            .Get<InstallationPathSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationPathSettings();
    InstallationPathLayout releaseStatusPathLayout =
        builder.Environment.IsDevelopment()
            ? InstallationPathLayout.Development
            : OperatingSystem.IsLinux()
                ? InstallationPathLayout.LinuxSystem
                : throw new InvalidOperationException(
                    "Standalone production release status requires Linux.");
    InstallationPaths releaseStatusPaths = InstallationPaths.Resolve(
        builder.Environment.ContentRootPath,
        releaseStatusPathLayout,
        releaseStatusPathSettings);
    InstallationSetupStore releaseStatusSetupStore =
        new(releaseStatusPaths.SetupStatePath);
    ReleaseStatusConsole releaseStatusCommandConsole = new(
        new ReleaseInstallationStatusReader(
            releaseStatusSetupStore,
            releaseStatusPaths));
    Environment.ExitCode = await releaseStatusCommandConsole.ExecuteAsync(
        releaseUpdateCommandLine,
        Console.Out);
    return;
}

if (installationSetupCommandLine.Command !=
    InstallationSetupConsoleCommandKind.None)
{
    InstallationPathSettings installationPathSettings =
        builder.Configuration
            .GetSection(InstallationPathSettings.SectionName)
            .Get<InstallationPathSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationPathSettings();
    InstallationPathLayout installationPathLayout =
        builder.Environment.IsDevelopment()
            ? InstallationPathLayout.Development
            : OperatingSystem.IsLinux()
                ? InstallationPathLayout.LinuxSystem
                : throw new InvalidOperationException(
                    "Standalone production setup commands require Linux.");
    InstallationPaths installationPaths = InstallationPaths.Resolve(
        builder.Environment.ContentRootPath,
        installationPathLayout,
        installationPathSettings);
    InstallationSetupStore installationSetupStore =
        new(installationPaths.SetupStatePath);
    InstallationBootstrapTokenService installationBootstrapTokenService =
        new(installationSetupStore);
    InstallationSetupConsole installationSetupConsole =
        new(installationSetupStore, installationBootstrapTokenService);
    bool interactiveConsole =
        !Console.IsInputRedirected && !Console.IsOutputRedirected;
    await installationSetupConsole.ExecuteAsync(
        installationSetupCommandLine,
        installationPaths,
        Console.Out,
        interactiveTokenOutput: !Console.IsOutputRedirected,
        interactiveSecretInput: interactiveConsole,
        secretReader: interactiveConsole
            ? InstallationSetupConsoleSecretReader.ReadAsync
            : null);
    return;
}

InstallationSetupOnlySettings installationSetupOnlySettings =
    builder.Configuration
        .GetSection(InstallationSetupOnlySettings.SectionName)
        .Get<InstallationSetupOnlySettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new InstallationSetupOnlySettings();
InstallationRuntimeSettings installationRuntimeSettings =
    builder.Configuration
        .GetSection(InstallationRuntimeSettings.SectionName)
        .Get<InstallationRuntimeSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new InstallationRuntimeSettings();
Func<InstallationPaths> resolveInstallationPaths = () =>
{
    InstallationPathSettings installationPathSettings =
        builder.Configuration
            .GetSection(InstallationPathSettings.SectionName)
            .Get<InstallationPathSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationPathSettings();
    InstallationPathLayout installationPathLayout =
        builder.Environment.IsDevelopment()
            ? InstallationPathLayout.Development
            : OperatingSystem.IsLinux()
                ? InstallationPathLayout.LinuxSystem
                : throw new InvalidOperationException(
                    "Standalone installation startup requires Linux.");
    return InstallationPaths.Resolve(
        builder.Environment.ContentRootPath,
        installationPathLayout,
        installationPathSettings);
};
InstallationHostStartupPlan installationHostStartupPlan =
    await InstallationHostStartupPlanner.CreateAsync(
        installationSetupOnlySettings,
        installationRuntimeSettings,
        resolveInstallationPaths);
if (installationHostStartupPlan.Mode == InstallationHostStartupMode.SetupOnly)
{
    if (productionTxPreflightRequested)
    {
        throw new InvalidOperationException(
            "Production TX activation preflight cannot run in setup-only mode.");
    }
    InstallationSetupHttpSecuritySettings setupHttpSecuritySettings =
        builder.Configuration
            .GetSection(InstallationSetupHttpSecuritySettings.SectionName)
            .Get<InstallationSetupHttpSecuritySettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationSetupHttpSecuritySettings();
    _ = InstallationSetupOnlyProgramComposition.Configure(
        builder,
        installationHostStartupPlan,
        setupHttpSecuritySettings);
    WebApplication setupOnlyApplication = builder.Build();
    _ = setupOnlyApplication.Services
        .GetRequiredService<InstallationSetupCenterApplication>();
    _ = InstallationSetupOnlyHttpAdapter.Map(setupOnlyApplication);
    _ = InstallationSetupBrowserShell.Map(setupOnlyApplication);
    await setupOnlyApplication.RunAsync();
    return;
}

AuthSettings authSettings =
    builder.Configuration.GetSection(AuthSettings.SectionName).Get<AuthSettings>() ??
    new AuthSettings();
RadioSettings radioSettings =
    builder.Configuration.GetSection(RadioSettings.SectionName).Get<RadioSettings>() ??
    new RadioSettings();
RemoteStationSettings remoteStationSettings =
    builder.Configuration
        .GetSection(RemoteStationSettings.SectionName)
        .Get<RemoteStationSettings>() ??
    new RemoteStationSettings();
IndependentTxWatchdogSettings independentTxWatchdogSettings =
    builder.Configuration
        .GetSection(IndependentTxWatchdogSettings.SectionName)
        .Get<IndependentTxWatchdogSettings>() ??
    new IndependentTxWatchdogSettings();
ReleaseManifestTrustSettings releaseManifestTrustSettings =
    builder.Configuration
        .GetSection(ReleaseManifestTrustSettings.SectionName)
        .Get<ReleaseManifestTrustSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new ReleaseManifestTrustSettings();
ReleaseMigrationRunnerTrustSettings releaseMigrationRunnerTrustSettings =
    builder.Configuration
        .GetSection(ReleaseMigrationRunnerTrustSettings.SectionName)
        .Get<ReleaseMigrationRunnerTrustSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new ReleaseMigrationRunnerTrustSettings();
StationTxCommandTrustSettings stationTxCommandTrustSettings =
    builder.Configuration
        .GetSection(StationTxCommandTrustSettings.SectionName)
        .Get<StationTxCommandTrustSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new StationTxCommandTrustSettings();
StationTxCommandSigningSettings stationTxCommandSigningSettings =
    builder.Configuration
        .GetSection(StationTxCommandSigningSettings.SectionName)
        .Get<StationTxCommandSigningSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new StationTxCommandSigningSettings();
StationTxCommandEnvelopeCoordinatorSettings
    stationTxCommandEnvelopeCoordinatorSettings =
        builder.Configuration
            .GetSection(
                StationTxCommandEnvelopeCoordinatorSettings.SectionName)
            .Get<StationTxCommandEnvelopeCoordinatorSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new StationTxCommandEnvelopeCoordinatorSettings();
StationTxCommandTransportSettings stationTxCommandTransportSettings =
    builder.Configuration
        .GetSection(StationTxCommandTransportSettings.SectionName)
        .Get<StationTxCommandTransportSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new StationTxCommandTransportSettings();
StationTxEmergencyUnkeyTransportSettings
    stationTxEmergencyUnkeyTransportSettings =
        builder.Configuration
            .GetSection(StationTxEmergencyUnkeyTransportSettings.SectionName)
            .Get<StationTxEmergencyUnkeyTransportSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new StationTxEmergencyUnkeyTransportSettings();
StationTxProductionActivationSettings stationTxProductionActivationSettings =
    builder.Configuration
        .GetSection(StationTxProductionActivationSettings.SectionName)
        .Get<StationTxProductionActivationSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new StationTxProductionActivationSettings();
if (productionTxPreflightRequested)
{
    StationTxProductionActivationPreflightReport report =
        StationTxProductionActivationPreflight.Evaluate(
            productionTxPreflightRadioId!,
            builder.Environment.ContentRootPath,
            stationTxProductionActivationSettings,
            radioSettings,
            stationTxCommandTrustSettings,
            stationTxCommandSigningSettings,
            stationTxCommandEnvelopeCoordinatorSettings,
            stationTxCommandTransportSettings,
            stationTxEmergencyUnkeyTransportSettings,
            independentTxWatchdogSettings);
    Console.WriteLine(JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = report.ReadyForOperatorActivation ? 0 : 2;
    return;
}

StationTxCommandTransportRegistrationDiagnostics
    stationTxCommandTransportRegistration =
        StationTxCommandTransportSettingsValidator.CreateDiagnostics(
            stationTxCommandTransportSettings);
StationTxEmergencyUnkeyTransportRegistrationDiagnostics
    stationTxEmergencyUnkeyTransportRegistration =
        StationTxEmergencyUnkeyTransportSettingsValidator.CreateDiagnostics(
            stationTxEmergencyUnkeyTransportSettings);
StationTxProductionActivationConfigurationDiagnostics
    stationTxProductionActivationConfiguration =
        StationTxProductionActivationConfigurationInterlock.ValidateOrThrow(
            stationTxProductionActivationSettings,
            radioSettings,
            stationTxCommandTrustSettings,
            stationTxCommandSigningSettings,
            stationTxCommandEnvelopeCoordinatorSettings,
            stationTxCommandTransportSettings,
            stationTxEmergencyUnkeyTransportSettings,
            independentTxWatchdogSettings);
StationTxProductionActivationPlanner stationTxProductionActivationPlanner =
    new(() => stationTxProductionActivationConfiguration);
StationTxProductionActivationBindingDiagnostics
    stationTxProductionActivationBinding =
        StationTxProductionActivationBinder.Bind(
            stationTxProductionActivationPlanner.Snapshot,
            localFlexSessionEligible: string.Equals(
                radioSettings.Mode,
                "FlexRx",
                StringComparison.OrdinalIgnoreCase),
            allowTransmitConfigured: radioSettings.AllowTransmit,
            browserTxLeaseConfigured: radioSettings.BrowserTxLeaseEnabled);
ReverseProxySettings reverseProxySettings =
    builder.Configuration
        .GetSection(ReverseProxySettings.SectionName)
        .Get<ReverseProxySettings>() ??
    new ReverseProxySettings();
string[] allowedOrigins =
    builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddSingleton(Options.Create(authSettings));
builder.Services.AddSingleton(Options.Create(radioSettings));
builder.Services.AddSingleton(Options.Create(remoteStationSettings));
builder.Services.AddSingleton(Options.Create(independentTxWatchdogSettings));
builder.Services.AddSingleton(Options.Create(releaseManifestTrustSettings));
builder.Services.AddSingleton(Options.Create(releaseMigrationRunnerTrustSettings));
builder.Services.AddSingleton(Options.Create(stationTxCommandTrustSettings));
builder.Services.AddSingleton(Options.Create(stationTxCommandSigningSettings));
builder.Services.AddSingleton(
    Options.Create(stationTxCommandEnvelopeCoordinatorSettings));
builder.Services.AddSingleton(
    Options.Create(stationTxCommandTransportSettings));
builder.Services.AddSingleton(
    Options.Create(stationTxEmergencyUnkeyTransportSettings));
builder.Services.AddSingleton(
    Options.Create(stationTxProductionActivationSettings));
builder.Services.AddSingleton(stationTxProductionActivationConfiguration);
builder.Services.AddSingleton(stationTxProductionActivationBinding);
builder.Services.AddSingleton<StationTxIndependentWatchdogRegistry>();
builder.Services.AddSingleton<ReleaseManifestTrustRegistry>();
builder.Services.AddSingleton<SignedReleaseManifestVerifier>();
builder.Services.AddSingleton<SignedReleaseManifestVerificationService>();
builder.Services.AddSingleton<
    LocalOfflineReleaseBundleVerificationService>();
builder.Services.AddSingleton<OfflineReleaseBundleCheckConsole>();
builder.Services.AddSingleton(
    services =>
    {
        InstallationPaths statusPaths = resolveInstallationPaths();
        return new ReleaseInstallationStatusReader(
            new InstallationSetupStore(statusPaths.SetupStatePath),
            statusPaths);
    });
builder.Services.AddSingleton<ReleaseStatusConsole>();
builder.Services.AddSingleton<OfflineReleaseInstallPreflightPlanner>();
builder.Services.AddSingleton<OfflineReleaseInstallPreflightConsole>();
builder.Services.AddSingleton<VerifiedReleaseInstallationPlanComposer>();
builder.Services.AddSingleton<VerifiedReleaseStagingService>();
builder.Services.AddSingleton<VerifiedReleasePublicationService>();
builder.Services.AddSingleton<VerifiedReleaseActivationPlanComposer>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationConfigurationBackupPlanner>(
        _ => new VerifiedReleaseActivationConfigurationBackupPlanner(
            resolveInstallationPaths()));
builder.Services.AddSingleton<
    VerifiedReleaseActivationConfigurationBackupService>();
builder.Services.AddSingleton<VerifiedReleaseActivationMigrationPlanComposer>();
builder.Services.AddSingleton<ReleaseMigrationRunnerTrustRegistry>();
builder.Services.AddSingleton<VerifiedReleaseActivationMigrationRunnerSelector>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationMigrationRunnerInvocationService>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationMigrationExecutionService>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationServiceControlPlanComposer>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationHealthVerificationPlanComposer>();
builder.Services.AddSingleton<VerifiedReleaseActivationReadinessEvaluator>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationLeaseQuiescenceBoundary>();
builder.Services.AddSingleton<VerifiedReleaseActivationEvidenceCollector>();
builder.Services.AddSingleton<StationTxCommandTrustRegistry>();
builder.Services.AddSingleton<StationTxCommandSigningAuthority>();
builder.Services.AddSingleton<StationTxCommandEnvelopeCoordinator>();
builder.Services.AddSingleton(
    Options.Create(new OriginSettings { Values = allowedOrigins }));
ConfigureReverseProxy(builder.Services, reverseProxySettings);
string dataProtectionPath =
    builder.Configuration["DataProtection:KeyPath"] ??
    Path.Combine(builder.Environment.ContentRootPath, ".data-protection");
string? configuredRadioAccessPolicyPath =
    builder.Configuration["RadioAccess:PolicyPath"];
string radioAccessPolicyPath =
    string.IsNullOrWhiteSpace(configuredRadioAccessPolicyPath)
        ? Path.Combine(
        builder.Environment.ContentRootPath,
        ".radio-access",
        "policies.json")
        : configuredRadioAccessPolicyPath;
string? configuredAdministrativeAuditPath =
    builder.Configuration["RadioAccess:AuditPath"];
string administrativeAuditPath =
    string.IsNullOrWhiteSpace(configuredAdministrativeAuditPath)
        ? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(radioAccessPolicyPath)) ??
            builder.Environment.ContentRootPath,
            "audit.json")
        : configuredAdministrativeAuditPath;
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("AetherSDR.Web");

ConfigureAuthentication(builder, authSettings);
ConfigureAuthorization(builder.Services);

builder.Services.AddSingleton<RadioSelectionManager>();
builder.Services.AddSingleton<TxLeaseManager>();
builder.Services.AddSingleton<RadioTxOccupancyRegistry>();
builder.Services.AddHostedService<TxLeaseWatchdogService>();
builder.Services.AddSingleton<RadioCapacityHistoryService>();
builder.Services.AddSingleton<IHostedService>(
    services => services.GetRequiredService<RadioCapacityHistoryService>());
builder.Services.AddSingleton(
    services => new RadioAccessPolicyStore(
        radioAccessPolicyPath,
        services.GetRequiredService<
            ILogger<RadioAccessPolicyStore>>()));
builder.Services.AddSingleton(
    services => new AdministrativeAuditStore(
        administrativeAuditPath,
        services.GetRequiredService<
            ILogger<AdministrativeAuditStore>>()));
builder.Services.AddSingleton<RadioSessionRegistry>();
builder.Services.AddSingleton<RadioPresenceRegistry>();
builder.Services.AddSingleton<RadioAdministrationService>();
builder.Services.AddSingleton<IHostedService>(
    services => services.GetRequiredService<RadioSessionRegistry>());
builder.Services.AddHostedService<FlexRadioDiscoveryService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<RemoteStationCatalogService>();
builder.Services.AddSingleton<IHostedService>(
    services => services.GetRequiredService<RemoteStationCatalogService>());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter(
        "websocket",
        limiter =>
        {
            limiter.PermitLimit = 20;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        });
    options.AddFixedWindowLimiter(
        "station-enrollment",
        limiter =>
        {
            limiter.PermitLimit = 10;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        });
});

WebApplication app = builder.Build();
ReleaseManifestTrustRegistry releaseManifestTrustRegistry =
    app.Services.GetRequiredService<ReleaseManifestTrustRegistry>();
SignedReleaseManifestVerificationService releaseManifestVerificationService =
    app.Services.GetRequiredService<SignedReleaseManifestVerificationService>();
LocalOfflineReleaseBundleVerificationService offlineReleaseBundleService =
    app.Services.GetRequiredService<
        LocalOfflineReleaseBundleVerificationService>();
OfflineReleaseBundleCheckConsole offlineReleaseBundleCheckConsole =
    app.Services.GetRequiredService<OfflineReleaseBundleCheckConsole>();
ReleaseStatusConsole releaseStatusConsole =
    app.Services.GetRequiredService<ReleaseStatusConsole>();
OfflineReleaseInstallPreflightConsole releaseInstallPreflightConsole =
    app.Services.GetRequiredService<OfflineReleaseInstallPreflightConsole>();
VerifiedReleaseInstallationPlanComposer releaseInstallationPlanComposer =
    app.Services.GetRequiredService<VerifiedReleaseInstallationPlanComposer>();
VerifiedReleaseStagingService verifiedReleaseStagingService =
    app.Services.GetRequiredService<VerifiedReleaseStagingService>();
VerifiedReleasePublicationService verifiedReleasePublicationService =
    app.Services.GetRequiredService<VerifiedReleasePublicationService>();
VerifiedReleaseActivationPlanComposer releaseActivationPlanComposer =
    app.Services.GetRequiredService<VerifiedReleaseActivationPlanComposer>();
VerifiedReleaseActivationConfigurationBackupPlanner
    releaseActivationConfigurationBackupPlanner =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationConfigurationBackupPlanner>();
VerifiedReleaseActivationConfigurationBackupService
    releaseActivationConfigurationBackupService =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationConfigurationBackupService>();
VerifiedReleaseActivationMigrationPlanComposer
    releaseActivationMigrationPlanComposer =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationMigrationPlanComposer>();
ReleaseMigrationRunnerTrustRegistry releaseMigrationRunnerTrustRegistry =
    app.Services.GetRequiredService<ReleaseMigrationRunnerTrustRegistry>();
VerifiedReleaseActivationMigrationRunnerSelector
    releaseActivationMigrationRunnerSelector =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationMigrationRunnerSelector>();
VerifiedReleaseActivationMigrationRunnerInvocationService
    releaseActivationMigrationRunnerInvocationService =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationMigrationRunnerInvocationService>();
VerifiedReleaseActivationMigrationExecutionService
    releaseActivationMigrationExecutionService =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationMigrationExecutionService>();
VerifiedReleaseActivationServiceControlPlanComposer
    releaseActivationServiceControlPlanComposer =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationServiceControlPlanComposer>();
VerifiedReleaseActivationHealthVerificationPlanComposer
    releaseActivationHealthVerificationPlanComposer =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationHealthVerificationPlanComposer>();
VerifiedReleaseActivationReadinessEvaluator releaseActivationReadinessEvaluator =
    app.Services.GetRequiredService<
        VerifiedReleaseActivationReadinessEvaluator>();
VerifiedReleaseActivationLeaseQuiescenceBoundary
    releaseActivationLeaseQuiescence =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationLeaseQuiescenceBoundary>();
VerifiedReleaseActivationEvidenceCollector releaseActivationEvidenceCollector =
    app.Services.GetRequiredService<
        VerifiedReleaseActivationEvidenceCollector>();
StationTxIndependentWatchdogRegistry independentTxWatchdogRegistry =
    app.Services.GetRequiredService<StationTxIndependentWatchdogRegistry>();
StationTxCommandTrustRegistry stationTxCommandTrustRegistry =
    app.Services.GetRequiredService<StationTxCommandTrustRegistry>();
StationTxCommandSigningAuthority stationTxCommandSigningAuthority =
    app.Services.GetRequiredService<StationTxCommandSigningAuthority>();
StationTxCommandEnvelopeCoordinator stationTxCommandEnvelopeCoordinator =
    app.Services.GetRequiredService<StationTxCommandEnvelopeCoordinator>();

if (reverseProxySettings.Enabled)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; connect-src 'self' ws: wss:; " +
        "img-src 'self' data:; style-src 'self'; script-src 'self'; " +
        "worker-src 'self'; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none';";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] =
        "camera=(), geolocation=(), microphone=(self), payment=(), usb=()";
    await next();
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseWebSockets(
    new WebSocketOptions
    {
        KeepAliveInterval = TimeSpan.FromSeconds(20),
        AllowedOrigins = { }
    });

app.MapGet(
        "/healthz",
        () =>
        {
            ReleaseManifestTrustDiagnostics releaseTrust =
                releaseManifestTrustRegistry.Snapshot;
            SignedReleaseManifestVerificationServiceDiagnostics releaseVerification =
                releaseManifestVerificationService.Snapshot;
            LocalOfflineReleaseBundleVerificationDiagnostics offlineBundle =
                offlineReleaseBundleService.Snapshot;
            OfflineReleaseBundleCheckConsoleDiagnostics offlineBundleCheck =
                offlineReleaseBundleCheckConsole.Snapshot;
            ReleaseStatusConsoleDiagnostics releaseStatus =
                releaseStatusConsole.Snapshot;
            OfflineReleaseInstallPreflightConsoleDiagnostics
                releaseInstallPreflight = releaseInstallPreflightConsole.Snapshot;
            VerifiedReleaseInstallationPlanDiagnostics releaseInstallationPlan =
                releaseInstallationPlanComposer.Snapshot;
            VerifiedReleaseStagingDiagnostics releaseStaging =
                verifiedReleaseStagingService.Snapshot;
            VerifiedReleasePublicationDiagnostics releasePublication =
                verifiedReleasePublicationService.Snapshot;
            VerifiedReleaseActivationPlanDiagnostics releaseActivationPlan =
                releaseActivationPlanComposer.Snapshot;
            VerifiedReleaseActivationConfigurationBackupPlanDiagnostics
                releaseActivationConfigurationBackup =
                    releaseActivationConfigurationBackupPlanner.Snapshot;
            VerifiedReleaseActivationConfigurationBackupDiagnostics
                releaseActivationConfigurationBackupExecution =
                    releaseActivationConfigurationBackupService.Snapshot;
            VerifiedReleaseActivationConfigurationBackupStateDiagnostics
                releaseActivationConfigurationBackupState =
                    releaseActivationConfigurationBackupService.State;
            VerifiedReleaseActivationMigrationPlanDiagnostics
                releaseActivationMigrationPlan =
                    releaseActivationMigrationPlanComposer.Snapshot;
            ReleaseMigrationRunnerTrustDiagnostics
                releaseMigrationRunnerTrust =
                    releaseMigrationRunnerTrustRegistry.Snapshot;
            VerifiedReleaseActivationMigrationRunnerSelectionDiagnostics
                releaseActivationMigrationRunnerSelection =
                    releaseActivationMigrationRunnerSelector.Snapshot;
            VerifiedReleaseActivationMigrationRunnerInvocationDiagnostics
                releaseActivationMigrationRunnerInvocation =
                    releaseActivationMigrationRunnerInvocationService.Snapshot;
            VerifiedReleaseActivationMigrationExecutionDiagnostics
                releaseActivationMigrationExecution =
                    releaseActivationMigrationExecutionService.Snapshot;
            VerifiedReleaseActivationMigrationExecutionStateDiagnostics
                releaseActivationMigrationExecutionState =
                    releaseActivationMigrationExecutionService.State;
            VerifiedReleaseActivationServiceControlPlanDiagnostics
                releaseActivationServiceControlPlan =
                    releaseActivationServiceControlPlanComposer.Snapshot;
            VerifiedReleaseActivationHealthVerificationPlanDiagnostics
                releaseActivationHealthVerificationPlan =
                    releaseActivationHealthVerificationPlanComposer.Snapshot;
            VerifiedReleaseActivationReadinessDiagnostics
                releaseActivationReadiness =
                    releaseActivationReadinessEvaluator.Snapshot;
            VerifiedReleaseActivationLeaseQuiescenceDiagnostics
                releaseActivationLeaseQuiescenceDiagnostics =
                    releaseActivationLeaseQuiescence.Snapshot;
            VerifiedReleaseActivationLeaseQuiescenceStateDiagnostics
                releaseActivationLeaseQuiescenceState =
                    releaseActivationLeaseQuiescence.State;
            VerifiedReleaseActivationEvidenceCollectionDiagnostics
                releaseActivationEvidence =
                    releaseActivationEvidenceCollector.Snapshot;
            StationTxIndependentWatchdogAggregate watchdog =
                independentTxWatchdogRegistry.Snapshot;
            StationTxCommandTrustDiagnostics commandTrust =
                stationTxCommandTrustRegistry.Snapshot;
            StationTxCommandSigningDiagnostics commandSigning =
                stationTxCommandSigningAuthority.Snapshot;
            StationTxCommandEnvelopeCoordinatorDiagnostics commandCoordinator =
                stationTxCommandEnvelopeCoordinator.Snapshot;
            StationTxProductionReadinessInputs productionReadinessInputs =
                new(
                    radioSettings.AllowTransmit,
                    radioSettings.BrowserTxLeaseEnabled,
                    CommandCoordinatorAttached: commandCoordinator.Registered,
                    commandCoordinator.SubmissionEnabled,
                    commandCoordinator.SigningAvailable,
                    commandCoordinator.SignatureVerificationAvailable,
                    CommandBoundaryEnabled:
                        stationTxProductionActivationBinding.Binding
                            .CommandBoundaryEnabled,
                    CommandAdapterRegistered: true,
                    GateTransmitEnabled:
                        stationTxProductionActivationBinding.Binding
                            .CommandGateTransmitEnabled,
                    CommandTransportAvailable: false,
                    SetTransmitAvailable: false,
                    EmergencyUnkeyTransportAvailable: false,
                    SafetyArmAuthorityRegistered: true,
                    WatchdogSupervisionEnabled:
                        watchdog.SupervisionRegistered,
                    WatchdogProcessRunning:
                        watchdog.RunningProcessCount > 0,
                    WatchdogIpcConnected:
                        watchdog.ConnectedProcessCount > 0,
                    WatchdogCommandTransportAvailable:
                        watchdog.CommandTransportAvailable,
                    WatchdogArmingAvailable: watchdog.ArmingAvailable);
            StationTxProductionActivationCompositionDiagnostics
                productionActivation =
                    new StationTxProductionActivationComposition(
                        () => stationTxProductionActivationConfiguration,
                        () => stationTxProductionActivationPlanner.Snapshot,
                        () => stationTxProductionActivationBinding,
                        () => productionReadinessInputs)
                    .Snapshot;
            StationTxProductionReadinessDiagnostics productionReadiness =
                productionActivation.Readiness;
            return Results.Ok(new
            {
                status = "ok",
                releaseManifestTrustVerificationEnabled =
                    releaseTrust.VerificationEnabled,
                releaseManifestTrustedKeyCount = releaseTrust.TrustedKeyCount,
                releaseManifestSignatureVerificationAvailable =
                    releaseTrust.SignatureVerificationAvailable,
                releaseManifestLocalVerificationRegistered =
                    releaseVerification.Registered,
                releaseManifestLocalVerificationAvailable =
                    releaseVerification.LocalVerificationAvailable,
                releaseManifestNetworkDownloadRegistered =
                    releaseVerification.NetworkDownloadRegistered,
                releaseManifestInstallationRegistered =
                    releaseVerification.InstallationRegistered,
                releaseManifestActivationRegistered =
                    releaseVerification.ActivationRegistered,
                releaseOfflineBundleReaderRegistered =
                    offlineBundle.Registered,
                releaseOfflineBundleDirectoryReadRegistered =
                    offlineBundle.DirectoryReadRegistered,
                releaseOfflineBundleArchiveExtractionRegistered =
                    offlineBundle.ArchiveExtractionRegistered,
                releaseOfflineBundleNetworkDownloadRegistered =
                    offlineBundle.NetworkDownloadRegistered,
                releaseOfflineBundleInstallationRegistered =
                    offlineBundle.InstallationRegistered,
                releaseOfflineBundleActivationRegistered =
                    offlineBundle.ActivationRegistered,
                releaseOfflineBundleCliCallerRegistered =
                    offlineBundleCheck.Registered,
                releaseOfflineBundleAdminCallerRegistered =
                    offlineBundle.AdminCallerRegistered,
                releaseOfflineBundleBrowserCallerRegistered =
                    offlineBundle.BrowserCallerRegistered,
                releaseStatusCliRegistered = releaseStatus.Registered,
                releaseStatusSetupStateReadRegistered =
                    releaseStatus.SetupStateReadRegistered,
                releaseStatusReleaseInventoryReadRegistered =
                    releaseStatus.ReleaseInventoryReadRegistered,
                releaseStatusCurrentPointerReadRegistered =
                    releaseStatus.CurrentPointerReadRegistered,
                releaseStatusNetworkDownloadRegistered =
                    releaseStatus.NetworkDownloadRegistered,
                releaseStatusArchiveExtractionRegistered =
                    releaseStatus.ArchiveExtractionRegistered,
                releaseStatusStagingRegistered =
                    releaseStatus.StagingRegistered,
                releaseStatusInstallationRegistered =
                    releaseStatus.InstallationRegistered,
                releaseStatusActivationRegistered =
                    releaseStatus.ActivationRegistered,
                releaseStatusRollbackRegistered =
                    releaseStatus.RollbackRegistered,
                releaseStatusMigrationRegistered =
                    releaseStatus.MigrationRegistered,
                releaseStatusServiceControlRegistered =
                    releaseStatus.ServiceControlRegistered,
                releaseStatusAdminCallerRegistered =
                    releaseStatus.AdminCallerRegistered,
                releaseStatusBrowserCallerRegistered =
                    releaseStatus.BrowserCallerRegistered,
                releaseStatusRadioCallerRegistered =
                    releaseStatus.RadioCallerRegistered,
                releaseStatusWatchdogCallerRegistered =
                    releaseStatus.WatchdogCallerRegistered,
                releaseStatusCommandCallerRegistered =
                    releaseStatus.CommandCallerRegistered,
                releaseStatusLeaseCallerRegistered =
                    releaseStatus.LeaseCallerRegistered,
                releaseStatusTxCallerRegistered =
                    releaseStatus.TxCallerRegistered,
                releaseInstallPreflightCliRegistered =
                    releaseInstallPreflight.Registered,
                releaseInstallPreflightSetupStateReadRegistered =
                    releaseInstallPreflight.SetupStateReadRegistered,
                releaseInstallPreflightReleaseInventoryReadRegistered =
                    releaseInstallPreflight.ReleaseInventoryReadRegistered,
                releaseInstallPreflightCurrentPointerReadRegistered =
                    releaseInstallPreflight.CurrentPointerReadRegistered,
                releaseInstallPreflightSignedBundleVerificationRegistered =
                    releaseInstallPreflight.SignedBundleVerificationRegistered,
                releaseInstallPreflightNetworkDownloadRegistered =
                    releaseInstallPreflight.NetworkDownloadRegistered,
                releaseInstallPreflightArchiveExtractionRegistered =
                    releaseInstallPreflight.ArchiveExtractionRegistered,
                releaseInstallPreflightStagingRegistered =
                    releaseInstallPreflight.StagingRegistered,
                releaseInstallPreflightInstallationRegistered =
                    releaseInstallPreflight.InstallationRegistered,
                releaseInstallPreflightActivationRegistered =
                    releaseInstallPreflight.ActivationRegistered,
                releaseInstallPreflightRollbackRegistered =
                    releaseInstallPreflight.RollbackRegistered,
                releaseInstallPreflightMigrationExecutionRegistered =
                    releaseInstallPreflight.MigrationExecutionRegistered,
                releaseInstallPreflightServiceControlRegistered =
                    releaseInstallPreflight.ServiceControlRegistered,
                releaseInstallPreflightAdminCallerRegistered =
                    releaseInstallPreflight.AdminCallerRegistered,
                releaseInstallPreflightBrowserCallerRegistered =
                    releaseInstallPreflight.BrowserCallerRegistered,
                releaseInstallPreflightRadioCallerRegistered =
                    releaseInstallPreflight.RadioCallerRegistered,
                releaseInstallPreflightWatchdogCallerRegistered =
                    releaseInstallPreflight.WatchdogCallerRegistered,
                releaseInstallPreflightCommandCallerRegistered =
                    releaseInstallPreflight.CommandCallerRegistered,
                releaseInstallPreflightLeaseCallerRegistered =
                    releaseInstallPreflight.LeaseCallerRegistered,
                releaseInstallPreflightTxCallerRegistered =
                    releaseInstallPreflight.TxCallerRegistered,
                releaseInstallationPlanComposerRegistered =
                    releaseInstallationPlan.Registered,
                releaseInstallationPlanVerifiedManifestInputRegistered =
                    releaseInstallationPlan.VerifiedManifestInputRegistered,
                releaseInstallationPlanPathCompositionRegistered =
                    releaseInstallationPlan.InstallationPathCompositionRegistered,
                releaseInstallationPlanNetworkDownloadRegistered =
                    releaseInstallationPlan.NetworkDownloadRegistered,
                releaseInstallationPlanArchiveExtractionRegistered =
                    releaseInstallationPlan.ArchiveExtractionRegistered,
                releaseInstallationPlanFileWriteRegistered =
                    releaseInstallationPlan.FileWriteRegistered,
                releaseInstallationPlanStagingExecutionRegistered =
                    releaseInstallationPlan.StagingExecutionRegistered,
                releaseInstallationPlanInstallationExecutionRegistered =
                    releaseInstallationPlan.InstallationExecutionRegistered,
                releaseInstallationPlanActivationRegistered =
                    releaseInstallationPlan.ActivationRegistered,
                releaseInstallationPlanRollbackRegistered =
                    releaseInstallationPlan.RollbackRegistered,
                releaseInstallationPlanMigrationExecutionRegistered =
                    releaseInstallationPlan.MigrationExecutionRegistered,
                releaseInstallationPlanServiceControlRegistered =
                    releaseInstallationPlan.ServiceControlRegistered,
                releaseInstallationPlanAdminCallerRegistered =
                    releaseInstallationPlan.AdminCallerRegistered,
                releaseInstallationPlanBrowserCallerRegistered =
                    releaseInstallationPlan.BrowserCallerRegistered,
                releaseInstallationPlanRadioCallerRegistered =
                    releaseInstallationPlan.RadioCallerRegistered,
                releaseInstallationPlanWatchdogCallerRegistered =
                    releaseInstallationPlan.WatchdogCallerRegistered,
                releaseInstallationPlanCommandCallerRegistered =
                    releaseInstallationPlan.CommandCallerRegistered,
                releaseInstallationPlanLeaseCallerRegistered =
                    releaseInstallationPlan.LeaseCallerRegistered,
                releaseInstallationPlanTxCallerRegistered =
                    releaseInstallationPlan.TxCallerRegistered,
                releaseStagingServiceRegistered = releaseStaging.Registered,
                releaseStagingStatusRevalidationRegistered =
                    releaseStaging.StatusRevalidationRegistered,
                releaseStagingVerifiedBundleReadRegistered =
                    releaseStaging.VerifiedBundleReadRegistered,
                releaseStagingFileWriteRegistered =
                    releaseStaging.FileWriteRegistered,
                releaseStagingExecutionRegistered =
                    releaseStaging.StagingExecutionRegistered,
                releaseStagingImmutableFreezeRegistered =
                    releaseStaging.ImmutableFreezeRegistered,
                releaseStagingCleanupRegistered =
                    releaseStaging.CleanupRegistered,
                releaseStagingNetworkDownloadRegistered =
                    releaseStaging.NetworkDownloadRegistered,
                releaseStagingArchiveExtractionRegistered =
                    releaseStaging.ArchiveExtractionRegistered,
                releaseStagingInstallationExecutionRegistered =
                    releaseStaging.InstallationExecutionRegistered,
                releaseStagingActivationRegistered =
                    releaseStaging.ActivationRegistered,
                releaseStagingCurrentPointerMutationRegistered =
                    releaseStaging.CurrentPointerMutationRegistered,
                releaseStagingRollbackRegistered =
                    releaseStaging.RollbackRegistered,
                releaseStagingMigrationExecutionRegistered =
                    releaseStaging.MigrationExecutionRegistered,
                releaseStagingServiceControlRegistered =
                    releaseStaging.ServiceControlRegistered,
                releaseStagingCliCallerRegistered =
                    releaseStaging.CliCallerRegistered,
                releaseStagingAdminCallerRegistered =
                    releaseStaging.AdminCallerRegistered,
                releaseStagingBrowserCallerRegistered =
                    releaseStaging.BrowserCallerRegistered,
                releaseStagingRadioCallerRegistered =
                    releaseStaging.RadioCallerRegistered,
                releaseStagingWatchdogCallerRegistered =
                    releaseStaging.WatchdogCallerRegistered,
                releaseStagingCommandCallerRegistered =
                    releaseStaging.CommandCallerRegistered,
                releaseStagingLeaseCallerRegistered =
                    releaseStaging.LeaseCallerRegistered,
                releaseStagingTxCallerRegistered =
                    releaseStaging.TxCallerRegistered,
                releasePublicationServiceRegistered =
                    releasePublication.Registered,
                releasePublicationStatusRevalidationRegistered =
                    releasePublication.StatusRevalidationRegistered,
                releasePublicationFrozenStagingValidationRegistered =
                    releasePublication.FrozenStagingValidationRegistered,
                releasePublicationRootPermissionTransitionRegistered =
                    releasePublication.RootPermissionTransitionRegistered,
                releasePublicationAtomicDirectoryPublishRegistered =
                    releasePublication.AtomicDirectoryPublishRegistered,
                releasePublicationPublishedTreeValidationRegistered =
                    releasePublication.PublishedTreeValidationRegistered,
                releasePublicationNetworkDownloadRegistered =
                    releasePublication.NetworkDownloadRegistered,
                releasePublicationArchiveExtractionRegistered =
                    releasePublication.ArchiveExtractionRegistered,
                releasePublicationFileCopyRegistered =
                    releasePublication.FileCopyRegistered,
                releasePublicationCurrentPointerMutationRegistered =
                    releasePublication.CurrentPointerMutationRegistered,
                releasePublicationActivationRegistered =
                    releasePublication.ActivationRegistered,
                releasePublicationRollbackRegistered =
                    releasePublication.RollbackRegistered,
                releasePublicationMigrationExecutionRegistered =
                    releasePublication.MigrationExecutionRegistered,
                releasePublicationServiceControlRegistered =
                    releasePublication.ServiceControlRegistered,
                releasePublicationCliCallerRegistered =
                    releasePublication.CliCallerRegistered,
                releasePublicationAdminCallerRegistered =
                    releasePublication.AdminCallerRegistered,
                releasePublicationBrowserCallerRegistered =
                    releasePublication.BrowserCallerRegistered,
                releasePublicationRadioCallerRegistered =
                    releasePublication.RadioCallerRegistered,
                releasePublicationWatchdogCallerRegistered =
                    releasePublication.WatchdogCallerRegistered,
                releasePublicationCommandCallerRegistered =
                    releasePublication.CommandCallerRegistered,
                releasePublicationLeaseCallerRegistered =
                    releasePublication.LeaseCallerRegistered,
                releasePublicationTxCallerRegistered =
                    releasePublication.TxCallerRegistered,
                releaseActivationPlanComposerRegistered =
                    releaseActivationPlan.Registered,
                releaseActivationPlanPublishedReleaseInputRegistered =
                    releaseActivationPlan.PublishedReleaseInputRegistered,
                releaseActivationPlanPathCompositionRegistered =
                    releaseActivationPlan.ActivationPathCompositionRegistered,
                releaseActivationPlanTxQuiescencePlanningRegistered =
                    releaseActivationPlan.TxQuiescencePlanningRegistered,
                releaseActivationPlanBackupPlanningRegistered =
                    releaseActivationPlan.BackupPlanningRegistered,
                releaseActivationPlanMigrationPlanningRegistered =
                    releaseActivationPlan.MigrationPlanningRegistered,
                releaseActivationPlanServiceRestartPlanningRegistered =
                    releaseActivationPlan.ServiceRestartPlanningRegistered,
                releaseActivationPlanHealthVerificationPlanningRegistered =
                    releaseActivationPlan.HealthVerificationPlanningRegistered,
                releaseActivationPlanRollbackPlanningRegistered =
                    releaseActivationPlan.RollbackPlanningRegistered,
                releaseActivationPlanNetworkDownloadRegistered =
                    releaseActivationPlan.NetworkDownloadRegistered,
                releaseActivationPlanArchiveExtractionRegistered =
                    releaseActivationPlan.ArchiveExtractionRegistered,
                releaseActivationPlanFileWriteRegistered =
                    releaseActivationPlan.FileWriteRegistered,
                releaseActivationPlanCurrentPointerMutationRegistered =
                    releaseActivationPlan.CurrentPointerMutationRegistered,
                releaseActivationPlanActivationExecutionRegistered =
                    releaseActivationPlan.ActivationExecutionRegistered,
                releaseActivationPlanBackupExecutionRegistered =
                    releaseActivationPlan.BackupExecutionRegistered,
                releaseActivationPlanMigrationExecutionRegistered =
                    releaseActivationPlan.MigrationExecutionRegistered,
                releaseActivationPlanServiceControlRegistered =
                    releaseActivationPlan.ServiceControlRegistered,
                releaseActivationPlanHealthProbeCallerRegistered =
                    releaseActivationPlan.HealthProbeCallerRegistered,
                releaseActivationPlanCliCallerRegistered =
                    releaseActivationPlan.CliCallerRegistered,
                releaseActivationPlanAdminCallerRegistered =
                    releaseActivationPlan.AdminCallerRegistered,
                releaseActivationPlanBrowserCallerRegistered =
                    releaseActivationPlan.BrowserCallerRegistered,
                releaseActivationPlanRadioCallerRegistered =
                    releaseActivationPlan.RadioCallerRegistered,
                releaseActivationPlanWatchdogCallerRegistered =
                    releaseActivationPlan.WatchdogCallerRegistered,
                releaseActivationPlanCommandCallerRegistered =
                    releaseActivationPlan.CommandCallerRegistered,
                releaseActivationPlanLeaseCallerRegistered =
                    releaseActivationPlan.LeaseCallerRegistered,
                releaseActivationPlanTxCallerRegistered =
                    releaseActivationPlan.TxCallerRegistered,
                releaseActivationConfigurationBackupPlannerRegistered =
                    releaseActivationConfigurationBackup.Registered,
                releaseActivationConfigurationBackupPlanInputRegistered =
                    releaseActivationConfigurationBackup
                        .ActivationPlanInputRegistered,
                releaseActivationConfigurationBackupPathsInputRegistered =
                    releaseActivationConfigurationBackup
                        .InstallationPathsInputRegistered,
                releaseActivationConfigurationBackupExactPlanBindingRegistered =
                    releaseActivationConfigurationBackup
                        .ExactActivationPlanBindingRegistered,
                releaseActivationConfigurationBackupConfigurationSourcePlanningRegistered =
                    releaseActivationConfigurationBackup
                        .ConfigurationSourcePlanningRegistered,
                releaseActivationConfigurationBackupStateSourcePlanningRegistered =
                    releaseActivationConfigurationBackup
                        .StateSourcePlanningRegistered,
                releaseActivationConfigurationBackupSecretSourcePlanningRegistered =
                    releaseActivationConfigurationBackup
                        .SecretSourcePlanningRegistered,
                releaseActivationConfigurationBackupReleaseRootAgreementRegistered =
                    releaseActivationConfigurationBackup
                        .ReleaseRootAgreementRegistered,
                releaseActivationConfigurationBackupRootSeparationRegistered =
                    releaseActivationConfigurationBackup
                        .BackupRootSeparationRegistered,
                releaseActivationConfigurationBackupIdentityPlanningRegistered =
                    releaseActivationConfigurationBackup
                        .BackupIdentityPlanningRegistered,
                releaseActivationConfigurationBackupManifestPlanningRegistered =
                    releaseActivationConfigurationBackup
                        .BackupManifestPlanningRegistered,
                releaseActivationConfigurationBackupAtomicPublicationPlanningRegistered =
                    releaseActivationConfigurationBackup
                        .AtomicPublicationPlanningRegistered,
                releaseActivationConfigurationBackupSourceReadRegistered =
                    releaseActivationConfigurationBackup.SourceReadRegistered,
                releaseActivationConfigurationBackupFileWriteRegistered =
                    releaseActivationConfigurationBackup.FileWriteRegistered,
                releaseActivationConfigurationBackupDirectoryMutationRegistered =
                    releaseActivationConfigurationBackup
                        .DirectoryMutationRegistered,
                releaseActivationConfigurationBackupExistingOverwriteRegistered =
                    releaseActivationConfigurationBackup
                        .ExistingBackupOverwriteRegistered,
                releaseActivationConfigurationBackupExecutionRegistered =
                    releaseActivationConfigurationBackup.BackupExecutionRegistered,
                releaseActivationConfigurationBackupEvidenceRegistered =
                    releaseActivationConfigurationBackup
                        .ConfigurationBackupEvidenceRegistered,
                releaseActivationConfigurationBackupCurrentPointerMutationRegistered =
                    releaseActivationConfigurationBackup
                        .CurrentPointerMutationRegistered,
                releaseActivationConfigurationBackupActivationAuthorityRegistered =
                    releaseActivationConfigurationBackup
                        .ActivationAuthorityRegistered,
                releaseActivationConfigurationBackupOperationalCallerRegistered =
                    releaseActivationConfigurationBackup
                        .OperationalCallerRegistered,
                releaseActivationConfigurationBackupCliCallerRegistered =
                    releaseActivationConfigurationBackup.CliCallerRegistered,
                releaseActivationConfigurationBackupAdminCallerRegistered =
                    releaseActivationConfigurationBackup.AdminCallerRegistered,
                releaseActivationConfigurationBackupBrowserCallerRegistered =
                    releaseActivationConfigurationBackup.BrowserCallerRegistered,
                releaseActivationConfigurationBackupHttpCallerRegistered =
                    releaseActivationConfigurationBackup.HttpCallerRegistered,
                releaseActivationConfigurationBackupWebSocketCallerRegistered =
                    releaseActivationConfigurationBackup.WebSocketCallerRegistered,
                releaseActivationConfigurationBackupHostedServiceCallerRegistered =
                    releaseActivationConfigurationBackup
                        .HostedServiceCallerRegistered,
                releaseActivationConfigurationBackupTimerCallerRegistered =
                    releaseActivationConfigurationBackup.TimerCallerRegistered,
                releaseActivationConfigurationBackupAetherRemoteCallerRegistered =
                    releaseActivationConfigurationBackup
                        .AetherRemoteCallerRegistered,
                releaseActivationConfigurationBackupServiceControlCallerRegistered =
                    releaseActivationConfigurationBackup
                        .ServiceControlCallerRegistered,
                releaseActivationConfigurationBackupRadioCallerRegistered =
                    releaseActivationConfigurationBackup.RadioCallerRegistered,
                releaseActivationConfigurationBackupWatchdogCallerRegistered =
                    releaseActivationConfigurationBackup.WatchdogCallerRegistered,
                releaseActivationConfigurationBackupCommandCallerRegistered =
                    releaseActivationConfigurationBackup.CommandCallerRegistered,
                releaseActivationConfigurationBackupLeaseCallerRegistered =
                    releaseActivationConfigurationBackup.LeaseCallerRegistered,
                releaseActivationConfigurationBackupTxCallerRegistered =
                    releaseActivationConfigurationBackup.TxCallerRegistered,
                releaseActivationConfigurationBackupExecutorRegistered =
                    releaseActivationConfigurationBackupExecution.Registered,
                releaseActivationConfigurationBackupExecutorPlanInputRegistered =
                    releaseActivationConfigurationBackupExecution
                        .ExactBackupPlanInputRegistered,
                releaseActivationConfigurationBackupExecutorStatusDoubleReadRegistered =
                    releaseActivationConfigurationBackupExecution
                        .ReleaseStatusDoubleReadRegistered,
                releaseActivationConfigurationBackupExecutorBoundedTraversalRegistered =
                    releaseActivationConfigurationBackupExecution
                        .BoundedSourceTraversalRegistered,
                releaseActivationConfigurationBackupExecutorLinkRejectionRegistered =
                    releaseActivationConfigurationBackupExecution
                        .SymbolicLinkRejectionRegistered,
                releaseActivationConfigurationBackupExecutorDigestValidationRegistered =
                    releaseActivationConfigurationBackupExecution
                        .SourceDigestValidationRegistered,
                releaseActivationConfigurationBackupExecutorPrivateStagingRegistered =
                    releaseActivationConfigurationBackupExecution
                        .PrivateStagingRegistered,
                releaseActivationConfigurationBackupExecutorManifestWriteRegistered =
                    releaseActivationConfigurationBackupExecution
                        .ManifestWriteRegistered,
                releaseActivationConfigurationBackupExecutorDurableFlushRegistered =
                    releaseActivationConfigurationBackupExecution
                        .DurableFlushRegistered,
                releaseActivationConfigurationBackupExecutorImmutableFreezeRegistered =
                    releaseActivationConfigurationBackupExecution
                        .ImmutableFreezeRegistered,
                releaseActivationConfigurationBackupExecutorAtomicPublishRegistered =
                    releaseActivationConfigurationBackupExecution
                        .AtomicDirectoryPublishRegistered,
                releaseActivationConfigurationBackupExecutorTreeValidationRegistered =
                    releaseActivationConfigurationBackupExecution
                        .PublishedTreeValidationRegistered,
                releaseActivationConfigurationBackupExecutorCleanupRegistered =
                    releaseActivationConfigurationBackupExecution.CleanupRegistered,
                releaseActivationConfigurationBackupExecutorExactEvidenceRegistered =
                    releaseActivationConfigurationBackupExecution
                        .ExactPlanEvidenceRegistered,
                releaseActivationConfigurationBackupExecutorOverwriteRegistered =
                    releaseActivationConfigurationBackupExecution
                        .ExistingBackupOverwriteRegistered,
                releaseActivationConfigurationBackupExecutorCurrentPointerMutationRegistered =
                    releaseActivationConfigurationBackupExecution
                        .CurrentPointerMutationRegistered,
                releaseActivationConfigurationBackupExecutorActivationRegistered =
                    releaseActivationConfigurationBackupExecution
                        .ActivationExecutionRegistered,
                releaseActivationConfigurationBackupExecutorMigrationRegistered =
                    releaseActivationConfigurationBackupExecution
                        .MigrationExecutionRegistered,
                releaseActivationConfigurationBackupExecutorServiceControlRegistered =
                    releaseActivationConfigurationBackupExecution
                        .ServiceControlRegistered,
                releaseActivationConfigurationBackupExecutorHealthProbeCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .HealthProbeCallerRegistered,
                releaseActivationConfigurationBackupExecutorRollbackRegistered =
                    releaseActivationConfigurationBackupExecution
                        .RollbackExecutionRegistered,
                releaseActivationConfigurationBackupExecutorOperationalCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .OperationalCallerRegistered,
                releaseActivationConfigurationBackupExecutorCliCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .CliCallerRegistered,
                releaseActivationConfigurationBackupExecutorAdminCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .AdminCallerRegistered,
                releaseActivationConfigurationBackupExecutorBrowserCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .BrowserCallerRegistered,
                releaseActivationConfigurationBackupExecutorHttpCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .HttpCallerRegistered,
                releaseActivationConfigurationBackupExecutorWebSocketCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .WebSocketCallerRegistered,
                releaseActivationConfigurationBackupExecutorHostedServiceCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .HostedServiceCallerRegistered,
                releaseActivationConfigurationBackupExecutorTimerCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .TimerCallerRegistered,
                releaseActivationConfigurationBackupExecutorAetherRemoteCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .AetherRemoteCallerRegistered,
                releaseActivationConfigurationBackupExecutorRadioCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .RadioCallerRegistered,
                releaseActivationConfigurationBackupExecutorWatchdogCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .WatchdogCallerRegistered,
                releaseActivationConfigurationBackupExecutorCommandCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .CommandCallerRegistered,
                releaseActivationConfigurationBackupExecutorLeaseCallerRegistered =
                    releaseActivationConfigurationBackupExecution
                        .LeaseCallerRegistered,
                releaseActivationConfigurationBackupExecutorTxCallerRegistered =
                    releaseActivationConfigurationBackupExecution.TxCallerRegistered,
                releaseActivationConfigurationBackupReady =
                    releaseActivationConfigurationBackupState
                        .ConfigurationBackupReady,
                releaseActivationConfigurationBackupExactPlanActive =
                    releaseActivationConfigurationBackupState
                        .ExactActivationPlanBound,
                releaseActivationConfigurationBackupSourceDirectoryCount =
                    releaseActivationConfigurationBackupState
                        .SourceDirectoryCount,
                releaseActivationConfigurationBackupDirectoryCount =
                    releaseActivationConfigurationBackupState.DirectoryCount,
                releaseActivationConfigurationBackupFileCount =
                    releaseActivationConfigurationBackupState.FileCount,
                releaseActivationConfigurationBackupBytes =
                    releaseActivationConfigurationBackupState.BackupBytes,
                releaseActivationConfigurationBackupManifestPresent =
                    releaseActivationConfigurationBackupState.ManifestPresent,
                releaseActivationConfigurationBackupTreeImmutable =
                    releaseActivationConfigurationBackupState
                        .PublishedTreeImmutable,
                releaseActivationConfigurationBackupReconciliationRequired =
                    releaseActivationConfigurationBackupState
                        .ReconciliationRequired,
                releaseActivationConfigurationBackupExecutorCurrentPointerChanged =
                    releaseActivationConfigurationBackupState.CurrentPointerChanged,
                releaseActivationConfigurationBackupExecutorActivationAuthorized =
                    releaseActivationConfigurationBackupState.ActivationAuthorized,
                releaseActivationMigrationPlanComposerRegistered =
                    releaseActivationMigrationPlan.Registered,
                releaseActivationMigrationPlanInputRegistered =
                    releaseActivationMigrationPlan.ActivationPlanInputRegistered,
                releaseActivationMigrationBackupInputRegistered =
                    releaseActivationMigrationPlan
                        .ConfigurationBackupInputRegistered,
                releaseActivationMigrationExactPlanBindingRegistered =
                    releaseActivationMigrationPlan
                        .ExactActivationPlanBindingRegistered,
                releaseActivationMigrationExactBackupBindingRegistered =
                    releaseActivationMigrationPlan
                        .ExactConfigurationBackupBindingRegistered,
                releaseActivationMigrationImmutableBackupValidationRegistered =
                    releaseActivationMigrationPlan
                        .ImmutableBackupValidationRegistered,
                releaseActivationMigrationNoOpPlanningRegistered =
                    releaseActivationMigrationPlan.NoOpMigrationPlanningRegistered,
                releaseActivationMigrationRequiredPlanningRegistered =
                    releaseActivationMigrationPlan
                        .RequiredMigrationPlanningRegistered,
                releaseActivationMigrationSchemaValidationRegistered =
                    releaseActivationMigrationPlan
                        .SchemaTransitionValidationRegistered,
                releaseActivationMigrationIdentityValidationRegistered =
                    releaseActivationMigrationPlan
                        .MigrationIdentityValidationRegistered,
                releaseActivationMigrationStagedCopyPlanningRegistered =
                    releaseActivationMigrationPlan
                        .StagedCopyPathPlanningRegistered,
                releaseActivationMigrationManifestPlanningRegistered =
                    releaseActivationMigrationPlan
                        .MigrationManifestPlanningRegistered,
                releaseActivationMigrationAtomicPublicationPlanningRegistered =
                    releaseActivationMigrationPlan
                        .AtomicPublicationPlanningRegistered,
                releaseActivationMigrationRunnerSelectionRegistered =
                    releaseActivationMigrationPlan
                        .MigrationRunnerSelectionRegistered,
                releaseActivationMigrationSourceReadRegistered =
                    releaseActivationMigrationPlan.SourceReadRegistered,
                releaseActivationMigrationFileWriteRegistered =
                    releaseActivationMigrationPlan.FileWriteRegistered,
                releaseActivationMigrationDirectoryMutationRegistered =
                    releaseActivationMigrationPlan.DirectoryMutationRegistered,
                releaseActivationMigrationExecutionRegistered =
                    releaseActivationMigrationPlan.MigrationExecutionRegistered,
                releaseActivationMigrationEvidenceRegistered =
                    releaseActivationMigrationPlan.MigrationEvidenceRegistered,
                releaseActivationMigrationCurrentPointerMutationRegistered =
                    releaseActivationMigrationPlan
                        .CurrentPointerMutationRegistered,
                releaseActivationMigrationActivationAuthorityRegistered =
                    releaseActivationMigrationPlan.ActivationAuthorityRegistered,
                releaseActivationMigrationOperationalCallerRegistered =
                    releaseActivationMigrationPlan.OperationalCallerRegistered,
                releaseActivationMigrationCliCallerRegistered =
                    releaseActivationMigrationPlan.CliCallerRegistered,
                releaseActivationMigrationAdminCallerRegistered =
                    releaseActivationMigrationPlan.AdminCallerRegistered,
                releaseActivationMigrationBrowserCallerRegistered =
                    releaseActivationMigrationPlan.BrowserCallerRegistered,
                releaseActivationMigrationHttpCallerRegistered =
                    releaseActivationMigrationPlan.HttpCallerRegistered,
                releaseActivationMigrationWebSocketCallerRegistered =
                    releaseActivationMigrationPlan.WebSocketCallerRegistered,
                releaseActivationMigrationHostedServiceCallerRegistered =
                    releaseActivationMigrationPlan.HostedServiceCallerRegistered,
                releaseActivationMigrationTimerCallerRegistered =
                    releaseActivationMigrationPlan.TimerCallerRegistered,
                releaseActivationMigrationAetherRemoteCallerRegistered =
                    releaseActivationMigrationPlan.AetherRemoteCallerRegistered,
                releaseActivationMigrationServiceControlCallerRegistered =
                    releaseActivationMigrationPlan
                        .ServiceControlCallerRegistered,
                releaseActivationMigrationHealthProbeCallerRegistered =
                    releaseActivationMigrationPlan.HealthProbeCallerRegistered,
                releaseActivationMigrationRollbackCallerRegistered =
                    releaseActivationMigrationPlan.RollbackCallerRegistered,
                releaseActivationMigrationRadioCallerRegistered =
                    releaseActivationMigrationPlan.RadioCallerRegistered,
                releaseActivationMigrationWatchdogCallerRegistered =
                    releaseActivationMigrationPlan.WatchdogCallerRegistered,
                releaseActivationMigrationCommandCallerRegistered =
                    releaseActivationMigrationPlan.CommandCallerRegistered,
                releaseActivationMigrationLeaseCallerRegistered =
                    releaseActivationMigrationPlan.LeaseCallerRegistered,
                releaseActivationMigrationTxCallerRegistered =
                    releaseActivationMigrationPlan.TxCallerRegistered,
                releaseMigrationRunnerTrustRegistered =
                    releaseMigrationRunnerTrust.Registered,
                releaseMigrationRunnerTrustSelectionEnabled =
                    releaseMigrationRunnerTrust.SelectionEnabled,
                releaseMigrationRunnerTrustSelectionAvailable =
                    releaseMigrationRunnerTrust.SelectionAvailable,
                releaseMigrationRunnerTrustedRunnerCount =
                    releaseMigrationRunnerTrust.TrustedRunnerCount,
                releaseMigrationRunnerTrustedMigrationCount =
                    releaseMigrationRunnerTrust.TrustedMigrationCount,
                releaseMigrationRunnerTrustConfigurationRegistered =
                    releaseMigrationRunnerTrust
                        .FeatureOwnedConfigurationRegistered,
                releaseMigrationRunnerTrustBoundedRunnerListRegistered =
                    releaseMigrationRunnerTrust.BoundedRunnerListRegistered,
                releaseMigrationRunnerTrustBoundedMigrationListRegistered =
                    releaseMigrationRunnerTrust.BoundedMigrationListRegistered,
                releaseMigrationRunnerTrustCanonicalPathValidationRegistered =
                    releaseMigrationRunnerTrust
                        .CanonicalRunnerPathValidationRegistered,
                releaseMigrationRunnerTrustLinkRejectionRegistered =
                    releaseMigrationRunnerTrust.SymbolicLinkRejectionRegistered,
                releaseMigrationRunnerTrustSizeValidationRegistered =
                    releaseMigrationRunnerTrust.RunnerSizeValidationRegistered,
                releaseMigrationRunnerTrustPermissionValidationRegistered =
                    releaseMigrationRunnerTrust
                        .RunnerPermissionValidationRegistered,
                releaseMigrationRunnerTrustDigestPinningRegistered =
                    releaseMigrationRunnerTrust.RunnerDigestPinningRegistered,
                releaseMigrationRunnerTrustExactMappingRegistered =
                    releaseMigrationRunnerTrust.ExactMigrationMappingRegistered,
                releaseMigrationRunnerTrustArtifactReadRegistered =
                    releaseMigrationRunnerTrust.RunnerArtifactReadRegistered,
                releaseMigrationRunnerTrustInvocationRegistered =
                    releaseMigrationRunnerTrust.RunnerInvocationRegistered,
                releaseMigrationRunnerTrustExecutionRegistered =
                    releaseMigrationRunnerTrust.MigrationExecutionRegistered,
                releaseMigrationRunnerTrustEvidenceRegistered =
                    releaseMigrationRunnerTrust.MigrationEvidenceRegistered,
                releaseMigrationRunnerTrustCurrentPointerMutationRegistered =
                    releaseMigrationRunnerTrust.CurrentPointerMutationRegistered,
                releaseMigrationRunnerTrustActivationAuthorityRegistered =
                    releaseMigrationRunnerTrust.ActivationAuthorityRegistered,
                releaseMigrationRunnerTrustOperationalCallerRegistered =
                    releaseMigrationRunnerTrust.OperationalCallerRegistered,
                releaseMigrationRunnerTrustCliCallerRegistered =
                    releaseMigrationRunnerTrust.CliCallerRegistered,
                releaseMigrationRunnerTrustAdminCallerRegistered =
                    releaseMigrationRunnerTrust.AdminCallerRegistered,
                releaseMigrationRunnerTrustBrowserCallerRegistered =
                    releaseMigrationRunnerTrust.BrowserCallerRegistered,
                releaseMigrationRunnerTrustHttpCallerRegistered =
                    releaseMigrationRunnerTrust.HttpCallerRegistered,
                releaseMigrationRunnerTrustWebSocketCallerRegistered =
                    releaseMigrationRunnerTrust.WebSocketCallerRegistered,
                releaseMigrationRunnerTrustHostedServiceCallerRegistered =
                    releaseMigrationRunnerTrust.HostedServiceCallerRegistered,
                releaseMigrationRunnerTrustTimerCallerRegistered =
                    releaseMigrationRunnerTrust.TimerCallerRegistered,
                releaseMigrationRunnerTrustAetherRemoteCallerRegistered =
                    releaseMigrationRunnerTrust.AetherRemoteCallerRegistered,
                releaseMigrationRunnerTrustServiceControlCallerRegistered =
                    releaseMigrationRunnerTrust.ServiceControlCallerRegistered,
                releaseMigrationRunnerTrustHealthProbeCallerRegistered =
                    releaseMigrationRunnerTrust.HealthProbeCallerRegistered,
                releaseMigrationRunnerTrustRollbackCallerRegistered =
                    releaseMigrationRunnerTrust.RollbackCallerRegistered,
                releaseMigrationRunnerTrustRadioCallerRegistered =
                    releaseMigrationRunnerTrust.RadioCallerRegistered,
                releaseMigrationRunnerTrustWatchdogCallerRegistered =
                    releaseMigrationRunnerTrust.WatchdogCallerRegistered,
                releaseMigrationRunnerTrustCommandCallerRegistered =
                    releaseMigrationRunnerTrust.CommandCallerRegistered,
                releaseMigrationRunnerTrustLeaseCallerRegistered =
                    releaseMigrationRunnerTrust.LeaseCallerRegistered,
                releaseMigrationRunnerTrustTxCallerRegistered =
                    releaseMigrationRunnerTrust.TxCallerRegistered,
                releaseActivationMigrationRunnerSelectorRegistered =
                    releaseActivationMigrationRunnerSelection.Registered,
                releaseActivationMigrationRunnerSelectorPlanInputRegistered =
                    releaseActivationMigrationRunnerSelection
                        .MigrationPlanInputRegistered,
                releaseActivationMigrationRunnerSelectorTrustInputRegistered =
                    releaseActivationMigrationRunnerSelection
                        .RunnerTrustInputRegistered,
                releaseActivationMigrationRunnerSelectorExactPlanBindingRegistered =
                    releaseActivationMigrationRunnerSelection
                        .ExactMigrationPlanBindingRegistered,
                releaseActivationMigrationRunnerSelectorNoOpRegistered =
                    releaseActivationMigrationRunnerSelection
                        .NoOpMigrationResolutionRegistered,
                releaseActivationMigrationRunnerSelectorRequiredRegistered =
                    releaseActivationMigrationRunnerSelection
                        .RequiredRunnerSelectionRegistered,
                releaseActivationMigrationRunnerSelectorIdentityBindingRegistered =
                    releaseActivationMigrationRunnerSelection
                        .ExactMigrationIdentityBindingRegistered,
                releaseActivationMigrationRunnerSelectorSchemaBindingRegistered =
                    releaseActivationMigrationRunnerSelection
                        .SchemaTransitionBindingRegistered,
                releaseActivationMigrationRunnerSelectorProtocolBindingRegistered =
                    releaseActivationMigrationRunnerSelection
                        .RunnerProtocolBindingRegistered,
                releaseActivationMigrationRunnerSelectorDigestBindingRegistered =
                    releaseActivationMigrationRunnerSelection
                        .RunnerArtifactDigestBindingRegistered,
                releaseActivationMigrationRunnerSelectorInvocationRegistered =
                    releaseActivationMigrationRunnerSelection
                        .RunnerInvocationRegistered,
                releaseActivationMigrationRunnerSelectorSourceReadRegistered =
                    releaseActivationMigrationRunnerSelection
                        .MigrationSourceReadRegistered,
                releaseActivationMigrationRunnerSelectorFileWriteRegistered =
                    releaseActivationMigrationRunnerSelection.FileWriteRegistered,
                releaseActivationMigrationRunnerSelectorDirectoryMutationRegistered =
                    releaseActivationMigrationRunnerSelection
                        .DirectoryMutationRegistered,
                releaseActivationMigrationRunnerSelectorExecutionRegistered =
                    releaseActivationMigrationRunnerSelection
                        .MigrationExecutionRegistered,
                releaseActivationMigrationRunnerSelectorEvidenceRegistered =
                    releaseActivationMigrationRunnerSelection
                        .MigrationEvidenceRegistered,
                releaseActivationMigrationRunnerSelectorCurrentPointerMutationRegistered =
                    releaseActivationMigrationRunnerSelection
                        .CurrentPointerMutationRegistered,
                releaseActivationMigrationRunnerSelectorActivationAuthorityRegistered =
                    releaseActivationMigrationRunnerSelection
                        .ActivationAuthorityRegistered,
                releaseActivationMigrationRunnerSelectorOperationalCallerRegistered =
                    releaseActivationMigrationRunnerSelection
                        .OperationalCallerRegistered,
                releaseActivationMigrationRunnerSelectorCliCallerRegistered =
                    releaseActivationMigrationRunnerSelection.CliCallerRegistered,
                releaseActivationMigrationRunnerSelectorAdminCallerRegistered =
                    releaseActivationMigrationRunnerSelection.AdminCallerRegistered,
                releaseActivationMigrationRunnerSelectorBrowserCallerRegistered =
                    releaseActivationMigrationRunnerSelection
                        .BrowserCallerRegistered,
                releaseActivationMigrationRunnerSelectorHttpCallerRegistered =
                    releaseActivationMigrationRunnerSelection.HttpCallerRegistered,
                releaseActivationMigrationRunnerSelectorWebSocketCallerRegistered =
                    releaseActivationMigrationRunnerSelection
                        .WebSocketCallerRegistered,
                releaseActivationMigrationRunnerSelectorHostedServiceCallerRegistered =
                    releaseActivationMigrationRunnerSelection
                        .HostedServiceCallerRegistered,
                releaseActivationMigrationRunnerSelectorTimerCallerRegistered =
                    releaseActivationMigrationRunnerSelection.TimerCallerRegistered,
                releaseActivationMigrationRunnerSelectorAetherRemoteCallerRegistered =
                    releaseActivationMigrationRunnerSelection
                        .AetherRemoteCallerRegistered,
                releaseActivationMigrationRunnerSelectorServiceControlCallerRegistered =
                    releaseActivationMigrationRunnerSelection
                        .ServiceControlCallerRegistered,
                releaseActivationMigrationRunnerSelectorHealthProbeCallerRegistered =
                    releaseActivationMigrationRunnerSelection
                        .HealthProbeCallerRegistered,
                releaseActivationMigrationRunnerSelectorRollbackCallerRegistered =
                    releaseActivationMigrationRunnerSelection.RollbackCallerRegistered,
                releaseActivationMigrationRunnerSelectorRadioCallerRegistered =
                    releaseActivationMigrationRunnerSelection.RadioCallerRegistered,
                releaseActivationMigrationRunnerSelectorWatchdogCallerRegistered =
                    releaseActivationMigrationRunnerSelection
                        .WatchdogCallerRegistered,
                releaseActivationMigrationRunnerSelectorCommandCallerRegistered =
                    releaseActivationMigrationRunnerSelection.CommandCallerRegistered,
                releaseActivationMigrationRunnerSelectorLeaseCallerRegistered =
                    releaseActivationMigrationRunnerSelection.LeaseCallerRegistered,
                releaseActivationMigrationRunnerSelectorTxCallerRegistered =
                    releaseActivationMigrationRunnerSelection.TxCallerRegistered,
                releaseActivationMigrationRunnerInvocationRegistered =
                    releaseActivationMigrationRunnerInvocation.Registered,
                releaseActivationMigrationRunnerInvocationSelectionInputRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .RunnerSelectionInputRegistered,
                releaseActivationMigrationRunnerInvocationExactSelectionBindingRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .ExactRunnerSelectionBindingRegistered,
                releaseActivationMigrationRunnerInvocationNoOpRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .NoOpResolutionRegistered,
                releaseActivationMigrationRunnerInvocationArtifactRevalidationRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .ImmediateRunnerArtifactRevalidationRegistered,
                releaseActivationMigrationRunnerInvocationDirectProcessRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .DirectProcessInvocationRegistered,
                releaseActivationMigrationRunnerInvocationShellRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .ShellInvocationRegistered,
                releaseActivationMigrationRunnerInvocationClearedEnvironmentRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .ClearedEnvironmentRegistered,
                releaseActivationMigrationRunnerInvocationJsonStdinRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .BoundedJsonStdinRegistered,
                releaseActivationMigrationRunnerInvocationStdoutBoundRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .BoundedStdoutRegistered,
                releaseActivationMigrationRunnerInvocationStderrBoundRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .BoundedStderrRegistered,
                releaseActivationMigrationRunnerInvocationTimeoutRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .HardTimeoutRegistered,
                releaseActivationMigrationRunnerInvocationProcessTreeTerminationRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .ProcessTreeTerminationRegistered,
                releaseActivationMigrationRunnerInvocationProbeOnlyRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .ProbeOnlyProtocolRegistered,
                releaseActivationMigrationRunnerInvocationSourcePathInputRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .MigrationSourcePathInputRegistered,
                releaseActivationMigrationRunnerInvocationSourceReadRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .MigrationSourceReadRegistered,
                releaseActivationMigrationRunnerInvocationFileWriteRegistered =
                    releaseActivationMigrationRunnerInvocation.FileWriteRegistered,
                releaseActivationMigrationRunnerInvocationDirectoryMutationRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .DirectoryMutationRegistered,
                releaseActivationMigrationRunnerInvocationExecutionRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .MigrationExecutionRegistered,
                releaseActivationMigrationRunnerInvocationEvidenceRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .MigrationEvidenceRegistered,
                releaseActivationMigrationRunnerInvocationCurrentPointerMutationRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .CurrentPointerMutationRegistered,
                releaseActivationMigrationRunnerInvocationActivationAuthorityRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .ActivationAuthorityRegistered,
                releaseActivationMigrationRunnerInvocationOperationalCallerRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .OperationalCallerRegistered,
                releaseActivationMigrationRunnerInvocationCliCallerRegistered =
                    releaseActivationMigrationRunnerInvocation.CliCallerRegistered,
                releaseActivationMigrationRunnerInvocationAdminCallerRegistered =
                    releaseActivationMigrationRunnerInvocation.AdminCallerRegistered,
                releaseActivationMigrationRunnerInvocationBrowserCallerRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .BrowserCallerRegistered,
                releaseActivationMigrationRunnerInvocationHttpCallerRegistered =
                    releaseActivationMigrationRunnerInvocation.HttpCallerRegistered,
                releaseActivationMigrationRunnerInvocationWebSocketCallerRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .WebSocketCallerRegistered,
                releaseActivationMigrationRunnerInvocationHostedServiceCallerRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .HostedServiceCallerRegistered,
                releaseActivationMigrationRunnerInvocationTimerCallerRegistered =
                    releaseActivationMigrationRunnerInvocation.TimerCallerRegistered,
                releaseActivationMigrationRunnerInvocationAetherRemoteCallerRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .AetherRemoteCallerRegistered,
                releaseActivationMigrationRunnerInvocationServiceControlCallerRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .ServiceControlCallerRegistered,
                releaseActivationMigrationRunnerInvocationHealthProbeCallerRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .HealthProbeCallerRegistered,
                releaseActivationMigrationRunnerInvocationRollbackCallerRegistered =
                    releaseActivationMigrationRunnerInvocation.RollbackCallerRegistered,
                releaseActivationMigrationRunnerInvocationRadioCallerRegistered =
                    releaseActivationMigrationRunnerInvocation.RadioCallerRegistered,
                releaseActivationMigrationRunnerInvocationWatchdogCallerRegistered =
                    releaseActivationMigrationRunnerInvocation
                        .WatchdogCallerRegistered,
                releaseActivationMigrationRunnerInvocationCommandCallerRegistered =
                    releaseActivationMigrationRunnerInvocation.CommandCallerRegistered,
                releaseActivationMigrationRunnerInvocationLeaseCallerRegistered =
                    releaseActivationMigrationRunnerInvocation.LeaseCallerRegistered,
                releaseActivationMigrationRunnerInvocationTxCallerRegistered =
                    releaseActivationMigrationRunnerInvocation.TxCallerRegistered,
                releaseActivationMigrationExecutorRegistered =
                    releaseActivationMigrationExecution.Registered,
                releaseActivationMigrationExecutionInvocationInputRegistered =
                    releaseActivationMigrationExecution
                        .RunnerInvocationInputRegistered,
                releaseActivationMigrationExecutionExactInvocationBindingRegistered =
                    releaseActivationMigrationExecution
                        .ExactRunnerInvocationBindingRegistered,
                releaseActivationMigrationExecutionNoOpRegistered =
                    releaseActivationMigrationExecution.NoOpResolutionRegistered,
                releaseActivationMigrationExecutionStatusDoubleReadRegistered =
                    releaseActivationMigrationExecution
                        .ReleaseStatusDoubleReadRegistered,
                releaseActivationMigrationExecutionBackupManifestValidationRegistered =
                    releaseActivationMigrationExecution
                        .ImmutableBackupManifestValidationRegistered,
                releaseActivationMigrationExecutionBoundedTraversalRegistered =
                    releaseActivationMigrationExecution
                        .BoundedSourceTraversalRegistered,
                releaseActivationMigrationExecutionLinkRejectionRegistered =
                    releaseActivationMigrationExecution
                        .SymbolicLinkRejectionRegistered,
                releaseActivationMigrationExecutionPrivateStagingRegistered =
                    releaseActivationMigrationExecution.PrivateStagingRegistered,
                releaseActivationMigrationExecutionStagedCopyRegistered =
                    releaseActivationMigrationExecution.StagedCopyRegistered,
                releaseActivationMigrationExecutionRunnerRevalidationRegistered =
                    releaseActivationMigrationExecution
                        .ImmediateRunnerArtifactRevalidationRegistered,
                releaseActivationMigrationExecutionDirectRunnerRegistered =
                    releaseActivationMigrationExecution
                        .DirectRunnerExecutionRegistered,
                releaseActivationMigrationExecutionShellRegistered =
                    releaseActivationMigrationExecution.ShellInvocationRegistered,
                releaseActivationMigrationExecutionClearedEnvironmentRegistered =
                    releaseActivationMigrationExecution
                        .ClearedEnvironmentRegistered,
                releaseActivationMigrationExecutionBoundedJsonRegistered =
                    releaseActivationMigrationExecution
                        .BoundedJsonProtocolRegistered,
                releaseActivationMigrationExecutionTimeoutRegistered =
                    releaseActivationMigrationExecution.HardTimeoutRegistered,
                releaseActivationMigrationExecutionProcessTreeTerminationRegistered =
                    releaseActivationMigrationExecution
                        .ProcessTreeTerminationRegistered,
                releaseActivationMigrationExecutionManifestWriteRegistered =
                    releaseActivationMigrationExecution
                        .MigrationManifestWriteRegistered,
                releaseActivationMigrationExecutionDurableFlushRegistered =
                    releaseActivationMigrationExecution.DurableFlushRegistered,
                releaseActivationMigrationExecutionImmutableFreezeRegistered =
                    releaseActivationMigrationExecution.ImmutableFreezeRegistered,
                releaseActivationMigrationExecutionAtomicPublishRegistered =
                    releaseActivationMigrationExecution
                        .AtomicDirectoryPublishRegistered,
                releaseActivationMigrationExecutionTreeValidationRegistered =
                    releaseActivationMigrationExecution
                        .PublishedTreeValidationRegistered,
                releaseActivationMigrationExecutionCleanupRegistered =
                    releaseActivationMigrationExecution.CleanupRegistered,
                releaseActivationMigrationExecutionEvidenceRegistered =
                    releaseActivationMigrationExecution
                        .ExactMigrationEvidenceRegistered,
                releaseActivationMigrationExecutionOverwriteRegistered =
                    releaseActivationMigrationExecution
                        .ExistingMigrationOverwriteRegistered,
                releaseActivationMigrationExecutionCurrentPointerMutationRegistered =
                    releaseActivationMigrationExecution
                        .CurrentPointerMutationRegistered,
                releaseActivationMigrationExecutionActivationAuthorityRegistered =
                    releaseActivationMigrationExecution
                        .ActivationAuthorityRegistered,
                releaseActivationMigrationExecutionOperationalCallerRegistered =
                    releaseActivationMigrationExecution
                        .OperationalCallerRegistered,
                releaseActivationMigrationExecutionCliCallerRegistered =
                    releaseActivationMigrationExecution.CliCallerRegistered,
                releaseActivationMigrationExecutionAdminCallerRegistered =
                    releaseActivationMigrationExecution.AdminCallerRegistered,
                releaseActivationMigrationExecutionBrowserCallerRegistered =
                    releaseActivationMigrationExecution.BrowserCallerRegistered,
                releaseActivationMigrationExecutionHttpCallerRegistered =
                    releaseActivationMigrationExecution.HttpCallerRegistered,
                releaseActivationMigrationExecutionWebSocketCallerRegistered =
                    releaseActivationMigrationExecution.WebSocketCallerRegistered,
                releaseActivationMigrationExecutionHostedServiceCallerRegistered =
                    releaseActivationMigrationExecution.HostedServiceCallerRegistered,
                releaseActivationMigrationExecutionTimerCallerRegistered =
                    releaseActivationMigrationExecution.TimerCallerRegistered,
                releaseActivationMigrationExecutionAetherRemoteCallerRegistered =
                    releaseActivationMigrationExecution.AetherRemoteCallerRegistered,
                releaseActivationMigrationExecutionServiceControlCallerRegistered =
                    releaseActivationMigrationExecution
                        .ServiceControlCallerRegistered,
                releaseActivationMigrationExecutionHealthProbeCallerRegistered =
                    releaseActivationMigrationExecution
                        .HealthProbeCallerRegistered,
                releaseActivationMigrationExecutionRollbackCallerRegistered =
                    releaseActivationMigrationExecution.RollbackCallerRegistered,
                releaseActivationMigrationExecutionRadioCallerRegistered =
                    releaseActivationMigrationExecution.RadioCallerRegistered,
                releaseActivationMigrationExecutionWatchdogCallerRegistered =
                    releaseActivationMigrationExecution.WatchdogCallerRegistered,
                releaseActivationMigrationExecutionCommandCallerRegistered =
                    releaseActivationMigrationExecution.CommandCallerRegistered,
                releaseActivationMigrationExecutionLeaseCallerRegistered =
                    releaseActivationMigrationExecution.LeaseCallerRegistered,
                releaseActivationMigrationExecutionTxCallerRegistered =
                    releaseActivationMigrationExecution.TxCallerRegistered,
                releaseActivationMigrationReady =
                    releaseActivationMigrationExecutionState.MigrationReady,
                releaseActivationMigrationExactPlanActive =
                    releaseActivationMigrationExecutionState
                        .ExactActivationPlanBound,
                releaseActivationMigrationRequired =
                    releaseActivationMigrationExecutionState.MigrationRequired,
                releaseActivationMigrationDirectoryCount =
                    releaseActivationMigrationExecutionState.DirectoryCount,
                releaseActivationMigrationFileCount =
                    releaseActivationMigrationExecutionState.FileCount,
                releaseActivationMigrationBytes =
                    releaseActivationMigrationExecutionState.MigrationBytes,
                releaseActivationMigrationManifestPresent =
                    releaseActivationMigrationExecutionState.ManifestPresent,
                releaseActivationMigrationTreeImmutable =
                    releaseActivationMigrationExecutionState
                        .PublishedTreeImmutable,
                releaseActivationMigrationReconciliationRequired =
                    releaseActivationMigrationExecutionState
                        .ReconciliationRequired,
                releaseActivationMigrationCurrentPointerChanged =
                    releaseActivationMigrationExecutionState.CurrentPointerChanged,
                releaseActivationMigrationActivationAuthorized =
                    releaseActivationMigrationExecutionState.ActivationAuthorized,
                releaseActivationServiceControlPlanRegistered =
                    releaseActivationServiceControlPlan.Registered,
                releaseActivationServiceControlPlanInputRegistered =
                    releaseActivationServiceControlPlan
                        .ActivationPlanInputRegistered,
                releaseActivationServiceControlPlanExactBindingRegistered =
                    releaseActivationServiceControlPlan
                        .ExactActivationPlanBindingRegistered,
                releaseActivationServiceControlPlanNoOpRegistered =
                    releaseActivationServiceControlPlan.NoOpResolutionRegistered,
                releaseActivationServiceControlPlanServiceRestartRegistered =
                    releaseActivationServiceControlPlan
                        .ServiceRestartPlanningRegistered,
                releaseActivationServiceControlPlanHostRestartRegistered =
                    releaseActivationServiceControlPlan
                        .HostRestartPlanningRegistered,
                releaseActivationServiceControlPlanFixedMappingRegistered =
                    releaseActivationServiceControlPlan
                        .FixedServiceMappingRegistered,
                releaseActivationServiceControlPlanStopOrderingRegistered =
                    releaseActivationServiceControlPlan
                        .DeterministicStopOrderingRegistered,
                releaseActivationServiceControlPlanStartOrderingRegistered =
                    releaseActivationServiceControlPlan
                        .DeterministicStartOrderingRegistered,
                releaseActivationServiceControlPlanHostSupersessionRegistered =
                    releaseActivationServiceControlPlan
                        .HostRestartSupersessionRegistered,
                releaseActivationServiceControlPlanPreSwitchRegistered =
                    releaseActivationServiceControlPlan
                        .PreSwitchPhasePlanningRegistered,
                releaseActivationServiceControlPlanPostSwitchRegistered =
                    releaseActivationServiceControlPlan
                        .PostSwitchPhasePlanningRegistered,
                releaseActivationServiceControlPlanProcessInvocationRegistered =
                    releaseActivationServiceControlPlan
                        .ProcessInvocationRegistered,
                releaseActivationServiceControlPlanSystemdCommandRegistered =
                    releaseActivationServiceControlPlan.SystemdCommandRegistered,
                releaseActivationServiceControlPlanHostRestartExecutionRegistered =
                    releaseActivationServiceControlPlan
                        .HostRestartExecutionRegistered,
                releaseActivationServiceControlPlanEvidenceRegistered =
                    releaseActivationServiceControlPlan
                        .ServiceControlEvidenceRegistered,
                releaseActivationServiceControlPlanCurrentPointerMutationRegistered =
                    releaseActivationServiceControlPlan
                        .CurrentPointerMutationRegistered,
                releaseActivationServiceControlPlanActivationAuthorityRegistered =
                    releaseActivationServiceControlPlan
                        .ActivationAuthorityRegistered,
                releaseActivationServiceControlPlanOperationalCallerRegistered =
                    releaseActivationServiceControlPlan
                        .OperationalCallerRegistered,
                releaseActivationServiceControlPlanCliCallerRegistered =
                    releaseActivationServiceControlPlan.CliCallerRegistered,
                releaseActivationServiceControlPlanAdminCallerRegistered =
                    releaseActivationServiceControlPlan.AdminCallerRegistered,
                releaseActivationServiceControlPlanBrowserCallerRegistered =
                    releaseActivationServiceControlPlan.BrowserCallerRegistered,
                releaseActivationServiceControlPlanHttpCallerRegistered =
                    releaseActivationServiceControlPlan.HttpCallerRegistered,
                releaseActivationServiceControlPlanWebSocketCallerRegistered =
                    releaseActivationServiceControlPlan.WebSocketCallerRegistered,
                releaseActivationServiceControlPlanHostedServiceCallerRegistered =
                    releaseActivationServiceControlPlan
                        .HostedServiceCallerRegistered,
                releaseActivationServiceControlPlanTimerCallerRegistered =
                    releaseActivationServiceControlPlan.TimerCallerRegistered,
                releaseActivationServiceControlPlanAetherRemoteCallerRegistered =
                    releaseActivationServiceControlPlan
                        .AetherRemoteCallerRegistered,
                releaseActivationServiceControlPlanHealthProbeCallerRegistered =
                    releaseActivationServiceControlPlan
                        .HealthProbeCallerRegistered,
                releaseActivationServiceControlPlanRollbackCallerRegistered =
                    releaseActivationServiceControlPlan.RollbackCallerRegistered,
                releaseActivationServiceControlPlanRadioCallerRegistered =
                    releaseActivationServiceControlPlan.RadioCallerRegistered,
                releaseActivationServiceControlPlanWatchdogCallerRegistered =
                    releaseActivationServiceControlPlan.WatchdogCallerRegistered,
                releaseActivationServiceControlPlanCommandCallerRegistered =
                    releaseActivationServiceControlPlan.CommandCallerRegistered,
                releaseActivationServiceControlPlanLeaseCallerRegistered =
                    releaseActivationServiceControlPlan.LeaseCallerRegistered,
                releaseActivationServiceControlPlanTxCallerRegistered =
                    releaseActivationServiceControlPlan.TxCallerRegistered,
                releaseActivationHealthVerificationPlanRegistered =
                    releaseActivationHealthVerificationPlan.Registered,
                releaseActivationHealthVerificationPlanServiceControlInputRegistered =
                    releaseActivationHealthVerificationPlan
                        .ServiceControlPlanInputRegistered,
                releaseActivationHealthVerificationPlanExactServiceControlBindingRegistered =
                    releaseActivationHealthVerificationPlan
                        .ExactServiceControlPlanBindingRegistered,
                releaseActivationHealthVerificationPlanExactActivationBindingRegistered =
                    releaseActivationHealthVerificationPlan
                        .ExactActivationPlanBindingRegistered,
                releaseActivationHealthVerificationPlanCompleteCoverageRegistered =
                    releaseActivationHealthVerificationPlan
                        .CompleteServiceCoverageRegistered,
                releaseActivationHealthVerificationPlanUnitActivityRegistered =
                    releaseActivationHealthVerificationPlan
                        .UnitActivityPlanningRegistered,
                releaseActivationHealthVerificationPlanLoopbackHttpRegistered =
                    releaseActivationHealthVerificationPlan
                        .LoopbackHttpPlanningRegistered,
                releaseActivationHealthVerificationPlanFreshBrokerLinkRegistered =
                    releaseActivationHealthVerificationPlan
                        .FreshBrokerLinkPlanningRegistered,
                releaseActivationHealthVerificationPlanCanonicalGatewayHostRegistered =
                    releaseActivationHealthVerificationPlan
                        .CanonicalGatewayHostBindingRegistered,
                releaseActivationHealthVerificationPlanFixedMappingRegistered =
                    releaseActivationHealthVerificationPlan
                        .FixedHealthContractMappingRegistered,
                releaseActivationHealthVerificationPlanOrderingRegistered =
                    releaseActivationHealthVerificationPlan
                        .DeterministicOrderingRegistered,
                releaseActivationHealthVerificationPlanBoundedDeadlineRegistered =
                    releaseActivationHealthVerificationPlan
                        .BoundedDeadlinePlanningRegistered,
                releaseActivationHealthVerificationPlanPostSwitchRegistered =
                    releaseActivationHealthVerificationPlan
                        .PostSwitchPhasePlanningRegistered,
                releaseActivationHealthVerificationPlanPostHostRestartRegistered =
                    releaseActivationHealthVerificationPlan
                        .PostHostRestartPhasePlanningRegistered,
                releaseActivationHealthVerificationPlanNetworkRequestRegistered =
                    releaseActivationHealthVerificationPlan.NetworkRequestRegistered,
                releaseActivationHealthVerificationPlanSocketCallerRegistered =
                    releaseActivationHealthVerificationPlan.SocketCallerRegistered,
                releaseActivationHealthVerificationPlanHttpClientCallerRegistered =
                    releaseActivationHealthVerificationPlan
                        .HttpClientCallerRegistered,
                releaseActivationHealthVerificationPlanProcessInvocationRegistered =
                    releaseActivationHealthVerificationPlan
                        .ProcessInvocationRegistered,
                releaseActivationHealthVerificationPlanSystemdCommandRegistered =
                    releaseActivationHealthVerificationPlan
                        .SystemdCommandRegistered,
                releaseActivationHealthVerificationPlanJournalReadRegistered =
                    releaseActivationHealthVerificationPlan.JournalReadRegistered,
                releaseActivationHealthVerificationPlanEvidenceRegistered =
                    releaseActivationHealthVerificationPlan
                        .HealthEvidenceRegistered,
                releaseActivationHealthVerificationPlanCurrentPointerMutationRegistered =
                    releaseActivationHealthVerificationPlan
                        .CurrentPointerMutationRegistered,
                releaseActivationHealthVerificationPlanActivationAuthorityRegistered =
                    releaseActivationHealthVerificationPlan
                        .ActivationAuthorityRegistered,
                releaseActivationHealthVerificationPlanOperationalCallerRegistered =
                    releaseActivationHealthVerificationPlan
                        .OperationalCallerRegistered,
                releaseActivationHealthVerificationPlanCliCallerRegistered =
                    releaseActivationHealthVerificationPlan.CliCallerRegistered,
                releaseActivationHealthVerificationPlanAdminCallerRegistered =
                    releaseActivationHealthVerificationPlan.AdminCallerRegistered,
                releaseActivationHealthVerificationPlanBrowserCallerRegistered =
                    releaseActivationHealthVerificationPlan.BrowserCallerRegistered,
                releaseActivationHealthVerificationPlanHttpCallerRegistered =
                    releaseActivationHealthVerificationPlan.HttpCallerRegistered,
                releaseActivationHealthVerificationPlanWebSocketCallerRegistered =
                    releaseActivationHealthVerificationPlan
                        .WebSocketCallerRegistered,
                releaseActivationHealthVerificationPlanHostedServiceCallerRegistered =
                    releaseActivationHealthVerificationPlan
                        .HostedServiceCallerRegistered,
                releaseActivationHealthVerificationPlanTimerCallerRegistered =
                    releaseActivationHealthVerificationPlan.TimerCallerRegistered,
                releaseActivationHealthVerificationPlanAetherRemoteCallerRegistered =
                    releaseActivationHealthVerificationPlan
                        .AetherRemoteCallerRegistered,
                releaseActivationHealthVerificationPlanServiceControlCallerRegistered =
                    releaseActivationHealthVerificationPlan
                        .ServiceControlCallerRegistered,
                releaseActivationHealthVerificationPlanRollbackCallerRegistered =
                    releaseActivationHealthVerificationPlan.RollbackCallerRegistered,
                releaseActivationHealthVerificationPlanRadioCallerRegistered =
                    releaseActivationHealthVerificationPlan.RadioCallerRegistered,
                releaseActivationHealthVerificationPlanWatchdogCallerRegistered =
                    releaseActivationHealthVerificationPlan.WatchdogCallerRegistered,
                releaseActivationHealthVerificationPlanCommandCallerRegistered =
                    releaseActivationHealthVerificationPlan.CommandCallerRegistered,
                releaseActivationHealthVerificationPlanLeaseCallerRegistered =
                    releaseActivationHealthVerificationPlan.LeaseCallerRegistered,
                releaseActivationHealthVerificationPlanTxCallerRegistered =
                    releaseActivationHealthVerificationPlan.TxCallerRegistered,
                releaseActivationLeaseQuiescenceRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics.Registered,
                releaseActivationLeaseQuiescencePlanInputRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .ActivationPlanInputRegistered,
                releaseActivationLeaseQuiescenceTransactionPlanRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .TransactionBoundPlanCompositionRegistered,
                releaseActivationLeaseQuiescenceAdmissionAuthorityRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .AdmissionClosureAuthorityRegistered,
                releaseActivationLeaseQuiescenceActiveStateRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .ActiveClosureStateRegistered,
                releaseActivationLeaseQuiescenceAcquisitionSuppressionRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .AcquisitionSuppressionRegistered,
                releaseActivationLeaseQuiescenceRenewalSuppressionRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .RenewalSuppressionRegistered,
                releaseActivationLeaseQuiescenceObservationRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .ObservationOnlyLeaseSnapshotRegistered,
                releaseActivationLeaseQuiescenceDrainEvaluationRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .DrainEvaluationRegistered,
                releaseActivationLeaseQuiescenceForceReleaseRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .ExistingLeaseForceReleaseRegistered,
                releaseActivationLeaseQuiescenceTxLeaseMutationRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .TxLeaseMutationRegistered,
                releaseActivationLeaseQuiescenceRadioIdleInferenceRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .RadioIdleInferenceRegistered,
                releaseActivationLeaseQuiescenceRadioCommandRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .RadioCommandRegistered,
                releaseActivationLeaseQuiescenceWatchdogMutationRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .WatchdogMutationRegistered,
                releaseActivationLeaseQuiescenceActivationAuthorityRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .ActivationAuthorityRegistered,
                releaseActivationLeaseQuiescenceOperationalCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .OperationalCallerRegistered,
                releaseActivationLeaseQuiescenceCliCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .CliCallerRegistered,
                releaseActivationLeaseQuiescenceAdminCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .AdminCallerRegistered,
                releaseActivationLeaseQuiescenceBrowserCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .BrowserCallerRegistered,
                releaseActivationLeaseQuiescenceHttpCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .HttpCallerRegistered,
                releaseActivationLeaseQuiescenceWebSocketCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .WebSocketCallerRegistered,
                releaseActivationLeaseQuiescenceHostedServiceCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .HostedServiceCallerRegistered,
                releaseActivationLeaseQuiescenceTimerCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .TimerCallerRegistered,
                releaseActivationLeaseQuiescenceAetherRemoteCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .AetherRemoteCallerRegistered,
                releaseActivationLeaseQuiescenceCommandCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics
                        .CommandCallerRegistered,
                releaseActivationLeaseQuiescenceTxCallerRegistered =
                    releaseActivationLeaseQuiescenceDiagnostics.TxCallerRegistered,
                releaseActivationLeaseQuiescenceActive =
                    releaseActivationLeaseQuiescenceState.AdmissionClosureActive,
                releaseActivationLeaseQuiescenceExactTransactionActive =
                    releaseActivationLeaseQuiescenceState
                        .ExactTransactionBoundClosureActive,
                releaseActivationLeaseQuiescenceObservedLeaseCount =
                    releaseActivationLeaseQuiescenceState.ObservedTxLeaseCount,
                releaseActivationLeaseQuiescenceDrainSatisfied =
                    releaseActivationLeaseQuiescenceState.DrainSatisfied,
                releaseActivationLeaseQuiescenceLeaseMutationAuthorityAvailable =
                    releaseActivationLeaseQuiescenceState
                        .TxLeaseMutationAuthorityAvailable,
                releaseActivationLeaseQuiescenceRadioIdleProven =
                    releaseActivationLeaseQuiescenceState
                        .RadioAuthoritativeIdleProven,
                releaseActivationLeaseQuiescenceActivationAuthorized =
                    releaseActivationLeaseQuiescenceState.ActivationAuthorized,
                releaseActivationReadinessEvaluatorRegistered =
                    releaseActivationReadiness.Registered,
                releaseActivationReadinessPlanInputRegistered =
                    releaseActivationReadiness.ActivationPlanInputRegistered,
                releaseActivationReadinessStatusEvaluationRegistered =
                    releaseActivationReadiness.ReleaseStatusEvaluationRegistered,
                releaseActivationReadinessTxLeaseAdmissionEvaluationRegistered =
                    releaseActivationReadiness
                        .TxLeaseAdmissionEvaluationRegistered,
                releaseActivationReadinessSessionSafetyEvaluationRegistered =
                    releaseActivationReadiness.SessionSafetyEvaluationRegistered,
                releaseActivationReadinessRadioIdleEvaluationRegistered =
                    releaseActivationReadiness.RadioIdleEvaluationRegistered,
                releaseActivationReadinessWatchdogEvaluationRegistered =
                    releaseActivationReadiness.WatchdogEvaluationRegistered,
                releaseActivationReadinessBackupEvaluationRegistered =
                    releaseActivationReadiness
                        .BackupReadinessEvaluationRegistered,
                releaseActivationReadinessMigrationEvaluationRegistered =
                    releaseActivationReadiness
                        .MigrationReadinessEvaluationRegistered,
                releaseActivationReadinessServiceEvaluationRegistered =
                    releaseActivationReadiness
                        .ServiceControlReadinessEvaluationRegistered,
                releaseActivationReadinessHealthEvaluationRegistered =
                    releaseActivationReadiness
                        .HealthVerificationReadinessEvaluationRegistered,
                releaseActivationReadinessRollbackEvaluationRegistered =
                    releaseActivationReadiness
                        .RollbackReadinessEvaluationRegistered,
                releaseActivationReadinessOperatorApprovalEvaluationRegistered =
                    releaseActivationReadiness
                        .OperatorApprovalEvaluationRegistered,
                releaseActivationReadinessFileWriteRegistered =
                    releaseActivationReadiness.FileWriteRegistered,
                releaseActivationReadinessCurrentPointerMutationRegistered =
                    releaseActivationReadiness.CurrentPointerMutationRegistered,
                releaseActivationReadinessActivationExecutionRegistered =
                    releaseActivationReadiness.ActivationExecutionRegistered,
                releaseActivationReadinessTxLeaseMutationRegistered =
                    releaseActivationReadiness.TxLeaseMutationRegistered,
                releaseActivationReadinessRadioCommandRegistered =
                    releaseActivationReadiness.RadioCommandRegistered,
                releaseActivationReadinessWatchdogMutationRegistered =
                    releaseActivationReadiness.WatchdogMutationRegistered,
                releaseActivationReadinessBackupExecutionRegistered =
                    releaseActivationReadiness.BackupExecutionRegistered,
                releaseActivationReadinessMigrationExecutionRegistered =
                    releaseActivationReadiness.MigrationExecutionRegistered,
                releaseActivationReadinessServiceControlRegistered =
                    releaseActivationReadiness.ServiceControlRegistered,
                releaseActivationReadinessHealthProbeCallerRegistered =
                    releaseActivationReadiness.HealthProbeCallerRegistered,
                releaseActivationReadinessRollbackExecutionRegistered =
                    releaseActivationReadiness.RollbackExecutionRegistered,
                releaseActivationReadinessCliCallerRegistered =
                    releaseActivationReadiness.CliCallerRegistered,
                releaseActivationReadinessAdminCallerRegistered =
                    releaseActivationReadiness.AdminCallerRegistered,
                releaseActivationReadinessBrowserCallerRegistered =
                    releaseActivationReadiness.BrowserCallerRegistered,
                releaseActivationReadinessHostedServiceCallerRegistered =
                    releaseActivationReadiness.HostedServiceCallerRegistered,
                releaseActivationReadinessTimerCallerRegistered =
                    releaseActivationReadiness.TimerCallerRegistered,
                releaseActivationReadinessAetherRemoteCallerRegistered =
                    releaseActivationReadiness.AetherRemoteCallerRegistered,
                releaseActivationReadinessCommandCallerRegistered =
                    releaseActivationReadiness.CommandCallerRegistered,
                releaseActivationReadinessLeaseCallerRegistered =
                    releaseActivationReadiness.LeaseCallerRegistered,
                releaseActivationReadinessTxCallerRegistered =
                    releaseActivationReadiness.TxCallerRegistered,
                releaseActivationEvidenceCollectorRegistered =
                    releaseActivationEvidence.Registered,
                releaseActivationEvidencePlanInputRegistered =
                    releaseActivationEvidence.ActivationPlanInputRegistered,
                releaseActivationEvidenceStatusDoubleReadRegistered =
                    releaseActivationEvidence.ReleaseStatusDoubleReadRegistered,
                releaseActivationEvidenceObservationOnlyLeaseSnapshotRegistered =
                    releaseActivationEvidence
                        .ObservationOnlyTxLeaseSnapshotRegistered,
                releaseActivationEvidenceSessionDiagnosticsSnapshotRegistered =
                    releaseActivationEvidence
                        .SessionDiagnosticsSnapshotRegistered,
                releaseActivationEvidenceRadioOccupancySnapshotRegistered =
                    releaseActivationEvidence.RadioOccupancySnapshotRegistered,
                releaseActivationEvidenceWatchdogAggregateSnapshotRegistered =
                    releaseActivationEvidence.WatchdogAggregateSnapshotRegistered,
                releaseActivationEvidenceBoundedWindowRegistered =
                    releaseActivationEvidence.BoundedCollectionWindowRegistered,
                releaseActivationEvidenceMissingPrerequisitesFailClosedRegistered =
                    releaseActivationEvidence
                        .MissingPrerequisitesFailClosedRegistered,
                releaseActivationEvidenceTxLeaseAdmissionClosureEvidenceRegistered =
                    releaseActivationEvidence
                        .TxLeaseAdmissionClosureEvidenceRegistered,
                releaseActivationEvidenceBackupEvidenceRegistered =
                    releaseActivationEvidence
                        .ConfigurationBackupEvidenceRegistered,
                releaseActivationEvidenceMigrationEvidenceRegistered =
                    releaseActivationEvidence
                        .MigrationExecutionEvidenceRegistered,
                releaseActivationEvidenceServiceEvidenceRegistered =
                    releaseActivationEvidence.ServiceControlEvidenceRegistered,
                releaseActivationEvidenceHealthEvidenceRegistered =
                    releaseActivationEvidence
                        .HealthVerificationEvidenceRegistered,
                releaseActivationEvidenceRollbackEvidenceRegistered =
                    releaseActivationEvidence.RollbackEvidenceRegistered,
                releaseActivationEvidenceOperatorApprovalEvidenceRegistered =
                    releaseActivationEvidence.OperatorApprovalEvidenceRegistered,
                releaseActivationEvidenceFileWriteRegistered =
                    releaseActivationEvidence.FileWriteRegistered,
                releaseActivationEvidenceCurrentPointerMutationRegistered =
                    releaseActivationEvidence.CurrentPointerMutationRegistered,
                releaseActivationEvidenceActivationExecutionRegistered =
                    releaseActivationEvidence.ActivationExecutionRegistered,
                releaseActivationEvidenceTxLeaseMutationRegistered =
                    releaseActivationEvidence.TxLeaseMutationRegistered,
                releaseActivationEvidenceRadioCommandRegistered =
                    releaseActivationEvidence.RadioCommandRegistered,
                releaseActivationEvidenceWatchdogMutationRegistered =
                    releaseActivationEvidence.WatchdogMutationRegistered,
                releaseActivationEvidenceBackupExecutionRegistered =
                    releaseActivationEvidence.BackupExecutionRegistered,
                releaseActivationEvidenceMigrationExecutionRegistered =
                    releaseActivationEvidence.MigrationExecutionRegistered,
                releaseActivationEvidenceServiceControlRegistered =
                    releaseActivationEvidence.ServiceControlRegistered,
                releaseActivationEvidenceHealthProbeCallerRegistered =
                    releaseActivationEvidence.HealthProbeCallerRegistered,
                releaseActivationEvidenceRollbackExecutionRegistered =
                    releaseActivationEvidence.RollbackExecutionRegistered,
                releaseActivationEvidenceCliCallerRegistered =
                    releaseActivationEvidence.CliCallerRegistered,
                releaseActivationEvidenceAdminCallerRegistered =
                    releaseActivationEvidence.AdminCallerRegistered,
                releaseActivationEvidenceBrowserCallerRegistered =
                    releaseActivationEvidence.BrowserCallerRegistered,
                releaseActivationEvidenceHostedServiceCallerRegistered =
                    releaseActivationEvidence.HostedServiceCallerRegistered,
                releaseActivationEvidenceTimerCallerRegistered =
                    releaseActivationEvidence.TimerCallerRegistered,
                releaseActivationEvidenceAetherRemoteCallerRegistered =
                    releaseActivationEvidence.AetherRemoteCallerRegistered,
                releaseActivationEvidenceCommandCallerRegistered =
                    releaseActivationEvidence.CommandCallerRegistered,
                releaseActivationEvidenceLeaseCallerRegistered =
                    releaseActivationEvidence.LeaseCallerRegistered,
                releaseActivationEvidenceTxCallerRegistered =
                    releaseActivationEvidence.TxCallerRegistered,
                radioMode = radioSettings.Mode,
                transmitEnabled =
                    stationTxProductionActivationBinding.BindingApplied,
                browserTxLeaseEnabled = radioSettings.BrowserTxLeaseEnabled,
                txGateLifecycleRegistered = true,
                txLifecycleWatchdogRegistered = true,
                txBrowserIntentProtocolVersion =
                    RadioBrowserTxProtocol.Version,
                txBrowserIntentValidationRegistered = true,
                txBrowserIntentCommandTransportRegistered = false,
                txStationCommandProtocolVersion =
                    StationTxCommandBoundary.ProtocolVersion,
                txStationCommandBoundaryRegistered = true,
                txStationCommandBoundaryEnabled =
                    stationTxProductionActivationBinding.Binding
                        .CommandBoundaryEnabled,
                txStationCommandTrustVerificationEnabled =
                    commandTrust.VerificationEnabled,
                txStationCommandTrustedKeyCount = commandTrust.TrustedKeyCount,
                txStationCommandSignatureVerificationAvailable =
                    commandTrust.SignatureVerificationAvailable,
                txStationCommandSigningEnabled = commandSigning.SigningEnabled,
                txStationCommandSigningKeyConfigured =
                    commandSigning.KeyConfigured,
                txStationCommandSigningAvailable =
                    commandSigning.SigningAvailable,
                txStationCommandEnvelopeCoordinatorRegistered =
                    commandCoordinator.Registered,
                txStationCommandSessionCompositionRegistered = true,
                txStationCommandSessionCompositionBrowserIngressRegistered =
                    false,
                txStationCommandAdapterCompositionRegistered = true,
                txStationCommandAdapterExecutorAttached = true,
                txStationCommandAdapterExecutorRegistered = true,
                txStationCommandGateExecutorRegistered = true,
                txStationCommandGateExecutorTransmitEnabled =
                    stationTxProductionActivationBinding.Binding
                        .CommandGateTransmitEnabled,
                txStationCommandGateExecutorCommandTransportAvailable = false,
                txStationCommandGateExecutorSetTransmitAvailable = false,
                txStationCommandGateExecutorBrowserIngressRegistered = false,
                txStationCommandAdapterCompositionBrowserIngressRegistered =
                    false,
                txStationCommandSafetyArmCompositionRegistered = true,
                txStationCommandSafetyArmAuthorityAttached = true,
                txStationCommandSafetyArmAuthorityRegistered = true,
                txStationCommandSafetyArmAuthorityBoundaryEnabled = false,
                txStationCommandSafetyArmAuthorityCommandTransportAvailable =
                    false,
                txStationCommandSafetyArmAuthoritySetTransmitAvailable = false,
                txStationCommandSafetyArmAuthorityBrowserIngressRegistered =
                    false,
                txStationCommandSafetyArmAvailable = false,
                txStationCommandSafetyHeartbeatAvailable = false,
                txStationCommandSafetyAbortAvailable = false,
                txStationCommandSafetyArmCompositionBrowserIngressRegistered =
                    false,
                txStationCommandTransactionCompositionRegistered = true,
                txStationCommandTransactionLifecycleBoundaryRegistered = true,
                txStationCommandDirectSessionSubmissionRegistered = false,
                txStationCommandTransactionSafetyArmAttached = true,
                txStationCommandTransactionCommandCompositionAttached = true,
                txStationCommandTransactionKeyAvailable = false,
                txStationCommandTransactionHeartbeatAvailable = false,
                txStationCommandTransactionUnkeyAvailable = false,
                txStationCommandTransactionAbortAvailable = false,
                txStationCommandTransactionActive = false,
                txStationCommandTransactionReconciliationRequired = false,
                txStationCommandTransactionBrowserIngressRegistered =
                    stationTxProductionActivationBinding.BindingApplied,
                txStationCommandTransactionLifecycleBrowserIngressRegistered =
                    stationTxProductionActivationBinding.BindingApplied,
                txBrowserTxTransactionIngressRegistered = true,
                txBrowserTxTransactionIngressExecutionEnabled =
                    stationTxProductionActivationBinding.Binding
                        .BrowserTransactionIngressExecutionEnabled,
                txBrowserTxTransactionIngressBoundaryAttached = true,
                txBrowserTxTransactionIngressKeyAvailable = false,
                txBrowserTxTransactionIngressUnkeyAvailable = false,
                txBrowserTxTransactionIngressWebSocketCallerRegistered =
                    stationTxProductionActivationBinding.BindingApplied,
                txBrowserTxTransactionIngressHttpCallerRegistered = false,
                txBrowserTxTransactionIngressAetherRemoteCallerRegistered = false,
                txBrowserTxTransactionIngressWatchdogCallerRegistered = false,
                txBrowserTxTransactionIngressReconnectCallerRegistered = false,
                txBrowserTxTransactionIngressTimerCallerRegistered = false,
                txProductionCommandTransportRegistered =
                    stationTxCommandTransportRegistration.Registered,
                txProductionCommandTransportConfiguredEnabled =
                    stationTxCommandTransportRegistration.ConfiguredEnabled,
                txProductionCommandTransportAllowedRadioCount =
                    stationTxCommandTransportRegistration.AllowedRadioCount,
                txProductionCommandTransportCommandTimeoutMilliseconds =
                    stationTxCommandTransportRegistration
                        .CommandTimeoutMilliseconds,
                txProductionCommandTransportAvailable = false,
                txProductionCommandTransportSetTransmitAvailable = false,
                txProductionCommandTransportReason =
                    stationTxCommandTransportRegistration.Reason,
                txProductionCommandTransportWebSocketCallerRegistered = false,
                txProductionEmergencyUnkeyTransportRegistered =
                    stationTxEmergencyUnkeyTransportRegistration.Registered,
                txProductionEmergencyUnkeyTransportConfiguredEnabled =
                    stationTxEmergencyUnkeyTransportRegistration
                        .ConfiguredEnabled,
                txProductionEmergencyUnkeyTransportAllowedRadioCount =
                    stationTxEmergencyUnkeyTransportRegistration
                        .AllowedRadioCount,
                txProductionEmergencyUnkeyTransportCommandTimeoutMilliseconds =
                    stationTxEmergencyUnkeyTransportRegistration
                        .CommandTimeoutMilliseconds,
                txProductionEmergencyUnkeyTransportAvailable = false,
                txProductionEmergencyUnkeyTransportUnkeyAvailable = false,
                txProductionEmergencyUnkeyTransportReason =
                    stationTxEmergencyUnkeyTransportRegistration.Reason,
                txProductionEmergencyUnkeyTransportWebSocketCallerRegistered =
                    false,
                txProductionReadinessPolicyRegistered =
                    productionReadiness.Registered,
                txProductionReadinessReady = productionReadiness.Ready,
                txProductionReadinessReason = productionReadiness.Reason,
                txProductionReadinessMissingPrerequisites =
                    productionReadiness.MissingPrerequisites,
                txProductionReadinessLifecycleIngressRegistered = true,
                txProductionReadinessWebSocketCallerRegistered = false,
                txProductionActivationConfigurationRegistered =
                    stationTxProductionActivationConfiguration.Registered,
                txProductionActivationRequested =
                    stationTxProductionActivationConfiguration
                        .ActivationRequested,
                txProductionActivationConfigurationValid =
                    stationTxProductionActivationConfiguration
                        .ConfigurationValid,
                txProductionActivationConfigurationReason =
                    stationTxProductionActivationConfiguration.Reason,
                txProductionActivationConfigurationMissingPrerequisites =
                    stationTxProductionActivationConfiguration
                        .MissingPrerequisites,
                txProductionActivationCompositionRegistered =
                    productionActivation.Registered,
                txProductionActivationConfigurationInterlockAttached =
                    productionActivation.ConfigurationInterlockAttached,
                txProductionActivationPlanRegistered =
                    productionActivation.Plan.Registered,
                txProductionActivationPlanAttached =
                    productionActivation.ActivationPlanAttached,
                txProductionActivationPlanAvailable =
                    productionActivation.ActivationPlanAvailable,
                txProductionActivationPlanApplied =
                    productionActivation.ActivationPlanApplied,
                txProductionActivationPlanReason =
                    productionActivation.Plan.Reason,
                txProductionActivationPlanCommandBoundaryEnabled =
                    productionActivation.Plan.Plan.CommandBoundaryEnabled,
                txProductionActivationPlanCommandGateTransmitEnabled =
                    productionActivation.Plan.Plan.CommandGateTransmitEnabled,
                txProductionActivationPlanBrowserIngressExecutionEnabled =
                    productionActivation.Plan.Plan
                        .BrowserTransactionIngressExecutionEnabled,
                txProductionActivationPlanBrowserKeyingCapabilityEnabled =
                    productionActivation.Plan.Plan
                        .BrowserKeyingCapabilityEnabled,
                txProductionActivationPlanCallerRegistered = false,
                txProductionActivationBindingRegistered =
                    productionActivation.Binding.Registered,
                txProductionActivationBindingAttached =
                    productionActivation.ActivationBindingAttached,
                txProductionActivationBindingApplied =
                    productionActivation.ActivationBindingApplied,
                txProductionActivationBindingReason =
                    productionActivation.Binding.Reason,
                txProductionActivationBindingSessionEligible =
                    productionActivation.Binding.SessionEligible,
                txProductionActivationBindingCommandBoundaryEnabled =
                    productionActivation.Binding.Binding
                        .CommandBoundaryEnabled,
                txProductionActivationBindingCommandGateTransmitEnabled =
                    productionActivation.Binding.Binding
                        .CommandGateTransmitEnabled,
                txProductionActivationBindingBrowserIngressExecutionEnabled =
                    productionActivation.Binding.Binding
                        .BrowserTransactionIngressExecutionEnabled,
                txProductionActivationBindingBrowserKeyingCapabilityEnabled =
                    productionActivation.Binding.Binding
                        .BrowserKeyingCapabilityEnabled,
                txProductionActivationAvailable =
                    productionActivation.ActivationAvailable,
                txProductionActivationReason = productionActivation.Reason,
                txProductionActivationCallerRegistered =
                    stationTxProductionActivationBinding.BindingApplied,
                txStationCommandEnvelopeSubmissionEnabled =
                    commandCoordinator.SubmissionEnabled,
                txStationCommandEnvelopeSigningAvailable =
                    commandCoordinator.SigningAvailable,
                txStationCommandEnvelopeVerificationAvailable =
                    commandCoordinator.SignatureVerificationAvailable,
                txStationCommandEnvelopeBoundaryAttached =
                    commandCoordinator.BoundaryAttached,
                txStationCommandEnvelopeBoundaryVerificationAvailable =
                    commandCoordinator.BoundarySignatureVerificationAvailable,
                txStationCommandEnvelopeSubmissionAvailable =
                    commandCoordinator.SubmissionAvailable,
                txStationCommandEnvelopeSubmissionRegistered = false,
                txStationCommandAdapterRegistered = true,
                txStationCommandArmingAvailable = false,
                txStationCommandSetTransmitAvailable = false,
                txIndependentWatchdogHostPackaged = true,
                txIndependentWatchdogProtocolVersion =
                    AetherSDR.TxWatchdog.Protocol.WatchdogProtocol.Version,
                txIndependentWatchdogSupervisionRegistered =
                    watchdog.SupervisionRegistered,
                txIndependentWatchdogState = watchdog.State,
                txIndependentWatchdogConnected =
                    watchdog.ConnectedProcessCount > 0,
                txIndependentWatchdogSessionCount = watchdog.SessionCount,
                txIndependentWatchdogProcessCount =
                    watchdog.RunningProcessCount,
                txIndependentWatchdogConnectedProcessCount =
                    watchdog.ConnectedProcessCount,
                txIndependentWatchdogRegisteredIdentityCount =
                    watchdog.RegisteredIdentityCount,
                txIndependentWatchdogRestartCount = watchdog.RestartCount,
                txIndependentWatchdogArmedProcessCount =
                    watchdog.ArmedProcessCount,
                txIndependentWatchdogReconciliationRequiredCount =
                    watchdog.ReconciliationRequiredCount,
                txIndependentWatchdogUnkeyAttemptCount =
                    watchdog.UnkeyAttemptCount,
                txIndependentWatchdogUnkeyTransportRegistered = true,
                txIndependentWatchdogUnkeyTransportConfiguredEnabled =
                    independentTxWatchdogSettings
                        .RadioCommandTransportEnabled,
                txIndependentWatchdogUnkeyTransportAllowedRadioCount =
                    independentTxWatchdogSettings.AllowedRadioIds.Length,
                txIndependentWatchdogUnkeyTransportCommandTimeoutMilliseconds =
                    independentTxWatchdogSettings
                        .RadioCommandTimeoutMilliseconds,
                txIndependentWatchdogUnkeyTransportAvailable =
                    watchdog.CommandTransportAvailable,
                txIndependentWatchdogUnkeyTransportWebSocketCallerRegistered =
                    false,
                txIndependentWatchdogArmingRegistered = true,
                txIndependentWatchdogArmingConfiguredEnabled =
                    independentTxWatchdogSettings.ArmingEnabled,
                txIndependentWatchdogArmingWebSocketCallerRegistered = false,
                txIndependentWatchdogCommandTransportRegistered =
                    watchdog.CommandTransportAvailable,
                txIndependentWatchdogArmingAvailable =
                    watchdog.ArmingAvailable,
                txCommandTransportRegistered =
                    stationTxCommandTransportRegistration.Registered,
                txCommandTransportAvailable = false,
                txSafetySupervisorArmingAvailable = false
            });
        })
    .AllowAnonymous();

app.MapGet(
        "/auth/login",
        (HttpContext context, string? returnUrl) =>
        {
            string safeReturnUrl = SafeReturnUrl(returnUrl);
            if (string.Equals(
                    authSettings.Mode,
                    "Development",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.Redirect(safeReturnUrl);
            }

            AuthenticationProperties properties =
                new() { RedirectUri = safeReturnUrl };
            return Results.Challenge(
                properties,
                [OpenIdConnectDefaults.AuthenticationScheme]);
        })
    .AllowAnonymous();

app.MapGet(
        "/auth/logout",
        () =>
        {
            if (string.Equals(
                    authSettings.Mode,
                    "Development",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.Redirect("/");
            }

            AuthenticationProperties properties =
                new() { RedirectUri = "/" };
            return Results.SignOut(
                properties,
                [
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    OpenIdConnectDefaults.AuthenticationScheme
                ]);
        })
    .RequireAuthorization();

app.MapGet(
        "/api/account",
        (ClaimsPrincipal user) =>
        {
            string[] roles = AetherRoles.All.Where(user.IsInRole).ToArray();
            return Results.Ok(new
            {
                user = new
                {
                    id = user.FindFirstValue("oid") ??
                         user.FindFirstValue(ClaimTypes.NameIdentifier),
                    name = user.FindFirstValue("name") ?? user.Identity?.Name,
                    email = user.FindFirstValue("preferred_username") ??
                            user.FindFirstValue(ClaimTypes.Email),
                    roles
                },
                authMode = authSettings.Mode
            });
        })
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/api/session",
        async (
            string? browserClientId,
            string? sessionId,
            ClaimsPrincipal user,
            RadioSessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            RadioSession session;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                if (!sessions.TryGetOwned(sessionId, user, out RadioSession? owned) ||
                    owned is null)
                {
                    return Results.NotFound(
                        new { error = "That radio session is not available." });
                }
                if (!string.IsNullOrWhiteSpace(browserClientId) &&
                    !string.Equals(
                        browserClientId,
                        owned.BrowserClientId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "The browser client identifier does not match " +
                                "the requested radio session."
                        });
                }
                session = owned;
                session.Touch();
            }
            else
            {
                try
                {
                    session = await sessions.GetDefaultAsync(
                        user,
                        browserClientId,
                        cancellationToken);
                }
                catch (RadioAccessDeniedException exception)
                {
                    return RadioAccessDeniedResult(exception);
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
            }

            string[] roles = AetherRoles.All.Where(user.IsInRole).ToArray();
            return Results.Ok(new
            {
                user = new
                {
                    id = user.FindFirstValue("oid") ??
                         user.FindFirstValue(ClaimTypes.NameIdentifier),
                    name = user.FindFirstValue("name") ?? user.Identity?.Name,
                    email = user.FindFirstValue("preferred_username") ??
                            user.FindFirstValue(ClaimTypes.Email),
                    roles
                },
                authMode = authSettings.Mode,
                radioMode = radioSettings.Mode,
                allowTransmit = false,
                protocol = "aethersdr-web-experimental/v0",
                sessionId = session.SessionId,
                radioClient = new
                {
                    type = "gui",
                    guiClientId = session.GuiClientId
                }
            });
        })
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/api/radios/catalog",
        (RadioSelectionManager selectionManager) =>
            Results.Ok(selectionManager.GetSnapshot()))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/api/radio/state",
        (
            string? sessionId,
            ClaimsPrincipal user,
            RadioSessionRegistry sessions) =>
        {
            if (!sessions.TryGetOwned(sessionId, user, out RadioSession? session) ||
                session is null)
            {
                return Results.NotFound(
                    new { error = "That radio session is not available." });
            }

            return Results.Ok(session.Coordinator.Snapshot);
        })
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/api/radios",
        async (
            string? sessionId,
            string? browserClientId,
            ClaimsPrincipal user,
            RadioSelectionManager selectionManager,
            RadioSessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            RadioSession? session;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    session = await sessions.GetDefaultAsync(
                        user,
                        browserClientId,
                        cancellationToken);
                }
                catch (RadioAccessDeniedException exception)
                {
                    return RadioAccessDeniedResult(exception);
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(
                        new { error = exception.Message });
                }
            }
            else if (!sessions.TryGetOwned(sessionId, user, out session) ||
                     session is null)
            {
                return Results.NotFound(
                    new { error = "That radio session is not available." });
            }

            return Results.Ok(
                selectionManager.GetSnapshot(
                    session.Endpoint.RadioId,
                    session.Selection.LowBandwidth));
        })
    .RequireAuthorization(AetherPolicies.Observe);

app.MapPost(
        "/api/radios/select",
        async (
            SelectRadioRequest request,
            ClaimsPrincipal user,
            RadioSelectionManager selectionManager,
            RadioSessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            if (!selectionManager.TryResolve(
                    request.RadioId,
                    out SelectedRadioEndpoint selected,
                    out string? error))
            {
                return Results.BadRequest(new { error });
            }

            RadioSession? currentSession = null;
            if (!string.IsNullOrWhiteSpace(request.CurrentSessionId) &&
                (!sessions.TryGetOwned(
                    request.CurrentSessionId,
                    user,
                    out currentSession) ||
                 currentSession is null))
            {
                return Results.BadRequest(
                    new { error = "The current radio session is invalid." });
            }
            if (currentSession is not null &&
                !string.IsNullOrWhiteSpace(request.BrowserClientId) &&
                !string.Equals(
                    request.BrowserClientId,
                    currentSession.BrowserClientId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(
                    new
                    {
                        error =
                            "The browser client identifier does not match " +
                            "the current radio session."
                    });
            }

            RadioSession selectedSession;
            try
            {
                selectedSession = await sessions.GetOrCreateAsync(
                    user,
                    currentSession?.BrowserClientId ??
                        request.BrowserClientId,
                    selected,
                    request.LowBandwidth,
                    cancellationToken);
            }
            catch (RadioAccessDeniedException exception)
            {
                return RadioAccessDeniedResult(exception);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            bool connectionChanged =
                currentSession is null ||
                !string.Equals(
                    currentSession.SessionId,
                    selectedSession.SessionId,
                    StringComparison.Ordinal);
            if (connectionChanged && currentSession is not null)
            {
                await sessions.TerminateOwnedSessionAsync(
                    currentSession.SessionId,
                    user);
            }

            return Results.Ok(new
            {
                selected,
                connectionChanged,
                sessionId = selectedSession.SessionId
            });
        })
    .RequireAuthorization(AetherPolicies.Control);

app.MapPost(
        "/api/session/release",
        async (
            string? sessionId,
            ClaimsPrincipal user,
            RadioSessionRegistry sessions) =>
        {
            bool released = await sessions.TerminateOwnedSessionAsync(
                sessionId,
                user);
            return released
                ? Results.NoContent()
                : Results.NotFound(
                    new { error = "That radio session is not available." });
        })
    .RequireAuthorization(AetherPolicies.Observe);

app.MapPost(
        "/api/radio/low-bandwidth",
        (
            SetLowBandwidthRequest request,
            ClaimsPrincipal user,
            RadioSessionRegistry sessions) =>
        {
            if (!sessions.TryGetOwned(
                    request.SessionId,
                    user,
                    out RadioSession? session) ||
                session is null)
            {
                return Results.NotFound(
                    new { error = "That radio session is not available." });
            }

            bool reconnecting = session.SetLowBandwidth(request.Enabled);
            return Results.Ok(new
            {
                enabled = session.Selection.LowBandwidth,
                reconnecting
            });
        })
    .RequireAuthorization(AetherPolicies.Control);

app.MapGet(
        "/api/admin/radios",
        (RadioAdministrationService administration) =>
            Results.Ok(new { radios = administration.GetInventory() }))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapGet(
        "/api/admin/stations",
        (RemoteStationCatalogService remoteStations) =>
            Results.Ok(remoteStations.GetAdministrationSnapshot()))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/admin/stations/enrollment-codes",
        async (
            CreateRemoteStationEnrollmentRequest request,
            ClaimsPrincipal user,
            RemoteStationCatalogService remoteStations,
            AdministrativeAuditStore audit,
            CancellationToken cancellationToken) =>
        {
            (string administratorId, string administratorName) =
                GetAdministrativeActor(user);
            string stationTarget = $"station:{request.StationId}";
            try
            {
                RemoteStationEnrollmentCodeResult result =
                    await remoteStations.CreateEnrollmentCodeAsync(
                        request.StationId,
                        cancellationToken);
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.CreateStationEnrollmentCode,
                    stationTarget,
                    result.Purpose,
                    AdministrativeAuditResults.Succeeded,
                    $"Created a one-time {result.Purpose} code that expires " +
                    $"at {result.ExpiresAt:O}.");
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.CreateStationEnrollmentCode,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (RemoteStationManagementException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.CreateStationEnrollmentCode,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return RemoteStationManagementFailure(exception);
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                      IOException or
                      JsonException or
                      InvalidDataException)
            {
                const string message =
                    "Station enrollment is temporarily unavailable.";
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.CreateStationEnrollmentCode,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    message);
                return Results.Json(
                    new { error = message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/admin/stations/{stationId}/{action}",
        async (
            string stationId,
            string action,
            ClaimsPrincipal user,
            RemoteStationCatalogService remoteStations,
            AdministrativeAuditStore audit,
            CancellationToken cancellationToken) =>
        {
            (string administratorId, string administratorName) =
                GetAdministrativeActor(user);
            string stationTarget = $"station:{stationId}";
            string auditAction = action switch
            {
                "enable" =>
                    AdministrativeAuditActions.EnableStationCredential,
                "disable" =>
                    AdministrativeAuditActions.DisableStationCredential,
                "revoke" =>
                    AdministrativeAuditActions.RevokeStationCredential,
                _ => "station.credential.invalid"
            };
            try
            {
                RemoteStationCredentialAdministrationEntry result =
                    await remoteStations.SetCredentialStateAsync(
                        stationId,
                        action,
                        cancellationToken);
                audit.Record(
                    administratorId,
                    administratorName,
                    auditAction,
                    stationTarget,
                    result.State,
                    AdministrativeAuditResults.Succeeded,
                    $"Station credential changed to {result.State}.");
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    auditAction,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (RemoteStationManagementException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    auditAction,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return RemoteStationManagementFailure(exception);
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                      IOException or
                      JsonException or
                      InvalidDataException)
            {
                const string message =
                    "Station credential management is temporarily " +
                    "unavailable.";
                audit.Record(
                    administratorId,
                    administratorName,
                    auditAction,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    message);
                return Results.Json(
                    new { error = message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/station-enrollment/redeem",
        async (
            RedeemRemoteStationEnrollmentRequest request,
            RemoteStationCatalogService remoteStations,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RemoteStationEnrollmentResult result =
                    await remoteStations.RedeemEnrollmentAsync(
                        request,
                        cancellationToken);
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (RemoteStationManagementException exception)
            {
                return RemoteStationManagementFailure(exception);
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                      IOException or
                      JsonException or
                      InvalidDataException)
            {
                return Results.Json(
                    new
                    {
                        error =
                            "Station enrollment is temporarily unavailable."
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
    .AllowAnonymous()
    .RequireRateLimiting("station-enrollment");

app.MapGet(
        "/api/admin/audit",
        (
            int? limit,
            AdministrativeAuditStore audit) =>
            Results.Ok(new
            {
                events = audit.GetRecent(limit ?? 50)
            }))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/admin/radios/{radioId}/policy",
        (
            string radioId,
            UpdateRadioAccessPolicyRequest request,
            ClaimsPrincipal user,
            RadioAdministrationService administration,
            AdministrativeAuditStore audit) =>
        {
            (string administratorId, string administratorName) =
                GetAdministrativeActor(user);
            if (!RadioSessionRegistry.TryGetUserId(
                    user,
                    out administratorId))
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioPolicy,
                    radioId,
                    request.ReservedUserId,
                    AdministrativeAuditResults.Failed,
                    "A stable administrator ID was not available.");
                return Results.BadRequest(
                    new { error = "A stable administrator ID is required." });
            }

            try
            {
                RadioAccessPolicySnapshot policy =
                    administration.UpdatePolicy(
                        radioId,
                        request,
                        administratorId);
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioPolicy,
                    radioId,
                    policy.ReservedUserId,
                    AdministrativeAuditResults.Succeeded,
                    $"Access changed to {policy.Mode}; reservation " +
                    $"{(policy.ReservedUserId is null ? "cleared" : "set")}.");
                return Results.Ok(policy);
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioPolicy,
                    radioId,
                    request.ReservedUserId,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (KeyNotFoundException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioPolicy,
                    radioId,
                    request.ReservedUserId,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.NotFound(new { error = exception.Message });
            }
        })
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/admin/radios/{radioId}/operators/{userId}/disconnect",
        async (
            string radioId,
            string userId,
            ClaimsPrincipal user,
            RadioAdministrationService administration,
            AdministrativeAuditStore audit) =>
        {
            (string administratorId, string administratorName) =
                GetAdministrativeActor(user);
            try
            {
                ForceDisconnectResult result =
                    await administration.ForceDisconnectAsync(
                        radioId,
                        userId);
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.ForceDisconnectOperator,
                    radioId,
                    userId,
                    AdministrativeAuditResults.Succeeded,
                    $"Released {result.BrowserConnections} browser " +
                    $"connection(s) and {result.RadioSessions} radio session(s).");
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.ForceDisconnectOperator,
                    radioId,
                    userId,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (KeyNotFoundException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.ForceDisconnectOperator,
                    radioId,
                    userId,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.NotFound(new { error = exception.Message });
            }
        })
    .RequireAuthorization(AetherPolicies.Admin);

app.MapGet(
        "/styles.css",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "styles.css"),
                "text/css"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/portal.css",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "portal.css"),
                "text/css"))
    .AllowAnonymous();

app.MapGet(
    "/admin-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "admin-controls.js"),
                "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/admin-diagnostics.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "admin-diagnostics.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapGet(
    "/radio-select.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "radio-select.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/admin-page.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "admin-page.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapGet(
        "/app.js",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "app.js"),
                "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/waterfall.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "waterfall.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/slice-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "slice-controls.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/frequency-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "frequency-controls.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/range-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "range-controls.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/layout-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "layout-controls.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/band-plan.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "band-plan.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/audio.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "audio.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/radio-transport.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "radio-transport.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/radio-transport-core.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "radio-transport-core.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/network-profile.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "network-profile.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/tx-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "tx-controls.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/radio-transport-worker.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(
                environment.WebRootPath,
                "radio-transport-worker.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/microphone.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "microphone.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/audio-worklet.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "audio-worklet.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/meter.js",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "meter.js"),
                "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/assets/logo.png",
        () =>
            Results.File(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "wwwroot",
                    "assets",
                    "logo.png"),
                "image/png"))
    .AllowAnonymous();

app.MapGet(
        "/assets/s-meter-v1.json",
        () =>
            Results.File(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "wwwroot",
                    "assets",
                    "s-meter-v1.json"),
                "application/json"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/assets/bandplans/arrl-us.json",
        () =>
            Results.File(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "wwwroot",
                    "assets",
                    "bandplans",
                    "arrl-us.json"),
                "application/json"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/ws/radio",
        RadioWebSocketEndpoint.HandleAsync)
    .RequireAuthorization(AetherPolicies.Observe)
    .RequireRateLimiting("websocket");

app.MapGet(
        "/",
        (HttpContext context, IWebHostEnvironment environment) =>
            context.User.Identity?.IsAuthenticated == true
                ? Results.Redirect("/radios")
                : Results.File(
                    Path.Combine(environment.WebRootPath, "login.html"),
                    "text/html"))
    .AllowAnonymous();

app.MapGet(
        "/login",
        (HttpContext context, IWebHostEnvironment environment) =>
            context.User.Identity?.IsAuthenticated == true
                ? Results.Redirect("/radios")
                : Results.File(
                    Path.Combine(environment.WebRootPath, "login.html"),
                    "text/html"))
    .AllowAnonymous();

app.MapGet(
        "/access-denied",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "access-denied.html"),
                "text/html"))
    .AllowAnonymous();

app.MapGet(
        "/radios",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "radios.html"),
                "text/html"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/radio",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "index.html"),
                "text/html"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/admin",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "admin.html"),
                "text/html"))
    .RequireAuthorization(AetherPolicies.Admin);

app.Run();

static IResult RemoteStationManagementFailure(
    RemoteStationManagementException exception)
{
    int statusCode = exception.StatusCode switch
    {
        HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
        HttpStatusCode.NotFound => StatusCodes.Status404NotFound,
        HttpStatusCode.Conflict => StatusCodes.Status409Conflict,
        HttpStatusCode.ServiceUnavailable =>
            StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status502BadGateway
    };
    return Results.Json(
        new { error = exception.Message },
        statusCode: statusCode);
}

static void ConfigureAuthentication(
    WebApplicationBuilder builder,
    AuthSettings authSettings)
{
    bool developmentAuth = string.Equals(
        authSettings.Mode,
        "Development",
        StringComparison.OrdinalIgnoreCase);

    if (developmentAuth)
    {
        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Development authentication is forbidden outside the Development environment.");
        }

        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme =
                    DevelopmentAuthenticationDefaults.Scheme;
                options.DefaultChallengeScheme =
                    DevelopmentAuthenticationDefaults.Scheme;
            })
            .AddScheme<
                AuthenticationSchemeOptions,
                DevelopmentAuthenticationHandler>(
                DevelopmentAuthenticationDefaults.Scheme,
                _ => { });
        return;
    }

    string clientSecret = OidcClientSecretResolver.Resolve(authSettings);
    if (string.IsNullOrWhiteSpace(authSettings.Authority) ||
        string.IsNullOrWhiteSpace(authSettings.ClientId) ||
        string.IsNullOrWhiteSpace(clientSecret))
    {
        throw new InvalidOperationException(
            "OIDC authentication requires Auth:Authority, Auth:ClientId, and " +
            "either Auth:ClientSecret or Auth:ClientSecretFile.");
    }

    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme =
                CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultSignInScheme =
                CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme =
                OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(
            CookieAuthenticationDefaults.AuthenticationScheme,
            options =>
            {
                options.Cookie.Name = "__Host-AetherSdrWeb";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.AccessDeniedPath = "/access-denied";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
            })
        .AddOpenIdConnect(
            OpenIdConnectDefaults.AuthenticationScheme,
            options =>
            {
                options.Authority = authSettings.Authority.TrimEnd('/');
                options.ClientId = authSettings.ClientId;
                options.ClientSecret = clientSecret;
                options.CallbackPath = authSettings.CallbackPath;
                options.SignedOutCallbackPath =
                    authSettings.SignedOutCallbackPath;
                options.SignInScheme =
                    CookieAuthenticationDefaults.AuthenticationScheme;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = authSettings.NameClaimType,
                    RoleClaimType = authSettings.RoleClaimType,
                    ValidateIssuer = true
                };
            });
}

static void ConfigureAuthorization(IServiceCollection services)
{
    AuthorizationPolicy authenticated =
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

    services.AddAuthorizationBuilder()
        .SetFallbackPolicy(authenticated)
        .AddPolicy(
            AetherPolicies.Observe,
            policy => policy.RequireRole(
                AetherRoles.Observe,
                AetherRoles.Control,
                AetherRoles.Transmit,
                AetherRoles.Admin))
        .AddPolicy(
            AetherPolicies.Control,
            policy => policy.RequireRole(AetherRoles.Control, AetherRoles.Admin))
        .AddPolicy(
            AetherPolicies.Transmit,
            policy => policy.RequireRole(AetherRoles.Transmit, AetherRoles.Admin))
        .AddPolicy(
            AetherPolicies.Admin,
            policy => policy.RequireRole(AetherRoles.Admin));
}

static void ConfigureReverseProxy(
    IServiceCollection services,
    ReverseProxySettings settings)
{
    if (!settings.Enabled)
    {
        return;
    }
    if (settings.KnownProxies.Length == 0)
    {
        throw new InvalidOperationException(
            "ReverseProxy:KnownProxies must contain at least one trusted IP address.");
    }

    List<IPAddress> knownProxies = [];
    foreach (string configuredAddress in settings.KnownProxies)
    {
        if (!IPAddress.TryParse(
                configuredAddress?.Trim(),
                out IPAddress? address))
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownProxies contains an invalid IP address: " +
                $"'{configuredAddress}'.");
        }

        knownProxies.Add(address);
    }

    services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        foreach (IPAddress address in knownProxies)
        {
            options.KnownProxies.Add(address);
        }
    });
}

static string SafeReturnUrl(string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl) ||
        !Uri.TryCreate(returnUrl, UriKind.Relative, out Uri? parsed) ||
        !returnUrl.StartsWith("/", StringComparison.Ordinal) ||
        returnUrl.StartsWith("//", StringComparison.Ordinal))
    {
        return "/";
    }

    return parsed.ToString();
}

static IResult RadioAccessDeniedResult(
    RadioAccessDeniedException exception) =>
    Results.Conflict(new
    {
        code = "radio_access_denied",
        radioId = exception.RadioId,
        error = exception.Message
    });

static (string Id, string DisplayName) GetAdministrativeActor(
    ClaimsPrincipal user)
{
    string actorId =
        user.FindFirstValue("oid") ??
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        user.FindFirstValue("sub") ??
        "unknown";
    string displayName =
        user.FindFirstValue("name") ??
        user.Identity?.Name ??
        user.FindFirstValue("preferred_username") ??
        actorId;
    return (actorId.Trim(), displayName.Trim());
}

public partial class Program;
