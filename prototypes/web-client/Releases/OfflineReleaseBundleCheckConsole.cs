using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum ReleaseUpdateConsoleCommandKind
{
    None = 0,
    CheckOfflineBundle = 1,
    CheckGitHubRelease = 2,
    Status = 3
}

public sealed record ReleaseUpdateConsoleCommandLine(
    ReleaseUpdateConsoleCommandKind Command,
    string BundleDirectory,
    string InstalledVersion,
    InstallationUpdateChannel? UpdateChannel,
    string PinnedReleaseIdentity,
    int? ConfigurationSchemaVersion,
    int? ProtocolVersion,
    IReadOnlyList<string> ApplicationArguments)
{
    public static ReleaseUpdateConsoleCommandLine None(
        IReadOnlyList<string> applicationArguments) =>
        new(
            ReleaseUpdateConsoleCommandKind.None,
            string.Empty,
            string.Empty,
            UpdateChannel: null,
            string.Empty,
            ConfigurationSchemaVersion: null,
            ProtocolVersion: null,
            applicationArguments);
}

public static class ReleaseUpdateConsoleCommandParser
{
    public const string CheckOfflineBundleSwitch =
        "--check-offline-release-bundle";
    public const string CheckGitHubReleaseSwitch =
        "--check-github-release";
    public const string StatusSwitch = "--release-status";
    public const string InstalledVersionSwitch =
        "--release-check-installed-version";
    public const string UpdateChannelSwitch =
        "--release-check-update-channel";
    public const string PinnedReleaseIdentitySwitch =
        "--release-check-pinned-identity";
    public const string ConfigurationSchemaVersionSwitch =
        "--release-check-configuration-schema-version";
    public const string ProtocolVersionSwitch =
        "--release-check-protocol-version";

    private const int MaximumCompatibilityVersion = 1_000_000;

