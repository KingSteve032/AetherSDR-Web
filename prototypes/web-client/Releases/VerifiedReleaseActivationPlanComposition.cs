using System.Collections.ObjectModel;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationPlanFailureCode
{
    None = 0,
    PublicationNotEligible = 1,
    PublishedReleaseUnavailable = 2,
    PublicationPlanMismatch = 3,
    InvalidActivationPaths = 4,
    InvalidPackagePlan = 5,
    InvalidMigrationPlan = 6
}

public sealed record VerifiedReleaseActivationPlanCompositionResult(
    bool Succeeded,
    VerifiedReleaseActivationPlanFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    string TargetVersion,
    ReleaseManifestArchitecture? Architecture,
    int PackageCount,
    long PublishedBytes,
    int? TargetConfigurationSchemaVersion,
    ReleaseMigrationKind? MigrationKind,
    bool MigrationRequired,
    int RestartServiceCount,
    bool HostRestartRequired,
    bool TxLeaseAdmissionClosureRequired,
    bool RadioAuthoritativeIdleRequired,
    bool WatchdogsDisarmedRequired,
    bool ConfigurationBackupRequired,
    bool AtomicCurrentPointerSwitchRequired,
    bool ServiceHealthVerificationRequired,
    bool AutomaticRollbackRequired,
    bool OperatorApprovalRequired,
    bool CurrentPointerMutationPerformed,
    bool ActivationPerformed)
{
    internal VerifiedReleaseActivationPlan? Plan { get; init; }

    internal static VerifiedReleaseActivationPlanCompositionResult Failure(
        VerifiedReleaseActivationPlanFailureCode failureCode,
        string message,
        VerifiedReleasePublicationReport? publication = null) =>
        new(
            false,
            failureCode,
            message,
            publication?.SetupRevision,
            publication?.InstalledReleaseIdentity ?? string.Empty,
            publication?.TargetReleaseIdentity ?? string.Empty,
            TargetVersion: string.Empty,
            Architecture: null,
            publication?.PackageCount ?? 0,
            publication?.PublishedBytes ?? 0,
            TargetConfigurationSchemaVersion: null,
            MigrationKind: null,
            MigrationRequired: false,
            RestartServiceCount: 0,
            HostRestartRequired: false,
            TxLeaseAdmissionClosureRequired: true,
            RadioAuthoritativeIdleRequired: true,
            WatchdogsDisarmedRequired: true,
            ConfigurationBackupRequired: true,
            AtomicCurrentPointerSwitchRequired: true,
            ServiceHealthVerificationRequired: true,
            AutomaticRollbackRequired: true,
            OperatorApprovalRequired: true,
            CurrentPointerMutationPerformed: false,
            ActivationPerformed: false);

    internal static VerifiedReleaseActivationPlanCompositionResult Failure(
        VerifiedReleaseActivationPlanFailureCode failureCode,
        string message,
        VerifiedReleaseExtractedPublicationReport publication) =>
        new(
            false,
            failureCode,
            message,
            publication.SetupRevision,
            publication.InstalledReleaseIdentity,
            publication.TargetReleaseIdentity,
            TargetVersion: string.Empty,
            Architecture: null,
            publication.PackageCount,
            publication.PublishedBytes,
            TargetConfigurationSchemaVersion: null,
            MigrationKind: null,
            MigrationRequired: false,
            RestartServiceCount: 0,
            HostRestartRequired: false,
            TxLeaseAdmissionClosureRequired: true,
            RadioAuthoritativeIdleRequired: true,
            WatchdogsDisarmedRequired: true,
            ConfigurationBackupRequired: true,
            AtomicCurrentPointerSwitchRequired: true,
            ServiceHealthVerificationRequired: true,
            AutomaticRollbackRequired: true,
            OperatorApprovalRequired: true,
            CurrentPointerMutationPerformed: false,
            ActivationPerformed: false);

    internal static VerifiedReleaseActivationPlanCompositionResult Success(
        VerifiedReleasePublicationReport publication,
        VerifiedReleaseActivationPlan plan) =>
        new(
            true,
            VerifiedReleaseActivationPlanFailureCode.None,
            "A verified release activation transaction plan was composed without mutating current or executing activation work.",
            plan.SetupRevision,
            plan.InstalledReleaseIdentity,
            plan.TargetReleaseIdentity,
            plan.TargetVersion,
            plan.Architecture,
            plan.Packages.Count,
            publication.PublishedBytes,
            plan.TargetConfigurationSchemaVersion,
            plan.MigrationKind,
            plan.MigrationRequired,
            plan.RestartServiceCount,
            plan.RestartHost,
            TxLeaseAdmissionClosureRequired: true,
            RadioAuthoritativeIdleRequired: true,
            WatchdogsDisarmedRequired: true,
            ConfigurationBackupRequired: true,
            AtomicCurrentPointerSwitchRequired: true,
            ServiceHealthVerificationRequired: true,
            AutomaticRollbackRequired: true,
            OperatorApprovalRequired: true,
            CurrentPointerMutationPerformed: false,
            ActivationPerformed: false)
        {
            Plan = plan
        };

    internal static VerifiedReleaseActivationPlanCompositionResult Success(
        VerifiedReleaseExtractedPublicationReport publication,
        VerifiedReleaseActivationPlan plan) =>
        new(
            true,
            VerifiedReleaseActivationPlanFailureCode.None,
            "A verified extracted-release activation transaction plan was composed without mutating current or executing activation work.",
            plan.SetupRevision,
            plan.InstalledReleaseIdentity,
            plan.TargetReleaseIdentity,
            plan.TargetVersion,
            plan.Architecture,
            plan.Packages.Count,
            publication.PublishedBytes,
            plan.TargetConfigurationSchemaVersion,
            plan.MigrationKind,
            plan.MigrationRequired,
            plan.RestartServiceCount,
            plan.RestartHost,
            TxLeaseAdmissionClosureRequired: true,
            RadioAuthoritativeIdleRequired: true,
            WatchdogsDisarmedRequired: true,
            ConfigurationBackupRequired: true,
            AtomicCurrentPointerSwitchRequired: true,
            ServiceHealthVerificationRequired: true,
            AutomaticRollbackRequired: true,
            OperatorApprovalRequired: true,
            CurrentPointerMutationPerformed: false,
            ActivationPerformed: false)
        {
            Plan = plan
        };
}

