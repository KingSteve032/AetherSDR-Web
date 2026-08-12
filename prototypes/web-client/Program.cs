using System.Security.Claims;
using System.Net;
using System.Text.Json;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Operations;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

const string ProductionTxPreflightSwitch =
    "--validate-production-tx-activation";
const string ProductionTxRadioIdSwitch =
    "--production-tx-radio-id";
InstallationInstallerConsoleCommandLine installationInstallerCommandLine =
    InstallationInstallerConsoleCommandParser.Parse(args);
InstallationSetupConsoleCommandLine installationSetupCommandLine =
    InstallationSetupConsoleCommandParser.Parse(
        installationInstallerCommandLine.ApplicationArguments);
OfflineReleaseInstallPreflightCommandLine releaseInstallPreflightCommandLine =
    OfflineReleaseInstallPreflightCommandParser.Parse(
        installationSetupCommandLine.ApplicationArguments);
ReleaseUpdateConsoleCommandLine releaseUpdateCommandLine =
    ReleaseUpdateConsoleCommandParser.Parse(
        releaseInstallPreflightCommandLine.ApplicationArguments);
AetherIdentityDatabaseCommandLine identityDatabaseCommandLine =
    AetherIdentityDatabaseCommandParser.Parse(
        releaseUpdateCommandLine.ApplicationArguments);
OperationsConsoleCommandLine operationsCommandLine =
    OperationsConsoleCommandParser.Parse(
        identityDatabaseCommandLine.ApplicationArguments);
