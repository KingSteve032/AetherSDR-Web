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
public sealed class VerifiedReleaseActivationMigrationRunnerInvocationTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsOnly()
    {
        string[] methods =
            typeof(VerifiedReleaseActivationMigrationRunnerInvocationService)
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
    public void DiagnosticsSeparateProbeInvocationFromMigrationExecutionAndCallers()
    {
        VerifiedReleaseActivationMigrationRunnerInvocationDiagnostics snapshot =
            new VerifiedReleaseActivationMigrationRunnerInvocationService().Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.RunnerSelectionInputRegistered);
        Assert.True(snapshot.ExactRunnerSelectionBindingRegistered);
        Assert.True(snapshot.NoOpResolutionRegistered);
        Assert.True(snapshot.ImmediateRunnerArtifactRevalidationRegistered);
        Assert.True(snapshot.DirectProcessInvocationRegistered);
        Assert.False(snapshot.ShellInvocationRegistered);
        Assert.True(snapshot.ClearedEnvironmentRegistered);
        Assert.True(snapshot.BoundedJsonStdinRegistered);
        Assert.True(snapshot.BoundedStdoutRegistered);
        Assert.True(snapshot.BoundedStderrRegistered);
        Assert.True(snapshot.HardTimeoutRegistered);
        Assert.True(snapshot.ProcessTreeTerminationRegistered);
        Assert.True(snapshot.ProbeOnlyProtocolRegistered);
        Assert.False(snapshot.MigrationSourcePathInputRegistered);
        Assert.False(snapshot.MigrationSourceReadRegistered);
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
    public async Task RequiredMigrationInvokesExactPinnedProbeWithoutPathsOrExecution()
    {
        using Fixture fixture = new("success");
        VerifiedReleaseActivationMigrationRunnerInvocationService service =
            new(TimeSpan.FromSeconds(2));

        VerifiedReleaseActivationMigrationRunnerInvocationReport report =
            await service.InvokeAsync(fixture.SelectionReport);

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode.None,
            report.FailureCode);
        Assert.Equal(ReleaseMigrationKind.Required, report.MigrationKind);
        Assert.Equal(1, report.FromConfigurationSchemaVersion);
        Assert.Equal(2, report.ToConfigurationSchemaVersion);
        Assert.True(report.MigrationRequired);
        Assert.False(report.NoOpMigrationResolved);
        Assert.True(report.ExactRunnerSelectionBound);
        Assert.True(report.RunnerArtifactRevalidated);
        Assert.True(report.ShellInvocationDisabled);
        Assert.True(report.EnvironmentCleared);
        Assert.True(report.ProbeRequestSent);
        Assert.True(report.RunnerInvoked);
        Assert.True(report.ProbeResponseAccepted);
        Assert.Equal(1, report.RunnerProtocolVersion);
        Assert.False(report.MigrationSourcePathProvided);
        Assert.False(report.MigrationSourceReadPerformed);
        Assert.False(report.FileWritePerformed);
        Assert.False(report.DirectoryMutationPerformed);
        Assert.False(report.MigrationExecutionPerformed);
        Assert.False(report.MigrationReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
        Assert.False(Directory.Exists(fixture.MigrationPlan.StagingPath));
        Assert.False(Directory.Exists(fixture.MigrationPlan.PublishedPath));
        Assert.False(File.Exists(fixture.MigrationPlan.ManifestPath));
    }

    [Fact]
    public async Task NoMigrationResolvesWithoutTrustOrProcessInvocation()
    {
        using Fixture fixture = new(
            "success",
            ReleaseMigrationKind.None,
            migrationFrom: null,
            migrationTo: null,
            migrationIdentity: string.Empty,
            targetSchema: 1,
            trustEnabled: false);
        VerifiedReleaseActivationMigrationRunnerInvocationService service =
            new(TimeSpan.FromSeconds(1));

        VerifiedReleaseActivationMigrationRunnerInvocationReport report =
            await service.InvokeAsync(fixture.SelectionReport);

        Assert.True(report.Succeeded);
        Assert.Equal(ReleaseMigrationKind.None, report.MigrationKind);
        Assert.False(report.MigrationRequired);
        Assert.True(report.NoOpMigrationResolved);
        Assert.True(report.ExactRunnerSelectionBound);
        Assert.False(report.RunnerArtifactRevalidated);
        Assert.False(report.ProbeRequestSent);
        Assert.False(report.RunnerInvoked);
        Assert.False(report.ProbeResponseAccepted);
        Assert.Null(report.RunnerProtocolVersion);
        Assert.True(report.MigrationReady);
    }

    [Fact]
    public async Task RunnerChangedAfterSelectionFailsBeforeInvocation()
    {
        using Fixture fixture = new("success");
        File.SetUnixFileMode(
            fixture.RunnerPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        File.AppendAllText(fixture.RunnerPath, "\n# changed\n");
        File.SetUnixFileMode(
            fixture.RunnerPath,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);

        VerifiedReleaseActivationMigrationRunnerInvocationReport report =
            await new VerifiedReleaseActivationMigrationRunnerInvocationService(
                TimeSpan.FromSeconds(1)).InvokeAsync(fixture.SelectionReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                .RunnerArtifactChanged,
            exactSelectionBound: true,
            artifactRevalidated: false,
            runnerInvoked: false);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unknown")]
    [InlineData("mismatch")]
    [InlineData("stderr")]
    public async Task InvalidOrNoisyRunnerResponseFailsClosed(string behavior)
    {
        using Fixture fixture = new(behavior);

        VerifiedReleaseActivationMigrationRunnerInvocationReport report =
            await new VerifiedReleaseActivationMigrationRunnerInvocationService(
                TimeSpan.FromSeconds(2)).InvokeAsync(fixture.SelectionReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                .RunnerResponseInvalid,
            exactSelectionBound: true,
            artifactRevalidated: true,
            runnerInvoked: true);
    }

    [Fact]
    public async Task RunnerRejectionFailsClosed()
    {
        using Fixture fixture = new("reject");

        VerifiedReleaseActivationMigrationRunnerInvocationReport report =
            await new VerifiedReleaseActivationMigrationRunnerInvocationService(
                TimeSpan.FromSeconds(2)).InvokeAsync(fixture.SelectionReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                .RunnerProbeRejected,
            exactSelectionBound: true,
            artifactRevalidated: true,
            runnerInvoked: true);
    }

    [Fact]
    public async Task NonzeroRunnerExitFailsClosed()
    {
        using Fixture fixture = new("nonzero");

        VerifiedReleaseActivationMigrationRunnerInvocationReport report =
            await new VerifiedReleaseActivationMigrationRunnerInvocationService(
                TimeSpan.FromSeconds(2)).InvokeAsync(fixture.SelectionReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                .RunnerProcessFailed,
            exactSelectionBound: true,
            artifactRevalidated: true,
            runnerInvoked: true);
    }

    [Fact]
    public async Task OversizedRunnerOutputIsTerminatedAndFailsClosed()
    {
        using Fixture fixture = new("oversized");

        VerifiedReleaseActivationMigrationRunnerInvocationReport report =
            await new VerifiedReleaseActivationMigrationRunnerInvocationService(
                TimeSpan.FromSeconds(2)).InvokeAsync(fixture.SelectionReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                .RunnerOutputTooLarge,
            exactSelectionBound: true,
            artifactRevalidated: true,
            runnerInvoked: true);
    }

    [Fact]
    public async Task RunnerTimeoutTerminatesProcessTreeAndFailsClosed()
    {
        using Fixture fixture = new("timeout");

        VerifiedReleaseActivationMigrationRunnerInvocationReport report =
            await new VerifiedReleaseActivationMigrationRunnerInvocationService(
                TimeSpan.FromMilliseconds(150)).InvokeAsync(fixture.SelectionReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                .RunnerTimedOut,
            exactSelectionBound: true,
            artifactRevalidated: true,
            runnerInvoked: true);
    }

    [Fact]
    public async Task MissingOrTamperedExactSelectionFailsBeforeProcessStart()
    {
        using Fixture fixture = new("success");
        VerifiedReleaseActivationMigrationRunnerInvocationService service =
            new(TimeSpan.FromSeconds(1));

        VerifiedReleaseActivationMigrationRunnerInvocationReport missing =
            await service.InvokeAsync(
                fixture.SelectionReport with { Selection = null });
        VerifiedReleaseActivationMigrationRunnerInvocationReport tampered =
            await service.InvokeAsync(
                fixture.SelectionReport with
                {
                    ToConfigurationSchemaVersion = 3
                });
        VerifiedReleaseActivationMigrationRunnerInvocationReport ineligible =
            await service.InvokeAsync(
                fixture.SelectionReport with
                {
                    RunnerArtifactValidatedAtStartup = false
                });

        AssertFailure(
            missing,
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                .RunnerSelectionUnavailable);
        AssertFailure(
            tampered,
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                .RunnerSelectionMismatch);
        AssertFailure(
            ineligible,
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                .RunnerSelectionMismatch);
    }

    [Fact]
    public async Task PublicReportRedactsRunnerMigrationDigestRequestAndPaths()
    {
        using Fixture fixture = new("success");
        VerifiedReleaseActivationMigrationRunnerInvocationReport report =
            await new VerifiedReleaseActivationMigrationRunnerInvocationService(
                TimeSpan.FromSeconds(2)).InvokeAsync(fixture.SelectionReport);
        Assert.True(report.Succeeded);

        string json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.RunnerPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain("runner-v1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("schema-1-to-2", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.RunnerSha256, json, StringComparison.Ordinal);
        Assert.DoesNotContain("requestId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stagingPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publishedPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runnerInvoked", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertFailure(
        VerifiedReleaseActivationMigrationRunnerInvocationReport report,
        VerifiedReleaseActivationMigrationRunnerInvocationFailureCode failureCode,
        bool exactSelectionBound = false,
        bool artifactRevalidated = false,
        bool runnerInvoked = false)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.Equal(exactSelectionBound, report.ExactRunnerSelectionBound);
        Assert.Equal(artifactRevalidated, report.RunnerArtifactRevalidated);
        Assert.Equal(runnerInvoked, report.RunnerInvoked);
        Assert.False(report.ProbeResponseAccepted);
        Assert.False(report.MigrationSourcePathProvided);
        Assert.False(report.MigrationSourceReadPerformed);
        Assert.False(report.FileWritePerformed);
        Assert.False(report.DirectoryMutationPerformed);
        Assert.False(report.MigrationExecutionPerformed);
        Assert.False(report.MigrationReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ReleaseMigrationKind m_migrationKind;
        private readonly int? m_migrationFrom;
        private readonly int? m_migrationTo;
        private readonly string m_migrationIdentity;
        private readonly int m_targetSchema;

        internal Fixture(
            string behavior,
            ReleaseMigrationKind migrationKind = ReleaseMigrationKind.Required,
            int? migrationFrom = 1,
            int? migrationTo = 2,
            string migrationIdentity = "schema-1-to-2",
            int targetSchema = 2,
            bool trustEnabled = true)
        {
            m_migrationKind = migrationKind;
            m_migrationFrom = migrationFrom;
            m_migrationTo = migrationTo;
            m_migrationIdentity = migrationIdentity;
            m_targetSchema = targetSchema;
            Root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"migration-runner-invocation-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Root);
            File.SetUnixFileMode(
                Root,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            string runnerDirectory = Path.Combine(Root, "runner-trust");
            Directory.CreateDirectory(runnerDirectory);
            File.SetUnixFileMode(
                runnerDirectory,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            RunnerPath = Path.Combine(runnerDirectory, "migration-runner.py");
            File.WriteAllText(RunnerPath, CreateRunnerScript(behavior, Root));
            File.SetUnixFileMode(
                RunnerPath,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
            RunnerSha256 = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(RunnerPath)))
                .ToLowerInvariant();

            Paths = new InstallationPaths(
                Path.Combine(Root, "configuration"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(Root, "deployment", "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            VerifiedReleaseActivationPlanCompositionResult activationPlan =
                CreateActivationPlanResult();
            VerifiedReleaseActivationConfigurationBackupPlanReport backupPlan =
                new VerifiedReleaseActivationConfigurationBackupPlanner(Paths)
                    .Compose(activationPlan);
            Assert.True(backupPlan.Succeeded);
            VerifiedReleaseActivationConfigurationBackup backup = new(
                backupPlan.Plan!,
                directoryCount: 6,
                fileCount: 3,
                backupBytes: 51,
                manifestSha256: Enumerable.Repeat((byte)0x3A, 32).ToArray(),
                completedAt:
                    new DateTimeOffset(2026, 8, 4, 11, 40, 0, TimeSpan.Zero));
            VerifiedReleaseActivationMigrationPlanReport migrationPlanReport =
                new VerifiedReleaseActivationMigrationPlanComposer().Compose(
                    activationPlan,
                    VerifiedReleaseActivationConfigurationBackupReport.Success(
                        backup));
            Assert.True(migrationPlanReport.Succeeded);
            MigrationPlan = migrationPlanReport.Plan!;

            ReleaseMigrationRunnerTrustSettings trustSettings =
                migrationKind == ReleaseMigrationKind.None
                    ? new ReleaseMigrationRunnerTrustSettings
                    {
                        SelectionEnabled = trustEnabled,
                        Runners = []
                    }
                    : new ReleaseMigrationRunnerTrustSettings
                    {
                        SelectionEnabled = trustEnabled,
                        Runners =
                        [
                            new ReleaseMigrationRunnerTrustEntrySettings
                            {
                                RunnerIdentity = "runner-v1",
                                RunnerProtocolVersion =
                                    ReleaseMigrationRunnerTrustRegistry
                                        .CurrentRunnerProtocolVersion,
                                RunnerPath = RunnerPath,
                                Sha256 = RunnerSha256,
                                Migrations =
                                [
                                    new ReleaseMigrationRunnerTrustMappingSettings
                                    {
                                        MigrationIdentity = migrationIdentity,
                                        FromConfigurationSchemaVersion =
                                            migrationFrom!.Value,
                                        ToConfigurationSchemaVersion =
                                            migrationTo!.Value
                                    }
                                ]
                            }
                        ]
                    };
            ReleaseMigrationRunnerTrustRegistry trust = new(
                Options.Create(trustSettings),
                NullLogger<ReleaseMigrationRunnerTrustRegistry>.Instance);
            SelectionReport =
                new VerifiedReleaseActivationMigrationRunnerSelector(trust)
                    .Select(migrationPlanReport);
            Assert.True(SelectionReport.Succeeded);
        }

        internal string Root { get; }
        internal string RunnerPath { get; }
        internal string RunnerSha256 { get; }
        internal InstallationPaths Paths { get; }
        internal VerifiedReleaseActivationMigrationPlan MigrationPlan { get; }
        internal VerifiedReleaseActivationMigrationRunnerSelectionReport
            SelectionReport
        {
            get;
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(RunnerPath))
                {
                    File.SetUnixFileMode(
                        RunnerPath,
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
                }
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private VerifiedReleaseActivationPlanCompositionResult
            CreateActivationPlanResult()
        {
            string deploymentRoot = Path.Combine(Root, "deployment");
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
                deploymentRoot,
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
                releaseNotesSummary: "Migration runner invocation test release.");
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

        private static string CreateRunnerScript(
            string behavior,
            string forbiddenPath)
        {
            string behaviorJson = JsonSerializer.Serialize(behavior);
            string forbiddenJson = JsonSerializer.Serialize(forbiddenPath);
            return $$"""
#!/usr/bin/python3
import json
import os
import sys
import time

behavior = {{behaviorJson}}
forbidden_path = {{forbiddenJson}}
line = sys.stdin.readline()
if len(sys.argv) != 1:
    sys.exit(41)
if any(name in os.environ for name in ("HOME", "PATH", "DOTNET_ROOT", "ASPNETCORE_ENVIRONMENT")):
    sys.exit(42)
if forbidden_path in line:
    sys.exit(43)
request = json.loads(line)
if request.get("MigrationExecutionRequested") is not False:
    sys.exit(44)
if request.get("MigrationSourcePathsProvided") is not False:
    sys.exit(45)
if behavior == "timeout":
    time.sleep(10)
if behavior == "oversized":
    sys.stdout.write("x" * 20000)
    sys.stdout.flush()
    sys.exit(0)
if behavior == "malformed":
    print("not-json")
    sys.exit(0)
if behavior == "nonzero":
    sys.exit(7)
if behavior == "stderr":
    print("unexpected warning", file=sys.stderr)
response = {
    "ProtocolVersion": request["ProtocolVersion"],
    "Type": "aethersdr.release-migration.probe-result.v1",
    "RequestId": request["RequestId"],
    "RunnerIdentity": request["RunnerIdentity"],
    "MigrationIdentity": request["MigrationIdentity"],
    "FromConfigurationSchemaVersion": request["FromConfigurationSchemaVersion"],
    "ToConfigurationSchemaVersion": request["ToConfigurationSchemaVersion"],
    "ProbeAccepted": behavior != "reject",
    "MigrationExecutionPerformed": False,
    "FilesystemMutationPerformed": False,
    "MigrationSourcePathsReceived": False,
}
if behavior == "mismatch":
    response["MigrationIdentity"] = "different-migration"
if behavior == "unknown":
    response["Unexpected"] = True
print(json.dumps(response, separators=(",", ":")))
""";
        }
    }
}
