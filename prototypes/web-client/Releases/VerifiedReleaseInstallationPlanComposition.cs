using System.Collections.ObjectModel;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseInstallationPlanFailureCode
{
    None = 0,
    PreflightNotEligible = 1,
    VerifiedManifestUnavailable = 2,
    PreflightManifestMismatch = 3,
    InvalidInstallationPaths = 4,
    InvalidPackagePlan = 5,
    VerifiedBundleUnavailable = 6
}

public sealed record VerifiedReleaseInstallationPlanCompositionResult(
    bool Succeeded,
    VerifiedReleaseInstallationPlanFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    string TargetVersion,
    ReleaseManifestArchitecture? Architecture,
    int PackageCount,
    int? TargetConfigurationSchemaVersion,
    ReleaseMigrationKind? MigrationKind,
    bool MigrationRequired,
    int RestartServiceCount,
    bool HostRestartRequired,
    bool TxSupportCapable,
    bool ImmutableTargetRequired,
    bool TemporaryStagingRequired,
    bool AtomicDirectoryPublishRequired,
    bool AtomicCurrentPointerSwitchRequired,
    bool StablePreflightRevalidationRequired)
{
    internal VerifiedReleaseInstallationPlan? Plan { get; init; }

    internal static VerifiedReleaseInstallationPlanCompositionResult Failure(
        VerifiedReleaseInstallationPlanFailureCode failureCode,
        string message,
        OfflineReleaseInstallPreflightResult? preflight = null) =>
        new(
            false,
            failureCode,
            message,
            preflight?.SetupRevision,
            preflight?.InstalledReleaseIdentity ?? string.Empty,
            preflight?.TargetReleaseIdentity ?? string.Empty,
            preflight?.TargetVersion ?? string.Empty,
            preflight?.Architecture,
            preflight?.PackageCount ?? 0,
            TargetConfigurationSchemaVersion: null,
            MigrationKind: null,
            MigrationRequired: false,
            RestartServiceCount: 0,
            HostRestartRequired: false,
            preflight?.TargetTxSupportCapable ?? false,
            ImmutableTargetRequired: true,
            TemporaryStagingRequired: true,
            AtomicDirectoryPublishRequired: true,
            AtomicCurrentPointerSwitchRequired: true,
            StablePreflightRevalidationRequired: true);

    internal static VerifiedReleaseInstallationPlanCompositionResult Success(
        OfflineReleaseInstallPreflightResult preflight,
        VerifiedReleaseInstallationPlan plan) =>
        new(
            true,
            VerifiedReleaseInstallationPlanFailureCode.None,
            "A verified immutable release installation plan was composed without performing installation work.",
            preflight.SetupRevision,
            preflight.InstalledReleaseIdentity,
            plan.TargetReleaseIdentity,
            plan.TargetVersion,
            plan.Architecture,
            plan.Packages.Count,
            plan.TargetConfigurationSchemaVersion,
            plan.MigrationKind,
            plan.MigrationKind == ReleaseMigrationKind.Required,
            plan.RestartServiceCount,
            plan.RestartHost,
            plan.TxSupportCapable,
            ImmutableTargetRequired: true,
            TemporaryStagingRequired: true,
            AtomicDirectoryPublishRequired: true,
            AtomicCurrentPointerSwitchRequired: true,
            StablePreflightRevalidationRequired: true)
        {
            Plan = plan
        };
}

