using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationConfigurationBackupPlanTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsOnly()
    {
        string[] methods =
            typeof(VerifiedReleaseActivationConfigurationBackupPlanner)
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
    public void DiagnosticsSeparatePlanningFromExecutionEvidenceAndCallers()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationConfigurationBackupPlanDiagnostics snapshot =
            new VerifiedReleaseActivationConfigurationBackupPlanner(fixture.Paths)
                .Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ActivationPlanInputRegistered);
        Assert.True(snapshot.InstallationPathsInputRegistered);
        Assert.True(snapshot.ExactActivationPlanBindingRegistered);
        Assert.True(snapshot.ConfigurationSourcePlanningRegistered);
        Assert.True(snapshot.StateSourcePlanningRegistered);
        Assert.True(snapshot.SecretSourcePlanningRegistered);
        Assert.True(snapshot.ReleaseRootAgreementRegistered);
        Assert.True(snapshot.BackupRootSeparationRegistered);
        Assert.True(snapshot.BackupIdentityPlanningRegistered);
        Assert.True(snapshot.BackupManifestPlanningRegistered);
        Assert.True(snapshot.AtomicPublicationPlanningRegistered);
        Assert.False(snapshot.SourceReadRegistered);
        Assert.False(snapshot.FileWriteRegistered);
        Assert.False(snapshot.DirectoryMutationRegistered);
        Assert.False(snapshot.ExistingBackupOverwriteRegistered);
        Assert.False(snapshot.BackupExecutionRegistered);
        Assert.False(snapshot.ConfigurationBackupEvidenceRegistered);
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
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public void CompositionBindsExactPlanAndThreeDedicatedSourcesWithoutIo()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationConfigurationBackupPlanner planner =
            new(fixture.Paths);

        VerifiedReleaseActivationConfigurationBackupPlanReport report =
            planner.Compose(fixture.PlanResult);

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationConfigurationBackupPlanFailureCode.None,
            report.FailureCode);
        Assert.Equal(3, report.SourceDirectoryCount);
        Assert.True(report.ConfigurationDirectoryIncluded);
        Assert.True(report.StateDirectoryIncluded);
        Assert.True(report.SecretDirectoryIncluded);
        Assert.True(report.BackupRootSeparated);
        Assert.True(report.ExactActivationPlanBound);
        Assert.True(report.BackupManifestRequired);
        Assert.True(report.AtomicPublicationRequired);
        Assert.False(report.SourceReadPerformed);
        Assert.False(report.BackupWritePerformed);
        Assert.False(report.ExistingBackupOverwritten);
        Assert.False(report.ConfigurationBackupReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);

        VerifiedReleaseActivationConfigurationBackupPlan plan =
            Assert.IsType<VerifiedReleaseActivationConfigurationBackupPlan>(
                report.Plan);
        Assert.Same(fixture.PlanResult.Plan, plan.ActivationPlan);
        Assert.Equal(fixture.Paths.BackupDirectory, plan.BackupRootPath);
        Assert.Equal(
            Path.Combine(plan.PublishedPath, "backup-manifest.json"),
            plan.ManifestPath);
        Assert.False(plan.ExistingBackupOverwriteAllowed);
        Assert.True(plan.AtomicPublicationRequired);
        Assert.Equal(3, plan.Sources.Count);
        Assert.Collection(
            plan.Sources,
            source =>
            {
                Assert.Equal(
                    VerifiedReleaseActivationConfigurationBackupSourceKind
                        .Configuration,
                    source.Kind);
                Assert.Equal(
                    fixture.Paths.ConfigurationDirectory,
                    source.SourcePath);
                Assert.Equal(
                    Path.Combine(plan.StagingPath, "configuration"),
                    source.StagedPath);
            },
            source =>
            {
                Assert.Equal(
                    VerifiedReleaseActivationConfigurationBackupSourceKind.State,
                    source.Kind);
                Assert.Equal(fixture.Paths.StateDirectory, source.SourcePath);
                Assert.Equal(
                    Path.Combine(plan.StagingPath, "state"),
                    source.StagedPath);
            },
            source =>
            {
                Assert.Equal(
                    VerifiedReleaseActivationConfigurationBackupSourceKind.Secret,
                    source.Kind);
                Assert.Equal(fixture.Paths.SecretDirectory, source.SourcePath);
                Assert.Equal(
                    Path.Combine(plan.StagingPath, "secrets"),
                    source.StagedPath);
            });

        Assert.False(Directory.Exists(fixture.Root));
        Assert.False(Directory.Exists(plan.StagingPath));
        Assert.False(Directory.Exists(plan.PublishedPath));
        Assert.False(File.Exists(plan.ManifestPath));
    }

    [Fact]
    public void IndependentlyComposedExactPlansRetainDistinctBindings()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationPlanCompositionResult secondPlanResult =
            fixture.CreatePlanResult();
        VerifiedReleaseActivationConfigurationBackupPlanner planner =
            new(fixture.Paths);

        VerifiedReleaseActivationConfigurationBackupPlanReport first =
            planner.Compose(fixture.PlanResult);
        VerifiedReleaseActivationConfigurationBackupPlanReport second =
            planner.Compose(secondPlanResult);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotSame(first.Plan, second.Plan);
        Assert.Same(fixture.PlanResult.Plan, first.Plan!.ActivationPlan);
        Assert.Same(secondPlanResult.Plan, second.Plan!.ActivationPlan);
        Assert.NotSame(
            first.Plan.ActivationPlan,
            second.Plan.ActivationPlan);
        Assert.Equal(first.Plan.PublishedPath, second.Plan.PublishedPath);
    }

    [Fact]
    public void MissingOrTamperedActivationPlanFailsClosed()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationConfigurationBackupPlanner planner =
            new(fixture.Paths);

        VerifiedReleaseActivationConfigurationBackupPlanReport missing =
            planner.Compose(fixture.PlanResult with { Plan = null });
        VerifiedReleaseActivationConfigurationBackupPlanReport tampered =
            planner.Compose(
                fixture.PlanResult with
                {
                    TargetReleaseIdentity = "aethersdr-8.2.1"
                });
        VerifiedReleaseActivationConfigurationBackupPlanReport ineligible =
            planner.Compose(
                fixture.PlanResult with
                {
                    ConfigurationBackupRequired = false
                });

        AssertFailure(
            missing,
            VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                .ActivationPlanUnavailable);
        AssertFailure(
            tampered,
            VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                .ActivationPlanMismatch);
        AssertFailure(
            ineligible,
            VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                .ActivationPlanNotEligible);
    }

    [Fact]
    public void ReleaseRootMustMatchExactActivationPlan()
    {
        Fixture fixture = new();
        InstallationPaths mismatched = fixture.Paths with
        {
            ReleaseDirectory = Path.Combine(fixture.Root, "other-releases")
        };

        VerifiedReleaseActivationConfigurationBackupPlanReport report =
            new VerifiedReleaseActivationConfigurationBackupPlanner(mismatched)
                .Compose(fixture.PlanResult);

        AssertFailure(
            report,
            VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                .ReleaseRootMismatch);
    }

    [Theory]
    [InlineData("backup-under-state")]
    [InlineData("state-under-configuration")]
    [InlineData("backup-under-deployment")]
    public void OverlappingInstallationRootsFailClosed(string scenario)
    {
        Fixture fixture = new();
        InstallationPaths unsafePaths = scenario switch
        {
            "backup-under-state" => fixture.Paths with
            {
                BackupDirectory = Path.Combine(
                    fixture.Paths.StateDirectory,
                    "backups")
            },
            "state-under-configuration" => fixture.Paths with
            {
                StateDirectory = Path.Combine(
                    fixture.Paths.ConfigurationDirectory,
                    "state")
            },
            "backup-under-deployment" => fixture.Paths with
            {
                BackupDirectory = Path.Combine(
                    fixture.DeploymentRoot,
                    "backups")
            },
            _ => throw new InvalidOperationException("Unknown test scenario.")
        };

        VerifiedReleaseActivationConfigurationBackupPlanReport report =
            new VerifiedReleaseActivationConfigurationBackupPlanner(unsafePaths)
                .Compose(fixture.PlanResult);

        AssertFailure(
            report,
            VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                .BackupLayoutUnsafe);
    }

    [Fact]
    public void NonCanonicalInstallationPathFailsClosed()
    {
        Fixture fixture = new();
        InstallationPaths nonCanonical = fixture.Paths with
        {
            ConfigurationDirectory = Path.Combine(
                fixture.Root,
                "configuration",
                "..",
                "configuration")
        };

        VerifiedReleaseActivationConfigurationBackupPlanReport report =
            new VerifiedReleaseActivationConfigurationBackupPlanner(nonCanonical)
                .Compose(fixture.PlanResult);

        AssertFailure(
            report,
            VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                .InstallationPathsInvalid);
    }

    [Fact]
    public void PublicReportRedactsEveryPlannedPath()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationConfigurationBackupPlanReport report =
            new VerifiedReleaseActivationConfigurationBackupPlanner(fixture.Paths)
                .Compose(fixture.PlanResult);
        Assert.True(report.Succeeded);

        string json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Paths.ConfigurationDirectory,
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Paths.StateDirectory,
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Paths.SecretDirectory,
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Paths.BackupDirectory,
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".staging", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "backup-manifest.json",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "sourceDirectoryCount",
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertFailure(
        VerifiedReleaseActivationConfigurationBackupPlanReport report,
        VerifiedReleaseActivationConfigurationBackupPlanFailureCode failureCode)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.Null(report.Plan);
        Assert.False(report.SourceReadPerformed);
        Assert.False(report.BackupWritePerformed);
        Assert.False(report.ExistingBackupOverwritten);
        Assert.False(report.ConfigurationBackupReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
    }

    private sealed class Fixture
    {
        internal Fixture()
        {
            Root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"activation-backup-plan-{Guid.NewGuid():N}"));
            DeploymentRoot = Path.Combine(Root, "deployment");
            Paths = new InstallationPaths(
                Path.Combine(Root, "configuration"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(DeploymentRoot, "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            PlanResult = CreatePlanResult();
        }

        internal string Root { get; }
        internal string DeploymentRoot { get; }
        internal InstallationPaths Paths { get; }
        internal VerifiedReleaseActivationPlanCompositionResult PlanResult
        {
            get;
        }

        internal VerifiedReleaseActivationPlanCompositionResult CreatePlanResult()
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
                targetConfigurationSchemaVersion: 1,
                ReleaseMigrationKind.None,
                migrationFromConfigurationSchemaVersion: null,
                migrationToConfigurationSchemaVersion: null,
                migrationIdentity: string.Empty,
                restartGatewayWeb: false,
                restartBroker: false,
                restartAetherRemoteAgent: false,
                restartStationEngine: false,
                restartHost: false,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Configuration backup planning test release.");
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
