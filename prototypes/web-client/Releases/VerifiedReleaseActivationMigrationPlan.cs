using System.Collections.ObjectModel;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationMigrationPlanFailureCode
{
    None = 0,
    ActivationPlanNotEligible = 1,
    ActivationPlanUnavailable = 2,
    ActivationPlanMismatch = 3,
    ConfigurationBackupNotEligible = 4,
    ConfigurationBackupUnavailable = 5,
    ConfigurationBackupMismatch = 6,
    MigrationDeclarationInvalid = 7,
    MigrationLayoutUnsafe = 8
}

public sealed record VerifiedReleaseActivationMigrationPlanReport(
    bool Succeeded,
    VerifiedReleaseActivationMigrationPlanFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    ReleaseMigrationKind? MigrationKind,
    int? FromConfigurationSchemaVersion,
    int? ToConfigurationSchemaVersion,
    bool MigrationRequired,
    bool NoOpMigrationResolved,
    bool ExactActivationPlanBound,
    bool ExactConfigurationBackupBound,
    bool SourceBackupImmutable,
    bool StagedCopyRequired,
    bool MigrationManifestRequired,
    bool AtomicPublicationRequired,
    bool MigrationRunnerRequired,
    bool MigrationRunnerSelected,
    bool SourceReadPerformed,
    bool FileWritePerformed,
    bool MigrationExecutionPerformed,
    bool MigrationReady,
    bool CurrentPointerChanged,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationMigrationPlan? Plan { get; init; }

    internal static VerifiedReleaseActivationMigrationPlanReport Failure(
        VerifiedReleaseActivationMigrationPlanFailureCode failureCode,
        string message,
        VerifiedReleaseActivationPlanCompositionResult? activationPlan = null) =>
        new(
            false,
            failureCode,
            message,
            activationPlan?.SetupRevision,
            activationPlan?.InstalledReleaseIdentity ?? string.Empty,
            activationPlan?.TargetReleaseIdentity ?? string.Empty,
            activationPlan?.MigrationKind,
            FromConfigurationSchemaVersion: null,
            ToConfigurationSchemaVersion:
                activationPlan?.TargetConfigurationSchemaVersion,
            MigrationRequired: activationPlan?.MigrationRequired ?? false,
            NoOpMigrationResolved: false,
            ExactActivationPlanBound: false,
            ExactConfigurationBackupBound: false,
            SourceBackupImmutable: false,
            StagedCopyRequired: activationPlan?.MigrationRequired ?? false,
            MigrationManifestRequired: activationPlan?.MigrationRequired ?? false,
            AtomicPublicationRequired: activationPlan?.MigrationRequired ?? false,
            MigrationRunnerRequired: activationPlan?.MigrationRequired ?? false,
            MigrationRunnerSelected: false,
            SourceReadPerformed: false,
            FileWritePerformed: false,
            MigrationExecutionPerformed: false,
            MigrationReady: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationMigrationPlanReport Success(
        VerifiedReleaseActivationMigrationPlan plan) =>
        new(
            true,
            VerifiedReleaseActivationMigrationPlanFailureCode.None,
            plan.MigrationRequired
                ? "A verified exact-plan staged-copy migration transaction was composed without reading, writing, selecting a runner, or executing migration work."
                : "The signed release requires no configuration migration; the exact activation plan and backup were bound without filesystem mutation.",
            plan.ActivationPlan.SetupRevision,
            plan.ActivationPlan.InstalledReleaseIdentity,
            plan.ActivationPlan.TargetReleaseIdentity,
            plan.MigrationKind,
            plan.FromConfigurationSchemaVersion,
            plan.ToConfigurationSchemaVersion,
            plan.MigrationRequired,
            NoOpMigrationResolved: !plan.MigrationRequired,
            ExactActivationPlanBound: true,
            ExactConfigurationBackupBound: true,
            SourceBackupImmutable: true,
            StagedCopyRequired: plan.MigrationRequired,
            MigrationManifestRequired: plan.MigrationRequired,
            AtomicPublicationRequired: plan.MigrationRequired,
            MigrationRunnerRequired: plan.MigrationRequired,
            MigrationRunnerSelected: false,
            SourceReadPerformed: false,
            FileWritePerformed: false,
            MigrationExecutionPerformed: false,
            MigrationReady: !plan.MigrationRequired,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            Plan = plan
        };
}

public sealed record VerifiedReleaseActivationMigrationPlanDiagnostics(
    bool Registered,
    bool ActivationPlanInputRegistered,
    bool ConfigurationBackupInputRegistered,
    bool ExactActivationPlanBindingRegistered,
    bool ExactConfigurationBackupBindingRegistered,
    bool ImmutableBackupValidationRegistered,
    bool NoOpMigrationPlanningRegistered,
    bool RequiredMigrationPlanningRegistered,
    bool SchemaTransitionValidationRegistered,
    bool MigrationIdentityValidationRegistered,
    bool StagedCopyPathPlanningRegistered,
    bool MigrationManifestPlanningRegistered,
    bool AtomicPublicationPlanningRegistered,
    bool MigrationRunnerSelectionRegistered,
    bool SourceReadRegistered,
    bool FileWriteRegistered,
    bool DirectoryMutationRegistered,
    bool MigrationExecutionRegistered,
    bool MigrationEvidenceRegistered,
    bool CurrentPointerMutationRegistered,
    bool ActivationAuthorityRegistered,
    bool OperationalCallerRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool HttpCallerRegistered,
    bool WebSocketCallerRegistered,
    bool HostedServiceCallerRegistered,
    bool TimerCallerRegistered,
    bool AetherRemoteCallerRegistered,
    bool ServiceControlCallerRegistered,
    bool HealthProbeCallerRegistered,
    bool RollbackCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed record VerifiedReleaseActivationMigrationSourcePlan(
    VerifiedReleaseActivationConfigurationBackupSourceKind Kind,
    string SourcePath,
    string StagedPath);

internal sealed class VerifiedReleaseActivationMigrationPlan
{
    private readonly ReadOnlyCollection<VerifiedReleaseActivationMigrationSourcePlan>
        m_sources;

    internal VerifiedReleaseActivationMigrationPlan(
        VerifiedReleaseActivationPlan activationPlan,
        VerifiedReleaseActivationConfigurationBackup configurationBackup,
        ReleaseMigrationKind migrationKind,
        int? fromConfigurationSchemaVersion,
        int? toConfigurationSchemaVersion,
        string migrationIdentity,
        string migrationRootPath,
        string stagingPath,
        string publishedPath,
        string manifestPath,
        IReadOnlyList<VerifiedReleaseActivationMigrationSourcePlan> sources)
    {
        ActivationPlan = activationPlan ??
            throw new ArgumentNullException(nameof(activationPlan));
        ConfigurationBackup = configurationBackup ??
            throw new ArgumentNullException(nameof(configurationBackup));
        MigrationKind = migrationKind;
        FromConfigurationSchemaVersion = fromConfigurationSchemaVersion;
        ToConfigurationSchemaVersion = toConfigurationSchemaVersion;
        MigrationIdentity = migrationIdentity ?? string.Empty;
        MigrationRootPath = migrationRootPath ?? string.Empty;
        StagingPath = stagingPath ?? string.Empty;
        PublishedPath = publishedPath ?? string.Empty;
        ManifestPath = manifestPath ?? string.Empty;
        m_sources = Array.AsReadOnly(
            (sources ?? throw new ArgumentNullException(nameof(sources))).ToArray());
    }

    internal VerifiedReleaseActivationPlan ActivationPlan { get; }
    internal VerifiedReleaseActivationConfigurationBackup ConfigurationBackup
    {
        get;
    }
    internal ReleaseMigrationKind MigrationKind { get; }
    internal int? FromConfigurationSchemaVersion { get; }
    internal int? ToConfigurationSchemaVersion { get; }
    internal string MigrationIdentity { get; }
    internal string MigrationRootPath { get; }
    internal string StagingPath { get; }
    internal string PublishedPath { get; }
    internal string ManifestPath { get; }
    internal IReadOnlyList<VerifiedReleaseActivationMigrationSourcePlan> Sources =>
        m_sources;
    internal bool MigrationRequired =>
        MigrationKind == ReleaseMigrationKind.Required;
    internal bool ExistingMigrationOverwriteAllowed => false;
    internal bool AtomicPublicationRequired => MigrationRequired;
    internal bool MigrationRunnerRequired => MigrationRequired;
}

/// <summary>
/// Pure fail-closed composition of one exact verified activation plan and its
/// exact immutable configuration backup into a future staged-copy migration
/// transaction. A signed no-migration declaration resolves as a no-op. A signed
/// required migration produces separated staging, publication, and manifest paths
/// beneath the backup transaction root, but no migration program is selected and
/// no source read, copy, write, execution, current mutation, activation authority,
/// service, health, rollback, radio, watchdog, command, lease, or TX caller is
/// added.
/// </summary>
public sealed class VerifiedReleaseActivationMigrationPlanComposer
{
    private const int ExpectedSourceCount = 3;
    private const int MaximumMigrationIdentityLength = 96;

    public VerifiedReleaseActivationMigrationPlanComposer()
    {
        Snapshot = new VerifiedReleaseActivationMigrationPlanDiagnostics(
            Registered: true,
            ActivationPlanInputRegistered: true,
            ConfigurationBackupInputRegistered: true,
            ExactActivationPlanBindingRegistered: true,
            ExactConfigurationBackupBindingRegistered: true,
            ImmutableBackupValidationRegistered: true,
            NoOpMigrationPlanningRegistered: true,
            RequiredMigrationPlanningRegistered: true,
            SchemaTransitionValidationRegistered: true,
            MigrationIdentityValidationRegistered: true,
            StagedCopyPathPlanningRegistered: true,
            MigrationManifestPlanningRegistered: true,
            AtomicPublicationPlanningRegistered: true,
            MigrationRunnerSelectionRegistered: false,
            SourceReadRegistered: false,
            FileWriteRegistered: false,
            DirectoryMutationRegistered: false,
            MigrationExecutionRegistered: false,
            MigrationEvidenceRegistered: false,
            CurrentPointerMutationRegistered: false,
            ActivationAuthorityRegistered: false,
            OperationalCallerRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            HttpCallerRegistered: false,
            WebSocketCallerRegistered: false,
            HostedServiceCallerRegistered: false,
            TimerCallerRegistered: false,
            AetherRemoteCallerRegistered: false,
            ServiceControlCallerRegistered: false,
            HealthProbeCallerRegistered: false,
            RollbackCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationMigrationPlanDiagnostics Snapshot { get; }

    internal VerifiedReleaseActivationMigrationPlanReport Compose(
        VerifiedReleaseActivationPlanCompositionResult activationPlanResult,
        VerifiedReleaseActivationConfigurationBackupReport backupReport)
    {
        ArgumentNullException.ThrowIfNull(activationPlanResult);
        ArgumentNullException.ThrowIfNull(backupReport);

        if (!IsEligibleActivationPlan(activationPlanResult))
        {
            return VerifiedReleaseActivationMigrationPlanReport.Failure(
                VerifiedReleaseActivationMigrationPlanFailureCode
                    .ActivationPlanNotEligible,
                "A successful non-mutating verified activation plan is required.",
                activationPlanResult);
        }

        VerifiedReleaseActivationPlan? activationPlan = activationPlanResult.Plan;
        if (activationPlan is null)
        {
            return VerifiedReleaseActivationMigrationPlanReport.Failure(
                VerifiedReleaseActivationMigrationPlanFailureCode
                    .ActivationPlanUnavailable,
                "The successful activation plan does not retain its internal verified plan.",
                activationPlanResult);
        }
        if (!MatchesActivationPlan(activationPlanResult, activationPlan))
        {
            return VerifiedReleaseActivationMigrationPlanReport.Failure(
                VerifiedReleaseActivationMigrationPlanFailureCode
                    .ActivationPlanMismatch,
                "Activation plan metadata does not match its public summary.",
                activationPlanResult);
        }

        if (!IsEligibleConfigurationBackup(backupReport))
        {
            return VerifiedReleaseActivationMigrationPlanReport.Failure(
                VerifiedReleaseActivationMigrationPlanFailureCode
                    .ConfigurationBackupNotEligible,
                "A successful immutable exact-plan configuration backup is required.",
                activationPlanResult);
        }

        VerifiedReleaseActivationConfigurationBackup? configurationBackup =
            backupReport.Backup;
        if (configurationBackup is null)
        {
            return VerifiedReleaseActivationMigrationPlanReport.Failure(
                VerifiedReleaseActivationMigrationPlanFailureCode
                    .ConfigurationBackupUnavailable,
                "The successful configuration backup does not retain its internal verified artifact.",
                activationPlanResult);
        }
        if (!MatchesConfigurationBackup(
                activationPlan,
                backupReport,
                configurationBackup))
        {
            return VerifiedReleaseActivationMigrationPlanReport.Failure(
                VerifiedReleaseActivationMigrationPlanFailureCode
                    .ConfigurationBackupMismatch,
                "Configuration-backup metadata does not match the exact activation plan and immutable artifact.",
                activationPlanResult);
        }

        if (!ValidateMigrationDeclaration(activationPlan))
        {
            return VerifiedReleaseActivationMigrationPlanReport.Failure(
                VerifiedReleaseActivationMigrationPlanFailureCode
                    .MigrationDeclarationInvalid,
                "The signed migration declaration is incomplete or contradictory.",
                activationPlanResult);
        }

        try
        {
            if (activationPlan.MigrationKind == ReleaseMigrationKind.None)
            {
                return VerifiedReleaseActivationMigrationPlanReport.Success(
                    new VerifiedReleaseActivationMigrationPlan(
                        activationPlan,
                        configurationBackup,
                        ReleaseMigrationKind.None,
                        fromConfigurationSchemaVersion: null,
                        toConfigurationSchemaVersion: null,
                        migrationIdentity: string.Empty,
                        migrationRootPath: string.Empty,
                        stagingPath: string.Empty,
                        publishedPath: string.Empty,
                        manifestPath: string.Empty,
                        sources: []));
            }

            VerifiedReleaseActivationConfigurationBackupPlan backupPlan =
                configurationBackup.Plan;
            string backupPublishedPath = CanonicalDirectory(
                backupPlan.PublishedPath);
            string revisionRoot = CanonicalDirectory(
                Path.GetDirectoryName(backupPublishedPath) ?? string.Empty);
            string expectedActivationRoot = DirectChild(
                backupPlan.BackupRootPath,
                "activation");
            string expectedRevisionRoot = DirectChild(
                expectedActivationRoot,
                $"setup-{activationPlan.SetupRevision}");
            if (!PathEquals(revisionRoot, expectedRevisionRoot) ||
                !PathEquals(
                    Path.GetDirectoryName(backupPlan.StagingPath),
                    revisionRoot) ||
                !PathEquals(
                    Path.GetDirectoryName(backupPlan.PublishedPath),
                    revisionRoot) ||
                PathsOverlap(backupPlan.PublishedPath, activationPlan.DeploymentRootPath) ||
                PathsOverlap(backupPlan.PublishedPath, activationPlan.TargetReleasePath))
            {
                throw new InvalidOperationException(
                    "The immutable backup is not in the exact separated activation-backup layout.");
            }

            int fromSchema =
                activationPlan.MigrationFromConfigurationSchemaVersion!.Value;
            int toSchema =
                activationPlan.MigrationToConfigurationSchemaVersion!.Value;
            string transactionIdentity =
                $"schema-{fromSchema}-to-{toSchema}-" +
                activationPlan.MigrationIdentity;
            if (transactionIdentity.Length > 160 ||
                !IsBoundedAsciiToken(transactionIdentity, 160))
            {
                throw new InvalidOperationException(
                    "The migration transaction identity is unsafe.");
            }

            string migrationRoot = DirectChild(revisionRoot, "migration");
            string stagingPath = DirectChild(
                migrationRoot,
                $".{transactionIdentity}.staging");
            string publishedPath = DirectChild(
                migrationRoot,
                transactionIdentity);
            string manifestPath = DirectChild(
                publishedPath,
                "migration-manifest.json");
            if (PathEquals(stagingPath, publishedPath) ||
                PathsOverlap(stagingPath, backupPlan.PublishedPath) ||
                PathsOverlap(publishedPath, backupPlan.PublishedPath) ||
                PathsOverlap(stagingPath, activationPlan.DeploymentRootPath) ||
                PathsOverlap(publishedPath, activationPlan.DeploymentRootPath))
            {
                throw new InvalidOperationException(
                    "The staged migration layout overlaps immutable backup or deployment state.");
            }

            VerifiedReleaseActivationMigrationSourcePlan[] sources =
                backupPlan.Sources
                    .Select(source =>
                        new VerifiedReleaseActivationMigrationSourcePlan(
                            source.Kind,
                            DirectChild(
                                backupPlan.PublishedPath,
                                SourceDirectoryName(source.Kind)),
                            DirectChild(
                                stagingPath,
                                SourceDirectoryName(source.Kind))))
                    .ToArray();
            if (sources.Length != ExpectedSourceCount ||
                sources.Select(source => source.Kind).Distinct().Count() !=
                    ExpectedSourceCount ||
                sources.Select(source => source.SourcePath)
                    .Distinct(PathComparer).Count() != ExpectedSourceCount ||
                sources.Select(source => source.StagedPath)
                    .Distinct(PathComparer).Count() != ExpectedSourceCount ||
                sources.Any(source =>
                    !IsSameOrDescendant(
                        source.SourcePath,
                        backupPlan.PublishedPath) ||
                    !IsSameOrDescendant(source.StagedPath, stagingPath)))
            {
                throw new InvalidOperationException(
                    "The migration staged-copy source plan is incomplete or duplicated.");
            }

            return VerifiedReleaseActivationMigrationPlanReport.Success(
                new VerifiedReleaseActivationMigrationPlan(
                    activationPlan,
                    configurationBackup,
                    ReleaseMigrationKind.Required,
                    fromSchema,
                    toSchema,
                    activationPlan.MigrationIdentity,
                    migrationRoot,
                    stagingPath,
                    publishedPath,
                    manifestPath,
                    sources));
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return VerifiedReleaseActivationMigrationPlanReport.Failure(
                VerifiedReleaseActivationMigrationPlanFailureCode
                    .MigrationLayoutUnsafe,
                "The exact immutable backup cannot produce a separated bounded migration transaction.",
                activationPlanResult);
        }
    }

    private static bool IsEligibleActivationPlan(
        VerifiedReleaseActivationPlanCompositionResult result) =>
        result.Succeeded &&
        result.FailureCode == VerifiedReleaseActivationPlanFailureCode.None &&
        result.SetupRevision is > 0 &&
        result.PackageCount == 4 &&
        result.PublishedBytes > 0 &&
        result.TargetConfigurationSchemaVersion is > 0 &&
        result.MigrationKind is ReleaseMigrationKind.None or
            ReleaseMigrationKind.Required &&
        result.TxLeaseAdmissionClosureRequired &&
        result.RadioAuthoritativeIdleRequired &&
        result.WatchdogsDisarmedRequired &&
        result.ConfigurationBackupRequired &&
        result.AtomicCurrentPointerSwitchRequired &&
        result.ServiceHealthVerificationRequired &&
        result.AutomaticRollbackRequired &&
        result.OperatorApprovalRequired &&
        !result.CurrentPointerMutationPerformed &&
        !result.ActivationPerformed;

    private static bool MatchesActivationPlan(
        VerifiedReleaseActivationPlanCompositionResult result,
        VerifiedReleaseActivationPlan plan) =>
        result.SetupRevision == plan.SetupRevision &&
        string.Equals(
            result.InstalledReleaseIdentity,
            plan.InstalledReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            result.TargetReleaseIdentity,
            plan.TargetReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(result.TargetVersion, plan.TargetVersion, StringComparison.Ordinal) &&
        result.Architecture == plan.Architecture &&
        result.PackageCount == plan.Packages.Count &&
        result.TargetConfigurationSchemaVersion ==
            plan.TargetConfigurationSchemaVersion &&
        result.MigrationKind == plan.MigrationKind &&
        result.MigrationRequired == plan.MigrationRequired &&
        result.RestartServiceCount == plan.RestartServiceCount &&
        result.HostRestartRequired == plan.RestartHost &&
        plan.ConfigurationBackupRequired &&
        plan.AtomicCurrentPointerSwitchRequired &&
        plan.ServiceHealthVerificationRequired &&
        plan.AutomaticRollbackRequired &&
        plan.OperatorApprovalRequired;

    private static bool IsEligibleConfigurationBackup(
        VerifiedReleaseActivationConfigurationBackupReport report) =>
        report.Succeeded &&
        report.FailureCode ==
            VerifiedReleaseActivationConfigurationBackupFailureCode.None &&
        report.SetupRevision is > 0 &&
        !string.IsNullOrEmpty(report.InstalledReleaseIdentity) &&
        !string.IsNullOrEmpty(report.TargetReleaseIdentity) &&
        report.SourceDirectoryCount == ExpectedSourceCount &&
        report.DirectoryCount >= ExpectedSourceCount &&
        report.FileCount >= 0 &&
        report.BackupBytes >= 0 &&
        report.SourceSnapshotStable &&
        report.ManifestWritten &&
        report.StagingTreeImmutable &&
        report.AtomicPublicationCompleted &&
        report.PublishedTreeValidated &&
        !report.ExistingBackupOverwritten &&
        report.ConfigurationBackupReady &&
        !report.CurrentPointerChanged &&
        !report.ActivationPerformed &&
        !report.ReconciliationRequired;

    private static bool MatchesConfigurationBackup(
        VerifiedReleaseActivationPlan activationPlan,
        VerifiedReleaseActivationConfigurationBackupReport report,
        VerifiedReleaseActivationConfigurationBackup backup)
    {
        VerifiedReleaseActivationConfigurationBackupPlan plan = backup.Plan;
        if (!ReferenceEquals(plan.ActivationPlan, activationPlan) ||
            report.SetupRevision != activationPlan.SetupRevision ||
            !string.Equals(
                report.InstalledReleaseIdentity,
                activationPlan.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.TargetReleaseIdentity,
                activationPlan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            report.SourceDirectoryCount != plan.Sources.Count ||
            report.DirectoryCount != backup.DirectoryCount ||
            report.FileCount != backup.FileCount ||
            report.BackupBytes != backup.BackupBytes ||
            backup.ManifestSha256.Length != 32 ||
            backup.CompletedAt == default ||
            plan.Sources.Count != ExpectedSourceCount ||
            plan.ExistingBackupOverwriteAllowed ||
            !plan.AtomicPublicationRequired)
        {
            return false;
        }

        try
        {
            string publishedPath = CanonicalDirectory(plan.PublishedPath);
            string manifestPath = CanonicalFile(plan.ManifestPath);
            if (!PathEquals(
                    Path.GetDirectoryName(manifestPath),
                    publishedPath) ||
                !string.Equals(
                    Path.GetFileName(manifestPath),
                    "backup-manifest.json",
                    StringComparison.Ordinal) ||
                PathEquals(plan.StagingPath, plan.PublishedPath))
            {
                return false;
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return true;
    }

    private static bool ValidateMigrationDeclaration(
        VerifiedReleaseActivationPlan plan) =>
        plan.MigrationKind switch
        {
            ReleaseMigrationKind.None =>
                plan.MigrationFromConfigurationSchemaVersion is null &&
                plan.MigrationToConfigurationSchemaVersion is null &&
                string.IsNullOrEmpty(plan.MigrationIdentity),
            ReleaseMigrationKind.Required =>
                plan.MigrationFromConfigurationSchemaVersion is >= 1 &&
                plan.MigrationToConfigurationSchemaVersion ==
                    plan.TargetConfigurationSchemaVersion &&
                plan.MigrationFromConfigurationSchemaVersion <
                    plan.MigrationToConfigurationSchemaVersion &&
                IsBoundedAsciiToken(
                    plan.MigrationIdentity,
                    MaximumMigrationIdentityLength) &&
                plan.RestartGatewayWeb,
            _ => false
        };

    private static string SourceDirectoryName(
        VerifiedReleaseActivationConfigurationBackupSourceKind kind) =>
        kind switch
        {
            VerifiedReleaseActivationConfigurationBackupSourceKind.Configuration =>
                "configuration",
            VerifiedReleaseActivationConfigurationBackupSourceKind.State => "state",
            VerifiedReleaseActivationConfigurationBackupSourceKind.Secret => "secrets",
            _ => throw new InvalidOperationException(
                "The configuration-backup source kind is unsupported.")
        };

    private static bool IsBoundedAsciiToken(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }
        return true;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string CanonicalDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
        {
            throw new InvalidOperationException(
                "A canonical absolute directory is required.");
        }
        string canonical = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(value));
        if (!string.Equals(value, canonical, PathComparison))
        {
            throw new InvalidOperationException(
                "The directory path must already be canonical.");
        }
        return canonical;
    }

    private static string CanonicalFile(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
        {
            throw new InvalidOperationException(
                "A canonical absolute file path is required.");
        }
        string canonical = Path.GetFullPath(value);
        if (!string.Equals(value, canonical, PathComparison))
        {
            throw new InvalidOperationException(
                "The file path must already be canonical.");
        }
        return canonical;
    }

    private static string DirectChild(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(child) ||
            child is "." or ".." ||
            child.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException(
                "A safe direct child name is required.");
        }

        string canonicalParent = CanonicalDirectory(parent);
        string result = Path.GetFullPath(Path.Combine(canonicalParent, child));
        if (!PathEquals(Path.GetDirectoryName(result), canonicalParent))
        {
            throw new InvalidOperationException(
                "The planned path escaped its parent directory.");
        }
        return result;
    }

    private static bool PathsOverlap(string left, string right) =>
        IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left);

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        if (PathEquals(candidate, parent))
        {
            return true;
        }

        string canonicalCandidate = CanonicalDirectory(candidate);
        string canonicalParent = CanonicalDirectory(parent);
        string prefix = canonicalParent.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalParent
            : canonicalParent + Path.DirectorySeparatorChar;
        return canonicalCandidate.StartsWith(prefix, PathComparison);
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);
    }
}