public sealed record VerifiedReleaseInstallationPlanDiagnostics(
    bool Registered,
    bool VerifiedManifestInputRegistered,
    bool InstallationPathCompositionRegistered,
    bool NetworkDownloadRegistered,
    bool ArchiveExtractionRegistered,
    bool FileWriteRegistered,
    bool StagingExecutionRegistered,
    bool InstallationExecutionRegistered,
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

internal sealed class VerifiedReleaseInstallationPackagePlan
{
    private readonly byte[] m_sha256;

    internal VerifiedReleaseInstallationPackagePlan(
        VerifiedReleasePackageSnapshot package,
        string targetPath)
    {
        ArgumentNullException.ThrowIfNull(package);
        PackageIdentity = package.PackageIdentity;
        Role = package.Role;
        SourceRelativePath = package.RelativePath;
        TargetPath = targetPath;
        Length = package.Length;
        m_sha256 = package.Sha256.ToArray();
    }

    internal string PackageIdentity { get; }
    internal ReleasePackageRole Role { get; }
    internal string SourceRelativePath { get; }
    internal string TargetPath { get; }
    internal long Length { get; }
    internal ReadOnlySpan<byte> Sha256 => m_sha256;
}

internal sealed class VerifiedReleaseInstallationPlan
{
    private readonly ReadOnlyCollection<VerifiedReleaseInstallationPackagePlan>
        m_packages;
    private readonly byte[] m_manifestSha256;

    internal VerifiedReleaseInstallationPlan(
        long setupRevision,
        string installedReleaseIdentity,
        string targetReleaseIdentity,
        string targetVersion,
        ReleaseManifestArchitecture architecture,
        InstallationUpdateChannel updateChannel,
        string pinnedReleaseIdentity,
        bool installTransmitSupport,
        string bundleDirectory,
        long manifestLength,
        ReadOnlySpan<byte> manifestSha256,
        string releaseRootPath,
        string deploymentRootPath,
        string targetReleasePath,
        IReadOnlyList<VerifiedReleaseInstallationPackagePlan> packages,
        int targetConfigurationSchemaVersion,
        ReleaseMigrationKind migrationKind,
        int? migrationFromConfigurationSchemaVersion,
        int? migrationToConfigurationSchemaVersion,
        string migrationIdentity,
        bool restartGatewayWeb,
        bool restartBroker,
        bool restartAetherRemoteAgent,
        bool restartStationEngine,
        bool restartHost,
        bool txSupportCapable,
        string releaseNotesTitle,
        string releaseNotesSummary)
    {
        SetupRevision = setupRevision;
        InstalledReleaseIdentity = installedReleaseIdentity;
        TargetReleaseIdentity = targetReleaseIdentity;
        TargetVersion = targetVersion;
        if (manifestLength is < 1 or > SignedReleaseManifestJson.MaximumManifestBytes ||
            manifestSha256.Length != 32)
        {
            throw new ArgumentException(
                "A verified installation plan requires one bounded manifest digest.");
        }

        Architecture = architecture;
        UpdateChannel = updateChannel;
        PinnedReleaseIdentity = pinnedReleaseIdentity;
        InstallTransmitSupport = installTransmitSupport;
        BundleDirectory = bundleDirectory;
        ManifestLength = manifestLength;
        m_manifestSha256 = manifestSha256.ToArray();
        ReleaseRootPath = releaseRootPath;
        DeploymentRootPath = deploymentRootPath;
        TargetReleasePath = targetReleasePath;
        m_packages = Array.AsReadOnly(packages.ToArray());
        TargetConfigurationSchemaVersion = targetConfigurationSchemaVersion;
        MigrationKind = migrationKind;
        MigrationFromConfigurationSchemaVersion =
            migrationFromConfigurationSchemaVersion;
        MigrationToConfigurationSchemaVersion =
            migrationToConfigurationSchemaVersion;
        MigrationIdentity = migrationIdentity;
        RestartGatewayWeb = restartGatewayWeb;
        RestartBroker = restartBroker;
        RestartAetherRemoteAgent = restartAetherRemoteAgent;
        RestartStationEngine = restartStationEngine;
        RestartHost = restartHost;
        TxSupportCapable = txSupportCapable;
        ReleaseNotesTitle = releaseNotesTitle;
        ReleaseNotesSummary = releaseNotesSummary;
    }

    internal long SetupRevision { get; }
    internal string InstalledReleaseIdentity { get; }
    internal string TargetReleaseIdentity { get; }
    internal string TargetVersion { get; }
    internal ReleaseManifestArchitecture Architecture { get; }
    internal InstallationUpdateChannel UpdateChannel { get; }
    internal string PinnedReleaseIdentity { get; }
    internal bool InstallTransmitSupport { get; }
    internal string BundleDirectory { get; }
    internal long ManifestLength { get; }
    internal ReadOnlySpan<byte> ManifestSha256 => m_manifestSha256;
    internal string ReleaseRootPath { get; }
    internal string DeploymentRootPath { get; }
    internal string TargetReleasePath { get; }
    internal IReadOnlyList<VerifiedReleaseInstallationPackagePlan> Packages =>
        m_packages;
    internal int TargetConfigurationSchemaVersion { get; }
    internal ReleaseMigrationKind MigrationKind { get; }
    internal int? MigrationFromConfigurationSchemaVersion { get; }
    internal int? MigrationToConfigurationSchemaVersion { get; }
    internal string MigrationIdentity { get; }
    internal bool RestartGatewayWeb { get; }
    internal bool RestartBroker { get; }
    internal bool RestartAetherRemoteAgent { get; }
    internal bool RestartStationEngine { get; }
    internal bool RestartHost { get; }
    internal bool TxSupportCapable { get; }
    internal string ReleaseNotesTitle { get; }
    internal string ReleaseNotesSummary { get; }
    internal int RestartServiceCount =>
        (RestartGatewayWeb ? 1 : 0) +
        (RestartBroker ? 1 : 0) +
        (RestartAetherRemoteAgent ? 1 : 0) +
        (RestartStationEngine ? 1 : 0);
}

/// <summary>
/// Pure composition of a successful, stable offline-install preflight and its
/// internal verified manifest snapshot into a future installation transaction
/// plan. It reads no filesystem state and owns no network, extraction, write,
/// staging execution, installation, activation, rollback, migration execution,
/// service, Admin, browser, radio, watchdog, command, lease, or TX operation.
/// </summary>
public sealed class VerifiedReleaseInstallationPlanComposer
{
    internal const string InitialInstallationBootstrapIdentity =
        "aethersdr-bootstrap-0.0.0";

    private static readonly ReleasePackageRole[] RequiredRoles =
    [
        ReleasePackageRole.GatewayWeb,
        ReleasePackageRole.Broker,
        ReleasePackageRole.AetherRemoteAgent,
        ReleasePackageRole.StationEngine
    ];

    public VerifiedReleaseInstallationPlanComposer()
    {
        Snapshot = new VerifiedReleaseInstallationPlanDiagnostics(
            Registered: true,
            VerifiedManifestInputRegistered: true,
            InstallationPathCompositionRegistered: true,
            NetworkDownloadRegistered: false,
            ArchiveExtractionRegistered: false,
            FileWriteRegistered: false,
            StagingExecutionRegistered: false,
            InstallationExecutionRegistered: false,
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

    public VerifiedReleaseInstallationPlanDiagnostics Snapshot { get; }

    public VerifiedReleaseInstallationPlanCompositionResult Compose(
        OfflineReleaseInstallPreflightResult preflight,
        InstallationPaths paths) =>
        Compose(preflight, paths, initialInstallation: false);

    internal VerifiedReleaseInstallationPlanCompositionResult ComposeInitial(
        OfflineReleaseInstallPreflightResult preflight,
        InstallationPaths paths) =>
        Compose(preflight, paths, initialInstallation: true);

    private VerifiedReleaseInstallationPlanCompositionResult Compose(
        OfflineReleaseInstallPreflightResult preflight,
        InstallationPaths paths,
        bool initialInstallation)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(paths);

        if (!preflight.Succeeded ||
            preflight.FailureCode !=
                OfflineReleaseInstallPreflightFailureCode.None ||
            preflight.SetupRevision is null or < 1 ||
            !preflight.CurrentPointerVerified ||
            !preflight.TargetAbsentFromInventory ||
            !preflight.StatusStable)
        {
            return VerifiedReleaseInstallationPlanCompositionResult.Failure(
                VerifiedReleaseInstallationPlanFailureCode.PreflightNotEligible,
                "A successful stable offline release install preflight is required.",
                preflight);
        }

        VerifiedReleaseManifestSnapshot? manifest = preflight.VerifiedManifest;
        if (manifest is null)
        {
            return VerifiedReleaseInstallationPlanCompositionResult.Failure(
                VerifiedReleaseInstallationPlanFailureCode.VerifiedManifestUnavailable,
                "The successful preflight does not retain verified manifest planning metadata.",
                preflight);
        }

        if (!MatchesPreflight(preflight, manifest))
        {
            return VerifiedReleaseInstallationPlanCompositionResult.Failure(
                VerifiedReleaseInstallationPlanFailureCode.PreflightManifestMismatch,
                "Verified manifest metadata does not match the successful preflight summary.",
                preflight);
        }

        VerifiedOfflineReleaseBundleSnapshot? bundle = preflight.VerifiedBundle;
        bool installedContextRetained = initialInstallation
            ? string.IsNullOrEmpty(preflight.InstalledVersion) &&
                string.Equals(
                    preflight.InstalledReleaseIdentity,
                    InitialInstallationBootstrapIdentity,
                    StringComparison.Ordinal)
            : !string.IsNullOrEmpty(preflight.InstalledVersion);
        if (bundle is null ||
            bundle.ManifestLength is < 1 or >
                SignedReleaseManifestJson.MaximumManifestBytes ||
            bundle.ManifestSha256.Length != 32 ||
            !IsCanonicalAbsolutePath(bundle.BundleDirectory) ||
            preflight.UpdateChannel is null ||
            preflight.ConfigurationSchemaVersion is null or < 1 ||
            preflight.ProtocolVersion is null or < 1 ||
            !installedContextRetained)
        {
            return VerifiedReleaseInstallationPlanCompositionResult.Failure(
                VerifiedReleaseInstallationPlanFailureCode.VerifiedBundleUnavailable,
                "The successful preflight does not retain one canonical verified bundle snapshot.",
                preflight);
        }

        string releaseRootPath;
        string deploymentRootPath;
        string targetReleasePath;
        try
        {
            InstallationPaths.Validate(paths);
            releaseRootPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(paths.ReleaseDirectory));
            deploymentRootPath =
                Path.GetDirectoryName(releaseRootPath) ??
                throw new InvalidOperationException(
                    "The release root requires a deployment parent directory.");
            targetReleasePath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(
                    Path.Combine(releaseRootPath, manifest.ReleaseIdentity)));
            if (!PathEquals(
                    Path.GetDirectoryName(targetReleasePath),
                    releaseRootPath))
            {
                throw new InvalidOperationException(
                    "The target release path must be a direct child of the release root.");
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return VerifiedReleaseInstallationPlanCompositionResult.Failure(
                VerifiedReleaseInstallationPlanFailureCode.InvalidInstallationPaths,
                "Installation paths cannot produce one canonical direct target release directory.",
                preflight);
        }

        if (!TryCreatePackagePlans(
                manifest,
                targetReleasePath,
                out VerifiedReleaseInstallationPackagePlan[] packagePlans))
        {
            return VerifiedReleaseInstallationPlanCompositionResult.Failure(
                VerifiedReleaseInstallationPlanFailureCode.InvalidPackagePlan,
                "Verified package metadata cannot produce one bounded installation package plan.",
                preflight);
        }

        VerifiedReleaseInstallationPlan plan = new(
            preflight.SetupRevision.Value,
            preflight.InstalledReleaseIdentity,
            manifest.ReleaseIdentity,
            manifest.Version,
            manifest.Architecture,
            preflight.UpdateChannel.Value,
            preflight.PinnedReleaseIdentity,
            preflight.SetupInstallTransmitSupport,
            bundle.BundleDirectory,
            bundle.ManifestLength,
            bundle.ManifestSha256,
            releaseRootPath,
            deploymentRootPath,
            targetReleasePath,
            packagePlans,
            manifest.TargetConfigurationSchemaVersion,
            manifest.MigrationKind,
            manifest.MigrationFromConfigurationSchemaVersion,
            manifest.MigrationToConfigurationSchemaVersion,
            manifest.MigrationIdentity,
            manifest.RestartGatewayWeb,
            manifest.RestartBroker,
            manifest.RestartAetherRemoteAgent,
            manifest.RestartStationEngine,
            manifest.RestartHost,
            manifest.TxSupportCapable,
            manifest.ReleaseNotesTitle,
            manifest.ReleaseNotesSummary);
        return VerifiedReleaseInstallationPlanCompositionResult.Success(
            preflight,
            plan);
    }

    private static bool MatchesPreflight(
        OfflineReleaseInstallPreflightResult preflight,
        VerifiedReleaseManifestSnapshot manifest)
    {
        ReleaseManifestChannel expectedChannel = preflight.UpdateChannel switch
        {
            InstallationUpdateChannel.Stable => ReleaseManifestChannel.Stable,
            InstallationUpdateChannel.Beta => ReleaseManifestChannel.Beta,
            InstallationUpdateChannel.Pinned => ReleaseManifestChannel.Pinned,
            _ => ReleaseManifestChannel.Unknown
        };

        return manifest.SchemaVersion ==
                SignedReleaseManifestPayload.CurrentSchemaVersion &&
            string.Equals(
                manifest.ReleaseIdentity,
                preflight.TargetReleaseIdentity,
                StringComparison.Ordinal) &&
            string.Equals(
                manifest.Version,
                preflight.TargetVersion,
                StringComparison.Ordinal) &&
            manifest.Architecture == preflight.Architecture &&
            manifest.Channel == expectedChannel &&
            manifest.Packages.Count == preflight.PackageCount &&
            manifest.TxSupportCapable == preflight.TargetTxSupportCapable &&
            !string.Equals(
                manifest.ReleaseIdentity,
                preflight.InstalledReleaseIdentity,
                StringComparison.Ordinal) &&
            IsCanonicalReleaseIdentity(preflight.InstalledReleaseIdentity) &&
            IsCanonicalReleaseIdentity(manifest.ReleaseIdentity);
    }

    private static bool TryCreatePackagePlans(
        VerifiedReleaseManifestSnapshot manifest,
        string targetReleasePath,
        out VerifiedReleaseInstallationPackagePlan[] packagePlans)
    {
        packagePlans = [];
        if (manifest.Packages.Count != RequiredRoles.Length)
        {
            return false;
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        HashSet<string> relativePaths = new(StringComparer.Ordinal);
        HashSet<string> targetPaths = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        HashSet<ReleasePackageRole> roles = [];
        List<VerifiedReleaseInstallationPackagePlan> plans = [];
        string targetPrefix =
            targetReleasePath + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (VerifiedReleasePackageSnapshot package in manifest.Packages)
        {
            if (!RequiredRoles.Contains(package.Role) ||
                !roles.Add(package.Role) ||
                string.IsNullOrEmpty(package.PackageIdentity) ||
                !identities.Add(package.PackageIdentity) ||
                !ReleasePackagePath.IsSafe(package.RelativePath) ||
                !relativePaths.Add(package.RelativePath) ||
                package.Length is < 1 or >
                    SignedReleaseManifestVerifier.MaximumDeclaredPackageLength ||
                package.Sha256.Length != 32)
            {
                return false;
            }

            string targetPath;
            try
            {
                targetPath = Path.GetFullPath(
                    Path.Combine(
                        targetReleasePath,
                        package.RelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or
                    PathTooLongException)
            {
                return false;
            }

            if (!targetPath.StartsWith(targetPrefix, comparison) ||
                !targetPaths.Add(targetPath))
            {
                return false;
            }
            plans.Add(
                new VerifiedReleaseInstallationPackagePlan(package, targetPath));
        }

        if (!RequiredRoles.All(roles.Contains))
        {
            return false;
        }

        packagePlans = plans
            .OrderBy(plan => plan.Role)
            .ToArray();
        return true;
    }

    private static bool IsCanonicalAbsolutePath(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            !Path.IsPathFullyQualified(value))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(value),
                value,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsCanonicalReleaseIdentity(string value)
    {
        try
        {
            return string.Equals(
                InstallationReleaseIdentity.Parse(value),
                value,
                StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool PathEquals(string? left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