    public static ReleaseUpdateConsoleCommandLine Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        ReleaseUpdateConsoleCommandKind command =
            ReleaseUpdateConsoleCommandKind.None;
        string bundleDirectory = string.Empty;
        string installedVersion = string.Empty;
        InstallationUpdateChannel? updateChannel = null;
        string pinnedReleaseIdentity = string.Empty;
        int? configurationSchemaVersion = null;
        int? protocolVersion = null;
        bool installedVersionSeen = false;
        bool updateChannelSeen = false;
        bool pinnedIdentitySeen = false;
        bool configurationSchemaVersionSeen = false;
        bool protocolVersionSeen = false;
        List<string> applicationArguments = [];

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case CheckOfflineBundleSwitch:
                    SetCommand(
                        ref command,
                        ReleaseUpdateConsoleCommandKind.CheckOfflineBundle);
                    bundleDirectory = ValidateBundleDirectory(
                        RequireValue(arguments, ref index, argument));
                    break;
                case CheckGitHubReleaseSwitch:
                    SetCommand(
                        ref command,
                        ReleaseUpdateConsoleCommandKind.CheckGitHubRelease);
                    break;
                case StatusSwitch:
                    SetCommand(
                        ref command,
                        ReleaseUpdateConsoleCommandKind.Status);
                    break;
                case InstalledVersionSwitch:
                    RejectDuplicate(
                        ref installedVersionSeen,
                        "The release check installed version was supplied more than once.");
                    installedVersion = ValidateInstalledVersion(
                        RequireValue(arguments, ref index, argument));
                    break;
                case UpdateChannelSwitch:
                    RejectDuplicate(
                        ref updateChannelSeen,
                        "The release check update channel was supplied more than once.");
                    updateChannel = ParseUpdateChannel(
                        RequireValue(arguments, ref index, argument));
                    break;
                case PinnedReleaseIdentitySwitch:
                    RejectDuplicate(
                        ref pinnedIdentitySeen,
                        "The release check pinned identity was supplied more than once.");
                    string pinnedValue =
                        RequireValue(arguments, ref index, argument);
                    pinnedReleaseIdentity =
                        InstallationReleaseIdentity.Parse(pinnedValue);
                    if (!string.Equals(
                            pinnedValue,
                            pinnedReleaseIdentity,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The release check pinned identity must be canonical.");
                    }
                    break;
                case ConfigurationSchemaVersionSwitch:
                    RejectDuplicate(
                        ref configurationSchemaVersionSeen,
                        "The release check configuration schema version was supplied more than once.");
                    configurationSchemaVersion = ParsePositiveVersion(
                        RequireValue(arguments, ref index, argument),
                        "configuration schema version");
                    break;
                case ProtocolVersionSwitch:
                    RejectDuplicate(
                        ref protocolVersionSeen,
                        "The release check protocol version was supplied more than once.");
                    protocolVersion = ParsePositiveVersion(
                        RequireValue(arguments, ref index, argument),
                        "protocol version");
                    break;
                default:
                    applicationArguments.Add(argument);
                    break;
            }
        }

        bool hasReleaseOption =
            installedVersionSeen ||
            updateChannelSeen ||
            pinnedIdentitySeen ||
            configurationSchemaVersionSeen ||
            protocolVersionSeen;
        if (command == ReleaseUpdateConsoleCommandKind.None)
        {
            if (hasReleaseOption)
            {
                throw new InvalidOperationException(
                    "Release check options require --check-offline-release-bundle or --check-github-release.");
            }
            return ReleaseUpdateConsoleCommandLine.None(
                [.. applicationArguments]);
        }
        if (command == ReleaseUpdateConsoleCommandKind.Status)
        {
            if (hasReleaseOption)
            {
                throw new InvalidOperationException(
                    "Release check options cannot run with --release-status.");
            }
            return new ReleaseUpdateConsoleCommandLine(
                command,
                string.Empty,
                string.Empty,
                UpdateChannel: null,
                string.Empty,
                ConfigurationSchemaVersion: null,
                ProtocolVersion: null,
                [.. applicationArguments]);
        }

        if (!installedVersionSeen)
        {
            throw Missing(InstalledVersionSwitch);
        }
        if (!updateChannelSeen || updateChannel is null)
        {
            throw Missing(UpdateChannelSwitch);
        }
        if (!configurationSchemaVersionSeen ||
            configurationSchemaVersion is null)
        {
            throw Missing(ConfigurationSchemaVersionSwitch);
        }
        if (!protocolVersionSeen || protocolVersion is null)
        {
            throw Missing(ProtocolVersionSwitch);
        }

        if (updateChannel == InstallationUpdateChannel.Pinned)
        {
            if (!pinnedIdentitySeen)
            {
                throw Missing(PinnedReleaseIdentitySwitch);
            }
        }
        else if (pinnedIdentitySeen)
        {
            throw new InvalidOperationException(
                "Only the pinned release check channel may include a pinned release identity.");
        }

        return new ReleaseUpdateConsoleCommandLine(
            command,
            bundleDirectory,
            installedVersion,
            updateChannel,
            pinnedReleaseIdentity,
            configurationSchemaVersion,
            protocolVersion,
            [.. applicationArguments]);
    }

    private static void SetCommand(
        ref ReleaseUpdateConsoleCommandKind command,
        ReleaseUpdateConsoleCommandKind candidate)
    {
        if (command != ReleaseUpdateConsoleCommandKind.None)
        {
            throw new InvalidOperationException(
                "Only one release update console command may be requested.");
        }
        command = candidate;
    }

    private static string RequireValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string argument)
    {
        if (index + 1 >= arguments.Count ||
            arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{argument} requires one value.");
        }
        return arguments[++index];
    }

    private static void RejectDuplicate(ref bool seen, string message)
    {
        if (seen)
        {
            throw new InvalidOperationException(message);
        }
        seen = true;
    }

    private static string ValidateBundleDirectory(string? value)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length is 0 or >
                LocalOfflineReleaseBundleVerificationService.MaximumBundlePathLength ||
            !string.Equals(path, value, StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                "The offline release bundle check requires one canonical absolute directory path.");
        }

        string fullPath = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(path, fullPath, comparison))
        {
            throw new InvalidOperationException(
                "The offline release bundle check path must not contain relative segments.");
        }
        return fullPath;
    }

    private static string ValidateInstalledVersion(string? value)
    {
        if (!ReleaseSemanticVersion.TryParse(
                value,
                out ReleaseSemanticVersion parsed))
        {
            throw new InvalidOperationException(
                "The release check installed version must be one canonical semantic version.");
        }

        string installedVersion = value ?? string.Empty;
        if (!string.Equals(
                installedVersion,
                FormatSemanticVersion(parsed),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The release check installed version must be one canonical semantic version.");
        }
        return installedVersion;
    }

    private static string FormatSemanticVersion(ReleaseSemanticVersion version)
    {
        string value = $"{version.Major}.{version.Minor}.{version.Patch}";
        if (version.Prerelease.Length > 0)
        {
            value += $"-{version.Prerelease}";
        }
        if (version.BuildMetadata.Length > 0)
        {
            value += $"+{version.BuildMetadata}";
        }
        return value;
    }

    private static InstallationUpdateChannel ParseUpdateChannel(string value) =>
        value switch
        {
            "stable" => InstallationUpdateChannel.Stable,
            "beta" => InstallationUpdateChannel.Beta,
            "pinned" => InstallationUpdateChannel.Pinned,
            _ => throw new InvalidOperationException(
                "The release check update channel must be stable, beta, or pinned.")
        };

    private static int ParsePositiveVersion(string value, string name)
    {
        if (!int.TryParse(value, out int parsed) ||
            parsed is < 1 or > MaximumCompatibilityVersion ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The release check {name} must be a canonical positive integer.");
        }
        return parsed;
    }

    private static InvalidOperationException Missing(string option) =>
        new($"The release bundle check requires {option}.");
}