public sealed record VerifiedReleaseActivationPlanDiagnostics(
    bool Registered,
    bool PublishedReleaseInputRegistered,
    bool ActivationPathCompositionRegistered,
    bool TxQuiescencePlanningRegistered,
    bool BackupPlanningRegistered,
    bool MigrationPlanningRegistered,
    bool ServiceRestartPlanningRegistered,
    bool HealthVerificationPlanningRegistered,
    bool RollbackPlanningRegistered,
    bool NetworkDownloadRegistered,
    bool ArchiveExtractionRegistered,
    bool FileWriteRegistered,
    bool CurrentPointerMutationRegistered,
    bool ActivationExecutionRegistered,
    bool BackupExecutionRegistered,
    bool MigrationExecutionRegistered,
    bool ServiceControlRegistered,
    bool HealthProbeCallerRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed class VerifiedReleaseActivationPackagePlan
{
    private readonly byte[] m_sha256;

    internal VerifiedReleaseActivationPackagePlan(
        ReleasePackageRole role,
        string packageIdentity,
        string publishedPath,
        long length,
        ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != 32)
        {
            throw new InvalidOperationException(
                "Activation package SHA-256 metadata is invalid.");
        }
        Role = role;
        PackageIdentity = packageIdentity;
        PublishedPath = publishedPath;
        Length = length;
        m_sha256 = sha256.ToArray();
    }

    internal ReleasePackageRole Role { get; }
    internal string PackageIdentity { get; }
    internal string PublishedPath { get; }
    internal long Length { get; }
    internal ReadOnlySpan<byte> Sha256 => m_sha256;
}

internal sealed class VerifiedReleaseActivationFilePlan
{
    private readonly byte[] m_sha256;

    internal VerifiedReleaseActivationFilePlan(
        ReleasePackageRole role,
        string relativePath,
        string publishedPath,
        long length,
        ReadOnlySpan<byte> sha256,
        bool executable)
    {
        if (!ReleasePackagePath.IsSafe(relativePath) ||
            string.IsNullOrEmpty(publishedPath) ||
            length < 0 ||
            sha256.Length != 32)
        {
            throw new InvalidOperationException(
                "Activation file metadata is incomplete or unsafe.");
        }
        Role = role;
        RelativePath = relativePath;
        PublishedPath = publishedPath;
        Length = length;
        m_sha256 = sha256.ToArray();
        Executable = executable;
    }

    internal ReleasePackageRole Role { get; }
    internal string RelativePath { get; }
    internal string PublishedPath { get; }
    internal long Length { get; }
    internal ReadOnlySpan<byte> Sha256 => m_sha256;
    internal bool Executable { get; }
}

internal sealed class VerifiedReleaseActivationPlan
{
    private readonly ReadOnlyCollection<VerifiedReleaseActivationPackagePlan>
        m_packages;
    private readonly ReadOnlyCollection<VerifiedReleaseActivationFilePlan> m_files;
    private readonly byte[] m_manifestSha256;

