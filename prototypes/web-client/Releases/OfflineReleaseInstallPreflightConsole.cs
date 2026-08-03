using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum OfflineReleaseInstallPreflightCommandKind
{
    None = 0,
    Preflight = 1
}

public sealed record OfflineReleaseInstallPreflightCommandLine(
    OfflineReleaseInstallPreflightCommandKind Command,
    string BundleDirectory,
    string InstalledReleaseIdentity,
    string InstalledVersion,
    int? ConfigurationSchemaVersion,
    int? ProtocolVersion,
    IReadOnlyList<string> ApplicationArguments)
{
    public static OfflineReleaseInstallPreflightCommandLine None(
        IReadOnlyList<string> applicationArguments) =>
        new(
            OfflineReleaseInstallPreflightCommandKind.None,
            string.Empty,
            string.Empty,
            string.Empty,
            ConfigurationSchemaVersion: null,
            ProtocolVersion: null,
            applicationArguments);
}

public static class OfflineReleaseInstallPreflightCommandParser
{
    public const string PreflightSwitch =
        "--preflight-offline-release-install";
    public const string InstalledIdentitySwitch =
        "--release-preflight-installed-identity";
    public const string InstalledVersionSwitch =
        "--release-preflight-installed-version";
    public const string ConfigurationSchemaVersionSwitch =
        "--release-preflight-configuration-schema-version";
    public const string ProtocolVersionSwitch =
        "--release-preflight-protocol-version";

    private const int MaximumCompatibilityVersion = 1_000_000;

