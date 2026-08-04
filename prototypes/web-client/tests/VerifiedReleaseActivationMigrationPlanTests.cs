using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationMigrationPlanTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsOnly()
    {
        string[] methods =
            typeof(VerifiedReleaseActivationMigrationPlanComposer)
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(["get_Snapshot"], methods);
    }

    [Fact]
    public void DiagnosticsSeparatePlanningFromRunnerExecutionAndCallers()
    {
        VerifiedReleaseActivationMigrationPlanDiagnostics snapshot =
            new VerifiedReleaseActivationMigrationPlanComposer().Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ActivationPlanInputRegistered);
        Assert.True(snapshot.ConfigurationBackupInputRegistered);
        Assert.True(snapshot.ExactActivationPlanBindingRegistered);
        Assert.True(snapshot.ExactConfigurationBackupBindingRegistered);
        Assert.True(snapshot.ImmutableBackupValidationRegistered);
        Assert.True(snapshot.NoOpMigrationPlanningRegistered);
        Assert.True(snapshot.RequiredMigrationPlanningRegistered);
        Assert.True(snapshot.SchemaTransitionValidationRegistered);
        Assert.True(snapshot.MigrationIdentityValidationRegistered);
        Assert.True(snapshot.StagedCopyPathPlanningRegistered);
        Assert.True(snapshot.MigrationManifestPlanningRegistered);
        Assert.True(snapshot.AtomicPublicationPlanningRegistered);
        Assert.False(snapshot.MigrationRunnerSelectionRegistered);
        Assert.False(snapshot.SourceReadRegistered);
        Assert.False(snapshot.FileWriteRegistered);
        Assert.False(snapshot.DirectoryMutationRegistered);
        Assert.False(snapshot.MigrationExecutionRegistered);
        Assert.False(snapshot.MigrationEvidenceRegistered);
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
        Assert.False(snapshot.ServiceControlCallerRegistered);
        Assert.False(snapshot.HealthProbeCallerRegistered);
        Assert.False(snapshot.RollbackCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public void RequiredMigrationBindsExactPlanBackupAndSeparatedStagedCopy()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationMigrationPlanComposer composer = new();

        VerifiedReleaseActivationMigrationPlanReport report = composer.Compose(
            fixture.ActivationPlanResult,
            fixture.BackupReport);

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationMigrationPlanFailureCode.None,
            report.FailureCode);
        Assert.Equal(ReleaseMigrationKind.Required, report.MigrationKind);
        Assert.Equal(1, report.FromConfigurationSchemaVersion);
        Assert.Equal(2, report.ToConfigurationSchemaVersion);
        Assert.True(report.MigrationRequired);
        Assert.False(report.NoOpMigrationResolved);
        Assert.True(report.ExactActivationPlanBound);
        Assert.True(report.ExactConfigurationBackupBound);
        Assert.True(report.SourceBackupImmutable);
        Assert.True(report.StagedCopyRequired);
        Assert.True(report.MigrationManifestRequired);
        Assert.True(report.AtomicPublicationRequired);
        Assert.True(report.MigrationRunnerRequired);
        Assert.False(report.MigrationRunnerSelected);
        Assert.False(report.SourceReadPerformed);
        Assert.False(report.FileWritePerformed);
        Assert.False(report.MigrationExecutionPerformed);
        Assert.False(report.MigrationReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);

        VerifiedReleaseActivationMigrationPlan plan =
            Assert.IsType<VerifiedReleaseActivationMigrationPlan>(report.Plan);
        Assert.Same(fixture.ActivationPlanResult.Plan, plan.ActivationPlan);
        Assert.Same(fixture.BackupReport.Backup, plan.ConfigurationBackup);
        Assert.Equal("schema-1-to-2", plan.MigrationIdentity);
        Assert.Equal(
            Path.Combine(
                fixture.Paths.BackupDirectory,
                "activation",
                "setup-7",
                "migration"),
            plan.MigrationRootPath);
        Assert.Equal(
            Path.Combine(
                plan.MigrationRootPath,
                ".schema-1-to-2-schema-1-to-2.staging"),
            plan.StagingPath);
        Assert.Equal(
            Path.Combine(
                plan.MigrationRootPath,
                "schema-1-to-2-schema-1-to-2"),
            plan.PublishedPath);
        Assert.Equal(
            Path.Combine(plan.PublishedPath, "migration-manifest.json"),
            plan.ManifestPath);
        Assert.False(plan.ExistingMigrationOverwriteAllowed);
        Assert.True(plan.AtomicPublicationRequired);
        Assert.True(plan.MigrationRunnerRequired);
        Assert.Equal(3, plan.Sources.Count);
        Assert.Collection(
            plan.Sources,
            source => AssertSource(
                source,
                VerifiedReleaseActivationConfigurationBackupSourceKind
                    .Configuration,
                fixture.BackupPlan.PublishedPath,
                plan.StagingPath,
                "configuration"),
            source => AssertSource(
                source,
                VerifiedReleaseActivationConfigurationBackupSourceKind.State,
                fixture.BackupPlan.PublishedPath,
                plan.StagingPath,
                "state"),
            source => AssertSource(
                source,
                VerifiedReleaseActivationConfigurationBackupSourceKind.Secret,
                fixture.BackupPlan.PublishedPath,
                plan.StagingPath,
                "secrets"));

        Assert.False(Directory.Exists(fixture.Root));
        Assert.False(Directory.Exists(plan.StagingPath));
        Assert.False(Directory.Exists(plan.PublishedPath));
        Assert.False(File.Exists(plan.ManifestPath));
    }

    [Fact]
    public void NoMigrationDeclarationResolvesAsExactNoOpWithoutPaths()
    {
        Fixture fixture = new(
            migrationKind: ReleaseMigrationKind.None,
            migrationFrom: null,
            migrationTo: null,
            migrationIdentity: string.Empty,
            targetSchema: 1);

        VerifiedReleaseActivationMigrationPlanReport report =
            new VerifiedReleaseActivationMigrationPlanComposer().Compose(
                fixture.ActivationPlanResult,
                fixture.BackupReport);

        Assert.True(report.Succeeded);
        Assert.Equal(ReleaseMigrationKind.None, report.MigrationKind);
        Assert.False(report.MigrationRequired);
        Assert.True(report.NoOpMigrationResolved);
        Assert.True(report.ExactActivationPlanBound);
        Assert.True(report.ExactConfigurationBackupBound);
        Assert.True(report.SourceBackupImmutable);
        Assert.False(report.StagedCopyRequired);
        Assert.False(report.MigrationManifestRequired);
        Assert.False(report.AtomicPublicationRequired);
        Assert.False(report.MigrationRunnerRequired);
        Assert.False(report.MigrationRunnerSelected);
        Assert.True(report.MigrationReady);

        VerifiedReleaseActivationMigrationPlan plan = report.Plan!;
        Assert.False(plan.MigrationRequired);
        Assert.Empty(plan.MigrationIdentity);
        Assert.Empty(plan.MigrationRootPath);
        Assert.Empty(plan.StagingPath);
        Assert.Empty(plan.PublishedPath);
        Assert.Empty(plan.ManifestPath);
        Assert.Empty(plan.Sources);
    }

    [Fact]
    public void MissingOrTamperedActivationPlanFailsClosed()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationMigrationPlanComposer composer = new();

        VerifiedReleaseActivationMigrationPlanReport missing = composer.Compose(
            fixture.ActivationPlanResult with { Plan = null },
            fixture.BackupReport);
        VerifiedReleaseActivationMigrationPlanReport tampered = composer.Compose(
            fixture.ActivationPlanResult with
            {
                TargetConfigurationSchemaVersion = 3
            },
            fixture.BackupReport);
        VerifiedReleaseActivationMigrationPlanReport ineligible = composer.Compose(
            fixture.ActivationPlanResult with
            {
                ConfigurationBackupRequired = false
            },
            fixture.BackupReport);

        AssertFailure(
            missing,
            VerifiedReleaseActivationMigrationPlanFailureCode
                .ActivationPlanUnavailable);
        AssertFailure(
            tampered,
            VerifiedReleaseActivationMigrationPlanFailureCode
                .ActivationPlanMismatch);
        AssertFailure(
            ineligible,
            VerifiedReleaseActivationMigrationPlanFailureCode
                .ActivationPlanNotEligible);
    }

    [Fact]
    public void MissingTamperedOrFailedBackupFailsClosed()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationMigrationPlanComposer composer = new();

        VerifiedReleaseActivationMigrationPlanReport missing = composer.Compose(
            fixture.ActivationPlanResult,
            fixture.BackupReport with { Backup = null });
        VerifiedReleaseActivationMigrationPlanReport tampered = composer.Compose(
            fixture.ActivationPlanResult,
            fixture.BackupReport with
            {
                BackupBytes = fixture.BackupReport.BackupBytes + 1
            });
        VerifiedReleaseActivationMigrationPlanReport failed = composer.Compose(
            fixture.ActivationPlanResult,
            fixture.BackupReport with
            {
                Succeeded = false,
                FailureCode =
                    VerifiedReleaseActivationConfigurationBackupFailureCode
                        .BackupWriteFailed,
                ConfigurationBackupReady = false
            });

        AssertFailure(
            missing,
            VerifiedReleaseActivationMigrationPlanFailureCode
                .ConfigurationBackupUnavailable);
        AssertFailure(
            tampered,
            VerifiedReleaseActivationMigrationPlanFailureCode
                .ConfigurationBackupMismatch);
        AssertFailure(
            failed,
            VerifiedReleaseActivationMigrationPlanFailureCode
                .ConfigurationBackupNotEligible);
    }

    [Fact]
    public void EquivalentButDistinctActivationPlanCannotReuseBackup()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationPlanCompositionResult distinct =
            fixture.CreateActivationPlanResult();
        Assert.NotSame(fixture.ActivationPlanResult.Plan, distinct.Plan);

        VerifiedReleaseActivationMigrationPlanReport report =
            new VerifiedReleaseActivationMigrationPlanComposer().Compose(
                distinct,
                fixture.BackupReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationMigrationPlanFailureCode
                .ConfigurationBackupMismatch);
    }

    [Fact]
    public void UnsafeBackupPublicationLayoutCannotPlanMigration()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationConfigurationBackupPlan unsafePlan =
            new(
                fixture.ActivationPlanResult.Plan!,
                fixture.Paths.BackupDirectory,
                Path.Combine(fixture.DeploymentRoot, ".migration-source.staging"),
                Path.Combine(fixture.DeploymentRoot, "migration-source"),
                Path.Combine(
                    fixture.DeploymentRoot,
                    "migration-source",
                    "backup-manifest.json"),
                fixture.BackupPlan.Sources);
        VerifiedReleaseActivationConfigurationBackup unsafeBackup =
            new(
                unsafePlan,
                directoryCount: 6,
                fileCount: 3,
                backupBytes: 51,
                manifestSha256: Enumerable.Repeat((byte)0x3A, 32).ToArray(),
                completedAt:
                    new DateTimeOffset(2026, 8, 4, 10, 30, 0, TimeSpan.Zero));
        VerifiedReleaseActivationConfigurationBackupReport unsafeReport =
            VerifiedReleaseActivationConfigurationBackupReport.Success(unsafeBackup);

        VerifiedReleaseActivationMigrationPlanReport report =
            new VerifiedReleaseActivationMigrationPlanComposer().Compose(
                fixture.ActivationPlanResult,
                unsafeReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationMigrationPlanFailureCode
                .MigrationLayoutUnsafe);
    }

    [Fact]
    public void PublicReportRedactsPathsMigrationIdentityAndBackupDigest()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationMigrationPlanReport report =
            new VerifiedReleaseActivationMigrationPlanComposer().Compose(
                fixture.ActivationPlanResult,
                fixture.BackupReport);
        Assert.True(report.Succeeded);

        string json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.BackupPlan.PublishedPath,
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.ActivationPlanResult.Plan!.MigrationIdentity,
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(fixture.BackupReport.Backup!.ManifestSha256),
            json,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stagingPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publishedPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manifestPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("migrationRequired", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSource(
        VerifiedReleaseActivationMigrationSourcePlan source,
        VerifiedReleaseActivationConfigurationBackupSourceKind expectedKind,
        string backupPublishedPath,
        string stagingPath,
        string directoryName)
    {
        Assert.Equal(expectedKind, source.Kind);
        Assert.Equal(
            Path.Combine(backupPublishedPath, directoryName),
            source.SourcePath);
        Assert.Equal(Path.Combine(stagingPath, directoryName), source.StagedPath);
    }

    private static void AssertFailure(
        VerifiedReleaseActivationMigrationPlanReport report,
        VerifiedReleaseActivationMigrationPlanFailureCode failureCode)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.Null(report.Plan);
        Assert.False(report.ExactActivationPlanBound);
        Assert.False(report.ExactConfigurationBackupBound);
        Assert.False(report.SourceReadPerformed);
        Assert.False(report.FileWritePerformed);
        Assert.False(report.MigrationExecutionPerformed);
        Assert.False(report.MigrationReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
    }

    private sealed class Fixture
    {
        private readonly ReleaseMigrationKind m_migrationKind;
        private readonly int? m_migrationFrom;
        private readonly int? m_migrationTo;
        private readonly string m_migrationIdentity;
        private readonly int m_targetSchema;

        internal Fixture(
            ReleaseMigrationKind migrationKind = ReleaseMigrationKind.Required,
            int? migrationFrom = 1,
            int? migrationTo = 2,
            string migrationIdentity = "schema-1-to-2",
            int targetSchema = 2)
        {
            m_migrationKind = migrationKind;
            m_migrationFrom = migrationFrom;
            m_migrationTo = migrationTo;
            m_migrationIdentity = migrationIdentity;
            m_targetSchema = targetSchema;
            Root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"activation-migration-plan-{Guid.NewGuid():N}"));
            DeploymentRoot = Path.Combine(Root, "deployment");
            Paths = new InstallationPaths(
                Path.Combine(Root, "configuration"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(DeploymentRoot, "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            ActivationPlanResult = CreateActivationPlanResult();
            VerifiedReleaseActivationConfigurationBackupPlanReport backupPlanReport =
                new VerifiedReleaseActivationConfigurationBackupPlanner(Paths)
                    .Compose(ActivationPlanResult);
            Assert.True(backupPlanReport.Succeeded);
            BackupPlan = backupPlanReport.Plan!;
            VerifiedReleaseActivationConfigurationBackup backup =
                new(
                    BackupPlan,
                    directoryCount: 6,
                    fileCount: 3,
                    backupBytes: 51,
                    manifestSha256:
                        Enumerable.Repeat((byte)0x3A, 32).ToArray(),
                    completedAt:
                        new DateTimeOffset(2026, 8, 4, 10, 30, 0, TimeSpan.Zero));
            BackupReport =
                VerifiedReleaseActivationConfigurationBackupReport.Success(backup);
        }

        internal string Root { get; }
        internal string DeploymentRoot { get; }
        internal InstallationPaths Paths { get; }
        internal VerifiedReleaseActivationPlanCompositionResult ActivationPlanResult
        {
            get;
        }
        internal VerifiedReleaseActivationConfigurationBackupPlan BackupPlan { get; }
        internal VerifiedReleaseActivationConfigurationBackupReport BackupReport
        {
            get;
        }

        internal VerifiedReleaseActivationPlanCompositionResult
            CreateActivationPlanResult()
        {
            string releaseRoot = Paths.ReleaseDirectory;
            string targetPath = Path.Combine(releaseRoot, "aethersdr-8.2.0");
            VerifiedReleaseInstallationPackagePlan[] packages =
                CreatePackages(targetPath);
            VerifiedReleaseInstallationPlan installPlan = new(
                setupRevision: 7,
                installedReleaseIdentity: "aethersdr-8.1.0",
                targetReleaseIdentity: "aethersdr-8.2.0",
                targetVersion: "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                pinnedReleaseIdentity: string.Empty,
                installTransmitSupport: false,
                bundleDirectory: Path.Combine(Root, "bundle"),
                manifestLength: 37,
                manifestSha256: Enumerable.Repeat((byte)0x7A, 32).ToArray(),
                releaseRoot,
                DeploymentRoot,
                targetPath,
                packages,
                m_targetSchema,
                m_migrationKind,
                m_migrationFrom,
                m_migrationTo,
                m_migrationIdentity,
                restartGatewayWeb: m_migrationKind == ReleaseMigrationKind.Required,
                restartBroker: false,
                restartAetherRemoteAgent: false,
                restartStationEngine: false,
                restartHost: false,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Migration planning test release.");
            long bytes = 37 + packages.Sum(package => package.Length);
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(installPlan, targetPath, bytes));
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