    internal VerifiedReleaseActivationPlan(
        VerifiedReleaseInstallationPlan source,
        string installedReleasePath,
        string targetReleasePath,
        string currentPointerPath,
        string installedCurrentLinkTarget,
        string targetCurrentLinkTarget,
        IReadOnlyList<VerifiedReleaseActivationPackagePlan> packages,
        IReadOnlyList<VerifiedReleaseActivationFilePlan>? files = null,
        int extractedDirectoryCount = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(packages);

        SetupRevision = source.SetupRevision;
        InstalledReleaseIdentity = source.InstalledReleaseIdentity;
        TargetReleaseIdentity = source.TargetReleaseIdentity;
        TargetVersion = source.TargetVersion;
        Architecture = source.Architecture;
        UpdateChannel = source.UpdateChannel;
        PinnedReleaseIdentity = source.PinnedReleaseIdentity;
        InstallTransmitSupport = source.InstallTransmitSupport;
        ManifestLength = source.ManifestLength;
        m_manifestSha256 = source.ManifestSha256.ToArray();
        ReleaseRootPath = source.ReleaseRootPath;
        DeploymentRootPath = source.DeploymentRootPath;
        InstalledReleasePath = installedReleasePath;
        TargetReleasePath = targetReleasePath;
        CurrentPointerPath = currentPointerPath;
        InstalledCurrentLinkTarget = installedCurrentLinkTarget;
        TargetCurrentLinkTarget = targetCurrentLinkTarget;
        m_packages = Array.AsReadOnly(packages.ToArray());
        m_files = Array.AsReadOnly((files ?? []).ToArray());
        ExtractedDirectoryCount = extractedDirectoryCount;
        TargetConfigurationSchemaVersion =
            source.TargetConfigurationSchemaVersion;
        MigrationKind = source.MigrationKind;
        MigrationFromConfigurationSchemaVersion =
            source.MigrationFromConfigurationSchemaVersion;
        MigrationToConfigurationSchemaVersion =
            source.MigrationToConfigurationSchemaVersion;
        MigrationIdentity = source.MigrationIdentity;
        RestartGatewayWeb = source.RestartGatewayWeb;
        RestartBroker = source.RestartBroker;
        RestartAetherRemoteAgent = source.RestartAetherRemoteAgent;
        RestartStationEngine = source.RestartStationEngine;
        RestartHost = source.RestartHost;
        TxSupportCapable = source.TxSupportCapable;
        ReleaseNotesTitle = source.ReleaseNotesTitle;
        ReleaseNotesSummary = source.ReleaseNotesSummary;
    }

    internal long SetupRevision { get; }
    internal string InstalledReleaseIdentity { get; }
    internal string TargetReleaseIdentity { get; }
    internal string TargetVersion { get; }
    internal ReleaseManifestArchitecture Architecture { get; }
    internal InstallationUpdateChannel UpdateChannel { get; }
    internal string PinnedReleaseIdentity { get; }
    internal bool InstallTransmitSupport { get; }
    internal long ManifestLength { get; }
    internal ReadOnlySpan<byte> ManifestSha256 => m_manifestSha256;
    internal string ReleaseRootPath { get; }
    internal string DeploymentRootPath { get; }
    internal string InstalledReleasePath { get; }
    internal string TargetReleasePath { get; }
    internal string CurrentPointerPath { get; }
    internal string InstalledCurrentLinkTarget { get; }
    internal string TargetCurrentLinkTarget { get; }
    internal IReadOnlyList<VerifiedReleaseActivationPackagePlan> Packages =>
        m_packages;
    internal IReadOnlyList<VerifiedReleaseActivationFilePlan> Files => m_files;
    internal int ExtractedDirectoryCount { get; }
    internal bool UsesExtractedRoleTree => m_files.Count > 0;
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
    internal bool MigrationRequired =>
        MigrationKind == ReleaseMigrationKind.Required;
    internal int RestartServiceCount =>
        (RestartGatewayWeb ? 1 : 0) +
        (RestartBroker ? 1 : 0) +
        (RestartAetherRemoteAgent ? 1 : 0) +
        (RestartStationEngine ? 1 : 0);
    internal bool TxLeaseAdmissionClosureRequired => true;
    internal bool RadioAuthoritativeIdleRequired => true;
    internal bool WatchdogsDisarmedRequired => true;
    internal bool ConfigurationBackupRequired => true;
    internal bool AtomicCurrentPointerSwitchRequired => true;
    internal bool ServiceHealthVerificationRequired => true;
    internal bool AutomaticRollbackRequired => true;
    internal bool OperatorApprovalRequired => true;
}

/// <summary>
/// Pure composition of one successfully published, immutable, inactive release
/// into a future activation transaction plan. The plan records mandatory TX
/// quiescence, backup, migration, pointer-switch, service-health, and rollback
/// obligations. It performs no filesystem I/O, current mutation, activation,
/// backup, migration, service control, health probe, Admin/browser operation,
/// radio, watchdog, command, lease, or transmit action.
/// </summary>
public sealed class VerifiedReleaseActivationPlanComposer
{
    private static readonly ReleasePackageRole[] RequiredRoles =
    [
        ReleasePackageRole.GatewayWeb,
        ReleasePackageRole.Broker,
        ReleasePackageRole.AetherRemoteAgent,
        ReleasePackageRole.StationEngine
    ];