    public static OfflineReleaseInstallPreflightCommandLine Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        OfflineReleaseInstallPreflightCommandKind command =
            OfflineReleaseInstallPreflightCommandKind.None;
        string bundleDirectory = string.Empty;
        string installedIdentity = string.Empty;
        string installedVersion = string.Empty;
        int? configurationSchemaVersion = null;
        int? protocolVersion = null;
        bool installedIdentitySeen = false;
        bool installedVersionSeen = false;
        bool configurationSchemaVersionSeen = false;
        bool protocolVersionSeen = false;
        List<string> applicationArguments = [];

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case PreflightSwitch:
                    SetCommand(ref command);
                    bundleDirectory = ValidateBundleDirectory(
                        RequireValue(arguments, ref index, argument));
                    break;
                case InstalledIdentitySwitch:
                    RejectDuplicate(
                        ref installedIdentitySeen,
                        "The release preflight installed identity was supplied more than once.");
                    installedIdentity = ValidateReleaseIdentity(
                        RequireValue(arguments, ref index, argument));
                    break;
                case InstalledVersionSwitch:
                    RejectDuplicate(
                        ref installedVersionSeen,
                        "The release preflight installed version was supplied more than once.");
                    installedVersion = ValidateSemanticVersion(
                        RequireValue(arguments, ref index, argument));
                    break;
                case ConfigurationSchemaVersionSwitch:
                    RejectDuplicate(
                        ref configurationSchemaVersionSeen,
                        "The release preflight configuration schema version was supplied more than once.");
                    configurationSchemaVersion = ParsePositiveVersion(
                        RequireValue(arguments, ref index, argument),
                        "configuration schema version");
                    break;
                case ProtocolVersionSwitch:
                    RejectDuplicate(
                        ref protocolVersionSeen,
                        "The release preflight protocol version was supplied more than once.");
                    protocolVersion = ParsePositiveVersion(
                        RequireValue(arguments, ref index, argument),
                        "protocol version");
                    break;
                default:
                    applicationArguments.Add(argument);
                    break;
            }
        }

        bool hasPreflightOption =
            installedIdentitySeen ||
            installedVersionSeen ||
            configurationSchemaVersionSeen ||
            protocolVersionSeen;
        if (command == OfflineReleaseInstallPreflightCommandKind.None)
        {
            if (hasPreflightOption)
            {
                throw new InvalidOperationException(
                    "Release install preflight options require --preflight-offline-release-install.");
            }
            return OfflineReleaseInstallPreflightCommandLine.None(
                [.. applicationArguments]);
        }

        if (!installedIdentitySeen)
        {
            throw Missing(InstalledIdentitySwitch);
        }
        if (!installedVersionSeen)
        {
            throw Missing(InstalledVersionSwitch);
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

        return new OfflineReleaseInstallPreflightCommandLine(
            command,
            bundleDirectory,
            installedIdentity,
            installedVersion,
            configurationSchemaVersion,
            protocolVersion,
            [.. applicationArguments]);
    }

    private static void SetCommand(
        ref OfflineReleaseInstallPreflightCommandKind command)
    {
        if (command != OfflineReleaseInstallPreflightCommandKind.None)
        {
            throw new InvalidOperationException(
                "The offline release install preflight was requested more than once.");
        }
        command = OfflineReleaseInstallPreflightCommandKind.Preflight;
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
                "The offline release install preflight requires one canonical absolute bundle path.");
        }

        string fullPath = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(path, fullPath, comparison))
        {
            throw new InvalidOperationException(
                "The offline release install preflight bundle path must not contain relative segments.");
        }
        return fullPath;
    }

    private static string ValidateReleaseIdentity(string value)
    {
        string identity = InstallationReleaseIdentity.Parse(value);
        if (!string.Equals(identity, value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The release preflight installed identity must be canonical.");
        }
        return identity;
    }

    private static string ValidateSemanticVersion(string? value)
    {
        if (!ReleaseSemanticVersion.TryParse(
                value,
                out ReleaseSemanticVersion parsed))
        {
            throw new InvalidOperationException(
                "The release preflight installed version must be one canonical semantic version.");
        }

        string installedVersion = value ?? string.Empty;
        string canonical =
            $"{parsed.Major}.{parsed.Minor}.{parsed.Patch}" +
            (parsed.Prerelease.Length == 0
                ? string.Empty
                : $"-{parsed.Prerelease}") +
            (parsed.BuildMetadata.Length == 0
                ? string.Empty
                : $"+{parsed.BuildMetadata}");
        if (!string.Equals(installedVersion, canonical, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The release preflight installed version must be one canonical semantic version.");
        }
        return installedVersion;
    }

    private static int ParsePositiveVersion(string value, string name)
    {
        if (!int.TryParse(value, out int parsed) ||
            parsed is < 1 or > MaximumCompatibilityVersion ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The release preflight {name} must be a canonical positive integer.");
        }
        return parsed;
    }

    private static InvalidOperationException Missing(string option) =>
        new($"The offline release install preflight requires {option}.");
}

public enum OfflineReleaseInstallPreflightFailureCode
{
    None = 0,
    StatusUnavailable = 1,
    SetupIncomplete = 2,
    CurrentReleaseMissing = 3,
    InstalledReleaseMismatch = 4,
    BundleVerificationFailed = 5,
    InvalidTargetRelease = 6,
    TargetReleaseAlreadyPresent = 7,
    TxSupportMismatch = 8,
    StatusChangedDuringPreflight = 9
}

public sealed record OfflineReleaseInstallPreflightResult(
    bool Succeeded,
    OfflineReleaseInstallPreflightFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    ReleaseStatusFailureCode? StatusFailureCode,
    LocalOfflineReleaseBundleFailureCode? BundleFailureCode,
    ReleaseManifestFailureCode? ManifestFailureCode,
    InstallationUpdateChannel? UpdateChannel,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    string TargetVersion,
    ReleaseManifestArchitecture? Architecture,
    int PackageCount,
    long TotalPackageBytes,
    bool SetupInstallTransmitSupport,
    bool TargetTxSupportCapable,
    bool CurrentPointerVerified,
    bool TargetAbsentFromInventory,
    bool StatusStable)
{
    internal VerifiedReleaseManifestSnapshot? VerifiedManifest { get; init; }

    internal static OfflineReleaseInstallPreflightResult Failure(
        OfflineReleaseInstallPreflightFailureCode failureCode,
        string message,
        string installedReleaseIdentity,
        ReleaseStatusReadResult? status = null,
        LocalOfflineReleaseBundleVerificationReport? bundle = null,
        bool currentPointerVerified = false,
        bool targetAbsentFromInventory = false,
        bool statusStable = false) =>
        new(
            false,
            failureCode,
            message,
            status?.SetupRevision,
            status?.Succeeded == false ? status.FailureCode : null,
            bundle?.FailureCode,
            bundle?.Verification?.FailureCode,
            status?.UpdateChannel,
            installedReleaseIdentity,
            bundle?.Verification?.ReleaseIdentity ?? string.Empty,
            bundle?.Verification?.Version ?? string.Empty,
            bundle?.Verification?.Architecture,
            bundle?.PackageCount ?? 0,
            bundle?.TotalPackageBytes ?? 0,
            status?.InstallTransmitSupport ?? false,
            bundle?.Verification?.TxSupportCapable ?? false,
            currentPointerVerified,
            targetAbsentFromInventory,
            statusStable);

    internal static OfflineReleaseInstallPreflightResult Success(
        string installedReleaseIdentity,
        ReleaseStatusReadResult status,
        LocalOfflineReleaseBundleVerificationResult bundleResult)
    {
        LocalOfflineReleaseBundleVerificationReport bundle = bundleResult.Report;
        return new OfflineReleaseInstallPreflightResult(
            true,
            OfflineReleaseInstallPreflightFailureCode.None,
            "The verified offline release bundle is eligible for a future reviewed installation transaction.",
            status.SetupRevision,
            StatusFailureCode: null,
            BundleFailureCode: null,
            ManifestFailureCode: null,
            status.UpdateChannel,
            installedReleaseIdentity,
            bundle.Verification!.ReleaseIdentity,
            bundle.Verification.Version,
            bundle.Verification.Architecture,
            bundle.PackageCount,
            bundle.TotalPackageBytes,
            status.InstallTransmitSupport,
            bundle.Verification.TxSupportCapable,
            CurrentPointerVerified: true,
            TargetAbsentFromInventory: true,
            StatusStable: true)
        {
            VerifiedManifest = bundleResult.VerifiedManifest
        };
    }
}

public sealed record OfflineReleaseInstallPreflightConsoleDiagnostics(
    bool Registered,
    bool SetupStateReadRegistered,
    bool ReleaseInventoryReadRegistered,
    bool CurrentPointerReadRegistered,
    bool SignedBundleVerificationRegistered,
    bool NetworkDownloadRegistered,
    bool ArchiveExtractionRegistered,
    bool StagingRegistered,
    bool InstallationRegistered,
    bool ActivationRegistered,
    bool RollbackRegistered,
    bool MigrationExecutionRegistered,
    bool ServiceControlRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record OfflineReleaseInstallPreflightConsoleReport(
    int ReportVersion,
    string Command,
    bool Succeeded,
    int ExitCode,
    OfflineReleaseInstallPreflightFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    ReleaseStatusFailureCode? StatusFailureCode,
    LocalOfflineReleaseBundleFailureCode? BundleFailureCode,
    ReleaseManifestFailureCode? ManifestFailureCode,
    InstallationUpdateChannel? UpdateChannel,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    string TargetVersion,
    ReleaseManifestArchitecture? Architecture,
    int PackageCount,
    long TotalPackageBytes,
    bool SetupInstallTransmitSupport,
    bool TargetTxSupportCapable,
    bool CurrentPointerVerified,
    bool TargetAbsentFromInventory,
    bool StatusStable);

/// <summary>
/// Produces a read-only, path-redacted eligibility decision for one verified
/// offline release bundle. It owns no network, extraction, write, staging,
/// installation, activation, rollback, migration execution, service, Admin,
/// browser, radio, watchdog, command, lease, or transmit operation.
/// </summary>
public sealed class OfflineReleaseInstallPreflightPlanner
{
    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;
    private readonly Func<
        string,
        ReleaseManifestVerificationContext,
        LocalOfflineReleaseBundleVerificationResult> m_bundleVerifier;
    private readonly Func<ReleaseManifestArchitecture> m_architectureResolver;

    public OfflineReleaseInstallPreflightPlanner(
        ReleaseInstallationStatusReader statusReader,
        LocalOfflineReleaseBundleVerificationService bundleVerificationService)
        : this(
            CreateStatusReader(statusReader),
            CreateBundleVerifier(bundleVerificationService),
            ResolveCurrentArchitecture)
    {
    }

    internal OfflineReleaseInstallPreflightPlanner(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Func<
            string,
            ReleaseManifestVerificationContext,
            LocalOfflineReleaseBundleVerificationResult> bundleVerifier,
        Func<ReleaseManifestArchitecture> architectureResolver)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_bundleVerifier = bundleVerifier ??
            throw new ArgumentNullException(nameof(bundleVerifier));
        m_architectureResolver = architectureResolver ??
            throw new ArgumentNullException(nameof(architectureResolver));
    }

    public async Task<OfflineReleaseInstallPreflightResult> CreateAsync(
        OfflineReleaseInstallPreflightCommandLine commandLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        cancellationToken.ThrowIfCancellationRequested();
        if (commandLine.Command !=
            OfflineReleaseInstallPreflightCommandKind.Preflight)
        {
            throw new InvalidOperationException(
                "The offline release install preflight planner requires its exact command.");
        }

        ReleaseStatusReadResult firstStatus =
            await m_statusReader(cancellationToken);
        OfflineReleaseInstallPreflightResult? statusFailure =
            ValidateStatus(firstStatus, commandLine.InstalledReleaseIdentity);
        if (statusFailure is not null)
        {
            return statusFailure;
        }

        ReleaseManifestVerificationContext context = new(
            m_architectureResolver(),
            firstStatus.UpdateChannel!.Value,
            firstStatus.PinnedReleaseIdentity,
            commandLine.InstalledVersion,
            commandLine.ConfigurationSchemaVersion ??
                throw new InvalidOperationException(
                    "The offline release install preflight requires a configuration schema version."),
            commandLine.ProtocolVersion ??
                throw new InvalidOperationException(
                    "The offline release install preflight requires a protocol version."));

        LocalOfflineReleaseBundleVerificationResult bundleResult =
            m_bundleVerifier(
                commandLine.BundleDirectory,
                context);
        LocalOfflineReleaseBundleVerificationReport bundle = bundleResult.Report;
        if (!bundle.Succeeded)
        {
            return OfflineReleaseInstallPreflightResult.Failure(
                OfflineReleaseInstallPreflightFailureCode.BundleVerificationFailed,
                "The offline release bundle did not pass signed installation preflight verification.",
                commandLine.InstalledReleaseIdentity,
                firstStatus,
                bundle,
                currentPointerVerified: true);
        }

        string targetIdentity = bundle.Verification!.ReleaseIdentity;
        ReleaseStatusReadResult secondStatus =
            await m_statusReader(cancellationToken);
        if (!Equivalent(firstStatus, secondStatus))
        {
            return OfflineReleaseInstallPreflightResult.Failure(
                OfflineReleaseInstallPreflightFailureCode.StatusChangedDuringPreflight,
                "Local installation status changed while the offline release preflight was running.",
                commandLine.InstalledReleaseIdentity,
                secondStatus,
                bundle,
                currentPointerVerified: secondStatus.Succeeded &&
                    secondStatus.CurrentPointerPresent,
                targetAbsentFromInventory: secondStatus.Succeeded &&
                    !secondStatus.AvailableReleaseIdentities.Contains(
                        targetIdentity,
                        StringComparer.Ordinal));
        }
        if (string.Equals(
                targetIdentity,
                commandLine.InstalledReleaseIdentity,
                StringComparison.Ordinal))
        {
            return OfflineReleaseInstallPreflightResult.Failure(
                OfflineReleaseInstallPreflightFailureCode.InvalidTargetRelease,
                "The verified target release must differ from the active release.",
                commandLine.InstalledReleaseIdentity,
                secondStatus,
                bundle,
                currentPointerVerified: true,
                statusStable: true);
        }
        if (secondStatus.AvailableReleaseIdentities.Contains(
                targetIdentity,
                StringComparer.Ordinal))
        {
            return OfflineReleaseInstallPreflightResult.Failure(
                OfflineReleaseInstallPreflightFailureCode.TargetReleaseAlreadyPresent,
                "The verified target release is already present in the immutable release inventory.",
                commandLine.InstalledReleaseIdentity,
                secondStatus,
                bundle,
                currentPointerVerified: true,
                statusStable: true);
        }
        if (bundle.Verification.TxSupportCapable !=
            secondStatus.InstallTransmitSupport)
        {
            return OfflineReleaseInstallPreflightResult.Failure(
                OfflineReleaseInstallPreflightFailureCode.TxSupportMismatch,
                "The verified target release TX-support capability does not match completed installation policy.",
                commandLine.InstalledReleaseIdentity,
                secondStatus,
                bundle,
                currentPointerVerified: true,
                targetAbsentFromInventory: true,
                statusStable: true);
        }

        return OfflineReleaseInstallPreflightResult.Success(
            commandLine.InstalledReleaseIdentity,
            secondStatus,
            bundleResult);
    }

    private static OfflineReleaseInstallPreflightResult? ValidateStatus(
        ReleaseStatusReadResult status,
        string installedIdentity)
    {
        if (!status.Succeeded)
        {
            return OfflineReleaseInstallPreflightResult.Failure(
                OfflineReleaseInstallPreflightFailureCode.StatusUnavailable,
                "Local installation status is unavailable for offline release preflight.",
                installedIdentity,
                status);
        }
        if (!status.SetupComplete ||
            status.SetupLockMode != InstallationSetupLockMode.Complete ||
            status.LastCompletedStep != InstallationSetupStep.Administrator ||
            status.UpdateChannel is null)
        {
            return OfflineReleaseInstallPreflightResult.Failure(
                OfflineReleaseInstallPreflightFailureCode.SetupIncomplete,
                "Completed installation setup is required before release installation preflight.",
                installedIdentity,
                status);
        }
        if (!status.CurrentPointerPresent ||
            string.IsNullOrEmpty(status.ActiveReleaseIdentity))
        {
            return OfflineReleaseInstallPreflightResult.Failure(
                OfflineReleaseInstallPreflightFailureCode.CurrentReleaseMissing,
                "An active immutable release is required before update preflight.",
                installedIdentity,
                status);
        }
        if (!string.Equals(
                status.ActiveReleaseIdentity,
                installedIdentity,
                StringComparison.Ordinal))
        {
            return OfflineReleaseInstallPreflightResult.Failure(
                OfflineReleaseInstallPreflightFailureCode.InstalledReleaseMismatch,
                "The supplied installed release identity does not match the validated current pointer.",
                installedIdentity,
                status,
                currentPointerVerified: true);
        }
        return null;
    }

    private static bool Equivalent(
        ReleaseStatusReadResult first,
        ReleaseStatusReadResult second) =>
        first.Succeeded &&
        second.Succeeded &&
        first.SetupSchemaVersion == second.SetupSchemaVersion &&
        first.SetupRevision == second.SetupRevision &&
        first.SetupComplete == second.SetupComplete &&
        first.SetupLockMode == second.SetupLockMode &&
        first.LastCompletedStep == second.LastCompletedStep &&
        first.UpdateChannel == second.UpdateChannel &&
        string.Equals(
            first.PinnedReleaseIdentity,
            second.PinnedReleaseIdentity,
            StringComparison.Ordinal) &&
        first.InstallTransmitSupport == second.InstallTransmitSupport &&
        first.ReleaseDirectoryPresent == second.ReleaseDirectoryPresent &&
        first.CurrentPointerPresent == second.CurrentPointerPresent &&
        string.Equals(
            first.ActiveReleaseIdentity,
            second.ActiveReleaseIdentity,
            StringComparison.Ordinal) &&
        first.AvailableReleaseIdentities.SequenceEqual(
            second.AvailableReleaseIdentities,
            StringComparer.Ordinal);

    private static Func<CancellationToken, Task<ReleaseStatusReadResult>>
        CreateStatusReader(ReleaseInstallationStatusReader statusReader)
    {
        ArgumentNullException.ThrowIfNull(statusReader);
        return statusReader.ReadAsync;
    }

    private static Func<
        string,
        ReleaseManifestVerificationContext,
        LocalOfflineReleaseBundleVerificationResult> CreateBundleVerifier(
            LocalOfflineReleaseBundleVerificationService bundleVerificationService)
    {
        ArgumentNullException.ThrowIfNull(bundleVerificationService);
        return bundleVerificationService.VerifyDirectoryDetailed;
    }

    private static ReleaseManifestArchitecture ResolveCurrentArchitecture()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException(
                "Offline release install preflight requires a supported Linux runtime.");
        }
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => ReleaseManifestArchitecture.LinuxX64,
            Architecture.Arm64 => ReleaseManifestArchitecture.LinuxArm64,
            _ => throw new InvalidOperationException(
                "The current runtime architecture is unsupported for release preflight.")
        };
    }
}