public sealed record OfflineReleaseBundleCheckConsoleDiagnostics(
    bool Registered,
    bool LocalDirectoryReadRegistered,
    bool NetworkDownloadRegistered,
    bool ArchiveExtractionRegistered,
    bool StagingRegistered,
    bool InstallationRegistered,
    bool ActivationRegistered,
    bool RollbackRegistered,
    bool MigrationRegistered,
    bool ServiceControlRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record OfflineReleaseBundleCheckConsoleReport(
    int ReportVersion,
    string Command,
    bool Succeeded,
    int ExitCode,
    LocalOfflineReleaseBundleFailureCode FailureCode,
    string Message,
    int PackageCount,
    long TotalPackageBytes,
    ReleaseManifestVerificationReport? Verification);

/// <summary>
/// Read-only CLI adapter for checking one immutable local offline release bundle.
/// It has no network, extraction, staging, installation, activation, rollback,
/// migration, service, Admin, browser, radio, watchdog, command, lease, or TX
/// method.
/// </summary>
public sealed class OfflineReleaseBundleCheckConsole
{
    public const int SuccessExitCode = 0;
    public const int VerificationFailedExitCode = 2;

    private const int CurrentReportVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly LocalOfflineReleaseBundleVerificationService
        m_bundleVerificationService;
    private readonly Func<ReleaseManifestArchitecture> m_architectureResolver;

    public OfflineReleaseBundleCheckConsole(
        LocalOfflineReleaseBundleVerificationService bundleVerificationService)
        : this(bundleVerificationService, ResolveCurrentArchitecture)
    {
    }

    internal OfflineReleaseBundleCheckConsole(
        LocalOfflineReleaseBundleVerificationService bundleVerificationService,
        Func<ReleaseManifestArchitecture> architectureResolver)
    {
        m_bundleVerificationService = bundleVerificationService ??
            throw new ArgumentNullException(nameof(bundleVerificationService));
        m_architectureResolver = architectureResolver ??
            throw new ArgumentNullException(nameof(architectureResolver));
        Snapshot = new OfflineReleaseBundleCheckConsoleDiagnostics(
            Registered: true,
            LocalDirectoryReadRegistered: true,
            NetworkDownloadRegistered: false,
            ArchiveExtractionRegistered: false,
            StagingRegistered: false,
            InstallationRegistered: false,
            ActivationRegistered: false,
            RollbackRegistered: false,
            MigrationRegistered: false,
            ServiceControlRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public OfflineReleaseBundleCheckConsoleDiagnostics Snapshot { get; }

    public async Task<int> ExecuteAsync(
        ReleaseUpdateConsoleCommandLine commandLine,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        if (commandLine.Command !=
            ReleaseUpdateConsoleCommandKind.CheckOfflineBundle)
        {
            throw new InvalidOperationException(
                "The offline release bundle check console requires its exact command.");
        }

        ReleaseManifestVerificationContext context = new(
            m_architectureResolver(),
            commandLine.UpdateChannel ??
                throw new InvalidOperationException(
                    "The offline release bundle check requires an update channel."),
            commandLine.PinnedReleaseIdentity,
            commandLine.InstalledVersion,
            commandLine.ConfigurationSchemaVersion ??
                throw new InvalidOperationException(
                    "The offline release bundle check requires a configuration schema version."),
            commandLine.ProtocolVersion ??
                throw new InvalidOperationException(
                    "The offline release bundle check requires a protocol version."));

        LocalOfflineReleaseBundleVerificationReport verification =
            m_bundleVerificationService.VerifyDirectory(
                commandLine.BundleDirectory,
                context);
        int exitCode = verification.Succeeded
            ? SuccessExitCode
            : VerificationFailedExitCode;
        OfflineReleaseBundleCheckConsoleReport report = new(
            CurrentReportVersion,
            "check-offline-release-bundle",
            verification.Succeeded,
            exitCode,
            verification.FailureCode,
            verification.Message,
            verification.PackageCount,
            verification.TotalPackageBytes,
            verification.Verification);
        await output.WriteLineAsync(
            JsonSerializer.Serialize(report, JsonOptions));
        return exitCode;
    }

    private static ReleaseManifestArchitecture ResolveCurrentArchitecture()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException(
                "Offline release bundle checks require a supported Linux runtime.");
        }
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => ReleaseManifestArchitecture.LinuxX64,
            Architecture.Arm64 => ReleaseManifestArchitecture.LinuxArm64,
            _ => throw new InvalidOperationException(
                "The current runtime architecture is unsupported for release checks.")
        };
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.Strict
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}
