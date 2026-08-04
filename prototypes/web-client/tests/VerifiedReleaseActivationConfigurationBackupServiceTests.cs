using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleaseActivationConfigurationBackupServiceTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsAndStateOnly()
    {
        string[] methods =
            typeof(VerifiedReleaseActivationConfigurationBackupService)
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(["get_Snapshot", "get_State"], methods);
    }

    [Fact]
    public void DiagnosticsSeparateExecutionEvidenceAndAbsentCallers()
    {
        using Fixture fixture = new();
        VerifiedReleaseActivationConfigurationBackupService service =
            fixture.CreateService();
        VerifiedReleaseActivationConfigurationBackupDiagnostics snapshot =
            service.Snapshot;
        VerifiedReleaseActivationConfigurationBackupStateDiagnostics state =
            service.State;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ExactBackupPlanInputRegistered);
        Assert.True(snapshot.ReleaseStatusDoubleReadRegistered);
        Assert.True(snapshot.BoundedSourceTraversalRegistered);
        Assert.True(snapshot.SymbolicLinkRejectionRegistered);
        Assert.True(snapshot.SourceDigestValidationRegistered);
        Assert.True(snapshot.PrivateStagingRegistered);
        Assert.True(snapshot.ManifestWriteRegistered);
        Assert.True(snapshot.DurableFlushRegistered);
        Assert.True(snapshot.ImmutableFreezeRegistered);
        Assert.True(snapshot.AtomicDirectoryPublishRegistered);
        Assert.True(snapshot.PublishedTreeValidationRegistered);
        Assert.True(snapshot.CleanupRegistered);
        Assert.True(snapshot.ExactPlanEvidenceRegistered);
        Assert.False(snapshot.ExistingBackupOverwriteRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.ActivationExecutionRegistered);
        Assert.False(snapshot.MigrationExecutionRegistered);
        Assert.False(snapshot.ServiceControlRegistered);
        Assert.False(snapshot.HealthProbeCallerRegistered);
        Assert.False(snapshot.RollbackExecutionRegistered);
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

        Assert.False(state.ConfigurationBackupReady);
        Assert.False(state.ExactActivationPlanBound);
        Assert.Equal(0, state.SourceDirectoryCount);
        Assert.Equal(0, state.DirectoryCount);
        Assert.Equal(0, state.FileCount);
        Assert.Equal(0, state.BackupBytes);
        Assert.False(state.ManifestPresent);
        Assert.False(state.PublishedTreeImmutable);
        Assert.False(state.ReconciliationRequired);
        Assert.False(state.CurrentPointerChanged);
        Assert.False(state.ActivationAuthorized);
    }

    [Fact]
    public async Task ExactPlanBackupPublishesImmutableTreeAndEvidence()
    {
        using Fixture fixture = new();
        int statusReads = 0;
        VerifiedReleaseActivationConfigurationBackupService service =
            fixture.CreateService(_ =>
            {
                statusReads++;
                return Task.FromResult(fixture.Status);
            });

        VerifiedReleaseActivationConfigurationBackupReport report =
            await service.ExecuteAsync(fixture.BackupPlanReport);

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationConfigurationBackupFailureCode.None,
            report.FailureCode);
        Assert.Equal(3, statusReads);
        Assert.Equal(3, report.SourceDirectoryCount);
        Assert.Equal(5, report.DirectoryCount);
        Assert.Equal(3, report.FileCount);
        Assert.True(report.BackupBytes > fixture.SourceBytes);
        Assert.True(report.SourceSnapshotStable);
        Assert.True(report.ManifestWritten);
        Assert.True(report.StagingTreeImmutable);
        Assert.True(report.AtomicPublicationCompleted);
        Assert.True(report.PublishedTreeValidated);
        Assert.False(report.ExistingBackupOverwritten);
        Assert.True(report.ConfigurationBackupReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
        Assert.False(report.ReconciliationRequired);

        VerifiedReleaseActivationConfigurationBackupPlan plan =
            fixture.BackupPlan;
        Assert.False(Directory.Exists(plan.StagingPath));
        Assert.True(Directory.Exists(plan.PublishedPath));
        Assert.True(File.Exists(plan.ManifestPath));
        Assert.Equal(
            "configuration-value",
            File.ReadAllText(
                Path.Combine(
                    plan.PublishedPath,
                    "configuration",
                    "aethersdr.json")));
        Assert.Equal(
            "state-value",
            File.ReadAllText(
                Path.Combine(
                    plan.PublishedPath,
                    "state",
                    "setup",
                    "installation.json")));
        Assert.Equal(
            "secret-value",
            File.ReadAllText(
                Path.Combine(
                    plan.PublishedPath,
                    "secrets",
                    "data-protection",
                    "key.xml")));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserExecute,
            File.GetUnixFileMode(plan.PublishedPath));
        Assert.Equal(
            UnixFileMode.UserRead,
            File.GetUnixFileMode(plan.ManifestPath));

        string manifestJson = File.ReadAllText(plan.ManifestPath);
        Assert.Contains("\"schemaVersion\": 1", manifestJson);
        Assert.Contains("\"sourceDirectoryCount\": 3", manifestJson);
        Assert.Contains("\"fileCount\": 3", manifestJson);
        Assert.DoesNotContain(fixture.Root, manifestJson, StringComparison.Ordinal);

        VerifiedReleaseActivationConfigurationBackupStateDiagnostics state =
            service.State;
        Assert.True(state.ConfigurationBackupReady);
        Assert.True(state.ExactActivationPlanBound);
        Assert.Equal(3, state.SourceDirectoryCount);
        Assert.Equal(5, state.DirectoryCount);
        Assert.Equal(3, state.FileCount);
        Assert.True(state.BackupBytes > fixture.SourceBytes);
        Assert.True(state.ManifestPresent);
        Assert.True(state.PublishedTreeImmutable);
        Assert.False(state.ReconciliationRequired);

        VerifiedReleaseActivationConfigurationBackupObservation observation =
            service.Observe(fixture.PlanResult.Plan!);
        Assert.True(observation.ConfigurationBackupReady);
        Assert.Equal(3, observation.SourceDirectoryCount);
        Assert.Equal(5, observation.DirectoryCount);
        Assert.Equal(3, observation.FileCount);
        Assert.NotNull(observation.CompletedAt);
        Assert.False(observation.ReconciliationRequired);
    }

    [Fact]
    public async Task ExistingPublishedBackupIsNeverOverwritten()
    {
        using Fixture fixture = new();
        fixture.CreatePublishedSentinel();
        VerifiedReleaseActivationConfigurationBackupService service =
            fixture.CreateService();

        VerifiedReleaseActivationConfigurationBackupReport report =
            await service.ExecuteAsync(fixture.BackupPlanReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationConfigurationBackupFailureCode
                .BackupAlreadyPresent,
            report.FailureCode);
        Assert.False(report.ExistingBackupOverwritten);
        Assert.Equal(
            "sentinel",
            File.ReadAllText(
                Path.Combine(fixture.BackupPlan.PublishedPath, "sentinel.txt")));
        Assert.False(Directory.Exists(fixture.BackupPlan.StagingPath));
        Assert.False(service.State.ConfigurationBackupReady);
    }

    [Fact]
    public async Task ExistingStagingTreeIsNotReusedOrDeleted()
    {
        using Fixture fixture = new();
        fixture.CreateStagingSentinel();
        VerifiedReleaseActivationConfigurationBackupService service =
            fixture.CreateService();

        VerifiedReleaseActivationConfigurationBackupReport report =
            await service.ExecuteAsync(fixture.BackupPlanReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationConfigurationBackupFailureCode
                .StagingAlreadyPresent,
            report.FailureCode);
        Assert.Equal(
            "sentinel",
            File.ReadAllText(
                Path.Combine(fixture.BackupPlan.StagingPath, "sentinel.txt")));
        Assert.False(Directory.Exists(fixture.BackupPlan.PublishedPath));
    }

    [Fact]
    public async Task LinkedSourceFailsClosedBeforeBackupWrite()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.Root, "outside.txt");
        File.WriteAllText(outside, "outside");
        File.SetUnixFileMode(
            outside,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(
            Path.Combine(fixture.Paths.ConfigurationDirectory, "linked.json"),
            outside);
        VerifiedReleaseActivationConfigurationBackupService service =
            fixture.CreateService();

        VerifiedReleaseActivationConfigurationBackupReport report =
            await service.ExecuteAsync(fixture.BackupPlanReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationConfigurationBackupFailureCode
                .UnsafeSourceLayout,
            report.FailureCode);
        Assert.False(report.ManifestWritten);
        Assert.False(Directory.Exists(fixture.BackupPlan.StagingPath));
        Assert.False(Directory.Exists(fixture.BackupPlan.PublishedPath));
        Assert.Equal("outside", File.ReadAllText(outside));
    }

    [Fact]
    public async Task SharedSecretPermissionsFailClosed()
    {
        using Fixture fixture = new();
        File.SetUnixFileMode(
            fixture.SecretFile,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead);
        VerifiedReleaseActivationConfigurationBackupService service =
            fixture.CreateService();

        VerifiedReleaseActivationConfigurationBackupReport report =
            await service.ExecuteAsync(fixture.BackupPlanReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationConfigurationBackupFailureCode
                .UnsafeSourceLayout,
            report.FailureCode);
        Assert.False(Directory.Exists(fixture.BackupPlan.PublishedPath));
    }

    [Fact]
    public async Task StatusDriftRemovesPrivateStagingAndPublishesNothing()
    {
        using Fixture fixture = new();
        int reads = 0;
        VerifiedReleaseActivationConfigurationBackupService service =
            fixture.CreateService(_ =>
            {
                reads++;
                return Task.FromResult(
                    reads == 1
                        ? fixture.Status
                        : fixture.Status with
                        {
                            ActiveReleaseIdentity = "aethersdr-8.2.0"
                        });
            });

        VerifiedReleaseActivationConfigurationBackupReport report =
            await service.ExecuteAsync(fixture.BackupPlanReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationConfigurationBackupFailureCode.StatusMismatch,
            report.FailureCode);
        Assert.True(report.SourceSnapshotStable);
        Assert.True(report.ManifestWritten);
        Assert.True(report.StagingTreeImmutable);
        Assert.False(report.AtomicPublicationCompleted);
        Assert.False(Directory.Exists(fixture.BackupPlan.StagingPath));
        Assert.False(Directory.Exists(fixture.BackupPlan.PublishedPath));
        Assert.False(service.State.ReconciliationRequired);
    }

    [Fact]
    public async Task AmbiguousAtomicMoveRequiresReconciliation()
    {
        using Fixture fixture = new();
        VerifiedReleaseActivationConfigurationBackupService service =
            fixture.CreateService(
                directoryMove: (source, target) =>
                {
                    Directory.Move(source, target);
                    throw new IOException("ambiguous rename result");
                });

        VerifiedReleaseActivationConfigurationBackupReport report =
            await service.ExecuteAsync(fixture.BackupPlanReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationConfigurationBackupFailureCode
                .PublishedStateRequiresReconciliation,
            report.FailureCode);
        Assert.True(report.ReconciliationRequired);
        Assert.True(Directory.Exists(fixture.BackupPlan.PublishedPath));
        Assert.False(Directory.Exists(fixture.BackupPlan.StagingPath));
        Assert.True(service.State.ReconciliationRequired);
        Assert.False(service.State.ConfigurationBackupReady);
        Assert.False(
            service.Observe(fixture.PlanResult.Plan!)
                .ConfigurationBackupReady);
    }

    [Fact]
    public async Task EquivalentButDistinctActivationPlanCannotReuseEvidence()
    {
        using Fixture fixture = new();
        VerifiedReleaseActivationConfigurationBackupService service =
            fixture.CreateService();
        Assert.True(
            (await service.ExecuteAsync(fixture.BackupPlanReport)).Succeeded);
        VerifiedReleaseActivationPlanCompositionResult distinct =
            fixture.CreatePlanResult();

        VerifiedReleaseActivationConfigurationBackupObservation exact =
            service.Observe(fixture.PlanResult.Plan!);
        VerifiedReleaseActivationConfigurationBackupObservation other =
            service.Observe(distinct.Plan!);

        Assert.True(exact.ConfigurationBackupReady);
        Assert.False(other.ConfigurationBackupReady);
        Assert.Equal(0, other.SourceDirectoryCount);
        Assert.Equal(0, other.DirectoryCount);
        Assert.Equal(0, other.FileCount);
        Assert.Null(other.CompletedAt);
    }

    [Fact]
    public async Task CollectorConsumesOnlyExactPlanBackupEvidence()
    {
        using Fixture fixture = new();
        TxLeaseManager leases = new(fixture.Time);
        VerifiedReleaseActivationLeaseQuiescenceBoundary quiescence = new(leases);
        VerifiedReleaseActivationLeaseQuiescenceReport closed =
            quiescence.CloseAdmission(
                quiescence.Compose(fixture.PlanResult));
        Assert.True(closed.DrainSatisfied);
        VerifiedReleaseActivationConfigurationBackupService backup =
            fixture.CreateService();
        Assert.True(
            (await backup.ExecuteAsync(fixture.BackupPlanReport)).Succeeded);
        VerifiedReleaseActivationEvidenceCollector collector = new(
            _ => Task.FromResult(fixture.Status),
            quiescence.Observe,
            () => [],
            ReadyWatchdogs,
            fixture.Time,
            configurationBackupReader: backup.Observe);

        VerifiedReleaseActivationEvidenceCollectionReport collection =
            await collector.CollectAsync(fixture.PlanResult);

        Assert.True(collection.Succeeded);
        Assert.True(collection.ConfigurationBackupReady);
        VerifiedReleaseActivationReadinessReport readiness =
            new VerifiedReleaseActivationReadinessEvaluator(fixture.Time)
                .Evaluate(fixture.PlanResult, collection.Collection!.Evidence);
        Assert.Equal(
            VerifiedReleaseActivationReadinessFailureCode
                .HealthVerificationNotReady,
            readiness.FailureCode);

        VerifiedReleaseActivationPlanCompositionResult distinct =
            fixture.CreatePlanResult();
        VerifiedReleaseActivationEvidenceCollectionReport distinctCollection =
            await collector.CollectAsync(distinct);
        Assert.True(distinctCollection.Succeeded);
        Assert.False(distinctCollection.ConfigurationBackupReady);
    }

    [Fact]
    public async Task PublicReportAndStateRemainPathAndContentRedacted()
    {
        using Fixture fixture = new();
        VerifiedReleaseActivationConfigurationBackupService service =
            fixture.CreateService();
        VerifiedReleaseActivationConfigurationBackupReport report =
            await service.ExecuteAsync(fixture.BackupPlanReport);
        Assert.True(report.Succeeded);

        string reportJson = JsonSerializer.Serialize(report);
        string stateJson = JsonSerializer.Serialize(service.State);

        foreach (string secret in new[]
                 {
                     fixture.Root,
                     fixture.Paths.ConfigurationDirectory,
                     fixture.Paths.StateDirectory,
                     fixture.Paths.SecretDirectory,
                     fixture.Paths.BackupDirectory,
                     "aethersdr.json",
                     "installation.json",
                     "key.xml",
                     "configuration-value",
                     "state-value",
                     "secret-value"
                 })
        {
            Assert.DoesNotContain(secret, reportJson, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, stateJson, StringComparison.Ordinal);
        }
        Assert.Contains(
            "configurationBackupReady",
            reportJson,
            StringComparison.OrdinalIgnoreCase);
    }

    private static StationTxIndependentWatchdogAggregate ReadyWatchdogs() =>
        new(
            SupervisionRegistered: true,
            SessionCount: 0,
            RunningProcessCount: 0,
            ConnectedProcessCount: 0,
            RegisteredIdentityCount: 0,
            RestartCount: 0,
            CommandTransportAvailable: false,
            ArmingAvailable: false,
            State: "supervised-empty-disarmed",
            ArmedProcessCount: 0,
            ReconciliationRequiredCount: 0,
            UnkeyAttemptCount: 0);

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Time = new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero));
            Root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"activation-backup-execution-{Guid.NewGuid():N}"));
            DeploymentRoot = Path.Combine(Root, "deployment");
            Paths = new InstallationPaths(
                Path.Combine(Root, "configuration"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(DeploymentRoot, "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            CreateSourceLayout();
            PlanResult = CreatePlanResult();
            BackupPlanReport =
                new VerifiedReleaseActivationConfigurationBackupPlanner(Paths)
                    .Compose(PlanResult);
            Assert.True(BackupPlanReport.Succeeded);
            BackupPlan =
                Assert.IsType<VerifiedReleaseActivationConfigurationBackupPlan>(
                    BackupPlanReport.Plan);
            Status = new ReleaseStatusReadResult(
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
        }

        internal ManualTimeProvider Time { get; }
        internal string Root { get; }
        internal string DeploymentRoot { get; }
        internal InstallationPaths Paths { get; }
        internal VerifiedReleaseActivationPlanCompositionResult PlanResult { get; }
        internal VerifiedReleaseActivationConfigurationBackupPlanReport
            BackupPlanReport
        { get; }
        internal VerifiedReleaseActivationConfigurationBackupPlan BackupPlan { get; }
        internal ReleaseStatusReadResult Status { get; }
        internal string SecretFile =>
            Path.Combine(Paths.SecretDirectory, "data-protection", "key.xml");
        internal long SourceBytes =>
            "configuration-value".Length +
            "state-value".Length +
            "secret-value".Length;

        internal VerifiedReleaseActivationConfigurationBackupService CreateService(
            Func<CancellationToken, Task<ReleaseStatusReadResult>>? statusReader = null,
            Action<string, string>? directoryMove = null) =>
            new(
                statusReader ?? (_ => Task.FromResult(Status)),
                directoryMove,
                Time);

        internal void CreatePublishedSentinel()
        {
            string parent = Path.GetDirectoryName(BackupPlan.PublishedPath)!;
            Directory.CreateDirectory(parent);
            SetPrivateDirectories(Paths.BackupDirectory, parent);
            Directory.CreateDirectory(BackupPlan.PublishedPath);
            File.SetUnixFileMode(
                BackupPlan.PublishedPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            string sentinel = Path.Combine(
                BackupPlan.PublishedPath,
                "sentinel.txt");
            File.WriteAllText(sentinel, "sentinel");
            File.SetUnixFileMode(
                sentinel,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        internal void CreateStagingSentinel()
        {
            string parent = Path.GetDirectoryName(BackupPlan.StagingPath)!;
            Directory.CreateDirectory(parent);
            SetPrivateDirectories(Paths.BackupDirectory, parent);
            Directory.CreateDirectory(BackupPlan.StagingPath);
            File.SetUnixFileMode(
                BackupPlan.StagingPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            string sentinel = Path.Combine(
                BackupPlan.StagingPath,
                "sentinel.txt");
            File.WriteAllText(sentinel, "sentinel");
            File.SetUnixFileMode(
                sentinel,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        internal VerifiedReleaseActivationPlanCompositionResult CreatePlanResult()
        {
            string targetPath = Path.Combine(
                Paths.ReleaseDirectory,
                "aethersdr-8.2.0");
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
                Paths.ReleaseDirectory,
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
                releaseNotesSummary: "Configuration backup execution test release.");
            long bytes = 37 + packages.Sum(package => package.Length);
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(installPlan, targetPath, bytes));
            VerifiedReleaseActivationPlanCompositionResult result =
                new VerifiedReleaseActivationPlanComposer().Compose(publication);
            Assert.True(result.Succeeded);
            return result;
        }

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
            Directory.CreateDirectory(
                Path.Combine(Paths.StateDirectory, "setup"));
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
                         Paths.BackupDirectory
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
                Path.Combine(
                    Paths.StateDirectory,
                    "setup",
                    "installation.json"),
                "state-value");
            WritePrivateFile(SecretFile, "secret-value");
        }

        private static void SetPrivateDirectories(string root, string descendant)
        {
            string current = descendant;
            while (current.StartsWith(root, StringComparison.Ordinal))
            {
                File.SetUnixFileMode(
                    current,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
                if (string.Equals(current, root, StringComparison.Ordinal))
                {
                    break;
                }
                current = Path.GetDirectoryName(current)!;
            }
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
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