public sealed class OfflineReleaseInstallPreflightConsole
{
    public const int SuccessExitCode = 0;
    public const int PreflightFailedExitCode = 2;

    private const int CurrentReportVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly OfflineReleaseInstallPreflightPlanner m_planner;

    public OfflineReleaseInstallPreflightConsole(
        OfflineReleaseInstallPreflightPlanner planner)
    {
        m_planner = planner ?? throw new ArgumentNullException(nameof(planner));
        Snapshot = new OfflineReleaseInstallPreflightConsoleDiagnostics(
            Registered: true,
            SetupStateReadRegistered: true,
            ReleaseInventoryReadRegistered: true,
            CurrentPointerReadRegistered: true,
            SignedBundleVerificationRegistered: true,
            NetworkDownloadRegistered: false,
            ArchiveExtractionRegistered: false,
            StagingRegistered: false,
            InstallationRegistered: false,
            ActivationRegistered: false,
            RollbackRegistered: false,
            MigrationExecutionRegistered: false,
            ServiceControlRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public OfflineReleaseInstallPreflightConsoleDiagnostics Snapshot { get; }

    public async Task<int> ExecuteAsync(
        OfflineReleaseInstallPreflightCommandLine commandLine,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(output);

        OfflineReleaseInstallPreflightResult result =
            await m_planner.CreateAsync(commandLine, cancellationToken);
        int exitCode = result.Succeeded
            ? SuccessExitCode
            : PreflightFailedExitCode;
        OfflineReleaseInstallPreflightConsoleReport report = new(
            CurrentReportVersion,
            "preflight-offline-release-install",
            result.Succeeded,
            exitCode,
            result.FailureCode,
            result.Message,
            result.SetupRevision,
            result.StatusFailureCode,
            result.BundleFailureCode,
            result.ManifestFailureCode,
            result.UpdateChannel,
            result.InstalledReleaseIdentity,
            result.TargetReleaseIdentity,
            result.TargetVersion,
            result.Architecture,
            result.PackageCount,
            result.TotalPackageBytes,
            result.SetupInstallTransmitSupport,
            result.TargetTxSupportCapable,
            result.CurrentPointerVerified,
            result.TargetAbsentFromInventory,
            result.StatusStable);
        await output.WriteLineAsync(
            JsonSerializer.Serialize(report, JsonOptions));
        return exitCode;
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