    public VerifiedReleaseActivationPlanComposer()
    {
        Snapshot = new VerifiedReleaseActivationPlanDiagnostics(
            Registered: true,
            PublishedReleaseInputRegistered: true,
            ActivationPathCompositionRegistered: true,
            TxQuiescencePlanningRegistered: true,
            BackupPlanningRegistered: true,
            MigrationPlanningRegistered: true,
            ServiceRestartPlanningRegistered: true,
            HealthVerificationPlanningRegistered: true,
            RollbackPlanningRegistered: true,
            NetworkDownloadRegistered: false,
            ArchiveExtractionRegistered: false,
            FileWriteRegistered: false,
            CurrentPointerMutationRegistered: false,
            ActivationExecutionRegistered: false,
            BackupExecutionRegistered: false,
            MigrationExecutionRegistered: false,
            ServiceControlRegistered: false,
            HealthProbeCallerRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationPlanDiagnostics Snapshot { get; }

    public VerifiedReleaseActivationPlanCompositionResult Compose(
        VerifiedReleasePublicationReport publication)
    {
        ArgumentNullException.ThrowIfNull(publication);

        if (!IsEligiblePublication(publication))
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.PublicationNotEligible,
                "A successful immutable inactive publication without reconciliation is required.",
                publication);
        }

        VerifiedPublishedRelease? publishedRelease = publication.PublishedRelease;
        if (publishedRelease is null)
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.PublishedReleaseUnavailable,
                "The successful publication does not retain its verified internal release token.",
                publication);
        }

        VerifiedReleaseInstallationPlan source = publishedRelease.Plan;
        if (!MatchesPublication(publication, publishedRelease))
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.PublicationPlanMismatch,
                "Published release metadata does not match the successful publication summary.",
                publication);
        }

        string installedReleasePath;
        string targetReleasePath;
        string currentPointerPath;
        string installedCurrentLinkTarget;
        string targetCurrentLinkTarget;
        try
        {
            if (!ValidateSourcePlan(source, publishedRelease))
            {
                throw new InvalidOperationException(
                    "The published release plan is incomplete or non-canonical.");
            }

            installedReleasePath = CanonicalDirectReleasePath(
                source.ReleaseRootPath,
                source.InstalledReleaseIdentity);
            targetReleasePath = CanonicalDirectReleasePath(
                source.ReleaseRootPath,
                source.TargetReleaseIdentity);
            if (!PathEquals(targetReleasePath, publishedRelease.PublishedPath) ||
                !PathEquals(targetReleasePath, source.TargetReleasePath))
            {
                throw new InvalidOperationException(
                    "The published release path does not match the target plan.");
            }

            currentPointerPath = Path.GetFullPath(
                Path.Combine(source.DeploymentRootPath, "current"));
            if (!PathEquals(
                    Path.GetDirectoryName(currentPointerPath),
                    source.DeploymentRootPath))
            {
                throw new InvalidOperationException(
                    "The current pointer must be a direct deployment child.");
            }

            installedCurrentLinkTarget = CanonicalRelativeLinkTarget(
                source.DeploymentRootPath,
                installedReleasePath);
            targetCurrentLinkTarget = CanonicalRelativeLinkTarget(
                source.DeploymentRootPath,
                targetReleasePath);
            if (string.Equals(
                    installedCurrentLinkTarget,
                    targetCurrentLinkTarget,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The previous and target current-link values must differ.");
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.InvalidActivationPaths,
                "Published release metadata cannot produce canonical activation paths.",
                publication);
        }

        if (!TryCreatePackagePlans(
                source,
                targetReleasePath,
                out VerifiedReleaseActivationPackagePlan[] packages))
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.InvalidPackagePlan,
                "Published package metadata cannot produce one bounded activation service plan.",
                publication);
        }
        if (!ValidateMigrationPlan(source))
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.InvalidMigrationPlan,
                "Published migration metadata is incomplete or contradictory.",
                publication);
        }

        VerifiedReleaseActivationPlan plan = new(
            source,
            installedReleasePath,
            targetReleasePath,
            currentPointerPath,
            installedCurrentLinkTarget,
            targetCurrentLinkTarget,
            packages);
        return VerifiedReleaseActivationPlanCompositionResult.Success(
            publication,
            plan);
    }

    public VerifiedReleaseActivationPlanCompositionResult Compose(
        VerifiedReleaseExtractedPublicationReport publication)
    {
        ArgumentNullException.ThrowIfNull(publication);

        if (!IsEligibleExtractedPublication(publication))
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.PublicationNotEligible,
                "A successful immutable inactive extracted publication without reconciliation is required.",
                publication);
        }

        VerifiedExtractedPublishedRelease? publishedRelease =
            publication.PublishedRelease;
        if (publishedRelease is null)
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.PublishedReleaseUnavailable,
                "The successful extracted publication does not retain its verified internal release token.",
                publication);
        }

        VerifiedReleaseExtractedPublicationPlan extractedPlan =
            publishedRelease.Plan;
        VerifiedReleaseInstallationPlan source = extractedPlan.Source.Plan;
        if (!MatchesExtractedPublication(
                publication,
                publishedRelease,
                extractedPlan))
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.PublicationPlanMismatch,
                "Extracted publication metadata does not match its exact immutable release token.",
                publication);
        }

        string installedReleasePath;
        string targetReleasePath;
        string currentPointerPath;
        string installedCurrentLinkTarget;
        string targetCurrentLinkTarget;
        try
        {
            if (!ValidateExtractedSourcePlan(source, publishedRelease, extractedPlan))
            {
                throw new InvalidOperationException(
                    "The extracted published release plan is incomplete or non-canonical.");
            }

            installedReleasePath = CanonicalDirectReleasePath(
                source.ReleaseRootPath,
                source.InstalledReleaseIdentity);
            targetReleasePath = CanonicalDirectReleasePath(
                source.ReleaseRootPath,
                source.TargetReleaseIdentity);
            if (!PathEquals(targetReleasePath, publishedRelease.PublishedPath) ||
                !PathEquals(targetReleasePath, extractedPlan.TargetPath) ||
                !PathEquals(targetReleasePath, source.TargetReleasePath))
            {
                throw new InvalidOperationException(
                    "The extracted published release path does not match the target plan.");
            }

            currentPointerPath = Path.GetFullPath(
                Path.Combine(source.DeploymentRootPath, "current"));
            if (!PathEquals(
                    Path.GetDirectoryName(currentPointerPath),
                    source.DeploymentRootPath))
            {
                throw new InvalidOperationException(
                    "The current pointer must be a direct deployment child.");
            }

            installedCurrentLinkTarget = CanonicalRelativeLinkTarget(
                source.DeploymentRootPath,
                installedReleasePath);
            targetCurrentLinkTarget = CanonicalRelativeLinkTarget(
                source.DeploymentRootPath,
                targetReleasePath);
            if (string.Equals(
                    installedCurrentLinkTarget,
                    targetCurrentLinkTarget,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The previous and target current-link values must differ.");
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.InvalidActivationPaths,
                "Extracted publication metadata cannot produce canonical activation paths.",
                publication);
        }

        if (!TryCreateExtractedPackagePlans(
                source,
                targetReleasePath,
                out VerifiedReleaseActivationPackagePlan[] packages))
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.InvalidPackagePlan,
                "Extracted role-root metadata cannot produce one bounded activation service plan.",
                publication);
        }
        if (!TryCreateActivationFilePlans(
                extractedPlan,
                targetReleasePath,
                out VerifiedReleaseActivationFilePlan[] files))
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.InvalidPackagePlan,
                "The immutable extracted file inventory cannot be bound to the activation target.",
                publication);
        }
        if (!ValidateMigrationPlan(source))
        {
            return VerifiedReleaseActivationPlanCompositionResult.Failure(
                VerifiedReleaseActivationPlanFailureCode.InvalidMigrationPlan,
                "Published migration metadata is incomplete or contradictory.",
                publication);
        }

        VerifiedReleaseActivationPlan plan = new(
            source,
            installedReleasePath,
            targetReleasePath,
            currentPointerPath,
            installedCurrentLinkTarget,
            targetCurrentLinkTarget,
            packages,
            files,
            extractedPlan.DirectoryCount);
        return VerifiedReleaseActivationPlanCompositionResult.Success(
            publication,
            plan);
    }

    private static bool IsEligibleExtractedPublication(
        VerifiedReleaseExtractedPublicationReport publication) =>
        publication.Succeeded &&
        publication.FailureCode ==
            VerifiedReleaseExtractedPublicationFailureCode.None &&
        publication.SetupRevision is >= 1 &&
        !string.IsNullOrEmpty(publication.InstalledReleaseIdentity) &&
        !string.IsNullOrEmpty(publication.TargetReleaseIdentity) &&
        publication.PackageCount == RequiredRoles.Length &&
        publication.FileCount is >= 5 and <=
            VerifiedReleaseArchiveExtractionService.MaximumExtractedFileCount &&
        publication.DirectoryCount is >= 4 and <=
            VerifiedReleaseArchiveExtractionService.MaximumExtractedDirectoryCount &&
        publication.PublishedBytes > 0 &&
        publication.PublishedBytes <=
            VerifiedReleaseArchiveExtractionService.MaximumExpandedBytes &&
        publication.SourceExtractionTreeConsumed &&
        publication.TargetPublished &&
        publication.TargetImmutable &&
        !publication.CurrentPointerChanged &&
        !publication.ActivationPerformed &&
        !publication.ReconciliationRequired;

    private static bool MatchesExtractedPublication(
        VerifiedReleaseExtractedPublicationReport publication,
        VerifiedExtractedPublishedRelease publishedRelease,
        VerifiedReleaseExtractedPublicationPlan plan)
    {
        VerifiedReleaseInstallationPlan source = plan.Source.Plan;
        long expectedBytes;
        try
        {
            expectedBytes = plan.Files.Sum(file => file.Length);
        }
        catch (OverflowException)
        {
            return false;
        }

        return source.SetupRevision == publication.SetupRevision &&
            string.Equals(
                source.InstalledReleaseIdentity,
                publication.InstalledReleaseIdentity,
                StringComparison.Ordinal) &&
            string.Equals(
                source.TargetReleaseIdentity,
                publication.TargetReleaseIdentity,
                StringComparison.Ordinal) &&
            source.Packages.Count == publication.PackageCount &&
            plan.Files.Count == publication.FileCount &&
            plan.DirectoryCount == publication.DirectoryCount &&
            publishedRelease.PublishedBytes == publication.PublishedBytes &&
            plan.PublicationBytes == publication.PublishedBytes &&
            expectedBytes == publication.PublishedBytes;
    }

    private static bool ValidateExtractedSourcePlan(
        VerifiedReleaseInstallationPlan source,
        VerifiedExtractedPublishedRelease publishedRelease,
        VerifiedReleaseExtractedPublicationPlan extractedPlan)
    {
        if (source.SetupRevision < 1 ||
            !IsCanonicalReleaseIdentity(source.InstalledReleaseIdentity) ||
            !IsCanonicalReleaseIdentity(source.TargetReleaseIdentity) ||
            string.Equals(
                source.InstalledReleaseIdentity,
                source.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            !ReleaseSemanticVersion.TryParse(
                source.TargetVersion,
                out ReleaseSemanticVersion parsedVersion) ||
            !string.Equals(
                source.TargetVersion,
                CanonicalSemanticVersion(parsedVersion),
                StringComparison.Ordinal) ||
            source.Architecture is not ReleaseManifestArchitecture.LinuxX64 and
                not ReleaseManifestArchitecture.LinuxArm64 ||
            source.UpdateChannel is not InstallationUpdateChannel.Stable and
                not InstallationUpdateChannel.Beta and
                not InstallationUpdateChannel.Pinned ||
            source.InstallTransmitSupport != source.TxSupportCapable ||
            source.ManifestLength is < 1 or >
                SignedReleaseManifestJson.MaximumManifestBytes ||
            source.ManifestSha256.Length != 32 ||
            !IsCanonicalAbsolutePath(source.ReleaseRootPath) ||
            !IsCanonicalAbsolutePath(source.DeploymentRootPath) ||
            !IsCanonicalAbsolutePath(source.TargetReleasePath) ||
            !IsCanonicalAbsolutePath(publishedRelease.PublishedPath) ||
            !IsCanonicalAbsolutePath(extractedPlan.TargetPath) ||
            !PathEquals(
                Path.GetDirectoryName(source.ReleaseRootPath),
                source.DeploymentRootPath) ||
            !PathEquals(
                Path.GetDirectoryName(source.TargetReleasePath),
                source.ReleaseRootPath) ||
            source.Packages.Count != RequiredRoles.Length ||
            extractedPlan.Files.Count is < 5 or >
                VerifiedReleaseArchiveExtractionService.MaximumExtractedFileCount ||
            extractedPlan.DirectoryCount is < 4 or >
                VerifiedReleaseArchiveExtractionService.MaximumExtractedDirectoryCount)
        {
            return false;
        }

        if (source.UpdateChannel == InstallationUpdateChannel.Pinned)
        {
            if (!string.Equals(
                    source.PinnedReleaseIdentity,
                    source.TargetReleaseIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (!string.IsNullOrEmpty(source.PinnedReleaseIdentity))
        {
            return false;
        }

        return ReferenceEquals(extractedPlan.Source.Plan, source) &&
            PathEquals(extractedPlan.TargetPath, source.TargetReleasePath);
    }

    private static bool TryCreateExtractedPackagePlans(
        VerifiedReleaseInstallationPlan source,
        string targetReleasePath,
        out VerifiedReleaseActivationPackagePlan[] plans)
    {
        plans = [];
        if (source.Packages.Count != RequiredRoles.Length)
        {
            return false;
        }

        List<VerifiedReleaseActivationPackagePlan> result = [];
        HashSet<ReleasePackageRole> roles = [];
        foreach (VerifiedReleaseInstallationPackagePlan package in source.Packages)
        {
            if (!RequiredRoles.Contains(package.Role) ||
                !roles.Add(package.Role) ||
                string.IsNullOrWhiteSpace(package.PackageIdentity) ||
                package.Length is < 1 or >
                    SignedReleaseManifestVerifier.MaximumDeclaredPackageLength ||
                package.Sha256.Length != 32)
            {
                return false;
            }

            string roleRoot;
            try
            {
                roleRoot = Path.GetFullPath(
                    Path.Combine(targetReleasePath, RoleDirectoryName(package.Role)));
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or
                    PathTooLongException)
            {
                return false;
            }
            if (!PathEquals(Path.GetDirectoryName(roleRoot), targetReleasePath))
            {
                return false;
            }
            result.Add(
                new VerifiedReleaseActivationPackagePlan(
                    package.Role,
                    package.PackageIdentity,
                    roleRoot,
                    package.Length,
                    package.Sha256));
        }

        if (!roles.SetEquals(RequiredRoles))
        {
            return false;
        }
        plans = result
            .OrderBy(plan => Array.IndexOf(RequiredRoles, plan.Role))
            .ToArray();
        return true;
    }

    private static bool TryCreateActivationFilePlans(
        VerifiedReleaseExtractedPublicationPlan extractedPlan,
        string targetReleasePath,
        out VerifiedReleaseActivationFilePlan[] plans)
    {
        plans = [];
        HashSet<string> relativePaths = new(StringComparer.Ordinal);
        HashSet<string> targetPaths = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        HashSet<ReleasePackageRole> roles = [];
        bool manifestFound = false;
        List<VerifiedReleaseActivationFilePlan> result = [];
        string previous = string.Empty;

        foreach (VerifiedReleaseExtractedPublicationFilePlan file in
            extractedPlan.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            if (!ReleasePackagePath.IsSafe(file.RelativePath) ||
                !relativePaths.Add(file.RelativePath) ||
                file.Length < 0 ||
                file.Sha256.Length != 32 ||
                previous.Length > 0 &&
                    string.CompareOrdinal(previous, file.RelativePath) >= 0)
            {
                return false;
            }
            previous = file.RelativePath;

            string expectedTarget;
            try
            {
                expectedTarget = Path.GetFullPath(
                    Path.Combine(
                        targetReleasePath,
                        file.RelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or
                    PathTooLongException)
            {
                return false;
            }
            if (!PathEquals(expectedTarget, file.TargetPath) ||
                !expectedTarget.StartsWith(
                    targetReleasePath + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal) ||
                !targetPaths.Add(expectedTarget))
            {
                return false;
            }

            bool manifest = string.Equals(
                file.RelativePath,
                LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                StringComparison.Ordinal);
            if (manifest)
            {
                if (manifestFound ||
                    file.Role != ReleasePackageRole.Unknown ||
                    file.Executable ||
                    file.Length != extractedPlan.Source.Plan.ManifestLength ||
                    !file.Sha256.SequenceEqual(
                        extractedPlan.Source.Plan.ManifestSha256))
                {
                    return false;
                }
                manifestFound = true;
            }
            else
            {
                if (!RequiredRoles.Contains(file.Role) ||
                    !file.RelativePath.StartsWith(
                        RoleDirectoryName(file.Role) + "/",
                        StringComparison.Ordinal))
                {
                    return false;
                }
                roles.Add(file.Role);
            }

            result.Add(
                new VerifiedReleaseActivationFilePlan(
                    file.Role,
                    file.RelativePath,
                    expectedTarget,
                    file.Length,
                    file.Sha256,
                    file.Executable));
        }

        if (!manifestFound || !roles.SetEquals(RequiredRoles))
        {
            return false;
        }
        plans = result.ToArray();
        return true;
    }

    private static string RoleDirectoryName(ReleasePackageRole role) =>
        role switch
        {
            ReleasePackageRole.GatewayWeb => "gateway-web",
            ReleasePackageRole.Broker => "broker",
            ReleasePackageRole.AetherRemoteAgent => "aetherremote-agent",
            ReleasePackageRole.StationEngine => "station-engine",
            _ => throw new InvalidOperationException(
                "The activation role root is unsupported.")
        };

    private static bool IsEligiblePublication(
        VerifiedReleasePublicationReport publication) =>
        publication.Succeeded &&
        publication.FailureCode == VerifiedReleasePublicationFailureCode.None &&
        publication.SetupRevision is >= 1 &&
        !string.IsNullOrEmpty(publication.InstalledReleaseIdentity) &&
        !string.IsNullOrEmpty(publication.TargetReleaseIdentity) &&
        publication.PackageCount == RequiredRoles.Length &&
        publication.PublishedBytes > 0 &&
        publication.SourceStagingTreeConsumed &&
        publication.TargetPublished &&
        publication.TargetImmutable &&
        !publication.CurrentPointerChanged &&
        !publication.ActivationPerformed &&
        !publication.ReconciliationRequired;

    private static bool MatchesPublication(
        VerifiedReleasePublicationReport publication,
        VerifiedPublishedRelease publishedRelease)
    {
        VerifiedReleaseInstallationPlan source = publishedRelease.Plan;
        long expectedBytes;
        try
        {
            expectedBytes = checked(
                source.ManifestLength +
                source.Packages.Sum(package => package.Length));
        }
        catch (OverflowException)
        {
            return false;
        }

        return source.SetupRevision == publication.SetupRevision &&
            string.Equals(
                source.InstalledReleaseIdentity,
                publication.InstalledReleaseIdentity,
                StringComparison.Ordinal) &&
            string.Equals(
                source.TargetReleaseIdentity,
                publication.TargetReleaseIdentity,
                StringComparison.Ordinal) &&
            source.Packages.Count == publication.PackageCount &&
            publishedRelease.PublishedBytes == publication.PublishedBytes &&
            publication.PublishedBytes == expectedBytes;
    }

    private static bool ValidateSourcePlan(
        VerifiedReleaseInstallationPlan source,
        VerifiedPublishedRelease publishedRelease)
    {
        if (source.SetupRevision < 1 ||
            !IsCanonicalReleaseIdentity(source.InstalledReleaseIdentity) ||
            !IsCanonicalReleaseIdentity(source.TargetReleaseIdentity) ||
            string.Equals(
                source.InstalledReleaseIdentity,
                source.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            !ReleaseSemanticVersion.TryParse(
                source.TargetVersion,
                out ReleaseSemanticVersion parsedVersion) ||
            !string.Equals(
                source.TargetVersion,
                CanonicalSemanticVersion(parsedVersion),
                StringComparison.Ordinal) ||
            source.Architecture is not ReleaseManifestArchitecture.LinuxX64 and
                not ReleaseManifestArchitecture.LinuxArm64 ||
            source.UpdateChannel is not InstallationUpdateChannel.Stable and
                not InstallationUpdateChannel.Beta and
                not InstallationUpdateChannel.Pinned ||
            source.InstallTransmitSupport != source.TxSupportCapable ||
            source.ManifestLength is < 1 or >
                SignedReleaseManifestJson.MaximumManifestBytes ||
            source.ManifestSha256.Length != 32 ||
            !IsCanonicalAbsolutePath(source.ReleaseRootPath) ||
            !IsCanonicalAbsolutePath(source.DeploymentRootPath) ||
            !IsCanonicalAbsolutePath(source.TargetReleasePath) ||
            !IsCanonicalAbsolutePath(publishedRelease.PublishedPath) ||
            !PathEquals(
                Path.GetDirectoryName(source.ReleaseRootPath),
                source.DeploymentRootPath) ||
            !PathEquals(
                Path.GetDirectoryName(source.TargetReleasePath),
                source.ReleaseRootPath))
        {
            return false;
        }

        if (source.UpdateChannel == InstallationUpdateChannel.Pinned)
        {
            if (!string.Equals(
                    source.PinnedReleaseIdentity,
                    source.TargetReleaseIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (!string.IsNullOrEmpty(source.PinnedReleaseIdentity))
        {
            return false;
        }

        return true;
    }

    private static bool TryCreatePackagePlans(
        VerifiedReleaseInstallationPlan source,
        string targetReleasePath,
        out VerifiedReleaseActivationPackagePlan[] plans)
    {
        plans = [];
        if (source.Packages.Count != RequiredRoles.Length)
        {
            return false;
        }

        HashSet<ReleasePackageRole> roles = [];
        HashSet<string> identities = new(StringComparer.Ordinal);
        HashSet<string> publishedPaths = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        List<VerifiedReleaseActivationPackagePlan> result = [];

        foreach (VerifiedReleaseInstallationPackagePlan package in source.Packages)
        {
            if (!RequiredRoles.Contains(package.Role) ||
                !roles.Add(package.Role) ||
                string.IsNullOrWhiteSpace(package.PackageIdentity) ||
                !string.Equals(
                    package.PackageIdentity,
                    package.PackageIdentity.Trim(),
                    StringComparison.Ordinal) ||
                !identities.Add(package.PackageIdentity) ||
                !ReleasePackagePath.IsSafe(package.SourceRelativePath) ||
                package.Length is < 1 or >
                    SignedReleaseManifestVerifier.MaximumDeclaredPackageLength ||
                package.Sha256.Length != 32 ||
                !IsCanonicalAbsolutePath(package.TargetPath))
            {
                return false;
            }

            string expectedPath;
            try
            {
                expectedPath = Path.GetFullPath(
                    Path.Combine(
                        targetReleasePath,
                        package.SourceRelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or
                    PathTooLongException)
            {
                return false;
            }

            string targetPrefix = targetReleasePath + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!PathEquals(package.TargetPath, expectedPath) ||
                !expectedPath.StartsWith(targetPrefix, comparison) ||
                !publishedPaths.Add(expectedPath))
            {
                return false;
            }

            result.Add(
                new VerifiedReleaseActivationPackagePlan(
                    package.Role,
                    package.PackageIdentity,
                    expectedPath,
                    package.Length,
                    package.Sha256));
        }

        if (!roles.SetEquals(RequiredRoles))
        {
            return false;
        }

        plans = result
            .OrderBy(plan => Array.IndexOf(RequiredRoles, plan.Role))
            .ToArray();
        return true;
    }

    private static bool ValidateMigrationPlan(
        VerifiedReleaseInstallationPlan source)
    {
        if (source.TargetConfigurationSchemaVersion < 1)
        {
            return false;
        }

        return source.MigrationKind switch
        {
            ReleaseMigrationKind.None =>
                source.MigrationFromConfigurationSchemaVersion is null &&
                source.MigrationToConfigurationSchemaVersion is null &&
                string.IsNullOrEmpty(source.MigrationIdentity),
            ReleaseMigrationKind.Required =>
                source.MigrationFromConfigurationSchemaVersion is >= 1 &&
                source.MigrationToConfigurationSchemaVersion ==
                    source.TargetConfigurationSchemaVersion &&
                source.MigrationFromConfigurationSchemaVersion <
                    source.MigrationToConfigurationSchemaVersion &&
                !string.IsNullOrWhiteSpace(source.MigrationIdentity) &&
                string.Equals(
                    source.MigrationIdentity,
                    source.MigrationIdentity.Trim(),
                    StringComparison.Ordinal),
            _ => false
        };
    }

    private static string CanonicalDirectReleasePath(
        string releaseRootPath,
        string releaseIdentity)
    {
        string path = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(releaseRootPath, releaseIdentity)));
        if (!PathEquals(Path.GetDirectoryName(path), releaseRootPath) ||
            !string.Equals(
                Path.GetFileName(path),
                releaseIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A release path must be one canonical direct child.");
        }
        return path;
    }

    private static string CanonicalRelativeLinkTarget(
        string deploymentRootPath,
        string releasePath)
    {
        string relative = Path.GetRelativePath(deploymentRootPath, releasePath)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(relative) ||
            string.IsNullOrWhiteSpace(relative) ||
            relative == "." ||
            relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            relative.Contains(
                Path.DirectorySeparatorChar + ".." +
                Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            !PathEquals(
                Path.GetFullPath(Path.Combine(deploymentRootPath, relative)),
                releasePath))
        {
            throw new InvalidOperationException(
                "A current-link target must be canonical and remain inside the deployment root.");
        }
        return relative;
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

    private static bool IsCanonicalAbsolutePath(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            !Path.IsPathFullyQualified(value))
        {
            return false;
        }
        try
        {
            return PathEquals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)),
                Path.TrimEndingDirectorySeparator(value));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static string CanonicalSemanticVersion(
        ReleaseSemanticVersion version) =>
        $"{version.Major}.{version.Minor}.{version.Patch}" +
        (version.Prerelease.Length == 0
            ? string.Empty
            : $"-{version.Prerelease}") +
        (version.BuildMetadata.Length == 0
            ? string.Empty
            : $"+{version.BuildMetadata}");

    private static bool PathEquals(string? left, string? right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left ?? string.Empty),
            Path.TrimEndingDirectorySeparator(right ?? string.Empty),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