bool productionTxPreflightRequested = false;
string? productionTxPreflightRadioId = null;
List<string> applicationArguments = [];
for (int index = 0;
     index < operationsCommandLine.ApplicationArguments.Count;
     index++)
{
    string argument =
        operationsCommandLine.ApplicationArguments[index];
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
            index + 1 >= operationsCommandLine.ApplicationArguments.Count)
        {
            throw new InvalidOperationException(
                "Production TX activation preflight requires one exact radio ID.");
        }
        productionTxPreflightRadioId =
            operationsCommandLine.ApplicationArguments[++index];
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
if (installationInstallerCommandLine.Command !=
        InstallationInstallerConsoleCommandKind.None &&
    installationSetupCommandLine.Command !=
        InstallationSetupConsoleCommandKind.None)
{
    throw new InvalidOperationException(
        "Installer commands cannot run with installation setup commands.");
}
if (installationInstallerCommandLine.Command !=
        InstallationInstallerConsoleCommandKind.None &&
    releaseInstallPreflightCommandLine.Command !=
        OfflineReleaseInstallPreflightCommandKind.None)
{
    throw new InvalidOperationException(
        "Installer commands cannot run with release install preflight.");
}
if (installationInstallerCommandLine.Command !=
        InstallationInstallerConsoleCommandKind.None &&
    releaseUpdateCommandLine.Command !=
        ReleaseUpdateConsoleCommandKind.None)
{
    throw new InvalidOperationException(
        "Installer commands cannot run with release update commands.");
}
if (productionTxPreflightRequested &&
    installationInstallerCommandLine.Command !=
        InstallationInstallerConsoleCommandKind.None)
{
    throw new InvalidOperationException(
        "Installer commands cannot run with production TX preflight.");
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
if (identityDatabaseCommandLine.Command !=
        AetherIdentityDatabaseCommandKind.None &&
    (installationInstallerCommandLine.Command !=
        InstallationInstallerConsoleCommandKind.None ||
     installationSetupCommandLine.Command !=
        InstallationSetupConsoleCommandKind.None ||
     releaseInstallPreflightCommandLine.Command !=
        OfflineReleaseInstallPreflightCommandKind.None ||
     releaseUpdateCommandLine.Command !=
        ReleaseUpdateConsoleCommandKind.None ||
     operationsCommandLine.Command != OperationsConsoleCommandKind.None ||
     productionTxPreflightRequested))
{
    throw new InvalidOperationException(
        "Identity database commands cannot run with another standalone command.");
}
if (operationsCommandLine.Command != OperationsConsoleCommandKind.None &&
    (installationInstallerCommandLine.Command !=
        InstallationInstallerConsoleCommandKind.None ||
     installationSetupCommandLine.Command !=
        InstallationSetupConsoleCommandKind.None ||
     releaseInstallPreflightCommandLine.Command !=
        OfflineReleaseInstallPreflightCommandKind.None ||
     releaseUpdateCommandLine.Command != ReleaseUpdateConsoleCommandKind.None ||
     identityDatabaseCommandLine.Command != AetherIdentityDatabaseCommandKind.None ||
     productionTxPreflightRequested))
{
    throw new InvalidOperationException(
        "Encrypted backup commands cannot run with another standalone command.");
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(
    [.. applicationArguments]);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

if (operationsCommandLine.Command != OperationsConsoleCommandKind.None)
{
    InstallationPathSettings operationsPathSettings =
        builder.Configuration
            .GetSection(InstallationPathSettings.SectionName)
            .Get<InstallationPathSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationPathSettings();
    InstallationPathLayout operationsPathLayout =
        builder.Environment.IsDevelopment()
            ? InstallationPathLayout.Development
            : OperatingSystem.IsLinux()
                ? InstallationPathLayout.LinuxSystem
                : throw new InvalidOperationException(
                    "Standalone production backup/restore commands require Linux.");
    InstallationPaths operationsPaths = InstallationPaths.Resolve(
        builder.Environment.ContentRootPath,
        operationsPathLayout,
        operationsPathSettings);
    if (!builder.Environment.IsDevelopment() &&
        !string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "Production backup/restore commands must run as root so all protected authority can be read or restored exactly.");
    }
    Environment.ExitCode = await OperationsConsole.ExecuteAsync(
        operationsCommandLine,
        operationsPaths,
        TimeProvider.System,
        Console.Out);
    return;
}

if (identityDatabaseCommandLine.Command !=
    AetherIdentityDatabaseCommandKind.None)
{
    InstallationPathSettings identityPathSettings =
        builder.Configuration
            .GetSection(InstallationPathSettings.SectionName)
            .Get<InstallationPathSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationPathSettings();
    InstallationPathLayout identityPathLayout =
        builder.Environment.IsDevelopment()
            ? InstallationPathLayout.Development
            : OperatingSystem.IsLinux()
                ? InstallationPathLayout.LinuxSystem
                : throw new InvalidOperationException(
                    "Standalone production identity database commands require Linux.");
    InstallationPaths identityPaths = InstallationPaths.Resolve(
        builder.Environment.ContentRootPath,
        identityPathLayout,
        identityPathSettings);
    if (!builder.Environment.IsDevelopment() &&
        !string.Equals(
            Environment.UserName,
            "aethersdr",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "Production identity database commands must run as the aethersdr service user.");
    }
    Environment.ExitCode = await AetherIdentityDatabaseConsole.ExecuteAsync(
        identityDatabaseCommandLine,
        identityPaths,
        Console.Out);
    return;
}

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
    ReleaseUpdateConsoleCommandKind.CheckGitHubRelease)
{
    ReleaseManifestTrustSettings releaseCheckTrustSettings =
        builder.Configuration
            .GetSection(ReleaseManifestTrustSettings.SectionName)
            .Get<ReleaseManifestTrustSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new ReleaseManifestTrustSettings();
    GitHubReleaseSourceSettings releaseGitHubCheckSourceSettings =
        builder.Configuration
            .GetSection(GitHubReleaseSourceSettings.SectionName)
            .Get<GitHubReleaseSourceSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new GitHubReleaseSourceSettings();
    ReleaseManifestTrustRegistry releaseCheckTrustRegistry = new(
        Options.Create(releaseCheckTrustSettings),
        NullLogger<ReleaseManifestTrustRegistry>.Instance);
    SignedReleaseManifestVerificationService releaseCheckManifestService = new(
        releaseCheckTrustRegistry,
        new SignedReleaseManifestVerifier());
    LocalOfflineReleaseBundleVerificationService releaseCheckBundleService =
        new(releaseCheckManifestService);
    GitHubReleaseBundleSource releaseGitHubSource = new(
        releaseGitHubCheckSourceSettings,
        GitHubReleaseHttpClient.CreateStandaloneClient,
        releaseCheckBundleService,
        Path.GetTempPath(),
        NullLogger<GitHubReleaseBundleSource>.Instance);
    GitHubReleaseBundleCheckConsole releaseGitHubCheckConsole =
        new(releaseGitHubSource);
    Environment.ExitCode = await releaseGitHubCheckConsole.ExecuteAsync(
        releaseUpdateCommandLine,
        Console.Out);
    return;
}
if (releaseUpdateCommandLine.Command ==
    ReleaseUpdateConsoleCommandKind.DownloadGitHubRelease)
{
    InstallationPathSettings releaseDownloadPathSettings =
        builder.Configuration
            .GetSection(InstallationPathSettings.SectionName)
            .Get<InstallationPathSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationPathSettings();
    InstallationPathLayout releaseDownloadPathLayout =
        builder.Environment.IsDevelopment()
            ? InstallationPathLayout.Development
            : OperatingSystem.IsLinux()
                ? InstallationPathLayout.LinuxSystem
                : throw new InvalidOperationException(
                    "Persistent GitHub release download requires Linux.");
    InstallationPaths releaseDownloadPaths = InstallationPaths.Resolve(
        builder.Environment.ContentRootPath,
        releaseDownloadPathLayout,
        releaseDownloadPathSettings);
    ReleaseManifestTrustSettings releaseDownloadTrustSettings =
        builder.Configuration
            .GetSection(ReleaseManifestTrustSettings.SectionName)
            .Get<ReleaseManifestTrustSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new ReleaseManifestTrustSettings();
    GitHubReleaseSourceSettings releaseDownloadSourceSettings =
        builder.Configuration
            .GetSection(GitHubReleaseSourceSettings.SectionName)
            .Get<GitHubReleaseSourceSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new GitHubReleaseSourceSettings();
    ReleaseManifestTrustRegistry releaseDownloadTrustRegistry = new(
        Options.Create(releaseDownloadTrustSettings),
        NullLogger<ReleaseManifestTrustRegistry>.Instance);
    SignedReleaseManifestVerificationService releaseDownloadManifestService = new(
        releaseDownloadTrustRegistry,
        new SignedReleaseManifestVerifier());
    LocalOfflineReleaseBundleVerificationService releaseDownloadBundleService =
        new(releaseDownloadManifestService);
    GitHubReleaseBundleSource releaseDownloadGitHubSource = new(
        releaseDownloadSourceSettings,
        GitHubReleaseHttpClient.CreateStandaloneClient,
        releaseDownloadBundleService,
        releaseDownloadPaths.ReleaseDownloadDirectory,
        NullLogger<GitHubReleaseBundleSource>.Instance);
    GitHubReleaseBundleDownloadService releaseDownloadService = new(
        releaseDownloadGitHubSource,
        releaseDownloadBundleService,
        releaseDownloadPaths);
    GitHubReleaseBundleDownloadConsole releaseDownloadConsole =
        new(releaseDownloadService);
    Environment.ExitCode = await releaseDownloadConsole.ExecuteAsync(
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

if (installationInstallerCommandLine.Command !=
    InstallationInstallerConsoleCommandKind.None)
{
    InstallationPathSettings installerPathSettings =
        builder.Configuration
            .GetSection(InstallationPathSettings.SectionName)
            .Get<InstallationPathSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationPathSettings();
    InstallationPathLayout installerPathLayout =
        builder.Environment.IsDevelopment()
            ? InstallationPathLayout.Development
            : OperatingSystem.IsLinux()
                ? InstallationPathLayout.LinuxSystem
                : throw new InvalidOperationException(
                    "Standalone installer commands require Linux.");
    InstallationPaths installerPaths = InstallationPaths.Resolve(
        builder.Environment.ContentRootPath,
        installerPathLayout,
        installerPathSettings);
    InstallationInstallerExecutionSettings installerExecutionSettings =
        builder.Configuration
            .GetSection(InstallationInstallerExecutionSettings.SectionName)
            .Get<InstallationInstallerExecutionSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationInstallerExecutionSettings();
    InstallationInstallerUbuntuRuntimeSettings installerRuntimeSettings =
        builder.Configuration
            .GetSection(
                InstallationInstallerUbuntuRuntimeSettings.SectionName)
            .Get<InstallationInstallerUbuntuRuntimeSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new InstallationInstallerUbuntuRuntimeSettings();
    InstallationSetupStore installerSetupStore =
        new(installerPaths.SetupStatePath);
    InstallationInstallerVerifiedReleaseBinding? installerRelease = null;
    if (installationInstallerCommandLine.Command is
        InstallationInstallerConsoleCommandKind.Apply or
        InstallationInstallerConsoleCommandKind.Repair)
    {
        try
        {
            ReleaseManifestTrustSettings installerTrustSettings =
                builder.Configuration
                    .GetSection(ReleaseManifestTrustSettings.SectionName)
                    .Get<ReleaseManifestTrustSettings>(options =>
                        options.ErrorOnUnknownConfiguration = true) ??
                new ReleaseManifestTrustSettings();
            ReleaseManifestTrustRegistry installerTrustRegistry = new(
                Options.Create(installerTrustSettings),
                NullLogger<ReleaseManifestTrustRegistry>.Instance);
            SignedReleaseManifestVerificationService installerManifestVerifier =
                new(
                    installerTrustRegistry,
                    new SignedReleaseManifestVerifier());
            LocalOfflineReleaseBundleVerificationService
                installerBundleVerifier =
                    new(installerManifestVerifier);
            InstallationInstallerInitialReleasePreparation
                installerPreparation =
                    new(installerBundleVerifier);
            InstallationSetupState installerSetupState =
                await installerSetupStore.LoadAsync();
            installerRelease = await installerPreparation.PrepareAsync(
                installationInstallerCommandLine.BundleDirectory,
                installationInstallerCommandLine.Architecture ??
                    throw new InvalidOperationException(
                        "Installer architecture is required."),
                installationInstallerCommandLine.ReleaseIdentity,
                installationInstallerCommandLine.ConfigurationSchemaVersion ??
                    throw new InvalidOperationException(
                        "Installer configuration schema is required."),
                installationInstallerCommandLine.ProtocolVersion ??
                    throw new InvalidOperationException(
                        "Installer protocol version is required."),
                installerSetupState,
                installerPaths);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or IOException or
                UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException)
        {
            await Console.Out.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        outcome = "rejected",
                        code = "verified-release-preparation-rejected",
                        mutationAttempted = false
                    }));
            Environment.ExitCode = 2;
            return;
        }
    }
    InstallationInstallerUbuntuMutationExecutor installerMutationExecutor =
        new(new LocalInstallationInstallerUbuntuMutationPrimitives());
    LocalInstallationInstallerUbuntuRuntime installerRuntime =
        new(installerMutationExecutor, installerRuntimeSettings);
    InstallationInstallerUbuntuHostTransaction installerHost =
        new(
            installerRuntime,
            installerRelease,
            installationInstallerCommandLine
                .AuthenticationClientSecretSourceFile);
    using InstallationInstallerCoordinator installerCoordinator =
        new(
            installerSetupStore,
            installerHost,
            installerExecutionSettings);
    InstallationInstallerConsole installerConsole =
        new(installerCoordinator);
    Environment.ExitCode = await installerConsole.ExecuteAsync(
        installationInstallerCommandLine,
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
    InstallationPaths setupOnlyPaths =
        installationHostStartupPlan.Paths ??
        throw new InvalidOperationException(
            "Setup-only identity initialization requires resolved paths.");
    _ = await AetherSetupIdentityDatabaseBootstrap.EnsureInitializedAsync(
        setupOnlyPaths);
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

InstallationServiceHostSettings installationServiceHostSettings =
    builder.Configuration
        .GetSection(InstallationServiceHostSettings.SectionName)
        .Get<InstallationServiceHostSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new InstallationServiceHostSettings();
InstallationServiceHostRole installationServiceHostRole =
    InstallationServiceHost.Validate(installationServiceHostSettings);
AuthSettings authSettings =
    builder.Configuration
        .GetSection(AuthSettings.SectionName)
        .Get<AuthSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new AuthSettings();
AetherAuthenticationTopology authenticationTopology =
    installationServiceHostRole == InstallationServiceHostRole.StationEngine
        ? AetherAuthenticationConfiguration.CreateServiceBoundary()
        : AetherAuthenticationConfiguration.Validate(
            authSettings,
            builder.Environment.IsDevelopment());
if (installationServiceHostRole == InstallationServiceHostRole.Gateway &&
    authenticationTopology.Mode != AetherAuthenticationMode.Development)
{
    InstallationPaths identityRuntimePaths = resolveInstallationPaths();
    AetherIdentityDatabaseReport identityDatabase =
        await AetherIdentityDatabaseMigration.ValidateAsync(
            identityRuntimePaths);
    if (!string.Equals(
            identityDatabase.Outcome,
            "converged",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "The identity database must be explicitly initialized and " +
            "validated before production authentication can start.");
    }
    builder.Services.AddAetherIdentityPersistence(identityRuntimePaths);
    builder.Services.AddAetherLocalAuthenticationFoundation(
        authenticationTopology.LocalPolicy);
}
builder.Services.AddSingleton(authenticationTopology);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AetherExternalAuthenticationService>();
builder.Services.AddScoped<AetherAuthenticationSessionService>();
builder.Services.AddScoped<AetherAdministratorAuthorityService>();
builder.Services.AddScoped<AetherOpenIdConnectEvents>();
builder.Services.AddScoped<AetherCookieAuthenticationEvents>();
RadioSettings radioSettings =
    builder.Configuration.GetSection(RadioSettings.SectionName).Get<RadioSettings>() ??
    new RadioSettings();
RemoteStationSettings remoteStationSettings =
    builder.Configuration
        .GetSection(RemoteStationSettings.SectionName)
        .Get<RemoteStationSettings>() ??
    new RemoteStationSettings();
AetherRemoteBootstrapSettings aetherRemoteBootstrapSettings =
    builder.Configuration
        .GetSection(AetherRemoteBootstrapSettings.SectionName)
        .Get<AetherRemoteBootstrapSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new AetherRemoteBootstrapSettings();
OperationsSettings operationsSettings =
    builder.Configuration
        .GetSection(OperationsSettings.SectionName)
        .Get<OperationsSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new OperationsSettings();
OperationsReadinessService.ValidateSettings(operationsSettings);
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
GitHubReleaseSourceSettings releaseGitHubSourceSettings =
    builder.Configuration
        .GetSection(GitHubReleaseSourceSettings.SectionName)
        .Get<GitHubReleaseSourceSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new GitHubReleaseSourceSettings();
ReleaseMigrationRunnerTrustSettings releaseMigrationRunnerTrustSettings =
    builder.Configuration
        .GetSection(ReleaseMigrationRunnerTrustSettings.SectionName)
        .Get<ReleaseMigrationRunnerTrustSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new ReleaseMigrationRunnerTrustSettings();
ReleaseActivationServiceControlSettings
    releaseActivationServiceControlSettings =
        builder.Configuration
            .GetSection(ReleaseActivationServiceControlSettings.SectionName)
            .Get<ReleaseActivationServiceControlSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new ReleaseActivationServiceControlSettings();
ReleaseActivationCurrentPointerSwitchSettings
    releaseActivationCurrentPointerSwitchSettings =
        builder.Configuration
            .GetSection(
                ReleaseActivationCurrentPointerSwitchSettings.SectionName)
            .Get<ReleaseActivationCurrentPointerSwitchSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new ReleaseActivationCurrentPointerSwitchSettings();
ReleaseActivationHealthVerificationSettings
    releaseActivationHealthVerificationSettings =
        builder.Configuration
            .GetSection(
                ReleaseActivationHealthVerificationSettings.SectionName)
            .Get<ReleaseActivationHealthVerificationSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new ReleaseActivationHealthVerificationSettings();
ReleaseActivationRollbackSettings releaseActivationRollbackSettings =
    builder.Configuration
        .GetSection(ReleaseActivationRollbackSettings.SectionName)
        .Get<ReleaseActivationRollbackSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new ReleaseActivationRollbackSettings();
ReleaseActivationOperatorApprovalSettings
    releaseActivationOperatorApprovalSettings =
        builder.Configuration
            .GetSection(
                ReleaseActivationOperatorApprovalSettings.SectionName)
            .Get<ReleaseActivationOperatorApprovalSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new ReleaseActivationOperatorApprovalSettings();
ReleaseUpdateTransactionSettings releaseUpdateTransactionSettings =
    builder.Configuration
        .GetSection(ReleaseUpdateTransactionSettings.SectionName)
        .Get<ReleaseUpdateTransactionSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new ReleaseUpdateTransactionSettings();
ReleaseActivationHostRestartSettings releaseActivationHostRestartSettings =
    builder.Configuration
        .GetSection(ReleaseActivationHostRestartSettings.SectionName)
        .Get<ReleaseActivationHostRestartSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new ReleaseActivationHostRestartSettings();
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

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = AetherAntiforgery.HeaderName;
    options.Cookie.Name = "__Host-AetherSdrWeb-Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
builder.Services.AddSingleton(Options.Create(authSettings));
builder.Services.AddSingleton(Options.Create(radioSettings));
builder.Services.AddSingleton(Options.Create(remoteStationSettings));
builder.Services.AddSingleton(Options.Create(independentTxWatchdogSettings));
builder.Services.AddSingleton(Options.Create(releaseManifestTrustSettings));
builder.Services.AddSingleton(Options.Create(releaseGitHubSourceSettings));
builder.Services.AddSingleton(Options.Create(releaseMigrationRunnerTrustSettings));
builder.Services.AddSingleton(
    Options.Create(releaseActivationServiceControlSettings));
builder.Services.AddSingleton(
    Options.Create(releaseActivationCurrentPointerSwitchSettings));
builder.Services.AddSingleton(
    Options.Create(releaseActivationHealthVerificationSettings));
builder.Services.AddSingleton(
    Options.Create(releaseActivationRollbackSettings));
builder.Services.AddSingleton(
    Options.Create(releaseActivationOperatorApprovalSettings));
builder.Services.AddSingleton(Options.Create(releaseUpdateTransactionSettings));
builder.Services.AddSingleton(Options.Create(aetherRemoteBootstrapSettings));
builder.Services.AddSingleton(Options.Create(operationsSettings));
builder.Services.AddSingleton(Options.Create(installationRuntimeSettings));
builder.Services.AddSingleton(Options.Create(reverseProxySettings));
builder.Services.AddSingleton(
    Options.Create(releaseActivationHostRestartSettings));
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
builder.Services
    .AddHttpClient(
        GitHubReleaseHttpClient.ClientName,
        GitHubReleaseHttpClient.Configure)
    .ConfigurePrimaryHttpMessageHandler(
        GitHubReleaseHttpClient.CreateHandler);
builder.Services.AddSingleton<GitHubReleaseBundleSource>();
builder.Services.AddSingleton<GitHubReleaseBundleCheckConsole>();
builder.Services.AddSingleton<GitHubReleaseBundleDownloadService>(
    services =>
    {
        InstallationPaths downloadPaths = resolveInstallationPaths();
        GitHubReleaseSourceSettings sourceSettings =
            services.GetRequiredService<IOptions<GitHubReleaseSourceSettings>>()
                .Value;
        IHttpClientFactory clientFactory =
            services.GetRequiredService<IHttpClientFactory>();
        LocalOfflineReleaseBundleVerificationService bundleService =
            services.GetRequiredService<
                LocalOfflineReleaseBundleVerificationService>();
        GitHubReleaseBundleSource source = new(
            sourceSettings,
            () => clientFactory.CreateClient(
                GitHubReleaseHttpClient.ClientName),
            bundleService,
            downloadPaths.ReleaseDownloadDirectory,
            NullLogger<GitHubReleaseBundleSource>.Instance);
        return new GitHubReleaseBundleDownloadService(
            source,
            bundleService,
            downloadPaths);
    });
builder.Services.AddSingleton<GitHubReleaseBundleDownloadConsole>();
builder.Services.AddSingleton(_ => resolveInstallationPaths());
builder.Services.AddSingleton(
    services =>
    {
        InstallationPaths statusPaths =
            services.GetRequiredService<InstallationPaths>();
        return new InstallationSetupStore(statusPaths.SetupStatePath);
    });
builder.Services.AddSingleton(
    services =>
    {
        InstallationPaths statusPaths =
            services.GetRequiredService<InstallationPaths>();
        return new ReleaseInstallationStatusReader(
            services.GetRequiredService<InstallationSetupStore>(),
            statusPaths);
    });
builder.Services.AddSingleton<ReleaseStatusConsole>();
builder.Services.AddSingleton<InstallationBackupService>();
builder.Services.AddSingleton<AetherRemoteBootstrapService>();
builder.Services.AddSingleton<OfflineReleaseInstallPreflightPlanner>();
builder.Services.AddSingleton<OfflineReleaseInstallPreflightConsole>();
builder.Services.AddSingleton<VerifiedReleaseInstallationPlanComposer>();
builder.Services.AddSingleton<VerifiedReleaseStagingService>();
builder.Services.AddSingleton<VerifiedReleaseArchiveExtractionService>();
builder.Services.AddSingleton<
    VerifiedReleaseExtractedPublicationPlanComposer>();
builder.Services.AddSingleton<
    VerifiedReleaseExtractedPublicationService>();
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
    VerifiedReleaseActivationServiceControlExecutionService>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationCurrentPointerSwitchService>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationHealthVerificationPlanComposer>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationRollbackPlanComposer>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationRollbackExecutionService>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationHealthVerificationService>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationOperatorApprovalAuthority>();
builder.Services.AddSingleton<VerifiedReleaseActivationReadinessEvaluator>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationLeaseQuiescenceBoundary>();
builder.Services.AddSingleton<VerifiedReleaseActivationEvidenceCollector>();
builder.Services.AddSingleton<
    ReleaseUpdateOperatorAuthenticationEvidenceFactory>();
builder.Services.AddSingleton<ReleaseUpdateTransactionCoordinator>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationHostRestartTransport>();
builder.Services.AddSingleton<
    VerifiedReleaseActivationPostBootContinuationService>();
builder.Services.AddHostedService<
    VerifiedReleaseActivationPostBootContinuationHostedService>();
builder.Services.AddSingleton<ReleaseUpdateSupervisor>();
builder.Services.AddSingleton<ReleaseUpdateSupervisorClient>();
builder.Services.AddSingleton<ReleaseUpdateTransactionConsole>();
builder.Services.AddSingleton<StationTxCommandTrustRegistry>();
builder.Services.AddSingleton<StationTxCommandSigningAuthority>();
builder.Services.AddSingleton<StationTxCommandEnvelopeCoordinator>();
builder.Services.AddSingleton(
    Options.Create(new OriginSettings { Values = allowedOrigins }));
ConfigureReverseProxy(builder.Services, reverseProxySettings);
string dataProtectionPath =
    builder.Configuration["DataProtection:KeyPath"] ??
    Path.Combine(builder.Environment.ContentRootPath, ".data-protection");
InstallationPaths persistentRuntimePaths = resolveInstallationPaths();
string? configuredRadioAccessPolicyPath =
    builder.Configuration["RadioAccess:PolicyPath"];
string radioAccessPolicyPath =
    string.IsNullOrWhiteSpace(configuredRadioAccessPolicyPath)
        ? persistentRuntimePaths.RadioAccessPolicyPath
        : configuredRadioAccessPolicyPath;
string? configuredRadioOnboardingPolicyPath =
    builder.Configuration["RadioOnboarding:PolicyPath"];
string radioOnboardingPolicyPath =
    string.IsNullOrWhiteSpace(configuredRadioOnboardingPolicyPath)
        ? persistentRuntimePaths.RadioOnboardingPolicyPath
        : configuredRadioOnboardingPolicyPath;
string? configuredAdministrativeAuditPath =
    builder.Configuration["RadioAccess:AuditPath"];
string administrativeAuditPath =
    string.IsNullOrWhiteSpace(configuredAdministrativeAuditPath)
        ? persistentRuntimePaths.AdministrativeAuditPath
        : configuredAdministrativeAuditPath;
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("AetherSDR.Web");

AetherAuthenticationComposition.Configure(
    builder,
    authSettings,
    authenticationTopology);
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
    services => new RadioOnboardingPolicyStore(
        radioOnboardingPolicyPath,
        services.GetRequiredService<
            ILogger<RadioOnboardingPolicyStore>>(),
        services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(
    services => new AdministrativeAuditStore(
        administrativeAuditPath,
        services.GetRequiredService<
            ILogger<AdministrativeAuditStore>>()));
builder.Services.AddSingleton<RadioSessionRegistry>();
builder.Services.AddSingleton<RadioPresenceRegistry>();
builder.Services.AddSingleton<RadioAdministrationService>();
builder.Services.AddSingleton<OperationsReadinessService>();
builder.Services.AddSingleton<OperationsDiagnosticBundleService>();
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
    options.AddPolicy(
        AetherLocalAuthenticationDefaults.RateLimitPolicy,
        context => RateLimitPartition.GetFixedWindowLimiter(
            RequestRateLimitPartitionKey.ForAddress(context),
            _ => AetherLocalAuthenticationDefaults
                .CreateRateLimiterOptions(
                    authenticationTopology.LocalPolicy)));
    options.AddPolicy(
        AetherIdentityAdministrationDefaults.RateLimitPolicy,
        context => RateLimitPartition.GetFixedWindowLimiter(
            RequestRateLimitPartitionKey.ForAuthenticatedUserOrAddress(
                context),
            _ => AetherLocalAuthenticationDefaults
                .CreateRateLimiterOptions(
                    authenticationTopology.LocalPolicy)));
    options.AddPolicy(
        "websocket",
        context => RateLimitPartition.GetFixedWindowLimiter(
            RequestRateLimitPartitionKey.ForAuthenticatedUserOrAddress(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy(
        "station-enrollment",
        context => RateLimitPartition.GetFixedWindowLimiter(
            RequestRateLimitPartitionKey.ForAddress(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy(
        "admin-operations",
        context => RateLimitPartition.GetFixedWindowLimiter(
            RequestRateLimitPartitionKey.ForAuthenticatedUserOrAddress(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 6,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

WebApplication app = builder.Build();
if (releaseUpdateCommandLine.Command ==
    ReleaseUpdateConsoleCommandKind.TransactionSupervisor)
{
    if (!OperatingSystem.IsLinux())
    {
        Environment.ExitCode = 2;
        return;
    }
    ReleaseUpdateSupervisor supervisor =
        app.Services.GetRequiredService<ReleaseUpdateSupervisor>();
    await supervisor.RunAsync();
    return;
}
if (releaseUpdateCommandLine.Command is
    ReleaseUpdateConsoleCommandKind.InstallOfflineRelease or
    ReleaseUpdateConsoleCommandKind.RollbackTransaction or
    ReleaseUpdateConsoleCommandKind.TransactionStatus)
{
    bool interactive =
        !Console.IsInputRedirected && !Console.IsOutputRedirected;
    ReleaseUpdateTransactionConsole transactionConsole =
        app.Services.GetRequiredService<ReleaseUpdateTransactionConsole>();
    Environment.ExitCode = await transactionConsole.ExecuteAsync(
        releaseUpdateCommandLine,
        Console.In,
        Console.Out,
        interactive);
    return;
}
ReleaseManifestTrustRegistry releaseManifestTrustRegistry =
    app.Services.GetRequiredService<ReleaseManifestTrustRegistry>();
SignedReleaseManifestVerificationService releaseManifestVerificationService =
    app.Services.GetRequiredService<SignedReleaseManifestVerificationService>();
LocalOfflineReleaseBundleVerificationService offlineReleaseBundleService =
    app.Services.GetRequiredService<
        LocalOfflineReleaseBundleVerificationService>();
OfflineReleaseBundleCheckConsole offlineReleaseBundleCheckConsole =
    app.Services.GetRequiredService<OfflineReleaseBundleCheckConsole>();
GitHubReleaseBundleSource releaseGitHubBundleSource =
    app.Services.GetRequiredService<GitHubReleaseBundleSource>();
GitHubReleaseBundleCheckConsole releaseGitHubBundleCheckConsole =
    app.Services.GetRequiredService<GitHubReleaseBundleCheckConsole>();
GitHubReleaseBundleDownloadService releaseGitHubBundleDownloadService =
    app.Services.GetRequiredService<GitHubReleaseBundleDownloadService>();
GitHubReleaseBundleDownloadConsole releaseGitHubBundleDownloadConsole =
    app.Services.GetRequiredService<GitHubReleaseBundleDownloadConsole>();
ReleaseStatusConsole releaseStatusConsole =
    app.Services.GetRequiredService<ReleaseStatusConsole>();
OfflineReleaseInstallPreflightConsole releaseInstallPreflightConsole =
    app.Services.GetRequiredService<OfflineReleaseInstallPreflightConsole>();
VerifiedReleaseInstallationPlanComposer releaseInstallationPlanComposer =
    app.Services.GetRequiredService<VerifiedReleaseInstallationPlanComposer>();
VerifiedReleaseStagingService verifiedReleaseStagingService =
    app.Services.GetRequiredService<VerifiedReleaseStagingService>();
VerifiedReleaseArchiveExtractionService
    verifiedReleaseArchiveExtractionService =
        app.Services.GetRequiredService<
            VerifiedReleaseArchiveExtractionService>();
VerifiedReleaseExtractedPublicationPlanComposer
    releaseExtractedPublicationPlanComposer =
        app.Services.GetRequiredService<
            VerifiedReleaseExtractedPublicationPlanComposer>();
VerifiedReleaseExtractedPublicationService
    releaseExtractedPublicationService =
        app.Services.GetRequiredService<
            VerifiedReleaseExtractedPublicationService>();
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
VerifiedReleaseActivationServiceControlExecutionService
    releaseActivationServiceControlExecutionService =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationServiceControlExecutionService>();
VerifiedReleaseActivationCurrentPointerSwitchService
    releaseActivationCurrentPointerSwitchService =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationCurrentPointerSwitchService>();
VerifiedReleaseActivationHealthVerificationPlanComposer
    releaseActivationHealthVerificationPlanComposer =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationHealthVerificationPlanComposer>();
VerifiedReleaseActivationRollbackPlanComposer
    releaseActivationRollbackPlanComposer =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationRollbackPlanComposer>();
VerifiedReleaseActivationRollbackExecutionService
    releaseActivationRollbackExecutionService =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationRollbackExecutionService>();
VerifiedReleaseActivationHealthVerificationService
    releaseActivationHealthVerificationService =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationHealthVerificationService>();
VerifiedReleaseActivationOperatorApprovalAuthority
    releaseActivationOperatorApprovalAuthority =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationOperatorApprovalAuthority>();
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
ReleaseUpdateTransactionCoordinator releaseUpdateTransactionCoordinator =
    app.Services.GetRequiredService<ReleaseUpdateTransactionCoordinator>();
VerifiedReleaseActivationHostRestartTransport
    releaseActivationHostRestartTransport =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationHostRestartTransport>();
VerifiedReleaseActivationPostBootContinuationService
    releaseActivationPostBootContinuationService =
        app.Services.GetRequiredService<
            VerifiedReleaseActivationPostBootContinuationService>();
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
_ = AetherLocalAuthenticationHttpAdapter.Map(
    app,
    authenticationTopology);
_ = AetherIdentityAdministrationHttpAdapter.Map(
    app,
    authenticationTopology);
_ = ReleaseUpdateHttpAdapter.Map(app);
app.UseRateLimiter();
app.UseWebSockets(
    new WebSocketOptions
    {
        KeepAliveInterval = TimeSpan.FromSeconds(20),
        AllowedOrigins = { }
    });

app.MapGet(
        "/api/admin/diagnostics/health",
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
            GitHubReleaseBundleSourceDiagnostics releaseGitHubSource =
                releaseGitHubBundleSource.Snapshot;
            GitHubReleaseBundleCheckConsoleDiagnostics releaseGitHubCheck =
                releaseGitHubBundleCheckConsole.Snapshot;
            GitHubReleaseBundleDownloadDiagnostics releaseGitHubDownload =
                releaseGitHubBundleDownloadService.Snapshot;
            GitHubReleaseBundleDownloadConsoleDiagnostics
                releaseGitHubDownloadConsole =
                    releaseGitHubBundleDownloadConsole.Snapshot;
            ReleaseStatusConsoleDiagnostics releaseStatus =
                releaseStatusConsole.Snapshot;
            OfflineReleaseInstallPreflightConsoleDiagnostics
                releaseInstallPreflight = releaseInstallPreflightConsole.Snapshot;
            VerifiedReleaseInstallationPlanDiagnostics releaseInstallationPlan =
                releaseInstallationPlanComposer.Snapshot;
            VerifiedReleaseStagingDiagnostics releaseStaging =
                verifiedReleaseStagingService.Snapshot;
            VerifiedReleaseArchiveExtractionDiagnostics releaseExtraction =
                verifiedReleaseArchiveExtractionService.Snapshot;
            VerifiedReleaseExtractedPublicationPlanDiagnostics
                releaseExtractedPublicationPlan =
                    releaseExtractedPublicationPlanComposer.Snapshot;
            VerifiedReleaseExtractedPublicationDiagnostics
                releaseExtractedPublication =
                    releaseExtractedPublicationService.Snapshot;
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
            VerifiedReleaseActivationServiceControlExecutionDiagnostics
                releaseActivationServiceControlExecution =
                    releaseActivationServiceControlExecutionService.Snapshot;
            VerifiedReleaseActivationServiceControlExecutionStateDiagnostics
                releaseActivationServiceControlExecutionState =
                    releaseActivationServiceControlExecutionService.State;
            VerifiedReleaseActivationCurrentPointerSwitchDiagnostics
                releaseActivationCurrentPointerSwitch =
                    releaseActivationCurrentPointerSwitchService.Snapshot;
            VerifiedReleaseActivationCurrentPointerSwitchStateDiagnostics
                releaseActivationCurrentPointerSwitchState =
                    releaseActivationCurrentPointerSwitchService.State;
            VerifiedReleaseActivationHealthVerificationPlanDiagnostics
                releaseActivationHealthVerificationPlan =
                    releaseActivationHealthVerificationPlanComposer.Snapshot;
            VerifiedReleaseActivationRollbackPlanDiagnostics
                releaseActivationRollbackPlan =
                    releaseActivationRollbackPlanComposer.Snapshot;
            VerifiedReleaseActivationRollbackExecutionDiagnostics
                releaseActivationRollbackExecution =
                    releaseActivationRollbackExecutionService.Snapshot;
            VerifiedReleaseActivationRollbackExecutionStateDiagnostics
                releaseActivationRollbackExecutionState =
                    releaseActivationRollbackExecutionService.State;
            VerifiedReleaseActivationHealthVerificationDiagnostics
                releaseActivationHealthVerification =
                    releaseActivationHealthVerificationService.Snapshot;
            VerifiedReleaseActivationHealthVerificationStateDiagnostics
                releaseActivationHealthVerificationState =
                    releaseActivationHealthVerificationService.State;
            VerifiedReleaseActivationOperatorApprovalDiagnostics
                releaseActivationOperatorApproval =
                    releaseActivationOperatorApprovalAuthority.Snapshot;
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
            ReleaseUpdateTransactionDiagnostics releaseUpdateTransaction =
                releaseUpdateTransactionCoordinator.Snapshot;
            VerifiedReleaseActivationHostRestartDiagnostics
                releaseActivationHostRestart =
                    releaseActivationHostRestartTransport.Snapshot;
            VerifiedReleaseActivationPostBootContinuationDiagnostics
                releaseActivationPostBootContinuation =
                    releaseActivationPostBootContinuationService.Snapshot;
            VerifiedReleaseActivationPostBootContinuationStateDiagnostics
                releaseActivationPostBootContinuationState =
                    releaseActivationPostBootContinuationService.State;
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
                releaseGitHubSourceRegistered = releaseGitHubSource.Registered,
                releaseGitHubSourceEnabled = releaseGitHubSource.Enabled,
                releaseGitHubSourceRepositoryConfigured =
                    releaseGitHubSource.RepositoryConfigured,
                releaseGitHubMetadataReadRegistered =
                    releaseGitHubSource.GitHubMetadataReadRegistered,
                releaseGitHubAssetDownloadRegistered =
                    releaseGitHubSource.GitHubAssetDownloadRegistered,
                releaseGitHubTemporaryBundleWriteRegistered =
                    releaseGitHubSource.TemporaryBundleWriteRegistered,
                releaseGitHubTemporaryBundleCleanupRegistered =
                    releaseGitHubSource.TemporaryBundleCleanupRegistered,
                releaseGitHubLocalSignedVerificationRegistered =
                    releaseGitHubSource.LocalSignedVerificationRegistered,
                releaseGitHubMaximumReleaseCount =
                    releaseGitHubSource.MaximumReleaseCount,
                releaseGitHubRequestTimeoutSeconds =
                    releaseGitHubSource.RequestTimeoutSeconds,
                releaseGitHubPersistentDownloadRegistered =
                    releaseGitHubSource.PersistentDownloadRegistered,
                releaseGitHubArchiveExtractionRegistered =
                    releaseGitHubSource.ArchiveExtractionRegistered,
                releaseGitHubStagingRegistered =
                    releaseGitHubSource.StagingRegistered,
                releaseGitHubInstallationRegistered =
                    releaseGitHubSource.InstallationRegistered,
                releaseGitHubActivationRegistered =
                    releaseGitHubSource.ActivationRegistered,
                releaseGitHubRollbackRegistered =
                    releaseGitHubSource.RollbackRegistered,
                releaseGitHubMigrationRegistered =
                    releaseGitHubSource.MigrationRegistered,
                releaseGitHubServiceControlRegistered =
                    releaseGitHubSource.ServiceControlRegistered,
                releaseGitHubAdminCallerRegistered =
                    releaseGitHubSource.AdminCallerRegistered,
                releaseGitHubBrowserCallerRegistered =
                    releaseGitHubSource.BrowserCallerRegistered,
                releaseGitHubRadioCallerRegistered =
                    releaseGitHubSource.RadioCallerRegistered,
                releaseGitHubWatchdogCallerRegistered =
                    releaseGitHubSource.WatchdogCallerRegistered,
                releaseGitHubCommandCallerRegistered =
                    releaseGitHubSource.CommandCallerRegistered,
                releaseGitHubLeaseCallerRegistered =
                    releaseGitHubSource.LeaseCallerRegistered,
                releaseGitHubTxCallerRegistered =
                    releaseGitHubSource.TxCallerRegistered,
                releaseGitHubCheckCliRegistered = releaseGitHubCheck.Registered,
                releaseGitHubCheckNetworkReadRegistered =
                    releaseGitHubCheck.NetworkReadRegistered,
                releaseGitHubCheckPersistentDownloadRegistered =
                    releaseGitHubCheck.PersistentDownloadRegistered,
                releaseGitHubDownloadRegistered =
                    releaseGitHubDownload.Registered,
                releaseGitHubDownloadNetworkReadRegistered =
                    releaseGitHubDownload.NetworkReadRegistered,
                releaseGitHubDownloadLocalSignedVerificationRegistered =
                    releaseGitHubDownload.LocalSignedVerificationRegistered,
                releaseGitHubDownloadInstallationPathBindingRegistered =
                    releaseGitHubDownload.InstallationPathBindingRegistered,
                releaseGitHubDownloadPrivateRootRegistered =
                    releaseGitHubDownload.PrivateDownloadRootRegistered,
                releaseGitHubDownloadSameParentTemporaryBundleRegistered =
                    releaseGitHubDownload.SameParentTemporaryBundleRegistered,
                releaseGitHubDownloadAtomicDirectoryPublishRegistered =
                    releaseGitHubDownload.AtomicDirectoryPublishRegistered,
                releaseGitHubDownloadExistingBundleVerificationRegistered =
                    releaseGitHubDownload.ExistingBundleVerificationRegistered,
                releaseGitHubDownloadPersistentDownloadRegistered =
                    releaseGitHubDownload.PersistentDownloadRegistered,
                releaseGitHubDownloadArchiveExtractionRegistered =
                    releaseGitHubDownload.ArchiveExtractionRegistered,
                releaseGitHubDownloadStagingRegistered =
                    releaseGitHubDownload.StagingRegistered,
                releaseGitHubDownloadInstallationRegistered =
                    releaseGitHubDownload.InstallationRegistered,
                releaseGitHubDownloadActivationRegistered =
                    releaseGitHubDownload.ActivationRegistered,
                releaseGitHubDownloadRollbackRegistered =
                    releaseGitHubDownload.RollbackRegistered,
                releaseGitHubDownloadMigrationRegistered =
                    releaseGitHubDownload.MigrationRegistered,
                releaseGitHubDownloadServiceControlRegistered =
                    releaseGitHubDownload.ServiceControlRegistered,
                releaseGitHubDownloadAdminCallerRegistered =
                    releaseGitHubDownload.AdminCallerRegistered,
                releaseGitHubDownloadBrowserCallerRegistered =
                    releaseGitHubDownload.BrowserCallerRegistered,
                releaseGitHubDownloadRadioCallerRegistered =
                    releaseGitHubDownload.RadioCallerRegistered,
                releaseGitHubDownloadWatchdogCallerRegistered =
                    releaseGitHubDownload.WatchdogCallerRegistered,
                releaseGitHubDownloadCommandCallerRegistered =
                    releaseGitHubDownload.CommandCallerRegistered,
                releaseGitHubDownloadLeaseCallerRegistered =
                    releaseGitHubDownload.LeaseCallerRegistered,
                releaseGitHubDownloadTxCallerRegistered =
                    releaseGitHubDownload.TxCallerRegistered,
                releaseGitHubDownloadCliRegistered =
                    releaseGitHubDownloadConsole.CliCallerRegistered,
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
                releaseExtractionServiceRegistered =
                    releaseExtraction.Registered,
                releaseExtractionStatusRevalidationRegistered =
                    releaseExtraction.StatusRevalidationRegistered,
                releaseExtractionVerifiedStagingInputRegistered =
                    releaseExtraction.VerifiedStagingInputRegistered,
                releaseExtractionSourceArchiveDigestVerificationRegistered =
                    releaseExtraction.SourceArchiveDigestVerificationRegistered,
                releaseExtractionGzipDecompressionRegistered =
                    releaseExtraction.GzipDecompressionRegistered,
                releaseExtractionTarArchiveReadRegistered =
                    releaseExtraction.TarArchiveReadRegistered,
                releaseExtractionArchiveExtractionRegistered =
                    releaseExtraction.ArchiveExtractionRegistered,
                releaseExtractionPrivateStagingWriteRegistered =
                    releaseExtraction.PrivateStagingWriteRegistered,
                releaseExtractionExpandedContentHashRegistered =
                    releaseExtraction.ExpandedContentHashRegistered,
                releaseExtractionImmutableFreezeRegistered =
                    releaseExtraction.ImmutableFreezeRegistered,
                releaseExtractionCleanupRegistered =
                    releaseExtraction.CleanupRegistered,
                releaseExtractionNetworkDownloadRegistered =
                    releaseExtraction.NetworkDownloadRegistered,
                releaseExtractionPersistentDownloadRegistered =
                    releaseExtraction.PersistentDownloadRegistered,
                releaseExtractionPublicationRegistered =
                    releaseExtraction.PublicationRegistered,
                releaseExtractionInstallationExecutionRegistered =
                    releaseExtraction.InstallationExecutionRegistered,
                releaseExtractionActivationRegistered =
                    releaseExtraction.ActivationRegistered,
                releaseExtractionCurrentPointerMutationRegistered =
                    releaseExtraction.CurrentPointerMutationRegistered,
                releaseExtractionRollbackRegistered =
                    releaseExtraction.RollbackRegistered,
                releaseExtractionMigrationExecutionRegistered =
                    releaseExtraction.MigrationExecutionRegistered,
                releaseExtractionServiceControlRegistered =
                    releaseExtraction.ServiceControlRegistered,
                releaseExtractionCliCallerRegistered =
                    releaseExtraction.CliCallerRegistered,
                releaseExtractionAdminCallerRegistered =
                    releaseExtraction.AdminCallerRegistered,
                releaseExtractionBrowserCallerRegistered =
                    releaseExtraction.BrowserCallerRegistered,
                releaseExtractionRadioCallerRegistered =
                    releaseExtraction.RadioCallerRegistered,
                releaseExtractionWatchdogCallerRegistered =
                    releaseExtraction.WatchdogCallerRegistered,
                releaseExtractionCommandCallerRegistered =
                    releaseExtraction.CommandCallerRegistered,
                releaseExtractionLeaseCallerRegistered =
                    releaseExtraction.LeaseCallerRegistered,
                releaseExtractionTxCallerRegistered =
                    releaseExtraction.TxCallerRegistered,
                releaseExtractedPublicationPlanComposerRegistered =
                    releaseExtractedPublicationPlan.Registered,
                releaseExtractedPublicationPlanVerifiedExtractionInputRegistered =
                    releaseExtractedPublicationPlan
                        .VerifiedExtractionInputRegistered,
                releaseExtractedPublicationPlanSummaryValidationRegistered =
                    releaseExtractedPublicationPlan
                        .ExtractionSummaryValidationRegistered,
                releaseExtractedPublicationPlanFileInventoryCompositionRegistered =
                    releaseExtractedPublicationPlan
                        .ImmutableFileInventoryCompositionRegistered,
                releaseExtractedPublicationPlanExecutableIntentCompositionRegistered =
                    releaseExtractedPublicationPlan
                        .ExecutableIntentCompositionRegistered,
                releaseExtractedPublicationPlanSourcePathCompositionRegistered =
                    releaseExtractedPublicationPlan.SourcePathCompositionRegistered,
                releaseExtractedPublicationPlanTargetPathCompositionRegistered =
                    releaseExtractedPublicationPlan.TargetPathCompositionRegistered,
                releaseExtractedPublicationPlanNetworkDownloadRegistered =
                    releaseExtractedPublicationPlan.NetworkDownloadRegistered,
                releaseExtractedPublicationPlanArchiveExtractionExecutionRegistered =
                    releaseExtractedPublicationPlan
                        .ArchiveExtractionExecutionRegistered,
                releaseExtractedPublicationPlanFileWriteRegistered =
                    releaseExtractedPublicationPlan.FileWriteRegistered,
                releaseExtractedPublicationPlanAtomicPublishExecutionRegistered =
                    releaseExtractedPublicationPlan
                        .AtomicDirectoryPublishExecutionRegistered,
                releaseExtractedPublicationPlanCurrentPointerMutationRegistered =
                    releaseExtractedPublicationPlan
                        .CurrentPointerMutationRegistered,
                releaseExtractedPublicationPlanActivationRegistered =
                    releaseExtractedPublicationPlan.ActivationRegistered,
                releaseExtractedPublicationPlanRollbackRegistered =
                    releaseExtractedPublicationPlan.RollbackRegistered,
                releaseExtractedPublicationPlanMigrationExecutionRegistered =
                    releaseExtractedPublicationPlan.MigrationExecutionRegistered,
                releaseExtractedPublicationPlanServiceControlRegistered =
                    releaseExtractedPublicationPlan.ServiceControlRegistered,
                releaseExtractedPublicationPlanCliCallerRegistered =
                    releaseExtractedPublicationPlan.CliCallerRegistered,
                releaseExtractedPublicationPlanAdminCallerRegistered =
                    releaseExtractedPublicationPlan.AdminCallerRegistered,
                releaseExtractedPublicationPlanBrowserCallerRegistered =
                    releaseExtractedPublicationPlan.BrowserCallerRegistered,
                releaseExtractedPublicationPlanRadioCallerRegistered =
                    releaseExtractedPublicationPlan.RadioCallerRegistered,
                releaseExtractedPublicationPlanWatchdogCallerRegistered =
                    releaseExtractedPublicationPlan.WatchdogCallerRegistered,
                releaseExtractedPublicationPlanCommandCallerRegistered =
                    releaseExtractedPublicationPlan.CommandCallerRegistered,
                releaseExtractedPublicationPlanLeaseCallerRegistered =
                    releaseExtractedPublicationPlan.LeaseCallerRegistered,
                releaseExtractedPublicationPlanTxCallerRegistered =
                    releaseExtractedPublicationPlan.TxCallerRegistered,
                releaseExtractedPublicationServiceRegistered =
                    releaseExtractedPublication.Registered,
                releaseExtractedPublicationStatusRevalidationRegistered =
                    releaseExtractedPublication.StatusRevalidationRegistered,
                releaseExtractedPublicationVerifiedPlanInputRegistered =
                    releaseExtractedPublication.VerifiedPlanInputRegistered,
                releaseExtractedPublicationImmutableSourceValidationRegistered =
                    releaseExtractedPublication.ImmutableSourceValidationRegistered,
                releaseExtractedPublicationExecutableIntentValidationRegistered =
                    releaseExtractedPublication.ExecutableIntentValidationRegistered,
                releaseExtractedPublicationRootPermissionTransitionRegistered =
                    releaseExtractedPublication.RootPermissionTransitionRegistered,
                releaseExtractedPublicationAtomicDirectoryPublishRegistered =
                    releaseExtractedPublication.AtomicDirectoryPublishRegistered,
                releaseExtractedPublicationPublishedTreeValidationRegistered =
                    releaseExtractedPublication.PublishedTreeValidationRegistered,
                releaseExtractedPublicationNetworkDownloadRegistered =
                    releaseExtractedPublication.NetworkDownloadRegistered,
                releaseExtractedPublicationArchiveExtractionExecutionRegistered =
                    releaseExtractedPublication.ArchiveExtractionExecutionRegistered,
                releaseExtractedPublicationFileCopyRegistered =
                    releaseExtractedPublication.FileCopyRegistered,
                releaseExtractedPublicationCurrentPointerMutationRegistered =
                    releaseExtractedPublication.CurrentPointerMutationRegistered,
                releaseExtractedPublicationActivationRegistered =
                    releaseExtractedPublication.ActivationRegistered,
                releaseExtractedPublicationRollbackRegistered =
                    releaseExtractedPublication.RollbackRegistered,
                releaseExtractedPublicationMigrationExecutionRegistered =
                    releaseExtractedPublication.MigrationExecutionRegistered,
                releaseExtractedPublicationServiceControlRegistered =
                    releaseExtractedPublication.ServiceControlRegistered,
                releaseExtractedPublicationCliCallerRegistered =
                    releaseExtractedPublication.CliCallerRegistered,
                releaseExtractedPublicationAdminCallerRegistered =
                    releaseExtractedPublication.AdminCallerRegistered,
                releaseExtractedPublicationBrowserCallerRegistered =
                    releaseExtractedPublication.BrowserCallerRegistered,
                releaseExtractedPublicationRadioCallerRegistered =
                    releaseExtractedPublication.RadioCallerRegistered,
                releaseExtractedPublicationWatchdogCallerRegistered =
                    releaseExtractedPublication.WatchdogCallerRegistered,
                releaseExtractedPublicationCommandCallerRegistered =
                    releaseExtractedPublication.CommandCallerRegistered,
                releaseExtractedPublicationLeaseCallerRegistered =
                    releaseExtractedPublication.LeaseCallerRegistered,
                releaseExtractedPublicationTxCallerRegistered =
                    releaseExtractedPublication.TxCallerRegistered,
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
                releaseActivationServiceControlExecutionRegistered =
                    releaseActivationServiceControlExecution.Registered,
                releaseActivationServiceControlExecutionConfigurationRegistered =
                    releaseActivationServiceControlExecution
                        .ConfigurationRegistered,
                releaseActivationServiceControlExecutionEnabled =
                    releaseActivationServiceControlExecution.ExecutionEnabled,
                releaseActivationServiceControlExecutionAvailable =
                    releaseActivationServiceControlExecution.ExecutionAvailable,
                releaseActivationServiceControlExecutionPlanInputRegistered =
                    releaseActivationServiceControlExecution
                        .ExactServiceControlPlanInputRegistered,
                releaseActivationServiceControlExecutionExactPlanBindingRegistered =
                    releaseActivationServiceControlExecution
                        .ExactServiceControlPlanBindingRegistered,
                releaseActivationServiceControlExecutionExactActivationBindingRegistered =
                    releaseActivationServiceControlExecution
                        .ExactActivationPlanBindingRegistered,
                releaseActivationServiceControlExecutionPointerSwitchEvidenceInputRegistered =
                    releaseActivationServiceControlExecution
                        .ExactCurrentPointerSwitchEvidenceInputRegistered,
                releaseActivationServiceControlExecutionStatusDoubleReadRegistered =
                    releaseActivationServiceControlExecution
                        .ReleaseStatusDoubleReadRegistered,
                releaseActivationServiceControlExecutionSetupDoubleReadRegistered =
                    releaseActivationServiceControlExecution
                        .SetupStateDoubleReadRegistered,
                releaseActivationServiceControlExecutionTopologyBindingRegistered =
                    releaseActivationServiceControlExecution
                        .TopologyBindingRegistered,
                releaseActivationServiceControlExecutionPreSwitchRegistered =
                    releaseActivationServiceControlExecution
                        .PreSwitchStopPhaseRegistered,
                releaseActivationServiceControlExecutionPostSwitchRegistered =
                    releaseActivationServiceControlExecution
                        .PostSwitchStartPhaseRegistered,
                releaseActivationServiceControlExecutionNoOpRegistered =
                    releaseActivationServiceControlExecution
                        .NoOpResolutionRegistered,
                releaseActivationServiceControlExecutionOrderingRegistered =
                    releaseActivationServiceControlExecution
                        .DeterministicOrderingRegistered,
                releaseActivationServiceControlExecutionFixedMappingRegistered =
                    releaseActivationServiceControlExecution
                        .FixedUnitMappingRegistered,
                releaseActivationServiceControlExecutionDirectProcessRegistered =
                    releaseActivationServiceControlExecution
                        .DirectProcessRegistered,
                releaseActivationServiceControlExecutionShellRegistered =
                    releaseActivationServiceControlExecution.ShellRegistered,
                releaseActivationServiceControlExecutionClearedEnvironmentRegistered =
                    releaseActivationServiceControlExecution
                        .ClearedEnvironmentRegistered,
                releaseActivationServiceControlExecutionUserScopeRegistered =
                    releaseActivationServiceControlExecution
                        .UserUnitScopeRegistered,
                releaseActivationServiceControlExecutionSystemScopeRegistered =
                    releaseActivationServiceControlExecution
                        .SystemUnitScopeRegistered,
                releaseActivationServiceControlExecutionBoundedOutputRegistered =
                    releaseActivationServiceControlExecution
                        .BoundedOutputRegistered,
                releaseActivationServiceControlExecutionTimeoutRegistered =
                    releaseActivationServiceControlExecution
                        .HardTimeoutRegistered,
                releaseActivationServiceControlExecutionProcessTreeTerminationRegistered =
                    releaseActivationServiceControlExecution
                        .ProcessTreeTerminationRegistered,
                releaseActivationServiceControlExecutionEvidenceRegistered =
                    releaseActivationServiceControlExecution
                        .ExactPlanEvidenceRegistered,
                releaseActivationServiceControlExecutionReconciliationRegistered =
                    releaseActivationServiceControlExecution
                        .PartialFailureReconciliationRegistered,
                releaseActivationServiceControlExecutionAutomaticRetryRegistered =
                    releaseActivationServiceControlExecution
                        .AutomaticRetryRegistered,
                releaseActivationServiceControlExecutionHostRestartRegistered =
                    releaseActivationServiceControlExecution
                        .HostRestartExecutionRegistered,
                releaseActivationServiceControlExecutionRemoteControlRegistered =
                    releaseActivationServiceControlExecution
                        .RemoteServiceControlRegistered,
                releaseActivationServiceControlExecutionCurrentPointerMutationRegistered =
                    releaseActivationServiceControlExecution
                        .CurrentPointerMutationRegistered,
                releaseActivationServiceControlExecutionRollbackRegistered =
                    releaseActivationServiceControlExecution.RollbackRegistered,
                releaseActivationServiceControlExecutionActivationAuthorityRegistered =
                    releaseActivationServiceControlExecution
                        .ActivationAuthorityRegistered,
                releaseActivationServiceControlExecutionOperationalCallerRegistered =
                    releaseActivationServiceControlExecution
                        .OperationalCallerRegistered,
                releaseActivationServiceControlExecutionCliCallerRegistered =
                    releaseActivationServiceControlExecution.CliCallerRegistered,
                releaseActivationServiceControlExecutionAdminCallerRegistered =
                    releaseActivationServiceControlExecution.AdminCallerRegistered,
                releaseActivationServiceControlExecutionBrowserCallerRegistered =
                    releaseActivationServiceControlExecution
                        .BrowserCallerRegistered,
                releaseActivationServiceControlExecutionHttpCallerRegistered =
                    releaseActivationServiceControlExecution.HttpCallerRegistered,
                releaseActivationServiceControlExecutionWebSocketCallerRegistered =
                    releaseActivationServiceControlExecution
                        .WebSocketCallerRegistered,
                releaseActivationServiceControlExecutionHostedServiceCallerRegistered =
                    releaseActivationServiceControlExecution
                        .HostedServiceCallerRegistered,
                releaseActivationServiceControlExecutionTimerCallerRegistered =
                    releaseActivationServiceControlExecution.TimerCallerRegistered,
                releaseActivationServiceControlExecutionAetherRemoteCommandCallerRegistered =
                    releaseActivationServiceControlExecution
                        .AetherRemoteCommandCallerRegistered,
                releaseActivationServiceControlExecutionHealthProbeCallerRegistered =
                    releaseActivationServiceControlExecution
                        .HealthProbeCallerRegistered,
                releaseActivationServiceControlExecutionRadioCallerRegistered =
                    releaseActivationServiceControlExecution.RadioCallerRegistered,
                releaseActivationServiceControlExecutionWatchdogCallerRegistered =
                    releaseActivationServiceControlExecution
                        .WatchdogCallerRegistered,
                releaseActivationServiceControlExecutionCommandCallerRegistered =
                    releaseActivationServiceControlExecution.CommandCallerRegistered,
                releaseActivationServiceControlExecutionLeaseCallerRegistered =
                    releaseActivationServiceControlExecution.LeaseCallerRegistered,
                releaseActivationServiceControlExecutionTxCallerRegistered =
                    releaseActivationServiceControlExecution.TxCallerRegistered,
                releaseActivationServiceControlReady =
                    releaseActivationServiceControlExecutionState
                        .ServiceControlReady,
                releaseActivationServiceControlExactPlanActive =
                    releaseActivationServiceControlExecutionState
                        .ExactServiceControlPlanBound,
                releaseActivationServiceControlExactActivationActive =
                    releaseActivationServiceControlExecutionState
                        .ExactActivationPlanBound,
                releaseActivationServiceControlPreSwitchComplete =
                    releaseActivationServiceControlExecutionState
                        .PreSwitchStopComplete,
                releaseActivationServiceControlPostSwitchComplete =
                    releaseActivationServiceControlExecutionState
                        .PostSwitchStartComplete,
                releaseActivationServiceControlPlannedStopCount =
                    releaseActivationServiceControlExecutionState
                        .PlannedStopActionCount,
                releaseActivationServiceControlExecutedStopCount =
                    releaseActivationServiceControlExecutionState
                        .ExecutedStopActionCount,
                releaseActivationServiceControlNoOpStopCount =
                    releaseActivationServiceControlExecutionState
                        .TopologyNoOpStopActionCount,
                releaseActivationServiceControlPlannedStartCount =
                    releaseActivationServiceControlExecutionState
                        .PlannedStartActionCount,
                releaseActivationServiceControlExecutedStartCount =
                    releaseActivationServiceControlExecutionState
                        .ExecutedStartActionCount,
                releaseActivationServiceControlNoOpStartCount =
                    releaseActivationServiceControlExecutionState
                        .TopologyNoOpStartActionCount,
                releaseActivationServiceControlSetupStable =
                    releaseActivationServiceControlExecutionState.SetupStable,
                releaseActivationServiceControlTopologyStable =
                    releaseActivationServiceControlExecutionState.TopologyStable,
                releaseActivationServiceControlInstalledActiveDuringStop =
                    releaseActivationServiceControlExecutionState
                        .InstalledReleaseActiveDuringStop,
                releaseActivationServiceControlTargetActiveDuringStart =
                    releaseActivationServiceControlExecutionState
                        .TargetReleaseActiveDuringStart,
                releaseActivationServiceControlReconciliationRequired =
                    releaseActivationServiceControlExecutionState
                        .ReconciliationRequired,
                releaseActivationServiceControlHostRestartPerformed =
                    releaseActivationServiceControlExecutionState
                        .HostRestartPerformed,
                releaseActivationServiceControlCurrentPointerChanged =
                    releaseActivationServiceControlExecutionState
                        .CurrentPointerChanged,
                releaseActivationServiceControlRollbackPerformed =
                    releaseActivationServiceControlExecutionState
                        .RollbackPerformed,
                releaseActivationServiceControlActivationAuthorized =
                    releaseActivationServiceControlExecutionState
                        .ActivationAuthorized,
                releaseActivationCurrentPointerSwitchRegistered =
                    releaseActivationCurrentPointerSwitch.Registered,
                releaseActivationCurrentPointerSwitchConfigurationRegistered =
                    releaseActivationCurrentPointerSwitch.ConfigurationRegistered,
                releaseActivationCurrentPointerSwitchEnabled =
                    releaseActivationCurrentPointerSwitch.ExecutionEnabled,
                releaseActivationCurrentPointerSwitchAvailable =
                    releaseActivationCurrentPointerSwitch.ExecutionAvailable,
                releaseActivationCurrentPointerSwitchServiceControlPlanInputRegistered =
                    releaseActivationCurrentPointerSwitch
                        .ExactServiceControlPlanInputRegistered,
                releaseActivationCurrentPointerSwitchExactServiceControlBindingRegistered =
                    releaseActivationCurrentPointerSwitch
                        .ExactServiceControlPlanBindingRegistered,
                releaseActivationCurrentPointerSwitchExactActivationBindingRegistered =
                    releaseActivationCurrentPointerSwitch
                        .ExactActivationPlanBindingRegistered,
                releaseActivationCurrentPointerSwitchPreSwitchEvidenceRegistered =
                    releaseActivationCurrentPointerSwitch
                        .ExactPreSwitchEvidenceRegistered,
                releaseActivationCurrentPointerSwitchStatusDoubleReadRegistered =
                    releaseActivationCurrentPointerSwitch
                        .ReleaseStatusDoubleReadRegistered,
                releaseActivationCurrentPointerSwitchSetupDoubleReadRegistered =
                    releaseActivationCurrentPointerSwitch
                        .SetupStateDoubleReadRegistered,
                releaseActivationCurrentPointerSwitchInstalledActiveRegistered =
                    releaseActivationCurrentPointerSwitch
                        .InstalledActiveRequirementRegistered,
                releaseActivationCurrentPointerSwitchTargetActiveVerificationRegistered =
                    releaseActivationCurrentPointerSwitch
                        .TargetActiveVerificationRegistered,
                releaseActivationCurrentPointerSwitchImmutableTargetRegistered =
                    releaseActivationCurrentPointerSwitch
                        .ImmutableTargetRevalidationRegistered,
                releaseActivationCurrentPointerSwitchInstalledLinkTargetRegistered =
                    releaseActivationCurrentPointerSwitch
                        .ExactInstalledLinkTargetRegistered,
                releaseActivationCurrentPointerSwitchTargetLinkTargetRegistered =
                    releaseActivationCurrentPointerSwitch
                        .ExactTargetLinkTargetRegistered,
                releaseActivationCurrentPointerSwitchTemporaryLinkRegistered =
                    releaseActivationCurrentPointerSwitch
                        .SameDirectoryTemporaryLinkRegistered,
                releaseActivationCurrentPointerSwitchAtomicReplacementRegistered =
                    releaseActivationCurrentPointerSwitch
                        .AtomicLinkReplacementRegistered,
                releaseActivationCurrentPointerSwitchPostSwitchObservationRegistered =
                    releaseActivationCurrentPointerSwitch
                        .PostSwitchObservationRegistered,
                releaseActivationCurrentPointerSwitchEvidenceRegistered =
                    releaseActivationCurrentPointerSwitch
                        .ExactPlanEvidenceRegistered,
                releaseActivationCurrentPointerSwitchReconciliationRegistered =
                    releaseActivationCurrentPointerSwitch
                        .PartialFailureReconciliationRegistered,
                releaseActivationCurrentPointerSwitchAutomaticRetryRegistered =
                    releaseActivationCurrentPointerSwitch.AutomaticRetryRegistered,
                releaseActivationCurrentPointerSwitchServiceStartRegistered =
                    releaseActivationCurrentPointerSwitch.ServiceStartRegistered,
                releaseActivationCurrentPointerSwitchHostRestartRegistered =
                    releaseActivationCurrentPointerSwitch.HostRestartRegistered,
                releaseActivationCurrentPointerSwitchRemoteServiceControlRegistered =
                    releaseActivationCurrentPointerSwitch
                        .RemoteServiceControlRegistered,
                releaseActivationCurrentPointerSwitchHealthProbeRegistered =
                    releaseActivationCurrentPointerSwitch.HealthProbeRegistered,
                releaseActivationCurrentPointerSwitchRollbackRegistered =
                    releaseActivationCurrentPointerSwitch.RollbackRegistered,
                releaseActivationCurrentPointerSwitchActivationAuthorityRegistered =
                    releaseActivationCurrentPointerSwitch
                        .ActivationAuthorityRegistered,
                releaseActivationCurrentPointerSwitchOperationalCallerRegistered =
                    releaseActivationCurrentPointerSwitch
                        .OperationalCallerRegistered,
                releaseActivationCurrentPointerSwitchCliCallerRegistered =
                    releaseActivationCurrentPointerSwitch.CliCallerRegistered,
                releaseActivationCurrentPointerSwitchAdminCallerRegistered =
                    releaseActivationCurrentPointerSwitch.AdminCallerRegistered,
                releaseActivationCurrentPointerSwitchBrowserCallerRegistered =
                    releaseActivationCurrentPointerSwitch.BrowserCallerRegistered,
                releaseActivationCurrentPointerSwitchHttpCallerRegistered =
                    releaseActivationCurrentPointerSwitch.HttpCallerRegistered,
                releaseActivationCurrentPointerSwitchWebSocketCallerRegistered =
                    releaseActivationCurrentPointerSwitch.WebSocketCallerRegistered,
                releaseActivationCurrentPointerSwitchHostedServiceCallerRegistered =
                    releaseActivationCurrentPointerSwitch
                        .HostedServiceCallerRegistered,
                releaseActivationCurrentPointerSwitchTimerCallerRegistered =
                    releaseActivationCurrentPointerSwitch.TimerCallerRegistered,
                releaseActivationCurrentPointerSwitchAetherRemoteCommandCallerRegistered =
                    releaseActivationCurrentPointerSwitch
                        .AetherRemoteCommandCallerRegistered,
                releaseActivationCurrentPointerSwitchRadioCallerRegistered =
                    releaseActivationCurrentPointerSwitch.RadioCallerRegistered,
                releaseActivationCurrentPointerSwitchWatchdogCallerRegistered =
                    releaseActivationCurrentPointerSwitch.WatchdogCallerRegistered,
                releaseActivationCurrentPointerSwitchCommandCallerRegistered =
                    releaseActivationCurrentPointerSwitch.CommandCallerRegistered,
                releaseActivationCurrentPointerSwitchLeaseCallerRegistered =
                    releaseActivationCurrentPointerSwitch.LeaseCallerRegistered,
                releaseActivationCurrentPointerSwitchTxCallerRegistered =
                    releaseActivationCurrentPointerSwitch.TxCallerRegistered,
                releaseActivationCurrentPointerSwitchReady =
                    releaseActivationCurrentPointerSwitchState.PointerSwitchReady,
                releaseActivationCurrentPointerSwitchExactServiceControlActive =
                    releaseActivationCurrentPointerSwitchState
                        .ExactServiceControlPlanBound,
                releaseActivationCurrentPointerSwitchExactActivationActive =
                    releaseActivationCurrentPointerSwitchState
                        .ExactActivationPlanBound,
                releaseActivationCurrentPointerSwitchPreSwitchReady =
                    releaseActivationCurrentPointerSwitchState
                        .PreSwitchServiceControlReady,
                releaseActivationCurrentPointerSwitchCurrentPointerChanged =
                    releaseActivationCurrentPointerSwitchState
                        .CurrentPointerChanged,
                releaseActivationCurrentPointerSwitchTargetActive =
                    releaseActivationCurrentPointerSwitchState.TargetReleaseActive,
                releaseActivationCurrentPointerSwitchSetupStable =
                    releaseActivationCurrentPointerSwitchState.SetupStable,
                releaseActivationCurrentPointerSwitchTargetImmutable =
                    releaseActivationCurrentPointerSwitchState
                        .TargetReleaseImmutable,
                releaseActivationCurrentPointerSwitchAtomicCompleted =
                    releaseActivationCurrentPointerSwitchState
                        .AtomicSwitchCompleted,
                releaseActivationCurrentPointerSwitchReconciliationRequired =
                    releaseActivationCurrentPointerSwitchState
                        .ReconciliationRequired,
                releaseActivationCurrentPointerSwitchPostServiceControlReady =
                    releaseActivationCurrentPointerSwitchState
                        .PostSwitchServiceControlReady,
                releaseActivationCurrentPointerSwitchHealthReady =
                    releaseActivationCurrentPointerSwitchState
                        .HealthVerificationReady,
                releaseActivationCurrentPointerSwitchRollbackPerformed =
                    releaseActivationCurrentPointerSwitchState.RollbackPerformed,
                releaseActivationCurrentPointerSwitchActivationAuthorized =
                    releaseActivationCurrentPointerSwitchState
                        .ActivationAuthorized,
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
                releaseActivationRollbackPlanRegistered =
                    releaseActivationRollbackPlan.Registered,
                releaseActivationRollbackPlanActivationInputRegistered =
                    releaseActivationRollbackPlan.ActivationPlanInputRegistered,
                releaseActivationRollbackPlanBackupInputRegistered =
                    releaseActivationRollbackPlan
                        .ConfigurationBackupInputRegistered,
                releaseActivationRollbackPlanMigrationInputRegistered =
                    releaseActivationRollbackPlan.MigrationPlanInputRegistered,
                releaseActivationRollbackPlanServiceControlInputRegistered =
                    releaseActivationRollbackPlan
                        .ServiceControlPlanInputRegistered,
                releaseActivationRollbackPlanHealthInputRegistered =
                    releaseActivationRollbackPlan.HealthPlanInputRegistered,
                releaseActivationRollbackPlanExactActivationBindingRegistered =
                    releaseActivationRollbackPlan
                        .ExactActivationPlanBindingRegistered,
                releaseActivationRollbackPlanExactBackupBindingRegistered =
                    releaseActivationRollbackPlan
                        .ExactConfigurationBackupBindingRegistered,
                releaseActivationRollbackPlanExactMigrationBindingRegistered =
                    releaseActivationRollbackPlan
                        .ExactMigrationPlanBindingRegistered,
                releaseActivationRollbackPlanExactServiceControlBindingRegistered =
                    releaseActivationRollbackPlan
                        .ExactServiceControlPlanBindingRegistered,
                releaseActivationRollbackPlanExactHealthBindingRegistered =
                    releaseActivationRollbackPlan.ExactHealthPlanBindingRegistered,
                releaseActivationRollbackPlanImmutableBackupBindingRegistered =
                    releaseActivationRollbackPlan
                        .ImmutableOriginalBackupBindingRegistered,
                releaseActivationRollbackPlanOriginalBackupRestoreRegistered =
                    releaseActivationRollbackPlan
                        .OriginalBackupRestorePlanningRegistered,
                releaseActivationRollbackPlanReverseMigrationRunnerRegistered =
                    releaseActivationRollbackPlan
                        .ReverseMigrationRunnerPlanningRegistered,
                releaseActivationRollbackPlanThreeSourceRestoreRegistered =
                    releaseActivationRollbackPlan
                        .ThreeSourceRestorePlanningRegistered,
                releaseActivationRollbackPlanSameParentStagingRegistered =
                    releaseActivationRollbackPlan
                        .SameParentRestoreStagingRegistered,
                releaseActivationRollbackPlanDisplacedTreeRegistered =
                    releaseActivationRollbackPlan
                        .DisplacedLiveTreePlanningRegistered,
                releaseActivationRollbackPlanTargetStopRegistered =
                    releaseActivationRollbackPlan
                        .TargetServiceStopPlanningRegistered,
                releaseActivationRollbackPlanAtomicPointerRegistered =
                    releaseActivationRollbackPlan
                        .AtomicCurrentPointerRollbackPlanningRegistered,
                releaseActivationRollbackPlanInstalledStartRegistered =
                    releaseActivationRollbackPlan
                        .InstalledServiceStartPlanningRegistered,
                releaseActivationRollbackPlanInstalledHealthRegistered =
                    releaseActivationRollbackPlan
                        .InstalledHealthVerificationPlanningRegistered,
                releaseActivationRollbackPlanHostRestartRegistered =
                    releaseActivationRollbackPlan
                        .HostRestartRollbackPlanningRegistered,
                releaseActivationRollbackPlanSourceReadRegistered =
                    releaseActivationRollbackPlan.SourceReadRegistered,
                releaseActivationRollbackPlanFileWriteRegistered =
                    releaseActivationRollbackPlan.FileWriteRegistered,
                releaseActivationRollbackPlanDirectoryMutationRegistered =
                    releaseActivationRollbackPlan.DirectoryMutationRegistered,
                releaseActivationRollbackPlanProcessInvocationRegistered =
                    releaseActivationRollbackPlan.ProcessInvocationRegistered,
                releaseActivationRollbackPlanSystemdCommandRegistered =
                    releaseActivationRollbackPlan.SystemdCommandRegistered,
                releaseActivationRollbackPlanNetworkRequestRegistered =
                    releaseActivationRollbackPlan.NetworkRequestRegistered,
                releaseActivationRollbackPlanHealthProbeRegistered =
                    releaseActivationRollbackPlan.HealthProbeRegistered,
                releaseActivationRollbackPlanEvidenceRegistered =
                    releaseActivationRollbackPlan.RollbackEvidenceRegistered,
                releaseActivationRollbackPlanExecutionRegistered =
                    releaseActivationRollbackPlan.RollbackExecutionRegistered,
                releaseActivationRollbackPlanCurrentPointerMutationRegistered =
                    releaseActivationRollbackPlan
                        .CurrentPointerMutationRegistered,
                releaseActivationRollbackPlanActivationAuthorityRegistered =
                    releaseActivationRollbackPlan
                        .ActivationAuthorityRegistered,
                releaseActivationRollbackPlanOperationalCallerRegistered =
                    releaseActivationRollbackPlan.OperationalCallerRegistered,
                releaseActivationRollbackPlanCliCallerRegistered =
                    releaseActivationRollbackPlan.CliCallerRegistered,
                releaseActivationRollbackPlanAdminCallerRegistered =
                    releaseActivationRollbackPlan.AdminCallerRegistered,
                releaseActivationRollbackPlanBrowserCallerRegistered =
                    releaseActivationRollbackPlan.BrowserCallerRegistered,
                releaseActivationRollbackPlanHttpCallerRegistered =
                    releaseActivationRollbackPlan.HttpCallerRegistered,
                releaseActivationRollbackPlanWebSocketCallerRegistered =
                    releaseActivationRollbackPlan.WebSocketCallerRegistered,
                releaseActivationRollbackPlanHostedServiceCallerRegistered =
                    releaseActivationRollbackPlan.HostedServiceCallerRegistered,
                releaseActivationRollbackPlanTimerCallerRegistered =
                    releaseActivationRollbackPlan.TimerCallerRegistered,
                releaseActivationRollbackPlanAetherRemoteCallerRegistered =
                    releaseActivationRollbackPlan.AetherRemoteCallerRegistered,
                releaseActivationRollbackPlanRadioCallerRegistered =
                    releaseActivationRollbackPlan.RadioCallerRegistered,
                releaseActivationRollbackPlanWatchdogCallerRegistered =
                    releaseActivationRollbackPlan.WatchdogCallerRegistered,
                releaseActivationRollbackPlanCommandCallerRegistered =
                    releaseActivationRollbackPlan.CommandCallerRegistered,
                releaseActivationRollbackPlanLeaseCallerRegistered =
                    releaseActivationRollbackPlan.LeaseCallerRegistered,
                releaseActivationRollbackPlanTxCallerRegistered =
                    releaseActivationRollbackPlan.TxCallerRegistered,
                releaseActivationRollbackExecutorRegistered =
                    releaseActivationRollbackExecution.Registered,
                releaseActivationRollbackExecutorConfigurationRegistered =
                    releaseActivationRollbackExecution.ConfigurationRegistered,
                releaseActivationRollbackExecutorEnabled =
                    releaseActivationRollbackExecution.ExecutionEnabled,
                releaseActivationRollbackExecutorAvailable =
                    releaseActivationRollbackExecution.ExecutionAvailable,
                releaseActivationRollbackExecutorStationIdentityConfigured =
                    releaseActivationRollbackExecution
                        .ExpectedStationIdentityConfigured,
                releaseActivationRollbackExecutorPlanInputRegistered =
                    releaseActivationRollbackExecution
                        .ExactRollbackPlanInputRegistered,
                releaseActivationRollbackExecutorExactPlanBindingRegistered =
                    releaseActivationRollbackExecution
                        .ExactRollbackPlanBindingRegistered,
                releaseActivationRollbackExecutorExactActivationBindingRegistered =
                    releaseActivationRollbackExecution
                        .ExactActivationPlanBindingRegistered,
                releaseActivationRollbackExecutorPointerEvidenceInputRegistered =
                    releaseActivationRollbackExecution
                        .ExactCurrentPointerSwitchEvidenceInputRegistered,
                releaseActivationRollbackExecutorServiceFailureTriggerRegistered =
                    releaseActivationRollbackExecution
                        .PostSwitchServiceFailureTriggerRegistered,
                releaseActivationRollbackExecutorHealthFailureTriggerRegistered =
                    releaseActivationRollbackExecution
                        .PostSwitchHealthFailureTriggerRegistered,
                releaseActivationRollbackExecutorStatusDoubleReadRegistered =
                    releaseActivationRollbackExecution
                        .ReleaseStatusDoubleReadRegistered,
                releaseActivationRollbackExecutorSetupDoubleReadRegistered =
                    releaseActivationRollbackExecution.SetupStateDoubleReadRegistered,
                releaseActivationRollbackExecutorTopologyBindingRegistered =
                    releaseActivationRollbackExecution.TopologyBindingRegistered,
                releaseActivationRollbackExecutorBackupRevalidationRegistered =
                    releaseActivationRollbackExecution
                        .ImmutableOriginalBackupRevalidationRegistered,
                releaseActivationRollbackExecutorUnixModeRestoreRegistered =
                    releaseActivationRollbackExecution
                        .OriginalUnixModeRestoreRegistered,
                releaseActivationRollbackExecutorReverseMigrationRegistered =
                    releaseActivationRollbackExecution
                        .ReverseMigrationRunnerRegistered,
                releaseActivationRollbackExecutorThreeSourceRestoreRegistered =
                    releaseActivationRollbackExecution
                        .ThreeSourceRestoreRegistered,
                releaseActivationRollbackExecutorSameParentStagingRegistered =
                    releaseActivationRollbackExecution
                        .SameParentRestoreStagingRegistered,
                releaseActivationRollbackExecutorDisplacedTreeRegistered =
                    releaseActivationRollbackExecution
                        .DisplacedLiveTreeRegistered,
                releaseActivationRollbackExecutorTargetStopRegistered =
                    releaseActivationRollbackExecution.TargetServiceStopRegistered,
                releaseActivationRollbackExecutorDirectProcessRegistered =
                    releaseActivationRollbackExecution.DirectProcessRegistered,
                releaseActivationRollbackExecutorShellRegistered =
                    releaseActivationRollbackExecution.ShellRegistered,
                releaseActivationRollbackExecutorClearedEnvironmentRegistered =
                    releaseActivationRollbackExecution
                        .ClearedEnvironmentRegistered,
                releaseActivationRollbackExecutorUserUnitRegistered =
                    releaseActivationRollbackExecution.UserUnitScopeRegistered,
                releaseActivationRollbackExecutorSystemUnitRegistered =
                    releaseActivationRollbackExecution.SystemUnitScopeRegistered,
                releaseActivationRollbackExecutorBoundedOutputRegistered =
                    releaseActivationRollbackExecution.BoundedOutputRegistered,
                releaseActivationRollbackExecutorHardTimeoutRegistered =
                    releaseActivationRollbackExecution.HardTimeoutRegistered,
                releaseActivationRollbackExecutorProcessTerminationRegistered =
                    releaseActivationRollbackExecution
                        .ProcessTreeTerminationRegistered,
                releaseActivationRollbackExecutorAtomicDirectoryRegistered =
                    releaseActivationRollbackExecution
                        .AtomicDirectoryReplacementRegistered,
                releaseActivationRollbackExecutorAtomicPointerRegistered =
                    releaseActivationRollbackExecution
                        .AtomicCurrentPointerRollbackRegistered,
                releaseActivationRollbackExecutorInstalledStartRegistered =
                    releaseActivationRollbackExecution
                        .InstalledServiceStartRegistered,
                releaseActivationRollbackExecutorInstalledHealthRegistered =
                    releaseActivationRollbackExecution
                        .InstalledHealthVerificationRegistered,
                releaseActivationRollbackExecutorLoopbackHttpRegistered =
                    releaseActivationRollbackExecution.LoopbackOnlyHttpRegistered,
                releaseActivationRollbackExecutorProxyBypassRegistered =
                    releaseActivationRollbackExecution.ProxyBypassRegistered,
                releaseActivationRollbackExecutorRedirectRejectionRegistered =
                    releaseActivationRollbackExecution.RedirectRejectionRegistered,
                releaseActivationRollbackExecutorBoundedHttpBodyRegistered =
                    releaseActivationRollbackExecution.BoundedHttpBodyRegistered,
                releaseActivationRollbackExecutorFreshBrokerRegistered =
                    releaseActivationRollbackExecution
                        .FreshBrokerSnapshotRegistered,
                releaseActivationRollbackExecutorExactStationRegistered =
                    releaseActivationRollbackExecution
                        .ExactStationIdentityRegistered,
                releaseActivationRollbackExecutorBoundedDeadlineRegistered =
                    releaseActivationRollbackExecution.BoundedDeadlineRegistered,
                releaseActivationRollbackExecutorCleanupRegistered =
                    releaseActivationRollbackExecution
                        .DisplacedTreeCleanupRegistered,
                releaseActivationRollbackExecutorEvidenceRegistered =
                    releaseActivationRollbackExecution.ExactPlanEvidenceRegistered,
                releaseActivationRollbackExecutorReconciliationRegistered =
                    releaseActivationRollbackExecution
                        .PartialFailureReconciliationRegistered,
                releaseActivationRollbackExecutorAutomaticRetryRegistered =
                    releaseActivationRollbackExecution.AutomaticRetryRegistered,
                releaseActivationRollbackExecutorHostRestartRegistered =
                    releaseActivationRollbackExecution.HostRestartRegistered,
                releaseActivationRollbackExecutorRemoteControlRegistered =
                    releaseActivationRollbackExecution
                        .RemoteServiceControlRegistered,
                releaseActivationRollbackExecutorActivationAuthorityRegistered =
                    releaseActivationRollbackExecution
                        .ActivationAuthorityRegistered,
                releaseActivationRollbackExecutorOperationalCallerRegistered =
                    releaseActivationRollbackExecution
                        .OperationalCallerRegistered,
                releaseActivationRollbackExecutorCliCallerRegistered =
                    releaseActivationRollbackExecution.CliCallerRegistered,
                releaseActivationRollbackExecutorAdminCallerRegistered =
                    releaseActivationRollbackExecution.AdminCallerRegistered,
                releaseActivationRollbackExecutorBrowserCallerRegistered =
                    releaseActivationRollbackExecution.BrowserCallerRegistered,
                releaseActivationRollbackExecutorHttpCallerRegistered =
                    releaseActivationRollbackExecution.HttpCallerRegistered,
                releaseActivationRollbackExecutorWebSocketCallerRegistered =
                    releaseActivationRollbackExecution.WebSocketCallerRegistered,
                releaseActivationRollbackExecutorHostedServiceCallerRegistered =
                    releaseActivationRollbackExecution
                        .HostedServiceCallerRegistered,
                releaseActivationRollbackExecutorTimerCallerRegistered =
                    releaseActivationRollbackExecution.TimerCallerRegistered,
                releaseActivationRollbackExecutorAetherRemoteCallerRegistered =
                    releaseActivationRollbackExecution
                        .AetherRemoteCommandCallerRegistered,
                releaseActivationRollbackExecutorRadioCallerRegistered =
                    releaseActivationRollbackExecution.RadioCallerRegistered,
                releaseActivationRollbackExecutorWatchdogCallerRegistered =
                    releaseActivationRollbackExecution.WatchdogCallerRegistered,
                releaseActivationRollbackExecutorCommandCallerRegistered =
                    releaseActivationRollbackExecution.CommandCallerRegistered,
                releaseActivationRollbackExecutorLeaseCallerRegistered =
                    releaseActivationRollbackExecution.LeaseCallerRegistered,
                releaseActivationRollbackExecutorTxCallerRegistered =
                    releaseActivationRollbackExecution.TxCallerRegistered,
                releaseActivationRollbackReady =
                    releaseActivationRollbackExecutionState.RollbackReady,
                releaseActivationRollbackExactPlanActive =
                    releaseActivationRollbackExecutionState.ExactRollbackPlanBound,
                releaseActivationRollbackExactActivationActive =
                    releaseActivationRollbackExecutionState.ExactActivationPlanBound,
                releaseActivationRollbackPointerEvidenceActive =
                    releaseActivationRollbackExecutionState
                        .ExactPointerSwitchEvidenceBound,
                releaseActivationRollbackFailureTriggerActive =
                    releaseActivationRollbackExecutionState.ExactFailureTriggerBound,
                releaseActivationRollbackBackupValidated =
                    releaseActivationRollbackExecutionState
                        .ImmutableOriginalBackupValidated,
                releaseActivationRollbackRestoreSourceCount =
                    releaseActivationRollbackExecutionState.RestoreSourceCount,
                releaseActivationRollbackRestoreDirectoryCount =
                    releaseActivationRollbackExecutionState.RestoreDirectoryCount,
                releaseActivationRollbackRestoreFileCount =
                    releaseActivationRollbackExecutionState.RestoreFileCount,
                releaseActivationRollbackRestoreBytes =
                    releaseActivationRollbackExecutionState.RestoreBytes,
                releaseActivationRollbackExecutedStopCount =
                    releaseActivationRollbackExecutionState
                        .ExecutedStopActionCount,
                releaseActivationRollbackNoOpStopCount =
                    releaseActivationRollbackExecutionState
                        .TopologyNoOpStopActionCount,
                releaseActivationRollbackRestoredRootCount =
                    releaseActivationRollbackExecutionState.RestoredLiveRootCount,
                releaseActivationRollbackCurrentPointerChanged =
                    releaseActivationRollbackExecutionState
                        .CurrentPointerRolledBack,
                releaseActivationRollbackExecutedStartCount =
                    releaseActivationRollbackExecutionState
                        .ExecutedStartActionCount,
                releaseActivationRollbackNoOpStartCount =
                    releaseActivationRollbackExecutionState
                        .TopologyNoOpStartActionCount,
                releaseActivationRollbackVerifiedHealthCount =
                    releaseActivationRollbackExecutionState
                        .VerifiedHealthTargetCount,
                releaseActivationRollbackCleanupCount =
                    releaseActivationRollbackExecutionState
                        .DisplacedTreeCleanupCount,
                releaseActivationRollbackInstalledActive =
                    releaseActivationRollbackExecutionState
                        .InstalledReleaseActive,
                releaseActivationRollbackSetupStable =
                    releaseActivationRollbackExecutionState.SetupStable,
                releaseActivationRollbackTopologyStable =
                    releaseActivationRollbackExecutionState.TopologyStable,
                releaseActivationRollbackConfigurationRestored =
                    releaseActivationRollbackExecutionState
                        .ConfigurationRestored,
                releaseActivationRollbackServicesRestored =
                    releaseActivationRollbackExecutionState.ServicesRestored,
                releaseActivationRollbackHealthVerified =
                    releaseActivationRollbackExecutionState
                        .InstalledHealthVerified,
                releaseActivationRollbackPerformed =
                    releaseActivationRollbackExecutionState.RollbackPerformed,
                releaseActivationRollbackReconciliationRequired =
                    releaseActivationRollbackExecutionState
                        .ReconciliationRequired,
                releaseActivationRollbackActivationAuthorized =
                    releaseActivationRollbackExecutionState.ActivationAuthorized,
                releaseActivationOperatorApprovalRegistered =
                    releaseActivationOperatorApproval.Registered,
                releaseActivationOperatorApprovalEnabled =
                    releaseActivationOperatorApproval.AuthorityEnabled,
                releaseActivationOperatorApprovalMaximumAgeSeconds =
                    releaseActivationOperatorApproval.MaximumApprovalAgeSeconds,
                releaseActivationOperatorApprovalExactPlanBindingRegistered =
                    releaseActivationOperatorApproval.ExactPlanBindingRegistered,
                releaseActivationOperatorApprovalAuthenticationEvidenceRequired =
                    releaseActivationOperatorApproval.AuthenticationEvidenceRequired,
                releaseActivationOperatorApprovalAdministratorAuthorizationRequired =
                    releaseActivationOperatorApproval
                        .AdministratorAuthorizationRequired,
                releaseActivationOperatorApprovalReauthenticationRequired =
                    releaseActivationOperatorApproval.ReauthenticationRequired,
                releaseActivationOperatorApprovalBoundedLifetimeRegistered =
                    releaseActivationOperatorApproval
                        .BoundedApprovalLifetimeRegistered,
                releaseActivationOperatorApprovalSingleActiveRegistered =
                    releaseActivationOperatorApproval
                        .SingleActiveApprovalRegistered,
                releaseActivationOperatorApprovalRevocationRegistered =
                    releaseActivationOperatorApproval.RevocationRegistered,
                releaseActivationOperatorApprovalActive =
                    releaseActivationOperatorApproval.ActiveApproval,
                releaseActivationOperatorApprovalAvailable =
                    releaseActivationOperatorApproval.ApprovalAvailable,
                releaseActivationOperatorApprovalAttemptCount =
                    releaseActivationOperatorApproval.AttemptCount,
                releaseActivationOperatorApprovalAcceptedCount =
                    releaseActivationOperatorApproval.AcceptedCount,
                releaseActivationOperatorApprovalRejectedCount =
                    releaseActivationOperatorApproval.RejectedCount,
                releaseActivationOperatorApprovalRevokedCount =
                    releaseActivationOperatorApproval.RevokedCount,
                releaseActivationOperatorApprovalLastOutcome =
                    releaseActivationOperatorApproval.LastOutcome,
                releaseActivationOperatorApprovalLastObservedAt =
                    releaseActivationOperatorApproval.LastObservedAt,
                releaseActivationOperatorApprovalFileWriteRegistered =
                    releaseActivationOperatorApproval.FileWriteRegistered,
                releaseActivationOperatorApprovalCurrentPointerMutationRegistered =
                    releaseActivationOperatorApproval
                        .CurrentPointerMutationRegistered,
                releaseActivationOperatorApprovalActivationExecutionRegistered =
                    releaseActivationOperatorApproval.ActivationExecutionRegistered,
                releaseActivationOperatorApprovalActivationAuthorityRegistered =
                    releaseActivationOperatorApproval
                        .ActivationAuthorityRegistered,
                releaseActivationOperatorApprovalTxLeaseMutationRegistered =
                    releaseActivationOperatorApproval.TxLeaseMutationRegistered,
                releaseActivationOperatorApprovalRadioCommandRegistered =
                    releaseActivationOperatorApproval.RadioCommandRegistered,
                releaseActivationOperatorApprovalWatchdogMutationRegistered =
                    releaseActivationOperatorApproval.WatchdogMutationRegistered,
                releaseActivationOperatorApprovalBackupExecutionRegistered =
                    releaseActivationOperatorApproval.BackupExecutionRegistered,
                releaseActivationOperatorApprovalMigrationExecutionRegistered =
                    releaseActivationOperatorApproval.MigrationExecutionRegistered,
                releaseActivationOperatorApprovalServiceControlRegistered =
                    releaseActivationOperatorApproval.ServiceControlRegistered,
                releaseActivationOperatorApprovalHealthProbeCallerRegistered =
                    releaseActivationOperatorApproval.HealthProbeCallerRegistered,
                releaseActivationOperatorApprovalRollbackExecutionRegistered =
                    releaseActivationOperatorApproval.RollbackExecutionRegistered,
                releaseActivationOperatorApprovalCliCallerRegistered =
                    releaseActivationOperatorApproval.CliCallerRegistered,
                releaseActivationOperatorApprovalAdminCallerRegistered =
                    releaseActivationOperatorApproval.AdminCallerRegistered,
                releaseActivationOperatorApprovalBrowserCallerRegistered =
                    releaseActivationOperatorApproval.BrowserCallerRegistered,
                releaseActivationOperatorApprovalHttpCallerRegistered =
                    releaseActivationOperatorApproval.HttpCallerRegistered,
                releaseActivationOperatorApprovalWebSocketCallerRegistered =
                    releaseActivationOperatorApproval.WebSocketCallerRegistered,
                releaseActivationOperatorApprovalHostedServiceCallerRegistered =
                    releaseActivationOperatorApproval.HostedServiceCallerRegistered,
                releaseActivationOperatorApprovalTimerCallerRegistered =
                    releaseActivationOperatorApproval.TimerCallerRegistered,
                releaseActivationOperatorApprovalAetherRemoteCallerRegistered =
                    releaseActivationOperatorApproval.AetherRemoteCallerRegistered,
                releaseActivationOperatorApprovalCommandCallerRegistered =
                    releaseActivationOperatorApproval.CommandCallerRegistered,
                releaseActivationOperatorApprovalLeaseCallerRegistered =
                    releaseActivationOperatorApproval.LeaseCallerRegistered,
                releaseActivationOperatorApprovalTxCallerRegistered =
                    releaseActivationOperatorApproval.TxCallerRegistered,
                releaseActivationHealthVerificationExecutorRegistered =
                    releaseActivationHealthVerification.Registered,
                releaseActivationHealthVerificationExecutorConfigurationRegistered =
                    releaseActivationHealthVerification.ConfigurationRegistered,
                releaseActivationHealthVerificationExecutorEnabled =
                    releaseActivationHealthVerification.ExecutionEnabled,
                releaseActivationHealthVerificationExecutorAvailable =
                    releaseActivationHealthVerification.ExecutionAvailable,
                releaseActivationHealthVerificationExecutorStationIdentityConfigured =
                    releaseActivationHealthVerification
                        .ExpectedStationIdentityConfigured,
                releaseActivationHealthVerificationExecutorPlanInputRegistered =
                    releaseActivationHealthVerification
                        .ExactHealthPlanInputRegistered,
                releaseActivationHealthVerificationExecutorExactPlanBindingRegistered =
                    releaseActivationHealthVerification
                        .ExactHealthPlanBindingRegistered,
                releaseActivationHealthVerificationExecutorExactActivationBindingRegistered =
                    releaseActivationHealthVerification
                        .ExactActivationPlanBindingRegistered,
                releaseActivationHealthVerificationExecutorPointerSwitchEvidenceInputRegistered =
                    releaseActivationHealthVerification
                        .CurrentPointerSwitchEvidenceInputRegistered,
                releaseActivationHealthVerificationExecutorServiceControlInputRegistered =
                    releaseActivationHealthVerification
                        .ServiceControlEvidenceInputRegistered,
                releaseActivationHealthVerificationExecutorStatusDoubleReadRegistered =
                    releaseActivationHealthVerification
                        .ReleaseStatusDoubleReadRegistered,
                releaseActivationHealthVerificationExecutorSetupDoubleReadRegistered =
                    releaseActivationHealthVerification
                        .SetupStateDoubleReadRegistered,
                releaseActivationHealthVerificationExecutorTopologyBindingRegistered =
                    releaseActivationHealthVerification.TopologyBindingRegistered,
                releaseActivationHealthVerificationExecutorTargetActiveRegistered =
                    releaseActivationHealthVerification
                        .TargetActiveRequirementRegistered,
                releaseActivationHealthVerificationExecutorCanonicalGatewayHostRegistered =
                    releaseActivationHealthVerification
                        .CanonicalGatewayHostBindingRegistered,
                releaseActivationHealthVerificationExecutorUnitProcessRegistered =
                    releaseActivationHealthVerification
                        .UnitActivityProcessRegistered,
                releaseActivationHealthVerificationExecutorDirectProcessRegistered =
                    releaseActivationHealthVerification.DirectProcessRegistered,
                releaseActivationHealthVerificationExecutorShellRegistered =
                    releaseActivationHealthVerification.ShellRegistered,
                releaseActivationHealthVerificationExecutorClearedEnvironmentRegistered =
                    releaseActivationHealthVerification
                        .ClearedEnvironmentRegistered,
                releaseActivationHealthVerificationExecutorLoopbackHttpRegistered =
                    releaseActivationHealthVerification.LoopbackHttpRegistered,
                releaseActivationHealthVerificationExecutorProxyBypassRegistered =
                    releaseActivationHealthVerification.ProxyBypassRegistered,
                releaseActivationHealthVerificationExecutorRedirectRejectionRegistered =
                    releaseActivationHealthVerification.RedirectRejectionRegistered,
                releaseActivationHealthVerificationExecutorBoundedHttpBodyRegistered =
                    releaseActivationHealthVerification.BoundedHttpBodyRegistered,
                releaseActivationHealthVerificationExecutorFreshBrokerSnapshotRegistered =
                    releaseActivationHealthVerification
                        .FreshBrokerSnapshotRegistered,
                releaseActivationHealthVerificationExecutorExactStationRegistered =
                    releaseActivationHealthVerification
                        .ExactStationIdentityRegistered,
                releaseActivationHealthVerificationExecutorBoundedDeadlineRegistered =
                    releaseActivationHealthVerification.BoundedDeadlineRegistered,
                releaseActivationHealthVerificationExecutorOrderingRegistered =
                    releaseActivationHealthVerification
                        .DeterministicOrderingRegistered,
                releaseActivationHealthVerificationExecutorEvidenceRegistered =
                    releaseActivationHealthVerification.ExactPlanEvidenceRegistered,
                releaseActivationHealthVerificationExecutorJournalReadRegistered =
                    releaseActivationHealthVerification.JournalReadRegistered,
                releaseActivationHealthVerificationExecutorCredentialReadRegistered =
                    releaseActivationHealthVerification.CredentialReadRegistered,
                releaseActivationHealthVerificationExecutorServiceControlRegistered =
                    releaseActivationHealthVerification.ServiceControlRegistered,
                releaseActivationHealthVerificationExecutorCurrentPointerMutationRegistered =
                    releaseActivationHealthVerification
                        .CurrentPointerMutationRegistered,
                releaseActivationHealthVerificationExecutorRollbackRegistered =
                    releaseActivationHealthVerification.RollbackRegistered,
                releaseActivationHealthVerificationExecutorActivationAuthorityRegistered =
                    releaseActivationHealthVerification
                        .ActivationAuthorityRegistered,
                releaseActivationHealthVerificationExecutorOperationalCallerRegistered =
                    releaseActivationHealthVerification.OperationalCallerRegistered,
                releaseActivationHealthVerificationExecutorCliCallerRegistered =
                    releaseActivationHealthVerification.CliCallerRegistered,
                releaseActivationHealthVerificationExecutorAdminCallerRegistered =
                    releaseActivationHealthVerification.AdminCallerRegistered,
                releaseActivationHealthVerificationExecutorBrowserCallerRegistered =
                    releaseActivationHealthVerification.BrowserCallerRegistered,
                releaseActivationHealthVerificationExecutorHttpCallerRegistered =
                    releaseActivationHealthVerification.HttpCallerRegistered,
                releaseActivationHealthVerificationExecutorWebSocketCallerRegistered =
                    releaseActivationHealthVerification.WebSocketCallerRegistered,
                releaseActivationHealthVerificationExecutorHostedServiceCallerRegistered =
                    releaseActivationHealthVerification.HostedServiceCallerRegistered,
                releaseActivationHealthVerificationExecutorTimerCallerRegistered =
                    releaseActivationHealthVerification.TimerCallerRegistered,
                releaseActivationHealthVerificationExecutorAetherRemoteCommandCallerRegistered =
                    releaseActivationHealthVerification
                        .AetherRemoteCommandCallerRegistered,
                releaseActivationHealthVerificationExecutorRadioCallerRegistered =
                    releaseActivationHealthVerification.RadioCallerRegistered,
                releaseActivationHealthVerificationExecutorWatchdogCallerRegistered =
                    releaseActivationHealthVerification.WatchdogCallerRegistered,
                releaseActivationHealthVerificationExecutorCommandCallerRegistered =
                    releaseActivationHealthVerification.CommandCallerRegistered,
                releaseActivationHealthVerificationExecutorLeaseCallerRegistered =
                    releaseActivationHealthVerification.LeaseCallerRegistered,
                releaseActivationHealthVerificationExecutorTxCallerRegistered =
                    releaseActivationHealthVerification.TxCallerRegistered,
                releaseActivationHealthVerificationReady =
                    releaseActivationHealthVerificationState
                        .HealthVerificationReady,
                releaseActivationHealthVerificationExactPlanActive =
                    releaseActivationHealthVerificationState.ExactHealthPlanBound,
                releaseActivationHealthVerificationExactActivationActive =
                    releaseActivationHealthVerificationState
                        .ExactActivationPlanBound,
                releaseActivationHealthVerificationTargetCount =
                    releaseActivationHealthVerificationState.HealthTargetCount,
                releaseActivationHealthVerificationVerifiedTargetCount =
                    releaseActivationHealthVerificationState.VerifiedTargetCount,
                releaseActivationHealthVerificationUnitCheckCount =
                    releaseActivationHealthVerificationState
                        .UnitActivityCheckCount,
                releaseActivationHealthVerificationHttpCheckCount =
                    releaseActivationHealthVerificationState
                        .LoopbackHttpCheckCount,
                releaseActivationHealthVerificationBrokerLinkCheckCount =
                    releaseActivationHealthVerificationState
                        .FreshBrokerLinkCheckCount,
                releaseActivationHealthVerificationTargetActiveBefore =
                    releaseActivationHealthVerificationState
                        .TargetActiveBeforeVerification,
                releaseActivationHealthVerificationTargetActiveAfter =
                    releaseActivationHealthVerificationState
                        .TargetActiveAfterVerification,
                releaseActivationHealthVerificationSetupStable =
                    releaseActivationHealthVerificationState.SetupStable,
                releaseActivationHealthVerificationCanonicalHostBound =
                    releaseActivationHealthVerificationState
                        .CanonicalGatewayHostBound,
                releaseActivationHealthVerificationAllUnitsActive =
                    releaseActivationHealthVerificationState.AllUnitsActive,
                releaseActivationHealthVerificationAllContractsPassed =
                    releaseActivationHealthVerificationState
                        .AllHealthContractsPassed,
                releaseActivationHealthVerificationReconciliationRequired =
                    releaseActivationHealthVerificationState
                        .ReconciliationRequired,
                releaseActivationHealthVerificationServiceControlReady =
                    releaseActivationHealthVerificationState.ServiceControlReady,
                releaseActivationHealthVerificationCurrentPointerChanged =
                    releaseActivationHealthVerificationState.CurrentPointerChanged,
                releaseActivationHealthVerificationActivationAuthorized =
                    releaseActivationHealthVerificationState.ActivationAuthorized,
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
                releaseUpdateTransactionRegistered =
                    releaseUpdateTransaction.Registered,
                releaseUpdateTransactionExecutionEnabled =
                    releaseUpdateTransaction.ExecutionEnabled,
                releaseUpdateTransactionLeaseDrainSeconds =
                    releaseUpdateTransaction.LeaseDrainSeconds,
                releaseUpdateTransactionOfflinePreflightRegistered =
                    releaseUpdateTransaction.OfflinePreflightRegistered,
                releaseUpdateTransactionVerifiedStagingRegistered =
                    releaseUpdateTransaction.VerifiedStagingRegistered,
                releaseUpdateTransactionVerifiedExtractionRegistered =
                    releaseUpdateTransaction.VerifiedExtractionRegistered,
                releaseUpdateTransactionAtomicInactivePublicationRegistered =
                    releaseUpdateTransaction.AtomicInactivePublicationRegistered,
                releaseUpdateTransactionActivationAdaptationRegistered =
                    releaseUpdateTransaction.ActivationPlanAdaptationRegistered,
                releaseUpdateTransactionConfigurationBackupRegistered =
                    releaseUpdateTransaction
                        .ConfigurationBackupExecutionRegistered,
                releaseUpdateTransactionMigrationRegistered =
                    releaseUpdateTransaction.MigrationExecutionRegistered,
                releaseUpdateTransactionLeaseClosureRegistered =
                    releaseUpdateTransaction.TxLeaseAdmissionClosureRegistered,
                releaseUpdateTransactionSafetyEvidenceRegistered =
                    releaseUpdateTransaction
                        .RadioAuthoritativeSafetyEvidenceRegistered,
                releaseUpdateTransactionServiceControlRegistered =
                    releaseUpdateTransaction.ServiceControlExecutionRegistered,
                releaseUpdateTransactionCurrentPointerSwitchRegistered =
                    releaseUpdateTransaction
                        .AtomicCurrentPointerSwitchRegistered,
                releaseUpdateTransactionHealthVerificationRegistered =
                    releaseUpdateTransaction.HealthVerificationRegistered,
                releaseUpdateTransactionHostRestartRegistered =
                    releaseUpdateTransaction.HostRestartExecutionRegistered,
                releaseUpdateTransactionAutomaticRollbackRegistered =
                    releaseUpdateTransaction.AutomaticRollbackRegistered,
                releaseUpdateTransactionManualRollbackRegistered =
                    releaseUpdateTransaction.ManualRollbackRegistered,
                releaseUpdateTransactionAuthenticatedApprovalRegistered =
                    releaseUpdateTransaction.AuthenticatedApprovalRegistered,
                releaseUpdateTransactionDurableJournalRegistered =
                    releaseUpdateTransaction.DurableJournalRegistered,
                releaseUpdateTransactionCliCallerRegistered =
                    releaseUpdateTransaction.CliCallerRegistered,
                releaseUpdateTransactionAdminCallerRegistered =
                    releaseUpdateTransaction.AdminCallerRegistered,
                releaseUpdateTransactionBrowserCallerRegistered =
                    releaseUpdateTransaction.BrowserCallerRegistered,
                releaseUpdateTransactionRadioCommandRegistered =
                    releaseUpdateTransaction.RadioCommandRegistered,
                releaseUpdateTransactionTxCallerRegistered =
                    releaseUpdateTransaction.TxCallerRegistered,
                releaseActivationHostRestartRegistered =
                    releaseActivationHostRestart.Registered,
                releaseActivationHostRestartExecutionEnabled =
                    releaseActivationHostRestart.ExecutionEnabled,
                releaseActivationHostRestartExactPlanInputRegistered =
                    releaseActivationHostRestart
                        .ExactHostRestartPlanInputRegistered,
                releaseActivationHostRestartPointerEvidenceRegistered =
                    releaseActivationHostRestart
                        .ExactPointerEvidenceInputRegistered,
                releaseActivationHostRestartDurableMarkerRegistered =
                    releaseActivationHostRestart
                        .DurablePreRestartMarkerRegistered,
                releaseActivationHostRestartDirectSystemctlRegistered =
                    releaseActivationHostRestart.DirectSystemctlRegistered,
                releaseActivationHostRestartShellRegistered =
                    releaseActivationHostRestart.ShellRegistered,
                releaseActivationHostRestartArbitraryCommandRegistered =
                    releaseActivationHostRestart.ArbitraryCommandRegistered,
                releaseActivationHostRestartPostBootVerificationRequired =
                    releaseActivationHostRestart.PostBootVerificationRequired,
                releaseActivationHostRestartRadioCallerRegistered =
                    releaseActivationHostRestart.RadioCallerRegistered,
                releaseActivationHostRestartCommandCallerRegistered =
                    releaseActivationHostRestart.CommandCallerRegistered,
                releaseActivationHostRestartTxCallerRegistered =
                    releaseActivationHostRestart.TxCallerRegistered,
                releaseActivationPostBootContinuationRegistered =
                    releaseActivationPostBootContinuation.Registered,
                releaseActivationPostBootContinuationExecutionEnabled =
                    releaseActivationPostBootContinuation.ExecutionEnabled,
                releaseActivationPostBootContinuationMarkerReadRegistered =
                    releaseActivationPostBootContinuation
                        .OwnerOnlyMarkerReadRegistered,
                releaseActivationPostBootContinuationStrictSchemaRegistered =
                    releaseActivationPostBootContinuation
                        .StrictMarkerSchemaRegistered,
                releaseActivationPostBootContinuationFreshnessRegistered =
                    releaseActivationPostBootContinuation
                        .MarkerFreshnessRegistered,
                releaseActivationPostBootContinuationStatusDoubleReadRegistered =
                    releaseActivationPostBootContinuation
                        .ReleaseStatusDoubleReadRegistered,
                releaseActivationPostBootContinuationSetupDoubleReadRegistered =
                    releaseActivationPostBootContinuation
                        .SetupStateDoubleReadRegistered,
                releaseActivationPostBootContinuationActiveReleaseBindingRegistered =
                    releaseActivationPostBootContinuation
                        .ExactActiveReleaseBindingRegistered,
                releaseActivationPostBootContinuationUnitActivityRegistered =
                    releaseActivationPostBootContinuation
                        .FixedUnitActivityRegistered,
                releaseActivationPostBootContinuationLoopbackHealthRegistered =
                    releaseActivationPostBootContinuation
                        .LoopbackHealthRegistered,
                releaseActivationPostBootContinuationBrokerLinkRegistered =
                    releaseActivationPostBootContinuation
                        .FreshBrokerLinkRegistered,
                releaseActivationPostBootContinuationTerminalResultRegistered =
                    releaseActivationPostBootContinuation
                        .DurableTerminalResultRegistered,
                releaseActivationPostBootContinuationIdempotenceRegistered =
                    releaseActivationPostBootContinuation
                        .IdempotentMarkerConsumptionRegistered,
                releaseActivationPostBootContinuationApprovalReconstructionRegistered =
                    releaseActivationPostBootContinuation
                        .ApprovalAuthorityReconstructionRegistered,
                releaseActivationPostBootContinuationRollbackReconstructionRegistered =
                    releaseActivationPostBootContinuation
                        .RollbackAuthorityReconstructionRegistered,
                releaseActivationPostBootContinuationPointerMutationRegistered =
                    releaseActivationPostBootContinuation
                        .CurrentPointerMutationRegistered,
                releaseActivationPostBootContinuationServiceControlRegistered =
                    releaseActivationPostBootContinuation.ServiceControlRegistered,
                releaseActivationPostBootContinuationRadioCallerRegistered =
                    releaseActivationPostBootContinuation.RadioCallerRegistered,
                releaseActivationPostBootContinuationCommandCallerRegistered =
                    releaseActivationPostBootContinuation.CommandCallerRegistered,
                releaseActivationPostBootContinuationTxCallerRegistered =
                    releaseActivationPostBootContinuation.TxCallerRegistered,
                releaseActivationPostBootContinuationMarkerObserved =
                    releaseActivationPostBootContinuationState.MarkerObserved,
                releaseActivationPostBootContinuationResultObserved =
                    releaseActivationPostBootContinuationState
                        .TerminalResultObserved,
                releaseActivationPostBootContinuationCompleted =
                    releaseActivationPostBootContinuationState
                        .ContinuationCompleted,
                releaseActivationPostBootContinuationHealthVerified =
                    releaseActivationPostBootContinuationState.HealthVerified,
                releaseActivationPostBootContinuationMarkerConsumed =
                    releaseActivationPostBootContinuationState.MarkerConsumed,
                releaseActivationPostBootContinuationReconciliationRequired =
                    releaseActivationPostBootContinuationState
                        .ReconciliationRequired,
                releaseActivationPostBootContinuationUnitAttemptCount =
                    releaseActivationPostBootContinuationState
                        .UnitActivityAttemptCount,
                releaseActivationPostBootContinuationHttpAttemptCount =
                    releaseActivationPostBootContinuationState
                        .LoopbackHttpAttemptCount,
                releaseActivationPostBootContinuationBrokerAttemptCount =
                    releaseActivationPostBootContinuationState
                        .BrokerLinkObservationCount,
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
    .RequireAuthorization(AetherPolicies.Admin);

app.MapGet(
        "/api/admin/diagnostics/operations",
        async (
            OperationsReadinessService operations,
            CancellationToken cancellationToken) =>
            Results.Ok(await operations.GetSnapshotAsync(cancellationToken)))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/admin/diagnostics/operations/run",
        async (
            OperationsReadinessService operations,
            CancellationToken cancellationToken) =>
            Results.Ok(await operations.RunActiveChecksAsync(cancellationToken)))
    .RequireAuthorization(AetherPolicies.Admin)
    .RequireAetherAntiforgery()
    .RequireRateLimiting("admin-operations");

app.MapGet(
        "/api/admin/diagnostics/bundle",
        async (
            OperationsDiagnosticBundleService diagnostics,
            CancellationToken cancellationToken) =>
        {
            OperationsDiagnosticBundle bundle =
                await diagnostics.CreateAsync(cancellationToken);
            return Results.File(
                bundle.Content,
                "application/zip",
                bundle.FileName,
                enableRangeProcessing: false);
        })
    .RequireAuthorization(AetherPolicies.Admin)
    .RequireRateLimiting("admin-operations");

app.MapGet(
        "/healthz",
        () => Results.Ok(new
        {
            status = "ok",
            radioMode = radioSettings.Mode,
            transmitEnabled =
                stationTxProductionActivationBinding.BindingApplied
        }))
    .AllowAnonymous();

app.MapGet(
        AetherRemoteBootstrapService.WellKnownRoute,
        async (
            HttpContext context,
            AetherRemoteBootstrapService bootstrap,
            CancellationToken cancellationToken) =>
        {
            if (!bootstrap.Enabled)
            {
                return Results.NotFound();
            }
            try
            {
                AetherRemoteBootstrapDocument document =
                    await bootstrap.GetDocumentAsync(cancellationToken);
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                return Results.Ok(document);
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or IOException or
                      InvalidDataException or UnauthorizedAccessException or
                      System.Security.SecurityException)
            {
                return Results.Json(
                    new { error = "AetherRemote bootstrap is not ready." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
    .AllowAnonymous();

app.MapGet(
        AetherRemoteBootstrapService.InstallerRoute,
        (
            HttpContext context,
            AetherRemoteBootstrapService bootstrap) =>
        {
            if (!bootstrap.Enabled)
            {
                return Results.NotFound();
            }
            try
            {
                AetherRemoteBootstrapAsset installer =
                    bootstrap.ResolveInstallerAsset();
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                return Results.File(
                    installer.Path,
                    installer.ContentType,
                    installer.DownloadName);
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or IOException or
                      InvalidDataException or UnauthorizedAccessException or
                      System.Security.SecurityException)
            {
                return Results.NotFound();
            }
        })
    .AllowAnonymous();

app.MapGet(
        "/aetherremote/releases/{releaseIdentity}/{architecture}/{asset}",
        async (
            string releaseIdentity,
            string architecture,
            string asset,
            HttpContext context,
            AetherRemoteBootstrapService bootstrap,
            CancellationToken cancellationToken) =>
        {
            if (!bootstrap.Enabled)
            {
                return Results.NotFound();
            }
            try
            {
                AetherRemoteBootstrapAsset? releaseAsset =
                    await bootstrap.ResolveReleaseAssetAsync(
                        releaseIdentity,
                        architecture,
                        asset,
                        cancellationToken);
                if (releaseAsset is null)
                {
                    return Results.NotFound();
                }
                context.Response.Headers.CacheControl =
                    "public, max-age=31536000, immutable";
                return Results.File(
                    releaseAsset.Path,
                    releaseAsset.ContentType,
                    releaseAsset.DownloadName,
                    enableRangeProcessing: true);
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or IOException or
                      InvalidDataException or UnauthorizedAccessException or
                      System.Security.SecurityException)
            {
                return Results.Json(
                    new { error = "The requested signed release is unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
    .AllowAnonymous();

app.MapGet(
        "/auth/login",
        (string? returnUrl) =>
        {
            string safeReturnUrl = LocalReturnUrl.Normalize(returnUrl);
            if (authenticationTopology.Mode ==
                AetherAuthenticationMode.Development)
            {
                return Results.Redirect(safeReturnUrl);
            }
            if (authenticationTopology.ExternalProvider is null)
            {
                return Results.Redirect(
                    "/login?returnUrl=" +
                    Uri.EscapeDataString(safeReturnUrl));
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
        (HttpContext context, IAntiforgery antiforgery) =>
            AetherAntiforgery.IssueLogoutConfirmation(context, antiforgery))
    .RequireAuthorization();

app.MapPost(
        "/auth/logout",
        async (
            HttpContext context,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            if (authenticationTopology.Mode ==
                AetherAuthenticationMode.Development)
            {
                return Results.Redirect("/");
            }

            AetherAuthenticationSessionService sessions =
                context.RequestServices
                    .GetRequiredService<
                        AetherAuthenticationSessionService>();
            AetherAuthenticationSessionRevocationResult revoked =
                await sessions.RevokeAsync(
                    user,
                    "user-logout",
                    cancellationToken);
            if (!revoked.Succeeded)
            {
                return Results.Unauthorized();
            }

            AuthenticationProperties properties =
                new() { RedirectUri = "/" };
            string[] schemes =
                user.HasClaim(
                    claim =>
                        claim.Type ==
                        AetherIdentityClaimTypes.ProviderId)
                    ?
                    [
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        OpenIdConnectDefaults.AuthenticationScheme
                    ]
                    : [CookieAuthenticationDefaults.AuthenticationScheme];
            return Results.SignOut(properties, schemes);
        })
    .RequireAuthorization()
    .RequireAetherAntiforgery();

app.MapGet(
        "/api/antiforgery",
        (HttpContext context, IAntiforgery antiforgery) =>
            AetherAntiforgery.IssueToken(context, antiforgery))
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
    .RequireAuthorization(AetherPolicies.Control)
    .RequireAetherAntiforgery();

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
    .RequireAuthorization(AetherPolicies.Observe)
    .RequireAetherAntiforgery();

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
    .RequireAuthorization(AetherPolicies.Control)
    .RequireAetherAntiforgery();

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

app.MapGet(
        "/api/admin/stations/bootstrap",
        async (
            string? stationId,
            AetherRemoteBootstrapService bootstrap,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(
                    await bootstrap.GetAdminGuideAsync(
                        stationId,
                        cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        })
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/admin/stations/{stationId}/release-update",
        async (
            string stationId,
            ClaimsPrincipal user,
            AetherAdministratorAuthorityService authority,
            AetherRemoteBootstrapService bootstrap,
            RemoteStationCatalogService remoteStations,
            AdministrativeAuditStore audit,
            CancellationToken cancellationToken) =>
        {
            (string administratorId, string administratorName) =
                GetAdministrativeActor(user);
            string stationTarget = $"station:{stationId}";
            try
            {
                AetherAdministratorAuthorityEvidence evidence =
                    await authority.RequireFreshAsync(user, cancellationToken);
                administratorId = evidence.UserId.ToString("D");
            }
            catch (AetherAdministratorReauthenticationRequiredException)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateStationRelease,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    "Fresh durable administrator reauthentication was required.");
                return Results.Conflict(new
                {
                    code = "administrator-reauthentication-required",
                    error = "Fresh administrator reauthentication is required."
                });
            }

            try
            {
                RemoteStationManagementValidator.ValidateStationId(stationId);
                AetherRemoteBootstrapDocument document =
                    await bootstrap.GetDocumentAsync(cancellationToken);
                RemoteStationAdministrationSnapshot snapshot =
                    remoteStations.GetAdministrationSnapshot();
                RemoteStationAdministrationEntry? station =
                    snapshot.Stations.FirstOrDefault(candidate =>
                        string.Equals(
                            candidate.StationId,
                            stationId,
                            StringComparison.Ordinal));
                if (station is null)
                {
                    return Results.NotFound(
                        new { error = "That remote station is not connected." });
                }
                if (!station.Capabilities.Contains(
                        "release-update-v1",
                        StringComparer.Ordinal))
                {
                    return Results.Conflict(new
                    {
                        code = "station-release-update-unavailable",
                        error = "That station has not granted signed release updates."
                    });
                }
                if (string.Equals(
                        station.ReleaseIdentity,
                        document.ReleaseIdentity,
                        StringComparison.Ordinal))
                {
                    return Results.Ok(new
                    {
                        stationId,
                        releaseIdentity = document.ReleaseIdentity,
                        succeeded = true,
                        outcome = "already-current",
                        activeReleaseIdentity = station.ReleaseIdentity,
                        rolledBack = false
                    });
                }

                RemoteReleaseUpdateResult result =
                    await remoteStations.UpdateReleaseAsync(
                        new RemoteReleaseUpdateRequest(
                            stationId,
                            document.ReleaseIdentity),
                        cancellationToken);
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateStationRelease,
                    stationTarget,
                    document.ReleaseIdentity,
                    result.Succeeded
                        ? AdministrativeAuditResults.Succeeded
                        : AdministrativeAuditResults.Failed,
                    result.Succeeded
                        ? $"Station release updated to {result.ActiveReleaseIdentity}."
                        : result.RolledBack
                            ? $"Station update failed and rolled back to {result.ActiveReleaseIdentity}."
                            : $"Station update failed closed: {result.Outcome}.");
                return result.Succeeded
                    ? Results.Ok(result)
                    : Results.Conflict(new
                    {
                        code = "station-release-update-failed",
                        error = result.RolledBack
                            ? "The station rejected the new release and rolled back safely."
                            : "The station could not apply the requested signed release.",
                        result
                    });
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateStationRelease,
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
                    AdministrativeAuditActions.UpdateStationRelease,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return RemoteStationManagementFailure(exception);
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or IOException or
                      InvalidDataException or UnauthorizedAccessException or
                      System.Security.SecurityException or HttpRequestException or
                      JsonException)
            {
                const string message =
                    "Signed station release update is temporarily unavailable.";
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateStationRelease,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    message);
                return Results.Json(
                    new { error = message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
    .RequireAuthorization(AetherPolicies.Admin)
    .RequireAetherAntiforgery();

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
    .RequireAuthorization(AetherPolicies.Admin)
    .RequireAetherAntiforgery();

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
    .RequireAuthorization(AetherPolicies.Admin)
    .RequireAetherAntiforgery();

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
        "/api/admin/radios/{radioId}/identity",
        (
            string radioId,
            UpdateRadioOnboardingRequest request,
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
                    AdministrativeAuditActions.UpdateRadioIdentity,
                    radioId,
                    null,
                    AdministrativeAuditResults.Failed,
                    "A stable administrator ID was not available.");
                return Results.BadRequest(
                    new { error = "A stable administrator ID is required." });
            }

            try
            {
                RadioOnboardingPolicySnapshot policy =
                    administration.UpdateLabel(
                        radioId,
                        request,
                        administratorId);
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioIdentity,
                    radioId,
                    null,
                    AdministrativeAuditResults.Succeeded,
                    "The stable radio label was updated.");
                return Results.Ok(policy);
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioIdentity,
                    radioId,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (KeyNotFoundException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioIdentity,
                    radioId,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.NotFound(new { error = exception.Message });
            }
        })
    .RequireAuthorization(AetherPolicies.Admin)
    .RequireAetherAntiforgery();

app.MapPost(
        "/api/admin/radios/{radioId}/transmit-policy",
        async (
            string radioId,
            UpdateRadioTransmitPolicyRequest request,
            ClaimsPrincipal user,
            RadioAdministrationService administration,
            RadioSessionRegistry sessions,
            AetherAdministratorAuthorityService authority,
            AdministrativeAuditStore audit,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            (string administratorId, string administratorName) =
                GetAdministrativeActor(user);
            try
            {
                AetherAdministratorAuthorityEvidence evidence =
                    await authority.RequireFreshAsync(
                        user,
                        cancellationToken);
                administratorId = evidence.UserId.ToString("D");
            }
            catch (AetherAdministratorReauthenticationRequiredException)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioTransmitPolicy,
                    radioId,
                    request.State,
                    AdministrativeAuditResults.Failed,
                    "Fresh durable administrator reauthentication was required.");
                return Results.Conflict(new
                {
                    code = "administrator-reauthentication-required",
                    error = "Fresh administrator reauthentication is required."
                });
            }

            try
            {
                if (!RadioTransmitPolicyStates.TryNormalize(
                        request.State,
                        out string requestedState) ||
                    requestedState == RadioTransmitPolicyStates
                        .PrerequisitesFailed)
                {
                    throw new ArgumentException(
                        "State must be receive-only, tx-eligible, or " +
                        "temporarily-disabled.",
                        nameof(request));
                }

                RadioOnboardingIdentity identity =
                    administration.GetOnboardingIdentity(radioId);
                RadioTransmitPreflightSnapshot? preflight = null;
                string appliedState = requestedState;
                if (requestedState == RadioTransmitPolicyStates.TxEligible)
                {
                    preflight = RadioTransmitOnboardingPreflight.Evaluate(
                        identity,
                        builder.Environment.ContentRootPath,
                        stationTxProductionActivationSettings,
                        radioSettings,
                        stationTxCommandTrustSettings,
                        stationTxCommandSigningSettings,
                        stationTxCommandEnvelopeCoordinatorSettings,
                        stationTxCommandTransportSettings,
                        stationTxEmergencyUnkeyTransportSettings,
                        independentTxWatchdogSettings,
                        timeProvider);
                    if (!preflight.Ready)
                    {
                        appliedState =
                            RadioTransmitPolicyStates.PrerequisitesFailed;
                    }
                }

                RadioOnboardingPolicySnapshot policy =
                    administration.UpdateTransmitPolicy(
                        identity,
                        appliedState,
                        administratorId,
                        preflight);
                bool transmitEligible = string.Equals(
                    policy.TransmitPolicyState,
                    RadioTransmitPolicyStates.TxEligible,
                    StringComparison.Ordinal);
                RadioTransmitPolicyApplicationResult application =
                    await sessions.ApplyTransmitPolicyAsync(
                        radioId,
                        transmitEligible,
                        cancellationToken);
                bool prerequisitesFailed =
                    requestedState == RadioTransmitPolicyStates.TxEligible &&
                    !transmitEligible;
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioTransmitPolicy,
                    radioId,
                    identity.SourceRadioId,
                    prerequisitesFailed
                        ? AdministrativeAuditResults.Failed
                        : AdministrativeAuditResults.Succeeded,
                    prerequisitesFailed
                        ? $"TX eligibility was rejected: " +
                          $"{policy.TransmitPreflight?.Reason ?? "unknown"}."
                        : $"Transmit policy changed to " +
                          $"{policy.TransmitPolicyState}; " +
                          $"{application.RevokedLeases} lease(s) revoked.");
                return prerequisitesFailed
                    ? Results.Conflict(new
                    {
                        code = "radio-transmit-prerequisites-failed",
                        error = "Exact-radio transmit prerequisites failed: " +
                            $"{policy.TransmitPreflight?.Reason ?? "unknown"}.",
                        policy,
                        application
                    })
                    : Results.Ok(new { policy, application });
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioTransmitPolicy,
                    radioId,
                    request.State,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (KeyNotFoundException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioTransmitPolicy,
                    radioId,
                    request.State,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.NotFound(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioTransmitPolicy,
                    radioId,
                    request.State,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.Conflict(new { error = exception.Message });
            }
        })
    .RequireAuthorization(AetherPolicies.Admin)
    .RequireAetherAntiforgery();

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
    .RequireAuthorization(AetherPolicies.Admin)
    .RequireAetherAntiforgery();

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
    .RequireAuthorization(AetherPolicies.Admin)
    .RequireAetherAntiforgery();

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
