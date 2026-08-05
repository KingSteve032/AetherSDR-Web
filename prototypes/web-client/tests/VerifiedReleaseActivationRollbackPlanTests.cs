using System.Reflection;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationRollbackPlanTests : IDisposable
{
    private readonly string m_root = Path.Combine(
        Path.GetTempPath(),
        $"aethersdr-rollback-plan-{Guid.NewGuid():N}");

    [Fact]
    public void PublicSurfaceExposesDiagnosticsOnly()
    {
        string[] methods = typeof(VerifiedReleaseActivationRollbackPlanComposer)
            .GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["get_Snapshot"], methods);
    }

    [Fact]
    public void DiagnosticsDeclarePlanningOnlyBoundary()
    {
        VerifiedReleaseActivationRollbackPlanDiagnostics snapshot =
            new VerifiedReleaseActivationRollbackPlanComposer().Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ActivationPlanInputRegistered);
        Assert.True(snapshot.ConfigurationBackupInputRegistered);
        Assert.True(snapshot.MigrationPlanInputRegistered);
        Assert.True(snapshot.ServiceControlPlanInputRegistered);
        Assert.True(snapshot.HealthPlanInputRegistered);
        Assert.True(snapshot.ExactActivationPlanBindingRegistered);
        Assert.True(snapshot.ExactConfigurationBackupBindingRegistered);
        Assert.True(snapshot.ExactMigrationPlanBindingRegistered);
        Assert.True(snapshot.ExactServiceControlPlanBindingRegistered);
        Assert.True(snapshot.ExactHealthPlanBindingRegistered);
        Assert.True(snapshot.ImmutableOriginalBackupBindingRegistered);
        Assert.True(snapshot.OriginalBackupRestorePlanningRegistered);
        Assert.False(snapshot.ReverseMigrationRunnerPlanningRegistered);
        Assert.True(snapshot.ThreeSourceRestorePlanningRegistered);
        Assert.True(snapshot.SameParentRestoreStagingRegistered);
        Assert.True(snapshot.DisplacedLiveTreePlanningRegistered);
        Assert.True(snapshot.TargetServiceStopPlanningRegistered);
        Assert.True(snapshot.AtomicCurrentPointerRollbackPlanningRegistered);
        Assert.True(snapshot.InstalledServiceStartPlanningRegistered);
        Assert.True(snapshot.InstalledHealthVerificationPlanningRegistered);
        Assert.False(snapshot.HostRestartRollbackPlanningRegistered);
        AssertNoOperationalSurface(snapshot);
    }

    [Fact]
    public void ExactNoMigrationTransactionComposesWithoutAuthority()
    {
        Fixture fixture = CreateFixture(ReleaseMigrationKind.None);

        VerifiedReleaseActivationRollbackPlanReport report = fixture.Compose();

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackPlanFailureCode.None,
            report.FailureCode);
        Assert.False(report.MigrationRequired);
        Assert.Equal(3, report.RestoreSourceCount);
        Assert.Equal(4, report.StopActionCount);
        Assert.Equal(4, report.StartActionCount);
        Assert.Equal(4, report.HealthTargetCount);
        Assert.True(report.ExactActivationPlanBound);
        Assert.True(report.ExactConfigurationBackupBound);
        Assert.True(report.ExactMigrationPlanBound);
        Assert.True(report.ExactServiceControlPlanBound);
        Assert.True(report.ExactHealthPlanBound);
        Assert.True(report.ImmutableOriginalBackupBound);
        Assert.True(report.OriginalBackupRestorePlanned);
        Assert.False(report.ReverseMigrationRunnerPlanned);
        Assert.True(report.TargetServiceStopPlanned);
        Assert.True(report.ConfigurationRestorePlanned);
        Assert.True(report.AtomicCurrentPointerRollbackPlanned);
        Assert.True(report.InstalledServiceStartPlanned);
        Assert.True(report.InstalledHealthVerificationPlanned);
        Assert.False(report.HostRestartRequired);
        Assert.False(report.HostRestartRollbackPlanned);
        AssertNoExecution(report);

        VerifiedReleaseActivationRollbackPlan plan =
            Assert.IsType<VerifiedReleaseActivationRollbackPlan>(report.Plan);
        Assert.Same(fixture.ActivationPlan, plan.ActivationPlan);
        Assert.Same(fixture.Backup, plan.ConfigurationBackup);
        Assert.Same(fixture.MigrationPlan, plan.MigrationPlan);
        Assert.Same(fixture.ServiceControlPlan, plan.ServiceControlPlan);
        Assert.Same(fixture.HealthPlan, plan.HealthPlan);
        Assert.False(plan.ReverseMigrationRunnerRequired);
        Assert.Equal(
            fixture.ActivationPlan.TargetCurrentLinkTarget,
            plan.ExpectedCurrentLinkTarget);
        Assert.Equal(
            fixture.ActivationPlan.InstalledCurrentLinkTarget,
            plan.RollbackCurrentLinkTarget);
        Assert.NotEqual(
            fixture.ActivationPlan.CurrentPointerPath,
            plan.TemporaryCurrentPointerPath);
    }

    [Fact]
    public void RequiredMigrationRestoresOriginalBackupWithoutReverseRunner()
    {
        Fixture fixture = CreateFixture(ReleaseMigrationKind.Required);

        VerifiedReleaseActivationRollbackPlanReport report = fixture.Compose();

        Assert.True(report.Succeeded);
        Assert.True(report.MigrationRequired);
        Assert.True(report.OriginalBackupRestorePlanned);
        Assert.False(report.ReverseMigrationRunnerPlanned);
        VerifiedReleaseActivationRollbackPlan plan =
            Assert.IsType<VerifiedReleaseActivationRollbackPlan>(report.Plan);
        Assert.True(plan.MigrationPlan.MigrationRequired);
        Assert.False(plan.ReverseMigrationRunnerRequired);
        Assert.All(
            plan.RestoreSources,
            source => Assert.StartsWith(
                fixture.Backup.Plan.PublishedPath + Path.DirectorySeparatorChar,
                source.ImmutableBackupPath,
                StringComparison.Ordinal));
    }

    [Fact]
    public void RestoreMappingsUseExactLiveRootsAndSameParentStaging()
    {
        Fixture fixture = CreateFixture(ReleaseMigrationKind.None);

        VerifiedReleaseActivationRollbackPlan plan = Assert.IsType<
            VerifiedReleaseActivationRollbackPlan>(fixture.Compose().Plan);

        Assert.Equal(3, plan.RestoreSources.Count);
        foreach (VerifiedReleaseActivationRollbackRestoreSource source in
            plan.RestoreSources)
        {
            VerifiedReleaseActivationConfigurationBackupSourcePlan expected =
                fixture.Backup.Plan.Sources.Single(item =>
                    item.Kind == source.Kind);
            Assert.Equal(expected.SourcePath, source.LiveDestinationPath);
            Assert.Equal(
                Path.GetDirectoryName(source.LiveDestinationPath),
                Path.GetDirectoryName(source.RestoreStagingPath));
            Assert.Equal(
                Path.GetDirectoryName(source.LiveDestinationPath),
                Path.GetDirectoryName(source.DisplacedLivePath));
            Assert.NotEqual(
                source.LiveDestinationPath,
                source.RestoreStagingPath);
            Assert.NotEqual(
                source.LiveDestinationPath,
                source.DisplacedLivePath);
            Assert.NotEqual(
                source.RestoreStagingPath,
                source.DisplacedLivePath);
        }
    }

    [Fact]
    public void EquivalentBackupTokenIsRejected()
    {
        Fixture fixture = CreateFixture(ReleaseMigrationKind.None);
        Fixture equivalent = CreateFixture(
            ReleaseMigrationKind.None,
            fixture.Paths);

        VerifiedReleaseActivationRollbackPlanReport report =
            fixture.Composer.Compose(
                fixture.ActivationResult,
                equivalent.BackupReport,
                fixture.MigrationReport,
                fixture.ServiceControlReport,
                fixture.HealthReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationRollbackPlanFailureCode
                .ConfigurationBackupMismatch);
    }

    [Fact]
    public void EquivalentMigrationTokenIsRejected()
    {
        Fixture fixture = CreateFixture(ReleaseMigrationKind.Required);
        Fixture equivalent = CreateFixture(
            ReleaseMigrationKind.Required,
            fixture.Paths);

        VerifiedReleaseActivationRollbackPlanReport report =
            fixture.Composer.Compose(
                fixture.ActivationResult,
                fixture.BackupReport,
                equivalent.MigrationReport,
                fixture.ServiceControlReport,
                fixture.HealthReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationRollbackPlanFailureCode
                .MigrationPlanMismatch);
    }

    [Fact]
    public void EquivalentServiceControlTokenIsRejected()
    {
        Fixture fixture = CreateFixture(ReleaseMigrationKind.None);
        Fixture equivalent = CreateFixture(
            ReleaseMigrationKind.None,
            fixture.Paths);

        VerifiedReleaseActivationRollbackPlanReport report =
            fixture.Composer.Compose(
                fixture.ActivationResult,
                fixture.BackupReport,
                fixture.MigrationReport,
                equivalent.ServiceControlReport,
                fixture.HealthReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationRollbackPlanFailureCode
                .ServiceControlPlanMismatch);
    }

    [Fact]
    public void EquivalentHealthTokenIsRejected()
    {
        Fixture fixture = CreateFixture(ReleaseMigrationKind.None);
        Fixture equivalent = CreateFixture(
            ReleaseMigrationKind.None,
            fixture.Paths);

        VerifiedReleaseActivationRollbackPlanReport report =
            fixture.Composer.Compose(
                fixture.ActivationResult,
                fixture.BackupReport,
                fixture.MigrationReport,
                fixture.ServiceControlReport,
                equivalent.HealthReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationRollbackPlanFailureCode.HealthPlanMismatch);
    }

    [Fact]
    public void HostRestartPlanFailsClosed()
    {
        Fixture fixture = CreateFixture(
            ReleaseMigrationKind.None,
            restartHost: true);

        VerifiedReleaseActivationRollbackPlanReport report = fixture.Compose();

        AssertFailure(
            report,
            VerifiedReleaseActivationRollbackPlanFailureCode
                .HostRestartUnsupported);
        Assert.True(report.HostRestartRequired);
        Assert.False(report.HostRestartRollbackPlanned);
    }

    [Fact]
    public void BackupReportMetadataDriftIsRejected()
    {
        Fixture fixture = CreateFixture(ReleaseMigrationKind.None);
        VerifiedReleaseActivationConfigurationBackupReport tampered =
            fixture.BackupReport with
            {
                BackupBytes = fixture.BackupReport.BackupBytes + 1
            };

        VerifiedReleaseActivationRollbackPlanReport report =
            fixture.Composer.Compose(
                fixture.ActivationResult,
                tampered,
                fixture.MigrationReport,
                fixture.ServiceControlReport,
                fixture.HealthReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationRollbackPlanFailureCode
                .ConfigurationBackupMismatch);
    }

    [Fact]
    public void UnsafeBackupOverlapIsRejected()
    {
        Fixture fixture = CreateFixture(ReleaseMigrationKind.None);
        VerifiedReleaseActivationConfigurationBackupPlan unsafePlan = new(
            fixture.ActivationPlan,
            fixture.Paths.BackupDirectory,
            Path.Combine(fixture.Paths.StateDirectory, ".unsafe-staging"),
            fixture.Paths.StateDirectory,
            Path.Combine(
                fixture.Paths.StateDirectory,
                "backup-manifest.json"),
            fixture.Backup.Plan.Sources);
        VerifiedReleaseActivationConfigurationBackup unsafeBackup = new(
            unsafePlan,
            fixture.Backup.DirectoryCount,
            fixture.Backup.FileCount,
            fixture.Backup.BackupBytes,
            fixture.Backup.ManifestSha256,
            fixture.Backup.CompletedAt);
        VerifiedReleaseActivationConfigurationBackupReport unsafeReport =
            VerifiedReleaseActivationConfigurationBackupReport.Success(unsafeBackup);

        VerifiedReleaseActivationRollbackPlanReport report =
            fixture.Composer.Compose(
                fixture.ActivationResult,
                unsafeReport,
                fixture.MigrationReport,
                fixture.ServiceControlReport,
                fixture.HealthReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationRollbackPlanFailureCode
                .ConfigurationBackupMismatch);
    }

    [Fact]
    public void PublicReportDoesNotExposePathsUnitsOrHealthContracts()
    {
        Fixture fixture = CreateFixture(ReleaseMigrationKind.Required);

        VerifiedReleaseActivationRollbackPlanReport report = fixture.Compose();
        string publicText = string.Join(
            '|',
            report.ToString(),
            report.Message,
            report.InstalledReleaseIdentity,
            report.TargetReleaseIdentity);

        Assert.DoesNotContain(fixture.Root, publicText, StringComparison.Ordinal);
        Assert.DoesNotContain(".service", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("/healthz", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("migration-manifest", publicText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("backup-manifest", publicText,
            StringComparison.Ordinal);
    }

    private Fixture CreateFixture(
        ReleaseMigrationKind migrationKind,
        InstallationPaths? paths = null,
        bool restartHost = false) =>
        new(
            paths ?? CreatePaths(m_root),
            migrationKind,
            restartHost);

    private static InstallationPaths CreatePaths(string root) =>
        new(
            Path.Combine(root, "configuration"),
            Path.Combine(root, "state"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "deployment", "releases"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "logs"));

    private static void AssertFailure(
        VerifiedReleaseActivationRollbackPlanReport report,
        VerifiedReleaseActivationRollbackPlanFailureCode code)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(code, report.FailureCode);
        Assert.Null(report.Plan);
        AssertNoExecution(report);
    }

    private static void AssertNoExecution(
        VerifiedReleaseActivationRollbackPlanReport report)
    {
        Assert.False(report.SourceReadPerformed);
        Assert.False(report.FileWritePerformed);
        Assert.False(report.DirectoryMutationPerformed);
        Assert.False(report.ProcessInvocationPerformed);
        Assert.False(report.SystemdCommandPerformed);
        Assert.False(report.NetworkRequestPerformed);
        Assert.False(report.HealthProbePerformed);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.RollbackPerformed);
        Assert.False(report.RollbackReady);
        Assert.False(report.ActivationAuthorized);
    }

    private static void AssertNoOperationalSurface(
        VerifiedReleaseActivationRollbackPlanDiagnostics snapshot)
    {
        Assert.False(snapshot.SourceReadRegistered);
        Assert.False(snapshot.FileWriteRegistered);
        Assert.False(snapshot.DirectoryMutationRegistered);
        Assert.False(snapshot.ProcessInvocationRegistered);
        Assert.False(snapshot.SystemdCommandRegistered);
        Assert.False(snapshot.NetworkRequestRegistered);
        Assert.False(snapshot.HealthProbeRegistered);
        Assert.False(snapshot.RollbackEvidenceRegistered);
        Assert.False(snapshot.RollbackExecutionRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.ActivationAuthorityRegistered);
        Assert.False(snapshot.OperationalCallerRegistered);
        Assert.False(snapshot.CliCallerRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
        Assert.False(snapshot.HttpCallerRegistered);
        Assert.False(snapshot.WebSocketCallerRegistered);
        Assert.False(snapshot.HostedServiceCallerRegistered);
        Assert.False(snapshot.TimerCallerRegistered);
        Assert.False(snapshot.AetherRemoteCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    public void Dispose()
    {
        if (Directory.Exists(m_root))
        {
            Directory.Delete(m_root, recursive: true);
        }
    }

    private sealed class Fixture
    {
        internal Fixture(
            InstallationPaths paths,
            ReleaseMigrationKind migrationKind,
            bool restartHost)
        {
            Paths = paths;
            Root = Path.GetDirectoryName(paths.ConfigurationDirectory)!;
            ActivationResult = CreateActivationResult(
                paths,
                migrationKind,
                restartHost);
            ActivationPlan = Assert.IsType<VerifiedReleaseActivationPlan>(
                ActivationResult.Plan);

            VerifiedReleaseActivationConfigurationBackupPlanReport backupPlanReport =
                new VerifiedReleaseActivationConfigurationBackupPlanner(paths)
                    .Compose(ActivationResult);
            Assert.True(backupPlanReport.Succeeded);
            VerifiedReleaseActivationConfigurationBackupPlan backupPlan =
                Assert.IsType<
                    VerifiedReleaseActivationConfigurationBackupPlan>(
                        backupPlanReport.Plan);
            Backup = new VerifiedReleaseActivationConfigurationBackup(
                backupPlan,
                directoryCount: 3,
                fileCount: 4,
                backupBytes: 64,
                manifestSha256: Enumerable.Repeat((byte)0x5A, 32).ToArray(),
                completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));
            BackupReport =
                VerifiedReleaseActivationConfigurationBackupReport.Success(Backup);

            MigrationReport =
                new VerifiedReleaseActivationMigrationPlanComposer().Compose(
                    ActivationResult,
                    BackupReport);
            Assert.True(MigrationReport.Succeeded);
            MigrationPlan = Assert.IsType<VerifiedReleaseActivationMigrationPlan>(
                MigrationReport.Plan);

            ServiceControlReport =
                new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                    ActivationResult);
            Assert.True(ServiceControlReport.Succeeded);
            ServiceControlPlan = Assert.IsType<
                VerifiedReleaseActivationServiceControlPlan>(
                    ServiceControlReport.Plan);

            HealthReport =
                new VerifiedReleaseActivationHealthVerificationPlanComposer()
                    .Compose(ServiceControlReport);
            Assert.True(HealthReport.Succeeded);
            HealthPlan = Assert.IsType<
                VerifiedReleaseActivationHealthVerificationPlan>(
                    HealthReport.Plan);

            Composer = new VerifiedReleaseActivationRollbackPlanComposer();
        }

        internal string Root { get; }
        internal InstallationPaths Paths { get; }
        internal VerifiedReleaseActivationPlanCompositionResult ActivationResult
        {
            get;
        }
        internal VerifiedReleaseActivationPlan ActivationPlan { get; }
        internal VerifiedReleaseActivationConfigurationBackup Backup { get; }
        internal VerifiedReleaseActivationConfigurationBackupReport BackupReport
        {
            get;
        }
        internal VerifiedReleaseActivationMigrationPlanReport MigrationReport
        {
            get;
        }
        internal VerifiedReleaseActivationMigrationPlan MigrationPlan { get; }
        internal VerifiedReleaseActivationServiceControlPlanReport
            ServiceControlReport
        {
            get;
        }
        internal VerifiedReleaseActivationServiceControlPlan ServiceControlPlan
        {
            get;
        }
        internal VerifiedReleaseActivationHealthVerificationPlanReport HealthReport
        {
            get;
        }
        internal VerifiedReleaseActivationHealthVerificationPlan HealthPlan { get; }
        internal VerifiedReleaseActivationRollbackPlanComposer Composer { get; }

        internal VerifiedReleaseActivationRollbackPlanReport Compose() =>
            Composer.Compose(
                ActivationResult,
                BackupReport,
                MigrationReport,
                ServiceControlReport,
                HealthReport);

        private static VerifiedReleaseActivationPlanCompositionResult
            CreateActivationResult(
                InstallationPaths paths,
                ReleaseMigrationKind migrationKind,
                bool restartHost)
        {
            const string installedIdentity = "aethersdr-8.1.0";
            const string targetIdentity = "aethersdr-8.2.0";
            string targetPath = Path.Combine(
                paths.ReleaseDirectory,
                targetIdentity);
            VerifiedReleaseInstallationPackagePlan[] packages =
                CreatePackages(targetPath);
            VerifiedReleaseInstallationPlan installation = new(
                setupRevision: 7,
                installedReleaseIdentity: installedIdentity,
                targetReleaseIdentity: targetIdentity,
                targetVersion: "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                pinnedReleaseIdentity: string.Empty,
                installTransmitSupport: false,
                bundleDirectory: Path.Combine(paths.StateDirectory, "bundle"),
                manifestLength: 37,
                manifestSha256: Enumerable.Repeat((byte)0x7A, 32).ToArray(),
                releaseRootPath: paths.ReleaseDirectory,
                deploymentRootPath:
                    Path.GetDirectoryName(paths.ReleaseDirectory)!,
                targetReleasePath: targetPath,
                packages,
                targetConfigurationSchemaVersion:
                    migrationKind == ReleaseMigrationKind.Required ? 2 : 1,
                migrationKind,
                migrationFromConfigurationSchemaVersion:
                    migrationKind == ReleaseMigrationKind.Required ? 1 : null,
                migrationToConfigurationSchemaVersion:
                    migrationKind == ReleaseMigrationKind.Required ? 2 : null,
                migrationIdentity:
                    migrationKind == ReleaseMigrationKind.Required
                        ? "config-v2"
                        : string.Empty,
                restartGatewayWeb: true,
                restartBroker: true,
                restartAetherRemoteAgent: true,
                restartStationEngine: true,
                restartHost,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Rollback planner fixture.");
            long publishedBytes = checked(
                installation.ManifestLength +
                packages.Sum(package => package.Length));
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(
                        installation,
                        installation.TargetReleasePath,
                        publishedBytes));
            VerifiedReleaseActivationPlanCompositionResult result =
                new VerifiedReleaseActivationPlanComposer().Compose(publication);
            Assert.True(result.Succeeded);
            return result;
        }

        private static VerifiedReleaseInstallationPackagePlan[] CreatePackages(
            string targetPath)
        {
            (string Identity, ReleasePackageRole Role, string Relative, long Length)[]
                inputs =
                [
                    ("gateway", ReleasePackageRole.GatewayWeb,
                        "packages/gateway.tar", 11),
                    ("broker", ReleasePackageRole.Broker,
                        "packages/broker.tar", 12),
                    ("agent", ReleasePackageRole.AetherRemoteAgent,
                        "packages/agent.tar", 13),
                    ("engine", ReleasePackageRole.StationEngine,
                        "packages/engine.tar", 14)
                ];
            return inputs.Select((input, index) =>
            {
                SignedReleasePackage package = new()
                {
                    PackageIdentity = input.Identity,
                    Role = input.Role,
                    FileName = input.Relative,
                    Length = input.Length,
                    Sha256 = new string((char)('A' + index), 64)
                };
                return new VerifiedReleaseInstallationPackagePlan(
                    new VerifiedReleasePackageSnapshot(package),
                    Path.GetFullPath(
                        Path.Combine(
                            targetPath,
                            input.Relative.Replace(
                                '/',
                                Path.DirectorySeparatorChar))));
            }).ToArray();
        }
    }
}
