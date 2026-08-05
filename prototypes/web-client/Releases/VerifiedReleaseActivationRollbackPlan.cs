using System.Collections.ObjectModel;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationRollbackPlanFailureCode
{
    None = 0,
    ActivationPlanNotEligible = 1,
    ActivationPlanUnavailable = 2,
    ActivationPlanMismatch = 3,
    ConfigurationBackupNotEligible = 4,
    ConfigurationBackupUnavailable = 5,
    ConfigurationBackupMismatch = 6,
    MigrationPlanNotEligible = 7,
    MigrationPlanUnavailable = 8,
    MigrationPlanMismatch = 9,
    ServiceControlPlanNotEligible = 10,
    ServiceControlPlanUnavailable = 11,
    ServiceControlPlanMismatch = 12,
    HealthPlanNotEligible = 13,
    HealthPlanUnavailable = 14,
    HealthPlanMismatch = 15,
    HostRestartUnsupported = 16,
    RollbackLayoutUnsafe = 17
}

public sealed record VerifiedReleaseActivationRollbackPlanReport(
    bool Succeeded,
    VerifiedReleaseActivationRollbackPlanFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    bool MigrationRequired,
    int RestoreSourceCount,
    int StopActionCount,
    int StartActionCount,
    int HealthTargetCount,
    bool ExactActivationPlanBound,
    bool ExactConfigurationBackupBound,
    bool ExactMigrationPlanBound,
    bool ExactServiceControlPlanBound,
    bool ExactHealthPlanBound,
    bool ImmutableOriginalBackupBound,
    bool OriginalBackupRestorePlanned,
    bool ReverseMigrationRunnerPlanned,
    bool TargetServiceStopPlanned,
    bool ConfigurationRestorePlanned,
    bool AtomicCurrentPointerRollbackPlanned,
    bool InstalledServiceStartPlanned,
    bool InstalledHealthVerificationPlanned,
    bool HostRestartRequired,
    bool HostRestartRollbackPlanned,
    bool SourceReadPerformed,
    bool FileWritePerformed,
    bool DirectoryMutationPerformed,
    bool ProcessInvocationPerformed,
    bool SystemdCommandPerformed,
    bool NetworkRequestPerformed,
    bool HealthProbePerformed,
    bool CurrentPointerChanged,
    bool RollbackPerformed,
    bool RollbackReady,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationRollbackPlan? Plan { get; init; }

    internal static VerifiedReleaseActivationRollbackPlanReport Failure(
        VerifiedReleaseActivationRollbackPlanFailureCode failureCode,
        string message,
        VerifiedReleaseActivationPlanCompositionResult? activation = null) =>
        new(
            false,
            failureCode,
            message,
            activation?.SetupRevision,
            activation?.InstalledReleaseIdentity ?? string.Empty,
            activation?.TargetReleaseIdentity ?? string.Empty,
            activation?.MigrationRequired ?? false,
            RestoreSourceCount: 0,
            StopActionCount: 0,
            StartActionCount: 0,
            HealthTargetCount: 0,
            ExactActivationPlanBound: false,
            ExactConfigurationBackupBound: false,
            ExactMigrationPlanBound: false,
            ExactServiceControlPlanBound: false,
            ExactHealthPlanBound: false,
            ImmutableOriginalBackupBound: false,
            OriginalBackupRestorePlanned: false,
            ReverseMigrationRunnerPlanned: false,
            TargetServiceStopPlanned: false,
            ConfigurationRestorePlanned: false,
            AtomicCurrentPointerRollbackPlanned: false,
            InstalledServiceStartPlanned: false,
            InstalledHealthVerificationPlanned: false,
            HostRestartRequired: activation?.HostRestartRequired ?? false,
            HostRestartRollbackPlanned: false,
            SourceReadPerformed: false,
            FileWritePerformed: false,
            DirectoryMutationPerformed: false,
            ProcessInvocationPerformed: false,
            SystemdCommandPerformed: false,
            NetworkRequestPerformed: false,
            HealthProbePerformed: false,
            CurrentPointerChanged: false,
            RollbackPerformed: false,
            RollbackReady: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationRollbackPlanReport Success(
        VerifiedReleaseActivationRollbackPlan plan) =>
        new(
            true,
            VerifiedReleaseActivationRollbackPlanFailureCode.None,
            plan.MigrationPlan.MigrationRequired
                ? "An exact callerless rollback transaction was composed to restore the immutable original backup rather than reverse-running migration code."
                : "An exact callerless rollback transaction was composed from the immutable original backup without executing any rollback action.",
            plan.ActivationPlan.SetupRevision,
            plan.ActivationPlan.InstalledReleaseIdentity,
            plan.ActivationPlan.TargetReleaseIdentity,
            plan.MigrationPlan.MigrationRequired,
            plan.RestoreSources.Count,
            plan.ServiceControlPlan.StopActions.Count,
            plan.ServiceControlPlan.StartActions.Count,
            plan.HealthPlan.Targets.Count,
            ExactActivationPlanBound: true,
            ExactConfigurationBackupBound: true,
            ExactMigrationPlanBound: true,
            ExactServiceControlPlanBound: true,
            ExactHealthPlanBound: true,
            ImmutableOriginalBackupBound: true,
            OriginalBackupRestorePlanned: true,
            ReverseMigrationRunnerPlanned: false,
            TargetServiceStopPlanned:
                plan.ServiceControlPlan.StopActions.Count > 0,
            ConfigurationRestorePlanned: true,
            AtomicCurrentPointerRollbackPlanned: true,
            InstalledServiceStartPlanned:
                plan.ServiceControlPlan.StartActions.Count > 0,
            InstalledHealthVerificationPlanned: true,
            HostRestartRequired: false,
            HostRestartRollbackPlanned: false,
            SourceReadPerformed: false,
            FileWritePerformed: false,
            DirectoryMutationPerformed: false,
            ProcessInvocationPerformed: false,
            SystemdCommandPerformed: false,
            NetworkRequestPerformed: false,
            HealthProbePerformed: false,
            CurrentPointerChanged: false,
            RollbackPerformed: false,
            RollbackReady: false,
            ActivationAuthorized: false)
        {
            Plan = plan
        };
}

public sealed record VerifiedReleaseActivationRollbackPlanDiagnostics(
    bool Registered,
    bool ActivationPlanInputRegistered,
    bool ConfigurationBackupInputRegistered,
    bool MigrationPlanInputRegistered,
    bool ServiceControlPlanInputRegistered,
    bool HealthPlanInputRegistered,
    bool ExactActivationPlanBindingRegistered,
    bool ExactConfigurationBackupBindingRegistered,
    bool ExactMigrationPlanBindingRegistered,
    bool ExactServiceControlPlanBindingRegistered,
    bool ExactHealthPlanBindingRegistered,
    bool ImmutableOriginalBackupBindingRegistered,
    bool OriginalBackupRestorePlanningRegistered,
    bool ReverseMigrationRunnerPlanningRegistered,
    bool ThreeSourceRestorePlanningRegistered,
    bool SameParentRestoreStagingRegistered,
    bool DisplacedLiveTreePlanningRegistered,
    bool TargetServiceStopPlanningRegistered,
    bool AtomicCurrentPointerRollbackPlanningRegistered,
    bool InstalledServiceStartPlanningRegistered,
    bool InstalledHealthVerificationPlanningRegistered,
    bool HostRestartRollbackPlanningRegistered,
    bool SourceReadRegistered,
    bool FileWriteRegistered,
    bool DirectoryMutationRegistered,
    bool ProcessInvocationRegistered,
    bool SystemdCommandRegistered,
    bool NetworkRequestRegistered,
    bool HealthProbeRegistered,
    bool RollbackEvidenceRegistered,
    bool RollbackExecutionRegistered,
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
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed record VerifiedReleaseActivationRollbackRestoreSource(
    VerifiedReleaseActivationConfigurationBackupSourceKind Kind,
    string ImmutableBackupPath,
    string LiveDestinationPath,
    string RestoreStagingPath,
    string DisplacedLivePath);

internal sealed class VerifiedReleaseActivationRollbackPlan
{
    private readonly ReadOnlyCollection<
        VerifiedReleaseActivationRollbackRestoreSource> m_restoreSources;

    internal VerifiedReleaseActivationRollbackPlan(
        VerifiedReleaseActivationPlan activationPlan,
        VerifiedReleaseActivationConfigurationBackup configurationBackup,
        VerifiedReleaseActivationMigrationPlan migrationPlan,
        VerifiedReleaseActivationServiceControlPlan serviceControlPlan,
        VerifiedReleaseActivationHealthVerificationPlan healthPlan,
        string rollbackIdentity,
        string expectedCurrentLinkTarget,
        string rollbackCurrentLinkTarget,
        string temporaryCurrentPointerPath,
        IReadOnlyList<VerifiedReleaseActivationRollbackRestoreSource>
            restoreSources)
    {
        ActivationPlan = activationPlan ??
            throw new ArgumentNullException(nameof(activationPlan));
        ConfigurationBackup = configurationBackup ??
            throw new ArgumentNullException(nameof(configurationBackup));
        MigrationPlan = migrationPlan ??
            throw new ArgumentNullException(nameof(migrationPlan));
        ServiceControlPlan = serviceControlPlan ??
            throw new ArgumentNullException(nameof(serviceControlPlan));
        HealthPlan = healthPlan ??
            throw new ArgumentNullException(nameof(healthPlan));
        RollbackIdentity = rollbackIdentity;
        ExpectedCurrentLinkTarget = expectedCurrentLinkTarget;
        RollbackCurrentLinkTarget = rollbackCurrentLinkTarget;
        TemporaryCurrentPointerPath = temporaryCurrentPointerPath;
        m_restoreSources = Array.AsReadOnly(
            (restoreSources ??
                throw new ArgumentNullException(nameof(restoreSources))).ToArray());
    }

    internal VerifiedReleaseActivationPlan ActivationPlan { get; }
    internal VerifiedReleaseActivationConfigurationBackup ConfigurationBackup
    {
        get;
    }
    internal VerifiedReleaseActivationMigrationPlan MigrationPlan { get; }
    internal VerifiedReleaseActivationServiceControlPlan ServiceControlPlan
    {
        get;
    }
    internal VerifiedReleaseActivationHealthVerificationPlan HealthPlan { get; }
    internal string RollbackIdentity { get; }
    internal string ExpectedCurrentLinkTarget { get; }
    internal string RollbackCurrentLinkTarget { get; }
    internal string TemporaryCurrentPointerPath { get; }
    internal IReadOnlyList<VerifiedReleaseActivationRollbackRestoreSource>
        RestoreSources => m_restoreSources;
    internal bool ReverseMigrationRunnerRequired => false;
}

/// <summary>
/// Pure fail-closed composition of exact activation, immutable original-backup,
/// migration, service-control, and health-plan tokens into one future rollback
/// transaction. Required migration is reversed only by restoring the original
/// immutable backup; migration code is never run backward. Three live roots are
/// assigned same-parent staging and displaced-tree identities, followed by an
/// atomic current-pointer return, deterministic installed-service starts, and
/// installed-release health verification. Signed host-restart transactions fail
/// closed because no reviewed host-restart rollback transport exists. The planner
/// performs no source read, write, directory mutation, process, systemd command,
/// network request, health probe, current-pointer mutation, rollback, activation,
/// radio, watchdog, command, lease, or TX action and exposes no operational caller.
/// </summary>
public sealed class VerifiedReleaseActivationRollbackPlanComposer
{
    private const int ExpectedRestoreSourceCount = 3;
    private const int MaximumRollbackIdentityLength = 192;

    public VerifiedReleaseActivationRollbackPlanComposer()
    {
        Snapshot = new VerifiedReleaseActivationRollbackPlanDiagnostics(
            Registered: true,
            ActivationPlanInputRegistered: true,
            ConfigurationBackupInputRegistered: true,
            MigrationPlanInputRegistered: true,
            ServiceControlPlanInputRegistered: true,
            HealthPlanInputRegistered: true,
            ExactActivationPlanBindingRegistered: true,
            ExactConfigurationBackupBindingRegistered: true,
            ExactMigrationPlanBindingRegistered: true,
            ExactServiceControlPlanBindingRegistered: true,
            ExactHealthPlanBindingRegistered: true,
            ImmutableOriginalBackupBindingRegistered: true,
            OriginalBackupRestorePlanningRegistered: true,
            ReverseMigrationRunnerPlanningRegistered: false,
            ThreeSourceRestorePlanningRegistered: true,
            SameParentRestoreStagingRegistered: true,
            DisplacedLiveTreePlanningRegistered: true,
            TargetServiceStopPlanningRegistered: true,
            AtomicCurrentPointerRollbackPlanningRegistered: true,
            InstalledServiceStartPlanningRegistered: true,
            InstalledHealthVerificationPlanningRegistered: true,
            HostRestartRollbackPlanningRegistered: false,
            SourceReadRegistered: false,
            FileWriteRegistered: false,
            DirectoryMutationRegistered: false,
            ProcessInvocationRegistered: false,
            SystemdCommandRegistered: false,
            NetworkRequestRegistered: false,
            HealthProbeRegistered: false,
            RollbackEvidenceRegistered: false,
            RollbackExecutionRegistered: false,
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
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationRollbackPlanDiagnostics Snapshot { get; }

    internal VerifiedReleaseActivationRollbackPlanReport Compose(
        VerifiedReleaseActivationPlanCompositionResult activationResult,
        VerifiedReleaseActivationConfigurationBackupReport backupReport,
        VerifiedReleaseActivationMigrationPlanReport migrationReport,
        VerifiedReleaseActivationServiceControlPlanReport serviceControlReport,
        VerifiedReleaseActivationHealthVerificationPlanReport healthReport)
    {
        ArgumentNullException.ThrowIfNull(activationResult);
        ArgumentNullException.ThrowIfNull(backupReport);
        ArgumentNullException.ThrowIfNull(migrationReport);
        ArgumentNullException.ThrowIfNull(serviceControlReport);
        ArgumentNullException.ThrowIfNull(healthReport);

        if (!IsEligibleActivationResult(activationResult))
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .ActivationPlanNotEligible,
                "A successful non-mutating exact activation plan is required.",
                activationResult);
        }
        VerifiedReleaseActivationPlan? activation = activationResult.Plan;
        if (activation is null)
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .ActivationPlanUnavailable,
                "The successful activation report does not retain its exact internal plan.",
                activationResult);
        }
        if (!MatchesActivationResult(activationResult, activation))
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .ActivationPlanMismatch,
                "Activation metadata does not match its exact internal plan.",
                activationResult);
        }

        if (!IsEligibleBackupReport(backupReport))
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .ConfigurationBackupNotEligible,
                "A successful immutable exact-plan configuration backup is required.",
                activationResult);
        }
        VerifiedReleaseActivationConfigurationBackup? backup = backupReport.Backup;
        if (backup is null)
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .ConfigurationBackupUnavailable,
                "The successful configuration backup does not retain its exact internal artifact.",
                activationResult);
        }
        if (!MatchesBackup(activation, backupReport, backup))
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .ConfigurationBackupMismatch,
                "Configuration-backup metadata does not match the exact activation plan and immutable artifact.",
                activationResult);
        }

        if (!IsEligibleMigrationReport(migrationReport))
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .MigrationPlanNotEligible,
                "A successful exact migration plan is required.",
                activationResult);
        }
        VerifiedReleaseActivationMigrationPlan? migration = migrationReport.Plan;
        if (migration is null)
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .MigrationPlanUnavailable,
                "The successful migration report does not retain its exact internal plan.",
                activationResult);
        }
        if (!MatchesMigration(
                activation,
                backup,
                migrationReport,
                migration))
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .MigrationPlanMismatch,
                "Migration metadata does not match the exact activation plan and immutable original backup.",
                activationResult);
        }

        if (!IsEligibleServiceControlReport(serviceControlReport))
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .ServiceControlPlanNotEligible,
                "A successful exact non-executing service-control plan is required.",
                activationResult);
        }
        VerifiedReleaseActivationServiceControlPlan? serviceControl =
            serviceControlReport.Plan;
        if (serviceControl is null)
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .ServiceControlPlanUnavailable,
                "The successful service-control report does not retain its exact internal plan.",
                activationResult);
        }
        if (!MatchesServiceControl(
                activation,
                serviceControlReport,
                serviceControl))
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .ServiceControlPlanMismatch,
                "Service-control metadata or action ordering does not match the exact activation plan.",
                activationResult);
        }
        if (activation.RestartHost || serviceControl.HostRestartRequired)
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .HostRestartUnsupported,
                "Host-restart rollback remains outside this callerless planning boundary.",
                activationResult);
        }

        if (!IsEligibleHealthReport(healthReport))
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .HealthPlanNotEligible,
                "A successful exact non-executing health-verification plan is required.",
                activationResult);
        }
        VerifiedReleaseActivationHealthVerificationPlan? health = healthReport.Plan;
        if (health is null)
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .HealthPlanUnavailable,
                "The successful health report does not retain its exact internal plan.",
                activationResult);
        }
        if (!MatchesHealth(serviceControl, healthReport, health))
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .HealthPlanMismatch,
                "Health metadata or contracts do not match the exact service-control plan.",
                activationResult);
        }

        try
        {
            string rollbackIdentity =
                $"{activation.TargetReleaseIdentity}-to-" +
                $"{activation.InstalledReleaseIdentity}-setup-" +
                activation.SetupRevision;
            if (!IsBoundedAsciiToken(
                    rollbackIdentity,
                    MaximumRollbackIdentityLength))
            {
                throw new InvalidOperationException(
                    "The rollback identity is not a bounded ASCII token.");
            }

            VerifiedReleaseActivationConfigurationBackupPlan backupPlan =
                backup.Plan;
            string backupPublishedPath = CanonicalDirectory(
                backupPlan.PublishedPath);
            string expectedBackupManifest = DirectChild(
                backupPublishedPath,
                "backup-manifest.json");
            if (!PathEquals(expectedBackupManifest, backupPlan.ManifestPath) ||
                PathsOverlap(
                    backupPublishedPath,
                    activation.DeploymentRootPath) ||
                PathsOverlap(
                    backupPublishedPath,
                    activation.ReleaseRootPath))
            {
                throw new InvalidOperationException(
                    "The immutable backup is not separated from the deployment layout.");
            }

            VerifiedReleaseActivationRollbackRestoreSource[] restoreSources =
                backupPlan.Sources
                    .OrderBy(source => source.Kind)
                    .Select(source => CreateRestoreSource(
                        source,
                        backupPublishedPath,
                        rollbackIdentity,
                        activation))
                    .ToArray();
            if (!ValidateRestoreSources(
                    restoreSources,
                    backupPlan,
                    activation))
            {
                throw new InvalidOperationException(
                    "The rollback restore-source layout is incomplete or unsafe.");
            }

            string currentParent = CanonicalDirectory(
                Path.GetDirectoryName(activation.CurrentPointerPath) ??
                    string.Empty);
            string currentLeaf = Path.GetFileName(activation.CurrentPointerPath);
            if (string.IsNullOrEmpty(currentLeaf) ||
                !PathEquals(currentParent, activation.DeploymentRootPath))
            {
                throw new InvalidOperationException(
                    "The current pointer is not a direct child of the deployment root.");
            }
            string temporaryCurrentPointer = DirectChild(
                currentParent,
                $".{currentLeaf}.{rollbackIdentity}.rollback");
            if (PathEquals(
                    temporaryCurrentPointer,
                    activation.CurrentPointerPath) ||
                string.IsNullOrEmpty(activation.TargetCurrentLinkTarget) ||
                string.IsNullOrEmpty(activation.InstalledCurrentLinkTarget) ||
                string.Equals(
                    activation.TargetCurrentLinkTarget,
                    activation.InstalledCurrentLinkTarget,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The rollback pointer identities are incomplete or duplicated.");
            }

            VerifiedReleaseActivationRollbackPlan plan = new(
                activation,
                backup,
                migration,
                serviceControl,
                health,
                rollbackIdentity,
                activation.TargetCurrentLinkTarget,
                activation.InstalledCurrentLinkTarget,
                temporaryCurrentPointer,
                restoreSources);
            if (!ValidateComposedPlan(plan))
            {
                throw new InvalidOperationException(
                    "The exact rollback transaction is incomplete or contradictory.");
            }
            return VerifiedReleaseActivationRollbackPlanReport.Success(plan);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return VerifiedReleaseActivationRollbackPlanReport.Failure(
                VerifiedReleaseActivationRollbackPlanFailureCode
                    .RollbackLayoutUnsafe,
                "The exact activation artifacts cannot produce a separated bounded rollback transaction.",
                activationResult);
        }
    }

    private static VerifiedReleaseActivationRollbackRestoreSource
        CreateRestoreSource(
            VerifiedReleaseActivationConfigurationBackupSourcePlan source,
            string backupPublishedPath,
            string rollbackIdentity,
            VerifiedReleaseActivationPlan activation)
    {
        string backupSource = DirectChild(
            backupPublishedPath,
            SourceDirectoryName(source.Kind));
        string liveDestination = CanonicalDirectory(source.SourcePath);
        string liveParent = CanonicalDirectory(
            Path.GetDirectoryName(liveDestination) ?? string.Empty);
        string liveLeaf = Path.GetFileName(liveDestination);
        if (string.IsNullOrEmpty(liveLeaf) ||
            PathEquals(liveDestination, activation.DeploymentRootPath) ||
            PathsOverlap(liveDestination, activation.ReleaseRootPath) ||
            PathsOverlap(liveDestination, backupPublishedPath))
        {
            throw new InvalidOperationException(
                "A rollback destination overlaps immutable release or backup state.");
        }
        string staging = DirectChild(
            liveParent,
            $".{liveLeaf}.{rollbackIdentity}.restore-staging");
        string displaced = DirectChild(
            liveParent,
            $".{liveLeaf}.{rollbackIdentity}.restore-displaced");
        return new VerifiedReleaseActivationRollbackRestoreSource(
            source.Kind,
            backupSource,
            liveDestination,
            staging,
            displaced);
    }

    private static bool ValidateRestoreSources(
        IReadOnlyList<VerifiedReleaseActivationRollbackRestoreSource> sources,
        VerifiedReleaseActivationConfigurationBackupPlan backupPlan,
        VerifiedReleaseActivationPlan activation)
    {
        if (sources.Count != ExpectedRestoreSourceCount ||
            sources.Select(source => source.Kind).Distinct().Count() !=
                ExpectedRestoreSourceCount ||
            sources.Select(source => source.ImmutableBackupPath)
                .Distinct(PathComparer).Count() != ExpectedRestoreSourceCount ||
            sources.Select(source => source.LiveDestinationPath)
                .Distinct(PathComparer).Count() != ExpectedRestoreSourceCount ||
            sources.Select(source => source.RestoreStagingPath)
                .Distinct(PathComparer).Count() != ExpectedRestoreSourceCount ||
            sources.Select(source => source.DisplacedLivePath)
                .Distinct(PathComparer).Count() != ExpectedRestoreSourceCount)
        {
            return false;
        }

        foreach (VerifiedReleaseActivationRollbackRestoreSource source in sources)
        {
            VerifiedReleaseActivationConfigurationBackupSourcePlan? expected =
                backupPlan.Sources.SingleOrDefault(item => item.Kind == source.Kind);
            if (expected is null ||
                !PathEquals(source.LiveDestinationPath, expected.SourcePath) ||
                !PathEquals(
                    source.ImmutableBackupPath,
                    DirectChild(
                        backupPlan.PublishedPath,
                        SourceDirectoryName(source.Kind))) ||
                !PathEquals(
                    Path.GetDirectoryName(source.RestoreStagingPath),
                    Path.GetDirectoryName(source.LiveDestinationPath)) ||
                !PathEquals(
                    Path.GetDirectoryName(source.DisplacedLivePath),
                    Path.GetDirectoryName(source.LiveDestinationPath)) ||
                PathEquals(
                    source.RestoreStagingPath,
                    source.DisplacedLivePath) ||
                PathEquals(
                    source.RestoreStagingPath,
                    source.LiveDestinationPath) ||
                PathEquals(
                    source.DisplacedLivePath,
                    source.LiveDestinationPath) ||
                PathsOverlap(
                    source.RestoreStagingPath,
                    activation.DeploymentRootPath) ||
                PathsOverlap(
                    source.DisplacedLivePath,
                    activation.DeploymentRootPath) ||
                PathsOverlap(
                    source.RestoreStagingPath,
                    backupPlan.PublishedPath) ||
                PathsOverlap(
                    source.DisplacedLivePath,
                    backupPlan.PublishedPath))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidateComposedPlan(
        VerifiedReleaseActivationRollbackPlan plan) =>
        ReferenceEquals(
            plan.ConfigurationBackup.Plan.ActivationPlan,
            plan.ActivationPlan) &&
        ReferenceEquals(
            plan.MigrationPlan.ActivationPlan,
            plan.ActivationPlan) &&
        ReferenceEquals(
            plan.MigrationPlan.ConfigurationBackup,
            plan.ConfigurationBackup) &&
        ReferenceEquals(
            plan.ServiceControlPlan.ActivationPlan,
            plan.ActivationPlan) &&
        ReferenceEquals(
            plan.HealthPlan.ServiceControlPlan,
            plan.ServiceControlPlan) &&
        !plan.ActivationPlan.RestartHost &&
        !plan.ServiceControlPlan.HostRestartRequired &&
        plan.ServiceControlPlan.HostRestartActions.Count == 0 &&
        plan.RestoreSources.Count == ExpectedRestoreSourceCount &&
        !plan.ReverseMigrationRunnerRequired &&
        !string.IsNullOrEmpty(plan.RollbackIdentity) &&
        !string.IsNullOrEmpty(plan.ExpectedCurrentLinkTarget) &&
        !string.IsNullOrEmpty(plan.RollbackCurrentLinkTarget) &&
        !string.Equals(
            plan.ExpectedCurrentLinkTarget,
            plan.RollbackCurrentLinkTarget,
            StringComparison.Ordinal) &&
        !PathEquals(
            plan.TemporaryCurrentPointerPath,
            plan.ActivationPlan.CurrentPointerPath);

    private static bool IsEligibleActivationResult(
        VerifiedReleaseActivationPlanCompositionResult result) =>
        result.Succeeded &&
        result.FailureCode == VerifiedReleaseActivationPlanFailureCode.None &&
        result.SetupRevision is > 0 &&
        !string.IsNullOrEmpty(result.InstalledReleaseIdentity) &&
        !string.IsNullOrEmpty(result.TargetReleaseIdentity) &&
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

    private static bool MatchesActivationResult(
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
        string.Equals(
            result.TargetVersion,
            plan.TargetVersion,
            StringComparison.Ordinal) &&
        result.Architecture == plan.Architecture &&
        result.PackageCount == plan.Packages.Count &&
        result.TargetConfigurationSchemaVersion ==
            plan.TargetConfigurationSchemaVersion &&
        result.MigrationKind == plan.MigrationKind &&
        result.MigrationRequired == plan.MigrationRequired &&
        result.RestartServiceCount == plan.RestartServiceCount &&
        result.HostRestartRequired == plan.RestartHost &&
        plan.AutomaticRollbackRequired &&
        plan.OperatorApprovalRequired;

    private static bool IsEligibleBackupReport(
        VerifiedReleaseActivationConfigurationBackupReport report) =>
        report.Succeeded &&
        report.FailureCode ==
            VerifiedReleaseActivationConfigurationBackupFailureCode.None &&
        report.SetupRevision is > 0 &&
        report.SourceDirectoryCount == ExpectedRestoreSourceCount &&
        report.DirectoryCount >= ExpectedRestoreSourceCount &&
        report.FileCount >= 0 &&
        report.BackupBytes > 0 &&
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

    private static bool MatchesBackup(
        VerifiedReleaseActivationPlan activation,
        VerifiedReleaseActivationConfigurationBackupReport report,
        VerifiedReleaseActivationConfigurationBackup backup)
    {
        VerifiedReleaseActivationConfigurationBackupPlan plan = backup.Plan;
        if (!ReferenceEquals(plan.ActivationPlan, activation) ||
            report.SetupRevision != activation.SetupRevision ||
            !string.Equals(
                report.InstalledReleaseIdentity,
                activation.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.TargetReleaseIdentity,
                activation.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            report.SourceDirectoryCount != plan.Sources.Count ||
            report.DirectoryCount != backup.DirectoryCount ||
            report.FileCount != backup.FileCount ||
            report.BackupBytes != backup.BackupBytes ||
            plan.Sources.Count != ExpectedRestoreSourceCount ||
            backup.ManifestSha256.Length != 32 ||
            backup.CompletedAt == default ||
            plan.ExistingBackupOverwriteAllowed ||
            !plan.AtomicPublicationRequired)
        {
            return false;
        }

        try
        {
            string backupRoot = CanonicalDirectory(plan.BackupRootPath);
            string published = CanonicalDirectory(plan.PublishedPath);
            string staging = CanonicalDirectory(plan.StagingPath);
            string manifest = Path.GetFullPath(plan.ManifestPath);
            if (!Path.IsPathRooted(manifest) ||
                !string.Equals(plan.ManifestPath, manifest, PathComparison) ||
                !PathEquals(Path.GetDirectoryName(manifest), published) ||
                !string.Equals(
                    Path.GetFileName(manifest),
                    "backup-manifest.json",
                    StringComparison.Ordinal) ||
                PathEquals(staging, published) ||
                !IsSameOrDescendant(staging, backupRoot) ||
                !IsSameOrDescendant(published, backupRoot) ||
                PathsOverlap(published, activation.DeploymentRootPath) ||
                PathsOverlap(published, activation.ReleaseRootPath) ||
                plan.Sources.Select(source => source.Kind).Distinct().Count() !=
                    ExpectedRestoreSourceCount ||
                plan.Sources.Select(source => source.SourcePath)
                    .Distinct(PathComparer).Count() != ExpectedRestoreSourceCount ||
                plan.Sources.Any(source =>
                    !Path.IsPathRooted(source.SourcePath) ||
                    !Path.IsPathRooted(source.StagedPath) ||
                    PathsOverlap(source.SourcePath, published)))
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

    private static bool IsEligibleMigrationReport(
        VerifiedReleaseActivationMigrationPlanReport report) =>
        report.Succeeded &&
        report.FailureCode ==
            VerifiedReleaseActivationMigrationPlanFailureCode.None &&
        report.SetupRevision is > 0 &&
        report.MigrationKind is ReleaseMigrationKind.None or
            ReleaseMigrationKind.Required &&
        report.MigrationRequired ==
            (report.MigrationKind == ReleaseMigrationKind.Required) &&
        report.NoOpMigrationResolved == !report.MigrationRequired &&
        report.ExactActivationPlanBound &&
        report.ExactConfigurationBackupBound &&
        report.SourceBackupImmutable &&
        report.StagedCopyRequired == report.MigrationRequired &&
        report.MigrationManifestRequired == report.MigrationRequired &&
        report.AtomicPublicationRequired == report.MigrationRequired &&
        report.MigrationRunnerRequired == report.MigrationRequired &&
        !report.MigrationRunnerSelected &&
        !report.SourceReadPerformed &&
        !report.FileWritePerformed &&
        !report.MigrationExecutionPerformed &&
        report.MigrationReady == !report.MigrationRequired &&
        !report.CurrentPointerChanged &&
        !report.ActivationAuthorized;

    private static bool MatchesMigration(
        VerifiedReleaseActivationPlan activation,
        VerifiedReleaseActivationConfigurationBackup backup,
        VerifiedReleaseActivationMigrationPlanReport report,
        VerifiedReleaseActivationMigrationPlan migration) =>
        ReferenceEquals(migration.ActivationPlan, activation) &&
        ReferenceEquals(migration.ConfigurationBackup, backup) &&
        report.SetupRevision == activation.SetupRevision &&
        string.Equals(
            report.InstalledReleaseIdentity,
            activation.InstalledReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            report.TargetReleaseIdentity,
            activation.TargetReleaseIdentity,
            StringComparison.Ordinal) &&
        report.MigrationKind == migration.MigrationKind &&
        report.FromConfigurationSchemaVersion ==
            migration.FromConfigurationSchemaVersion &&
        report.ToConfigurationSchemaVersion ==
            migration.ToConfigurationSchemaVersion &&
        report.MigrationRequired == migration.MigrationRequired &&
        migration.ExistingMigrationOverwriteAllowed == false &&
        migration.AtomicPublicationRequired == migration.MigrationRequired &&
        migration.MigrationRunnerRequired == migration.MigrationRequired;

    private static bool IsEligibleServiceControlReport(
        VerifiedReleaseActivationServiceControlPlanReport report) =>
        report.Succeeded &&
        report.FailureCode ==
            VerifiedReleaseActivationServiceControlPlanFailureCode.None &&
        report.SetupRevision is > 0 &&
        report.RestartServiceCount is >= 0 and <= 4 &&
        report.ServiceControlRequired ==
            (report.RestartServiceCount > 0 || report.HostRestartRequired) &&
        report.NoOpServiceControlResolved == !report.ServiceControlRequired &&
        report.StopActionCount is >= 0 and <= 4 &&
        report.StartActionCount is >= 0 and <= 4 &&
        report.HostRestartActionCount is >= 0 and <= 1 &&
        report.ExactActivationPlanBound &&
        report.FixedServiceMappingBound &&
        report.DeterministicOrderingBound &&
        !report.ProcessInvocationPerformed &&
        !report.SystemdCommandPerformed &&
        !report.HostRestartPerformed &&
        report.ServiceControlReady == !report.ServiceControlRequired &&
        !report.CurrentPointerChanged &&
        !report.ActivationAuthorized;

    private static bool MatchesServiceControl(
        VerifiedReleaseActivationPlan activation,
        VerifiedReleaseActivationServiceControlPlanReport report,
        VerifiedReleaseActivationServiceControlPlan plan)
    {
        if (!ReferenceEquals(plan.ActivationPlan, activation) ||
            report.SetupRevision != activation.SetupRevision ||
            report.RestartServiceCount != activation.RestartServiceCount ||
            report.HostRestartRequired != activation.RestartHost ||
            report.StopActionCount != plan.StopActions.Count ||
            report.StartActionCount != plan.StartActions.Count ||
            report.HostRestartActionCount != plan.HostRestartActions.Count)
        {
            return false;
        }
        if (activation.RestartHost)
        {
            return plan.StopActions.Count == 0 &&
                plan.StartActions.Count == 0 &&
                plan.HostRestartActions.Count == 1;
        }
        return plan.HostRestartActions.Count == 0 &&
            ValidateServiceActions(
                plan.StopActions,
                ExpectedRoles(activation, stopOrder: true),
                VerifiedReleaseActivationServiceControlActionKind.Stop) &&
            ValidateServiceActions(
                plan.StartActions,
                ExpectedRoles(activation, stopOrder: false),
                VerifiedReleaseActivationServiceControlActionKind.Start);
    }

    private static bool IsEligibleHealthReport(
        VerifiedReleaseActivationHealthVerificationPlanReport report) =>
        report.Succeeded &&
        report.FailureCode ==
            VerifiedReleaseActivationHealthVerificationPlanFailureCode.None &&
        report.SetupRevision is > 0 &&
        report.HealthVerificationRequired &&
        report.HealthTargetCount == 4 &&
        report.UnitActivityCheckCount == 4 &&
        report.LoopbackHttpCheckCount == 3 &&
        report.FreshBrokerLinkCheckCount == 1 &&
        report.ExactServiceControlPlanBound &&
        report.ExactActivationPlanBound &&
        report.CompleteServiceCoverageBound &&
        report.FixedHealthContractMappingBound &&
        report.DeterministicOrderingBound &&
        report.LoopbackOnlyHttpBound &&
        report.CanonicalGatewayHostBindingRequired &&
        report.BoundedDeadlinePlanningBound &&
        report.PostSwitchVerificationPlanned &&
        !report.NetworkRequestPerformed &&
        !report.ProcessInvocationPerformed &&
        !report.SystemdCommandPerformed &&
        !report.JournalReadPerformed &&
        !report.HealthEvidenceProduced &&
        !report.ServiceHealthReady &&
        !report.CurrentPointerChanged &&
        !report.ActivationAuthorized;

    private static bool MatchesHealth(
        VerifiedReleaseActivationServiceControlPlan serviceControl,
        VerifiedReleaseActivationHealthVerificationPlanReport report,
        VerifiedReleaseActivationHealthVerificationPlan plan) =>
        ReferenceEquals(plan.ServiceControlPlan, serviceControl) &&
        report.SetupRevision == serviceControl.ActivationPlan.SetupRevision &&
        report.RestartServiceCount ==
            serviceControl.ActivationPlan.RestartServiceCount &&
        report.HostRestartRequired == serviceControl.ActivationPlan.RestartHost &&
        report.ServiceControlRequired == serviceControl.ServiceControlRequired &&
        report.HealthTargetCount == plan.Targets.Count &&
        ValidateHealthTargets(plan.Targets);

    private static bool ValidateServiceActions(
        IReadOnlyList<VerifiedReleaseActivationServiceControlAction> actions,
        IReadOnlyList<VerifiedReleaseActivationServiceRole> expectedRoles,
        VerifiedReleaseActivationServiceControlActionKind kind)
    {
        if (actions.Count != expectedRoles.Count)
        {
            return false;
        }
        for (int index = 0; index < actions.Count; index++)
        {
            VerifiedReleaseActivationServiceControlAction action = actions[index];
            VerifiedReleaseActivationServiceRole role = expectedRoles[index];
            if (action.Sequence != index + 1 ||
                action.Kind != kind ||
                action.ServiceRole != role ||
                !string.Equals(
                    action.UnitIdentity,
                    ExpectedUnitIdentity(role),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static VerifiedReleaseActivationServiceRole[] ExpectedRoles(
        VerifiedReleaseActivationPlan activation,
        bool stopOrder)
    {
        (VerifiedReleaseActivationServiceRole Role, bool Required)[] items =
            stopOrder
                ?
                [
                    (VerifiedReleaseActivationServiceRole.GatewayWeb,
                        activation.RestartGatewayWeb),
                    (VerifiedReleaseActivationServiceRole.Broker,
                        activation.RestartBroker),
                    (VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
                        activation.RestartAetherRemoteAgent),
                    (VerifiedReleaseActivationServiceRole.StationEngine,
                        activation.RestartStationEngine)
                ]
                :
                [
                    (VerifiedReleaseActivationServiceRole.StationEngine,
                        activation.RestartStationEngine),
                    (VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
                        activation.RestartAetherRemoteAgent),
                    (VerifiedReleaseActivationServiceRole.Broker,
                        activation.RestartBroker),
                    (VerifiedReleaseActivationServiceRole.GatewayWeb,
                        activation.RestartGatewayWeb)
                ];
        return items.Where(item => item.Required)
            .Select(item => item.Role)
            .ToArray();
    }

    private static bool ValidateHealthTargets(
        IReadOnlyList<VerifiedReleaseActivationHealthVerificationTarget> targets)
    {
        if (targets.Count != 4)
        {
            return false;
        }
        VerifiedReleaseActivationServiceRole[] roles =
        [
            VerifiedReleaseActivationServiceRole.StationEngine,
            VerifiedReleaseActivationServiceRole.Broker,
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
            VerifiedReleaseActivationServiceRole.GatewayWeb
        ];
        for (int index = 0; index < roles.Length; index++)
        {
            VerifiedReleaseActivationHealthVerificationTarget target =
                targets[index];
            VerifiedReleaseActivationServiceRole role = roles[index];
            if (target.Sequence != index + 1 ||
                target.ServiceRole != role ||
                !string.Equals(
                    target.UnitIdentity,
                    ExpectedUnitIdentity(role),
                    StringComparison.Ordinal) ||
                !target.RequireUnitActive ||
                !target.RequireFreshObservation ||
                target.DeadlineMilliseconds is < 1 or >
                    VerifiedReleaseActivationHealthVerificationPlanComposer
                        .MaximumDeadlineMilliseconds)
            {
                return false;
            }
            if (role == VerifiedReleaseActivationServiceRole.AetherRemoteAgent)
            {
                if (target.ContractKind !=
                        VerifiedReleaseActivationHealthContractKind.FreshBrokerLink ||
                    target.LoopbackPort is not null ||
                    !string.IsNullOrEmpty(target.HealthPath) ||
                    target.ExpectedHttpStatusCode is not null ||
                    target.RequireCanonicalHostHeader)
                {
                    return false;
                }
            }
            else if (target.ContractKind !=
                    VerifiedReleaseActivationHealthContractKind.LoopbackHttp ||
                target.LoopbackPort is null or < 1 or > 65_535 ||
                !string.Equals(
                    target.HealthPath,
                    VerifiedReleaseActivationHealthVerificationPlanComposer
                        .HealthPath,
                    StringComparison.Ordinal) ||
                target.ExpectedHttpStatusCode !=
                    VerifiedReleaseActivationHealthVerificationPlanComposer
                        .ExpectedHttpStatusCode ||
                target.RequireCanonicalHostHeader !=
                    (role == VerifiedReleaseActivationServiceRole.GatewayWeb))
            {
                return false;
            }
        }
        return true;
    }

    private static string ExpectedUnitIdentity(
        VerifiedReleaseActivationServiceRole role) =>
        role switch
        {
            VerifiedReleaseActivationServiceRole.GatewayWeb =>
                VerifiedReleaseActivationServiceControlPlanComposer
                    .GatewayWebUnitIdentity,
            VerifiedReleaseActivationServiceRole.Broker =>
                VerifiedReleaseActivationServiceControlPlanComposer
                    .BrokerUnitIdentity,
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent =>
                VerifiedReleaseActivationServiceControlPlanComposer
                    .AetherRemoteAgentUnitIdentity,
            VerifiedReleaseActivationServiceRole.StationEngine =>
                VerifiedReleaseActivationServiceControlPlanComposer
                    .StationEngineUnitIdentity,
            _ => string.Empty
        };

    private static string SourceDirectoryName(
        VerifiedReleaseActivationConfigurationBackupSourceKind kind) =>
        kind switch
        {
            VerifiedReleaseActivationConfigurationBackupSourceKind.Configuration =>
                "configuration",
            VerifiedReleaseActivationConfigurationBackupSourceKind.State =>
                "state",
            VerifiedReleaseActivationConfigurationBackupSourceKind.Secret =>
                "secrets",
            _ => throw new InvalidOperationException(
                "Unsupported rollback backup source kind.")
        };

    internal static VerifiedReleaseActivationRollbackPlan? ValidateReport(
        VerifiedReleaseActivationRollbackPlanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        VerifiedReleaseActivationRollbackPlan? plan = report.Plan;
        if (!report.Succeeded ||
            report.FailureCode !=
                VerifiedReleaseActivationRollbackPlanFailureCode.None ||
            report.SetupRevision is not > 0 ||
            string.IsNullOrEmpty(report.InstalledReleaseIdentity) ||
            string.IsNullOrEmpty(report.TargetReleaseIdentity) ||
            report.RestoreSourceCount != ExpectedRestoreSourceCount ||
            report.StopActionCount is < 0 or > 4 ||
            report.StartActionCount is < 0 or > 4 ||
            report.HealthTargetCount != 4 ||
            !report.ExactActivationPlanBound ||
            !report.ExactConfigurationBackupBound ||
            !report.ExactMigrationPlanBound ||
            !report.ExactServiceControlPlanBound ||
            !report.ExactHealthPlanBound ||
            !report.ImmutableOriginalBackupBound ||
            !report.OriginalBackupRestorePlanned ||
            report.ReverseMigrationRunnerPlanned ||
            !report.ConfigurationRestorePlanned ||
            !report.AtomicCurrentPointerRollbackPlanned ||
            !report.InstalledHealthVerificationPlanned ||
            report.HostRestartRequired ||
            report.HostRestartRollbackPlanned ||
            report.SourceReadPerformed ||
            report.FileWritePerformed ||
            report.DirectoryMutationPerformed ||
            report.ProcessInvocationPerformed ||
            report.SystemdCommandPerformed ||
            report.NetworkRequestPerformed ||
            report.HealthProbePerformed ||
            report.CurrentPointerChanged ||
            report.RollbackPerformed ||
            report.RollbackReady ||
            report.ActivationAuthorized ||
            plan is null ||
            report.SetupRevision != plan.ActivationPlan.SetupRevision ||
            !string.Equals(
                report.InstalledReleaseIdentity,
                plan.ActivationPlan.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.TargetReleaseIdentity,
                plan.ActivationPlan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            report.MigrationRequired != plan.MigrationPlan.MigrationRequired ||
            report.StopActionCount != plan.ServiceControlPlan.StopActions.Count ||
            report.StartActionCount != plan.ServiceControlPlan.StartActions.Count ||
            report.HealthTargetCount != plan.HealthPlan.Targets.Count ||
            report.TargetServiceStopPlanned !=
                (plan.ServiceControlPlan.StopActions.Count > 0) ||
            report.InstalledServiceStartPlanned !=
                (plan.ServiceControlPlan.StartActions.Count > 0) ||
            !ValidateComposedPlan(plan))
        {
            return null;
        }
        return plan;
    }

    private static bool IsBoundedAsciiToken(string value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
        {
            return false;
        }
        foreach (char character in value)
        {
            if (character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '-' or '.' or '_')
            {
                continue;
            }
            return false;
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
        string canonical =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!string.Equals(value, canonical, PathComparison))
        {
            throw new InvalidOperationException(
                "Directory paths must already be canonical.");
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
        string canonicalCandidate =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        string canonicalParent =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (string.Equals(canonicalCandidate, canonicalParent, PathComparison))
        {
            return true;
        }
        string prefix = canonicalParent + Path.DirectorySeparatorChar;
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
