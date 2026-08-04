using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleaseActivationMigrationExecutionServiceTests
{
    [Fact]
    public void PublicSurfaceExposesOnlyDiagnosticsAndState()
    {
        string[] publicMethods =
            typeof(VerifiedReleaseActivationMigrationExecutionService)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.DeclaringType ==
                    typeof(VerifiedReleaseActivationMigrationExecutionService))
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(["get_Snapshot", "get_State"], publicMethods);
        VerifiedReleaseActivationMigrationExecutionDiagnostics snapshot =
            new VerifiedReleaseActivationMigrationExecutionService(
                _ => Task.FromResult(default(ReleaseStatusReadResult)!)).Snapshot;
        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ExactRunnerInvocationBindingRegistered);
        Assert.True(snapshot.ImmutableBackupManifestValidationRegistered);
        Assert.True(snapshot.StagedCopyRegistered);
        Assert.True(snapshot.DirectRunnerExecutionRegistered);
        Assert.False(snapshot.ShellInvocationRegistered);
        Assert.True(snapshot.ClearedEnvironmentRegistered);
        Assert.True(snapshot.ExactMigrationEvidenceRegistered);
        Assert.False(snapshot.ExistingMigrationOverwriteRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.ActivationAuthorityRegistered);
        Assert.False(snapshot.OperationalCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public async Task NoOpMigrationBecomesReadyWithoutProcessOrFilesystemMutation()
    {
        using Fixture fixture = await Fixture.CreateAsync(
            "success",
            ReleaseMigrationKind.None);
        VerifiedReleaseActivationMigrationExecutionService service =
            fixture.CreateService();

        VerifiedReleaseActivationMigrationExecutionReport report =
            await service.ExecuteAsync(fixture.InvocationReport);

        Assert.True(report.Succeeded);
        Assert.True(report.NoOpMigrationResolved);
        Assert.True(report.ExactRunnerInvocationBound);
        Assert.True(report.MigrationReady);
        Assert.False(report.ImmutableBackupValidated);
        Assert.False(report.PrivateStagingCreated);
        Assert.False(report.RunnerInvoked);
        Assert.False(report.MigrationExecutionPerformed);
        Assert.False(report.MigrationManifestWritten);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
        Assert.False(Directory.Exists(fixture.MigrationPlan.MigrationRootPath));
        Assert.True(service.State.MigrationReady);
        Assert.False(service.State.MigrationRequired);
    }

    [Fact]
    public async Task RequiredMigrationCopiesExecutesFreezesAndPublishesExactTree()
    {
        using Fixture fixture = await Fixture.CreateAsync("success");
        string backupConfiguration = File.ReadAllText(
            Path.Combine(
                fixture.MigrationPlan.Sources.Single(source => source.Kind ==
                    VerifiedReleaseActivationConfigurationBackupSourceKind
                        .Configuration).SourcePath,
                "aethersdr.json"));
        VerifiedReleaseActivationMigrationExecutionService service =
            fixture.CreateService();

        VerifiedReleaseActivationMigrationExecutionReport report =
            await service.ExecuteAsync(fixture.InvocationReport);

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationMigrationExecutionFailureCode.None,
            report.FailureCode);
        Assert.True(report.ExactRunnerInvocationBound);
        Assert.True(report.ReleaseStatusStable);
        Assert.True(report.ImmutableBackupValidated);
        Assert.True(report.PrivateStagingCreated);
        Assert.True(report.StagedCopyCompleted);
        Assert.True(report.RunnerArtifactRevalidated);
        Assert.True(report.RunnerInvoked);
        Assert.True(report.MigrationExecutionPerformed);
        Assert.True(report.MigrationManifestWritten);
        Assert.True(report.StagingTreeImmutable);
        Assert.True(report.AtomicPublicationCompleted);
        Assert.True(report.PublishedTreeValidated);
        Assert.True(report.MigrationReady);
        Assert.False(report.ReconciliationRequired);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
        Assert.False(Directory.Exists(fixture.MigrationPlan.StagingPath));
        Assert.True(Directory.Exists(fixture.MigrationPlan.PublishedPath));
        Assert.True(File.Exists(fixture.MigrationPlan.ManifestPath));
        string migratedConfiguration = File.ReadAllText(
            Path.Combine(
                fixture.MigrationPlan.PublishedPath,
                "configuration",
                "aethersdr.json"));
        Assert.Equal("configuration-value-migrated", migratedConfiguration);
        Assert.Equal(
            backupConfiguration,
            File.ReadAllText(
                Path.Combine(
                    fixture.MigrationPlan.Sources.Single(source => source.Kind ==
                        VerifiedReleaseActivationConfigurationBackupSourceKind
                            .Configuration).SourcePath,
                    "aethersdr.json")));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserExecute,
            File.GetUnixFileMode(fixture.MigrationPlan.PublishedPath));
        Assert.Equal(
            UnixFileMode.UserRead,
            File.GetUnixFileMode(fixture.MigrationPlan.ManifestPath));
        Assert.True(service.State.MigrationReady);
        Assert.True(service.State.PublishedTreeImmutable);
        Assert.Equal(report.DirectoryCount, service.State.DirectoryCount);
        Assert.Equal(report.FileCount, service.State.FileCount);
        Assert.Equal(report.MigrationBytes, service.State.MigrationBytes);
        VerifiedReleaseActivationMigrationObservation observation =
            service.Observe(fixture.MigrationPlan.ActivationPlan);
        Assert.True(observation.MigrationReady);
        Assert.True(observation.MigrationRequired);
    }

    [Fact]
    public async Task ChangedRunnerAfterProbeFailsBeforeExecutionAndCleansStaging()
    {
        using Fixture fixture = await Fixture.CreateAsync("success");
        File.SetUnixFileMode(
            fixture.RunnerPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.AppendAllText(fixture.RunnerPath, "\n# changed\n");
        File.SetUnixFileMode(
            fixture.RunnerPath,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);

        VerifiedReleaseActivationMigrationExecutionReport report =
            await fixture.CreateService().ExecuteAsync(fixture.InvocationReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationMigrationExecutionFailureCode
                .RunnerArtifactChanged,
            immutableBackupValidated: true,
            stagedCopyCompleted: true);
        Assert.False(Directory.Exists(fixture.MigrationPlan.StagingPath));
        Assert.False(Directory.Exists(fixture.MigrationPlan.PublishedPath));
    }

    [Theory]
    [InlineData("reject-execution",
        VerifiedReleaseActivationMigrationExecutionFailureCode.RunnerExecutionRejected)]
    [InlineData("malformed-execution",
        VerifiedReleaseActivationMigrationExecutionFailureCode.RunnerResponseInvalid)]
    [InlineData("nonzero-execution",
        VerifiedReleaseActivationMigrationExecutionFailureCode.RunnerProcessFailed)]
    public async Task RunnerFailureFailsClosedAndRemovesPrivateStaging(
        string behavior,
        VerifiedReleaseActivationMigrationExecutionFailureCode failureCode)
    {
        using Fixture fixture = await Fixture.CreateAsync(behavior);

        VerifiedReleaseActivationMigrationExecutionReport report =
            await fixture.CreateService().ExecuteAsync(fixture.InvocationReport);

        AssertFailure(
            report,
            failureCode,
            immutableBackupValidated: true,
            stagedCopyCompleted: true,
            runnerArtifactRevalidated: true,
            runnerInvoked: true);
        Assert.False(Directory.Exists(fixture.MigrationPlan.StagingPath));
        Assert.False(Directory.Exists(fixture.MigrationPlan.PublishedPath));
    }

    [Fact]
    public async Task RunnerTimeoutTerminatesAndCleansStaging()
    {
        using Fixture fixture = await Fixture.CreateAsync("timeout-execution");
        VerifiedReleaseActivationMigrationExecutionService service =
            fixture.CreateService(timeout: TimeSpan.FromMilliseconds(150));

        VerifiedReleaseActivationMigrationExecutionReport report =
            await service.ExecuteAsync(fixture.InvocationReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationMigrationExecutionFailureCode.RunnerTimedOut,
            immutableBackupValidated: true,
            stagedCopyCompleted: true,
            runnerArtifactRevalidated: true,
            runnerInvoked: true);
        Assert.False(Directory.Exists(fixture.MigrationPlan.StagingPath));
        Assert.False(Directory.Exists(fixture.MigrationPlan.PublishedPath));
    }

    [Fact]
    public async Task AmbiguousAtomicPublicationRequiresReconciliation()
    {
        using Fixture fixture = await Fixture.CreateAsync("success");
        VerifiedReleaseActivationMigrationExecutionService service =
            fixture.CreateService(
                directoryMove: (source, destination) =>
                {
                    Directory.Move(source, destination);
                    throw new IOException("ambiguous publish");
                });

        VerifiedReleaseActivationMigrationExecutionReport report =
            await service.ExecuteAsync(fixture.InvocationReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationMigrationExecutionFailureCode
                .PublishedStateRequiresReconciliation,
            report.FailureCode);
        Assert.True(report.AtomicPublicationCompleted);
        Assert.True(report.ReconciliationRequired);
        Assert.True(Directory.Exists(fixture.MigrationPlan.PublishedPath));
        Assert.True(service.State.ReconciliationRequired);
        Assert.False(service.State.MigrationReady);
    }

    [Fact]
    public async Task ServiceWillNotOverwriteCompletedMigrationEvidence()
    {
        using Fixture fixture = await Fixture.CreateAsync("success");
        VerifiedReleaseActivationMigrationExecutionService service =
            fixture.CreateService();
        VerifiedReleaseActivationMigrationExecutionReport first =
            await service.ExecuteAsync(fixture.InvocationReport);
        Assert.True(first.Succeeded);

        VerifiedReleaseActivationMigrationExecutionReport second =
            await service.ExecuteAsync(fixture.InvocationReport);

        Assert.False(second.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationMigrationExecutionFailureCode
                .MigrationAlreadyPresent,
            second.FailureCode);
        Assert.True(Directory.Exists(fixture.MigrationPlan.PublishedPath));
    }

    private static void AssertFailure(
        VerifiedReleaseActivationMigrationExecutionReport report,
        VerifiedReleaseActivationMigrationExecutionFailureCode failureCode,
        bool immutableBackupValidated = false,
        bool stagedCopyCompleted = false,
        bool runnerArtifactRevalidated = false,
        bool runnerInvoked = false)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.True(report.ExactRunnerInvocationBound);
        Assert.Equal(immutableBackupValidated, report.ImmutableBackupValidated);
        Assert.Equal(stagedCopyCompleted, report.StagedCopyCompleted);
        Assert.Equal(
            runnerArtifactRevalidated,
            report.RunnerArtifactRevalidated);
        Assert.Equal(runnerInvoked, report.RunnerInvoked);
        Assert.False(report.MigrationReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            string behavior,
            ReleaseMigrationKind migrationKind)
        {
            Behavior = behavior;
            MigrationKind = migrationKind;
            Root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"migration-execution-{Guid.NewGuid():N}"));
            DeploymentRoot = Path.Combine(Root, "deployment");
            Paths = new InstallationPaths(
                Path.Combine(Root, "configuration"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(DeploymentRoot, "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            CreateSourceLayout();
            Status = CreateStatus();
            PlanResult = CreatePlanResult();
            string runnerDirectory = Path.Combine(Root, "runner-trust");
            Directory.CreateDirectory(runnerDirectory);
            File.SetUnixFileMode(
                runnerDirectory,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            RunnerPath = Path.Combine(runnerDirectory, "migration-runner.py");
        }

        internal string Behavior { get; }
        internal ReleaseMigrationKind MigrationKind { get; }
        internal string Root { get; }
        internal string DeploymentRoot { get; }
        internal InstallationPaths Paths { get; }
        internal string RunnerPath { get; }
        internal ReleaseStatusReadResult Status { get; }
        internal VerifiedReleaseActivationPlanCompositionResult PlanResult { get; }
        internal VerifiedReleaseActivationMigrationPlan MigrationPlan { get; private set; } = null!;
        internal VerifiedReleaseActivationMigrationRunnerInvocationReport InvocationReport { get; private set; } = null!;

        internal static async Task<Fixture> CreateAsync(
            string behavior,
            ReleaseMigrationKind migrationKind = ReleaseMigrationKind.Required)
        {
            Fixture fixture = new(behavior, migrationKind);
            VerifiedReleaseActivationConfigurationBackupPlanReport backupPlan =
                new VerifiedReleaseActivationConfigurationBackupPlanner(fixture.Paths)
                    .Compose(fixture.PlanResult);
            Assert.True(backupPlan.Succeeded);
            VerifiedReleaseActivationConfigurationBackupService backupService =
                new(
                    _ => Task.FromResult(fixture.Status),
                    Directory.Move,
                    TimeProvider.System);
            VerifiedReleaseActivationConfigurationBackupReport backup =
                await backupService.ExecuteAsync(backupPlan);
            Assert.True(backup.Succeeded);
            VerifiedReleaseActivationMigrationPlanReport migrationPlanReport =
                new VerifiedReleaseActivationMigrationPlanComposer().Compose(
                    fixture.PlanResult,
                    backup);
            Assert.True(migrationPlanReport.Succeeded);
            fixture.MigrationPlan = migrationPlanReport.Plan!;

            File.WriteAllText(
                fixture.RunnerPath,
                CreateRunnerScript(
                    behavior,
                    fixture.MigrationPlan.ConfigurationBackup.Plan.PublishedPath));
            File.SetUnixFileMode(
                fixture.RunnerPath,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
            string runnerSha = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(fixture.RunnerPath)))
                .ToLowerInvariant();
            ReleaseMigrationRunnerTrustSettings trustSettings =
                migrationKind == ReleaseMigrationKind.None
                    ? new ReleaseMigrationRunnerTrustSettings
                    {
                        SelectionEnabled = false,
                        Runners = []
                    }
                    : new ReleaseMigrationRunnerTrustSettings
                    {
                        SelectionEnabled = true,
                        Runners =
                        [
                            new ReleaseMigrationRunnerTrustEntrySettings
                            {
                                RunnerIdentity = "runner-v1",
                                RunnerProtocolVersion =
                                    ReleaseMigrationRunnerTrustRegistry
                                        .CurrentRunnerProtocolVersion,
                                RunnerPath = fixture.RunnerPath,
                                Sha256 = runnerSha,
                                Migrations =
                                [
                                    new ReleaseMigrationRunnerTrustMappingSettings
                                    {
                                        MigrationIdentity = "schema-1-to-2",
                                        FromConfigurationSchemaVersion = 1,
                                        ToConfigurationSchemaVersion = 2
                                    }
                                ]
                            }
                        ]
                    };
            ReleaseMigrationRunnerTrustRegistry trust = new(
                Options.Create(trustSettings),
                NullLogger<ReleaseMigrationRunnerTrustRegistry>.Instance);
            VerifiedReleaseActivationMigrationRunnerSelectionReport selection =
                new VerifiedReleaseActivationMigrationRunnerSelector(trust)
                    .Select(migrationPlanReport);
            Assert.True(selection.Succeeded);
            fixture.InvocationReport =
                await new VerifiedReleaseActivationMigrationRunnerInvocationService(
                    TimeSpan.FromSeconds(2)).InvokeAsync(selection);
            Assert.True(fixture.InvocationReport.Succeeded);
            return fixture;
        }

        internal VerifiedReleaseActivationMigrationExecutionService CreateService(
            Action<string, string>? directoryMove = null,
            TimeSpan? timeout = null) =>
            new(
                _ => Task.FromResult(Status),
                directoryMove,
                TimeProvider.System,
                timeout);

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }
            MakeWritable(new DirectoryInfo(Root));
            Directory.Delete(Root, recursive: true);
        }

        private void CreateSourceLayout()
        {
            Directory.CreateDirectory(Paths.ConfigurationDirectory);
            Directory.CreateDirectory(Path.Combine(Paths.StateDirectory, "setup"));
            Directory.CreateDirectory(
                Path.Combine(Paths.SecretDirectory, "data-protection"));
            Directory.CreateDirectory(Paths.BackupDirectory);
            Directory.CreateDirectory(Paths.ReleaseDirectory);
            Directory.CreateDirectory(Paths.LogDirectory);
            foreach (string directory in new[]
                     {
                         Paths.ConfigurationDirectory,
                         Paths.StateDirectory,
                         Path.Combine(Paths.StateDirectory, "setup"),
                         Paths.SecretDirectory,
                         Path.Combine(Paths.SecretDirectory, "data-protection"),
                         Paths.BackupDirectory,
                         Paths.ReleaseDirectory,
                         Paths.LogDirectory
                     })
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
            WritePrivateFile(
                Path.Combine(Paths.ConfigurationDirectory, "aethersdr.json"),
                "configuration-value");
            WritePrivateFile(
                Path.Combine(Paths.StateDirectory, "setup", "installation.json"),
                "state-value");
            WritePrivateFile(
                Path.Combine(
                    Paths.SecretDirectory,
                    "data-protection",
                    "key.xml"),
                "secret-value");
        }

        private ReleaseStatusReadResult CreateStatus() =>
            new(
                Succeeded: true,
                ReleaseStatusFailureCode.None,
                "The local release status was read successfully.",
                SetupSchemaVersion: 1,
                SetupRevision: 7,
                SetupComplete: true,
                SetupLockMode: InstallationSetupLockMode.Complete,
                LastCompletedStep: InstallationSetupStep.Administrator,
                UpdateChannel: InstallationUpdateChannel.Stable,
                PinnedReleaseIdentity: string.Empty,
                InstallTransmitSupport: false,
                ReleaseDirectoryPresent: true,
                AvailableReleaseCount: 2,
                AvailableReleaseIdentities:
                    ["aethersdr-8.1.0", "aethersdr-8.2.0"],
                CurrentPointerPresent: true,
                ActiveReleaseIdentity: "aethersdr-8.1.0",
                RollbackCandidateKnown: false);

        private VerifiedReleaseActivationPlanCompositionResult CreatePlanResult()
        {
            string targetPath = Path.Combine(
                Paths.ReleaseDirectory,
                "aethersdr-8.2.0");
            VerifiedReleaseInstallationPackagePlan[] packages =
                CreatePackages(targetPath);
            bool required = MigrationKind == ReleaseMigrationKind.Required;
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
                Paths.ReleaseDirectory,
                DeploymentRoot,
                targetPath,
                packages,
                targetConfigurationSchemaVersion: required ? 2 : 1,
                MigrationKind,
                migrationFromConfigurationSchemaVersion: required ? 1 : null,
                migrationToConfigurationSchemaVersion: required ? 2 : null,
                migrationIdentity: required ? "schema-1-to-2" : string.Empty,
                restartGatewayWeb: required,
                restartBroker: false,
                restartAetherRemoteAgent: false,
                restartStationEngine: false,
                restartHost: false,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Staged-copy migration execution test release.");
            long bytes = 37 + packages.Sum(package => package.Length);
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(installPlan, targetPath, bytes));
            VerifiedReleaseActivationPlanCompositionResult result =
                new VerifiedReleaseActivationPlanComposer().Compose(publication);
            Assert.True(result.Succeeded);
            return result;
        }

        private static void WritePrivateFile(string path, string content)
        {
            File.WriteAllText(path, content);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private static void MakeWritable(DirectoryInfo directory)
        {
            directory.Refresh();
            if (!directory.Exists)
            {
                return;
            }
            if (directory.LinkTarget is not null)
            {
                directory.Delete();
                return;
            }
            File.SetUnixFileMode(
                directory.FullName,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                entry.Refresh();
                if (entry is DirectoryInfo child)
                {
                    MakeWritable(child);
                }
                else if (entry.LinkTarget is not null)
                {
                    entry.Delete();
                }
                else
                {
                    File.SetUnixFileMode(
                        entry.FullName,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
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

        private static string CreateRunnerScript(
            string behavior,
            string forbiddenBackupPath)
        {
            string behaviorJson = JsonSerializer.Serialize(behavior);
            string forbiddenJson = JsonSerializer.Serialize(forbiddenBackupPath);
            return $$"""
#!/usr/bin/python3
import json
import os
import sys
import time

behavior = {{behaviorJson}}
forbidden_backup_path = {{forbiddenJson}}
line = sys.stdin.readline()
if len(sys.argv) != 1:
    sys.exit(41)
if any(name in os.environ for name in ("HOME", "PATH", "DOTNET_ROOT", "ASPNETCORE_ENVIRONMENT")):
    sys.exit(42)
request = json.loads(line)
request_type = request.get("Type")
if request_type == "aethersdr.release-migration.probe.v1":
    response = {
        "ProtocolVersion": request["ProtocolVersion"],
        "Type": "aethersdr.release-migration.probe-result.v1",
        "RequestId": request["RequestId"],
        "RunnerIdentity": request["RunnerIdentity"],
        "MigrationIdentity": request["MigrationIdentity"],
        "FromConfigurationSchemaVersion": request["FromConfigurationSchemaVersion"],
        "ToConfigurationSchemaVersion": request["ToConfigurationSchemaVersion"],
        "ProbeAccepted": True,
        "MigrationExecutionPerformed": False,
        "FilesystemMutationPerformed": False,
        "MigrationSourcePathsReceived": False,
    }
    print(json.dumps(response, separators=(",", ":")))
    sys.exit(0)
if request_type != "aethersdr.release-migration.execute.v1":
    sys.exit(43)
if forbidden_backup_path in line:
    sys.exit(44)
if request.get("MigrationExecutionRequested") is not True:
    sys.exit(45)
if request.get("SourceBackupPathsProvided") is not False:
    sys.exit(46)
if request.get("CurrentPointerMutationAuthorized") is not False:
    sys.exit(47)
if request.get("ActivationAuthorized") is not False:
    sys.exit(48)
if behavior == "timeout-execution":
    time.sleep(10)
if behavior == "malformed-execution":
    print("not-json")
    sys.exit(0)
if behavior == "nonzero-execution":
    sys.exit(7)
configuration_file = os.path.join(request["ConfigurationPath"], "aethersdr.json")
with open(configuration_file, "a", encoding="utf-8") as stream:
    stream.write("-migrated")
os.chmod(configuration_file, 0o600)
response = {
    "ProtocolVersion": request["ProtocolVersion"],
    "Type": "aethersdr.release-migration.execute-result.v1",
    "RequestId": request["RequestId"],
    "RunnerIdentity": request["RunnerIdentity"],
    "MigrationIdentity": request["MigrationIdentity"],
    "FromConfigurationSchemaVersion": request["FromConfigurationSchemaVersion"],
    "ToConfigurationSchemaVersion": request["ToConfigurationSchemaVersion"],
    "ExecutionAccepted": behavior != "reject-execution",
    "MigrationExecutionPerformed": True,
    "StagedCopyMutationPerformed": True,
    "SourceBackupPathsReceived": False,
    "CurrentPointerChanged": False,
    "ActivationPerformed": False,
    "ServiceControlPerformed": False,
    "RadioCommandPerformed": False,
    "TxCommandPerformed": False,
}
print(json.dumps(response, separators=(",", ":")))
""";
        }
    }
}
