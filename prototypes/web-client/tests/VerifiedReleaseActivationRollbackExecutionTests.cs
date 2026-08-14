using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleaseActivationRollbackExecutionTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsAndStateOnly()
    {
        MethodInfo[] methods = typeof(VerifiedReleaseActivationRollbackExecutionService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(methods, method =>
            method.ReturnType ==
                typeof(Task<VerifiedReleaseActivationRollbackExecutionReport>) ||
            method.Name.Contains("Execute", StringComparison.Ordinal));
        Assert.Contains(methods, method => method.Name == "get_Snapshot");
        Assert.Contains(methods, method => method.Name == "get_State");
    }

    [Fact]
    public void DisabledDefaultsRemainUnavailableAndZeroState()
    {
        VerifiedReleaseActivationRollbackExecutionService service = new(
            _ => throw new InvalidOperationException("status must not be read"),
            _ => throw new InvalidOperationException("setup must not be read"),
            () => throw new InvalidOperationException("stations must not be read"),
            new FakeServiceRuntime(),
            new FakeHealthRuntime(),
            new LinuxVerifiedReleaseActivationCurrentPointerRuntime(),
            Directory.Move,
            new ReleaseActivationRollbackSettings(),
            new StaticTimeProvider());

        VerifiedReleaseActivationRollbackExecutionDiagnostics snapshot =
            service.Snapshot;
        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ConfigurationRegistered);
        Assert.False(snapshot.ExecutionEnabled);
        Assert.False(snapshot.ExecutionAvailable);
        Assert.True(snapshot.ExactRollbackPlanInputRegistered);
        Assert.True(snapshot.ExactCurrentPointerSwitchEvidenceInputRegistered);
        Assert.True(snapshot.ImmutableOriginalBackupRevalidationRegistered);
        Assert.True(snapshot.OriginalUnixModeRestoreRegistered);
        Assert.False(snapshot.ReverseMigrationRunnerRegistered);
        Assert.False(snapshot.AutomaticRetryRegistered);
        Assert.False(snapshot.HostRestartRegistered);
        Assert.False(snapshot.RemoteServiceControlRegistered);
        AssertNoCallers(snapshot);

        VerifiedReleaseActivationRollbackExecutionStateDiagnostics state =
            service.State;
        Assert.False(state.RollbackReady);
        Assert.False(state.ExactRollbackPlanBound);
        Assert.Equal(0, state.RestoreSourceCount);
        Assert.Equal(0, state.RestoreDirectoryCount);
        Assert.Equal(0, state.RestoreFileCount);
        Assert.Equal(0, state.RestoreBytes);
        Assert.False(state.CurrentPointerRolledBack);
        Assert.False(state.RollbackPerformed);
        Assert.False(state.ReconciliationRequired);
        Assert.False(state.ActivationAuthorized);
    }

    [Fact]
    public void DisabledConfigurationRejectsStationIdentity()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new VerifiedReleaseActivationRollbackExecutionService(
                _ => throw new InvalidOperationException(),
                _ => throw new InvalidOperationException(),
                () => throw new InvalidOperationException(),
                new FakeServiceRuntime(),
                new FakeHealthRuntime(),
                new LinuxVerifiedReleaseActivationCurrentPointerRuntime(),
                Directory.Move,
                new ReleaseActivationRollbackSettings
                {
                    ExpectedStationId = "station-1"
                },
                new StaticTimeProvider()));
    }

    [Fact]
    public async Task HybridRollbackNoOpsStationOwnedAgentAndRequiresFreshBrokerLink()
    {
        using Fixture fixture = await Fixture.CreateAsync(
            topology: InstallationTopologyKind.HybridGateway);

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.ExecuteHealthFailureAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(3, report.ExecutedStopActionCount);
        Assert.Equal(1, report.TopologyNoOpStopActionCount);
        Assert.Equal(3, report.ExecutedStartActionCount);
        Assert.Equal(1, report.TopologyNoOpStartActionCount);
        Assert.Equal(4, report.VerifiedHealthTargetCount);
        Assert.Equal(3, report.UnitActivityCheckCount);
        Assert.Equal(3, report.LoopbackHttpCheckCount);
        Assert.Equal(1, report.FreshBrokerLinkCheckCount);
        Assert.True(report.RollbackPerformed);
        Assert.False(report.ReconciliationRequired);
        Assert.Equal(6, fixture.ServiceRuntime.Actions.Count);
        Assert.Equal(3, fixture.HealthRuntime.UnitChecks.Count);
        Assert.Equal(3, fixture.HealthRuntime.HttpChecks.Count);
    }

    [Fact]
    public async Task ExactHealthFailureRestoresOriginalStateAndModes()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.ExecuteHealthFailureAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode.None,
            report.FailureCode);
        Assert.True(report.ExactRollbackPlanBound);
        Assert.True(report.ExactPointerSwitchEvidenceBound);
        Assert.True(report.ExactFailureTriggerBound);
        Assert.True(report.ImmutableOriginalBackupRevalidated);
        Assert.Equal(3, report.RestoreSourceCount);
        Assert.Equal(5, report.RestoreDirectoryCount);
        Assert.Equal(3, report.RestoreFileCount);
        Assert.True(report.RestoreBytes > 0);
        Assert.Equal(4, report.PlannedStopActionCount);
        Assert.Equal(3, report.ExecutedStopActionCount);
        Assert.Equal(1, report.TopologyNoOpStopActionCount);
        Assert.Equal(3, report.RestoredLiveRootCount);
        Assert.True(report.AtomicCurrentPointerRollbackCompleted);
        Assert.Equal(4, report.PlannedStartActionCount);
        Assert.Equal(3, report.ExecutedStartActionCount);
        Assert.Equal(1, report.TopologyNoOpStartActionCount);
        Assert.Equal(4, report.HealthTargetCount);
        Assert.Equal(4, report.VerifiedHealthTargetCount);
        Assert.Equal(3, report.UnitActivityCheckCount);
        Assert.Equal(3, report.LoopbackHttpCheckCount);
        Assert.Equal(0, report.FreshBrokerLinkCheckCount);
        Assert.Equal(3, report.DisplacedTreeCleanupCount);
        Assert.False(report.ReverseMigrationRunnerUsed);
        Assert.True(report.ProcessInvocationPerformed);
        Assert.True(report.SystemdCommandPerformed);
        Assert.False(report.ShellUsed);
        Assert.True(report.NetworkRequestPerformed);
        Assert.True(report.CurrentPointerChanged);
        Assert.True(report.ConfigurationRestored);
        Assert.True(report.ServicesRestored);
        Assert.True(report.InstalledHealthVerified);
        Assert.True(report.RollbackPerformed);
        Assert.True(report.RollbackReady);
        Assert.False(report.ReconciliationRequired);
        Assert.False(report.ActivationAuthorized);

        Assert.Equal("configuration-value", File.ReadAllText(fixture.ConfigurationFile));
        Assert.Equal("state-value", File.ReadAllText(fixture.StateFile));
        Assert.Equal("secret-value", File.ReadAllText(fixture.SecretFile));
        Assert.Equal(fixture.ConfigurationFileMode,
            File.GetUnixFileMode(fixture.ConfigurationFile));
        Assert.Equal(fixture.StateFileMode, File.GetUnixFileMode(fixture.StateFile));
        Assert.Equal(fixture.SecretFileMode, File.GetUnixFileMode(fixture.SecretFile));
        Assert.Equal(fixture.ConfigurationDirectoryMode,
            File.GetUnixFileMode(fixture.Paths.ConfigurationDirectory));
        Assert.Equal(fixture.StateDirectoryMode,
            File.GetUnixFileMode(fixture.Paths.StateDirectory));
        Assert.Equal(fixture.SecretDirectoryMode,
            File.GetUnixFileMode(fixture.Paths.SecretDirectory));
        Assert.Equal(
            fixture.Plan.ActivationPlan.InstalledCurrentLinkTarget,
            new DirectoryInfo(fixture.Plan.ActivationPlan.CurrentPointerPath)
                .LinkTarget);
        Assert.All(fixture.Plan.RestoreSources, source =>
        {
            Assert.False(Directory.Exists(source.RestoreStagingPath));
            Assert.False(Directory.Exists(source.DisplacedLivePath));
        });
        Assert.Equal(6, fixture.ServiceRuntime.Actions.Count);
        Assert.Equal(3, fixture.HealthRuntime.UnitChecks.Count);
        Assert.Equal(3, fixture.HealthRuntime.HttpChecks.Count);

        VerifiedReleaseActivationRollbackExecutionStateDiagnostics state =
            fixture.Service.State;
        Assert.True(state.RollbackReady);
        Assert.True(state.ExactRollbackPlanBound);
        Assert.True(state.CurrentPointerRolledBack);
        Assert.True(state.ConfigurationRestored);
        Assert.True(state.ServicesRestored);
        Assert.True(state.InstalledHealthVerified);
        Assert.True(state.RollbackPerformed);
        Assert.False(state.ReconciliationRequired);

        VerifiedReleaseActivationRollbackObservation observation =
            fixture.Service.Observe(fixture.Plan.ActivationPlan);
        Assert.True(observation.RollbackReady);
        Assert.True(observation.RollbackPerformed);
        Assert.NotNull(observation.CompletedAt);
        Assert.False(observation.ReconciliationRequired);
    }

    [Fact]
    public async Task ExactPointerEvidenceIsRequiredBeforeAnyReadOrMutation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        VerifiedReleaseActivationCurrentPointerSwitchReport invalid =
            VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                    .PreSwitchServiceControlUnavailable,
                "missing",
                new ReleaseActivationCurrentPointerSwitchSettings
                {
                    ExecutionEnabled = true
                },
                fixture.ServiceControlReport);

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.Service.ExecuteAfterHealthFailureAsync(
                fixture.RollbackReport,
                invalid,
                fixture.HealthFailureReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode
                .CurrentPointerSwitchUnavailable,
            report.FailureCode);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Empty(fixture.ServiceRuntime.Actions);
        Assert.Equal(fixture.Plan.ActivationPlan.TargetCurrentLinkTarget,
            fixture.ReadCurrentLink());
        Assert.Equal("target-configuration", File.ReadAllText(fixture.ConfigurationFile));
    }

    [Fact]
    public async Task SuccessfulHealthReportCannotTriggerRollback()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        VerifiedReleaseActivationHealthVerificationEvidence evidence = new(
            fixture.Plan.HealthPlan,
            fixture.Time.GetUtcNow(),
            fixture.Time.GetUtcNow(),
            verifiedTargetCount: 4,
            unitActivityCheckCount: 3,
            loopbackHttpCheckCount: 3,
            freshBrokerLinkCheckCount: 0);
        VerifiedReleaseActivationHealthVerificationReport success =
            VerifiedReleaseActivationHealthVerificationReport.Success(
                new ReleaseActivationHealthVerificationSettings
                {
                    ExecutionEnabled = true
                },
                fixture.HealthPlanReport,
                evidence,
                new HealthProbeTally());

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.Service.ExecuteAfterHealthFailureAsync(
                fixture.RollbackReport,
                fixture.PointerReport,
                success);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode
                .FailureTriggerInvalid,
            report.FailureCode);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Empty(fixture.ServiceRuntime.Actions);
        Assert.Equal(fixture.Plan.ActivationPlan.TargetCurrentLinkTarget,
            fixture.ReadCurrentLink());
    }

    [Fact]
    public async Task EquivalentFailedHealthPlanCannotTriggerRollback()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        using Fixture equivalent = await Fixture.CreateAsync();

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.Service.ExecuteAfterHealthFailureAsync(
                fixture.RollbackReport,
                fixture.PointerReport,
                equivalent.HealthFailureReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode
                .FailureTriggerInvalid,
            report.FailureCode);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Empty(fixture.ServiceRuntime.Actions);
        Assert.Equal(fixture.Plan.ActivationPlan.TargetCurrentLinkTarget,
            fixture.ReadCurrentLink());
    }

    [Fact]
    public async Task ImmutableBackupDriftFailsBeforeServiceOrPointerMutation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string backupFile = Path.Combine(
            fixture.Plan.ConfigurationBackup.Plan.PublishedPath,
            "configuration",
            "aethersdr.json");
        File.SetUnixFileMode(
            backupFile,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(backupFile, "configuration-drift");
        File.SetUnixFileMode(backupFile, UnixFileMode.UserRead);

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.ExecuteHealthFailureAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode
                .ImmutableBackupInvalid,
            report.FailureCode);
        Assert.Empty(fixture.ServiceRuntime.Actions);
        Assert.Equal(fixture.Plan.ActivationPlan.TargetCurrentLinkTarget,
            fixture.ReadCurrentLink());
        Assert.Equal("target-configuration", File.ReadAllText(fixture.ConfigurationFile));
        Assert.False(fixture.Service.State.ReconciliationRequired);
    }

    [Fact]
    public async Task ExistingRollbackIdentitiesArePreservedAndRequireReconciliation()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        VerifiedReleaseActivationRollbackRestoreSource source =
            fixture.Plan.RestoreSources[0];
        Directory.CreateDirectory(source.RestoreStagingPath);
        Directory.CreateDirectory(source.DisplacedLivePath);
        string stagingSentinel = Path.Combine(
            source.RestoreStagingPath,
            "staging-sentinel.txt");
        string displacedSentinel = Path.Combine(
            source.DisplacedLivePath,
            "displaced-sentinel.txt");
        File.WriteAllText(stagingSentinel, "preserve staging");
        File.WriteAllText(displacedSentinel, "preserve displaced");

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.ExecuteHealthFailureAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode
                .ReconciliationRequired,
            report.FailureCode);
        Assert.True(report.ReconciliationRequired);
        Assert.Equal("preserve staging", File.ReadAllText(stagingSentinel));
        Assert.Equal("preserve displaced", File.ReadAllText(displacedSentinel));
        Assert.Empty(fixture.ServiceRuntime.Actions);
        Assert.Equal(fixture.Plan.ActivationPlan.TargetCurrentLinkTarget,
            fixture.ReadCurrentLink());
        Assert.True(fixture.Service.State.ReconciliationRequired);
    }

    [Fact]
    public async Task FailedPostStagingCleanupRequiresReconciliation()
    {
        using Fixture fixture = await Fixture.CreateAsync(
            activeIdentityFactory: read =>
                read == 2 ? "aethersdr-8.1.0" : "aethersdr-8.2.0",
            treeDelete: _ => false);

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.ExecuteHealthFailureAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode
                .ReconciliationRequired,
            report.FailureCode);
        Assert.True(report.ReconciliationRequired);
        Assert.All(fixture.Plan.RestoreSources, source =>
            Assert.True(Directory.Exists(source.RestoreStagingPath)));
        Assert.Empty(fixture.ServiceRuntime.Actions);
        Assert.Equal(fixture.Plan.ActivationPlan.TargetCurrentLinkTarget,
            fixture.ReadCurrentLink());
        Assert.True(fixture.Service.State.ReconciliationRequired);
    }

    [Fact]
    public async Task AmbiguousFirstDirectoryMoveRequiresReconciliation()
    {
        using Fixture fixture = await Fixture.CreateAsync(
            directoryMove: (source, destination) =>
            {
                Directory.Move(source, destination);
                throw new IOException("ambiguous move");
            });

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.ExecuteHealthFailureAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode
                .LiveRootRestoreFailed,
            report.FailureCode);
        Assert.True(report.ReconciliationRequired);
        Assert.True(fixture.Service.State.ReconciliationRequired);
        Assert.Equal(fixture.Plan.ActivationPlan.TargetCurrentLinkTarget,
            fixture.ReadCurrentLink());
        VerifiedReleaseActivationRollbackObservation observation =
            fixture.Service.Observe(fixture.Plan.ActivationPlan);
        Assert.False(observation.RollbackReady);
        Assert.True(observation.ReconciliationRequired);
    }

    [Fact]
    public async Task AmbiguousPointerReplacementRequiresReconciliation()
    {
        using Fixture fixture = await Fixture.CreateAsync(
            pointerRuntimeFactory: () => new AmbiguousPointerRuntime());

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.ExecuteHealthFailureAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode
                .CurrentPointerRollbackFailed,
            report.FailureCode);
        Assert.True(report.ConfigurationRestored);
        Assert.True(report.ReconciliationRequired);
        Assert.True(fixture.Service.State.ReconciliationRequired);
        Assert.Equal(fixture.Plan.ActivationPlan.InstalledCurrentLinkTarget,
            fixture.ReadCurrentLink());
        Assert.Empty(fixture.HealthRuntime.HttpChecks);
    }

    [Fact]
    public async Task InstalledHealthFailureRetainsReconciliationAndDisplacedTrees()
    {
        using Fixture fixture = await Fixture.CreateAsync(
            healthRuntimeFactory: () => new FakeHealthRuntime
            {
                HttpResult = HealthProbeAttemptResult.Reject("health failed")
            });

        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.ExecuteHealthFailureAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode
                .InstalledHealthVerificationFailed,
            report.FailureCode);
        Assert.True(report.CurrentPointerChanged);
        Assert.True(report.ConfigurationRestored);
        Assert.True(report.ServicesRestored);
        Assert.False(report.InstalledHealthVerified);
        Assert.True(report.RollbackPerformed);
        Assert.True(report.ReconciliationRequired);
        Assert.Equal(fixture.Plan.ActivationPlan.InstalledCurrentLinkTarget,
            fixture.ReadCurrentLink());
        Assert.All(fixture.Plan.RestoreSources, source =>
            Assert.True(Directory.Exists(source.DisplacedLivePath)));
    }

    [Fact]
    public async Task CompletedRollbackCannotRunTwice()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ExecuteHealthFailureAsync()).Succeeded);
        int processCount = fixture.ServiceRuntime.Actions.Count;

        VerifiedReleaseActivationRollbackExecutionReport second =
            await fixture.ExecuteHealthFailureAsync();

        Assert.False(second.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationRollbackExecutionFailureCode
                .RollbackAlreadyCompleted,
            second.FailureCode);
        Assert.Equal(processCount, fixture.ServiceRuntime.Actions.Count);
    }

    [Fact]
    public async Task PublicReportAndStateRemainPathContentAndStationRedacted()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        VerifiedReleaseActivationRollbackExecutionReport report =
            await fixture.ExecuteHealthFailureAsync();
        Assert.True(report.Succeeded);

        string publicText = JsonSerializer.Serialize(report) +
            JsonSerializer.Serialize(fixture.Service.State);
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
            Assert.DoesNotContain(secret, publicText, StringComparison.Ordinal);
        }
        Assert.Contains("rollbackReady", publicText,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoCallers(
        VerifiedReleaseActivationRollbackExecutionDiagnostics snapshot)
    {
        Assert.False(snapshot.ActivationAuthorityRegistered);
        Assert.False(snapshot.OperationalCallerRegistered);
        Assert.False(snapshot.CliCallerRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
        Assert.False(snapshot.HttpCallerRegistered);
        Assert.False(snapshot.WebSocketCallerRegistered);
        Assert.False(snapshot.HostedServiceCallerRegistered);
        Assert.False(snapshot.TimerCallerRegistered);
        Assert.False(snapshot.AetherRemoteCommandCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Action<string, string> m_directoryMove;
        private readonly Func<int, string>? m_activeIdentityFactory;
        private readonly Func<string, bool>? m_treeDelete;
        private readonly InstallationTopologyKind m_topology;
        private const string ExpectedStationId = "station-1";

        private Fixture(
            Action<string, string>? directoryMove,
            Func<IVerifiedReleaseActivationCurrentPointerRuntime>?
                pointerRuntimeFactory,
            Func<FakeHealthRuntime>? healthRuntimeFactory,
            Func<int, string>? activeIdentityFactory,
            Func<string, bool>? treeDelete,
            InstallationTopologyKind topology)
        {
            m_topology = topology;
            Time = new StaticTimeProvider();
            Root = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                $"rollback-execution-{Guid.NewGuid():N}"));
            DeploymentRoot = Path.Combine(Root, "deployment");
            Paths = new InstallationPaths(
                Path.Combine(Root, "configuration"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(DeploymentRoot, "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            ConfigurationDirectoryMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute;
            StateDirectoryMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute;
            SecretDirectoryMode = StateDirectoryMode;
            ConfigurationFileMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead;
            StateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            SecretFileMode = StateFileMode;
            CreateSourceLayout();
            Setup = CreateSetup();
            PlanResult = CreatePlanResult();
            BackupPlanReport =
                new VerifiedReleaseActivationConfigurationBackupPlanner(Paths)
                    .Compose(PlanResult);
            Assert.True(BackupPlanReport.Succeeded);
            m_directoryMove = directoryMove ?? Directory.Move;
            m_activeIdentityFactory = activeIdentityFactory;
            m_treeDelete = treeDelete;
            PointerRuntime = pointerRuntimeFactory?.Invoke() ??
                new LinuxVerifiedReleaseActivationCurrentPointerRuntime();
            ServiceRuntime = new FakeServiceRuntime();
            HealthRuntime = healthRuntimeFactory?.Invoke() ?? new FakeHealthRuntime();
        }

        internal static async Task<Fixture> CreateAsync(
            Action<string, string>? directoryMove = null,
            Func<IVerifiedReleaseActivationCurrentPointerRuntime>?
                pointerRuntimeFactory = null,
            Func<FakeHealthRuntime>? healthRuntimeFactory = null,
            Func<int, string>? activeIdentityFactory = null,
            Func<string, bool>? treeDelete = null,
            InstallationTopologyKind topology =
                InstallationTopologyKind.PersonalSingleStation)
        {
            Fixture fixture = new(
                directoryMove,
                pointerRuntimeFactory,
                healthRuntimeFactory,
                activeIdentityFactory,
                treeDelete,
                topology);
            await fixture.InitializeAsync();
            return fixture;
        }

        internal StaticTimeProvider Time { get; }
        internal string Root { get; }
        internal string DeploymentRoot { get; }
        internal InstallationPaths Paths { get; }
        internal InstallationSetupState Setup { get; }
        internal VerifiedReleaseActivationPlanCompositionResult PlanResult { get; }
        internal VerifiedReleaseActivationConfigurationBackupPlanReport
            BackupPlanReport
        { get; }
        internal VerifiedReleaseActivationConfigurationBackupReport BackupReport
        { get; private set; } = null!;
        internal VerifiedReleaseActivationMigrationPlanReport MigrationReport
        { get; private set; } = null!;
        internal VerifiedReleaseActivationServiceControlPlanReport ServiceControlReport
        { get; private set; } = null!;
        internal VerifiedReleaseActivationHealthVerificationPlanReport HealthPlanReport
        { get; private set; } = null!;
        internal VerifiedReleaseActivationRollbackPlanReport RollbackReport
        { get; private set; } = null!;
        internal VerifiedReleaseActivationRollbackPlan Plan { get; private set; } = null!;
        internal VerifiedReleaseActivationCurrentPointerSwitchReport PointerReport
        { get; private set; } = null!;
        internal VerifiedReleaseActivationHealthVerificationReport HealthFailureReport
        { get; private set; } = null!;
        internal VerifiedReleaseActivationRollbackExecutionService Service
        { get; private set; } = null!;
        internal FakeServiceRuntime ServiceRuntime { get; }
        internal FakeHealthRuntime HealthRuntime { get; }
        internal IVerifiedReleaseActivationCurrentPointerRuntime PointerRuntime { get; }
        internal int StatusReads { get; private set; }
        internal UnixFileMode ConfigurationDirectoryMode { get; }
        internal UnixFileMode StateDirectoryMode { get; }
        internal UnixFileMode SecretDirectoryMode { get; }
        internal UnixFileMode ConfigurationFileMode { get; }
        internal UnixFileMode StateFileMode { get; }
        internal UnixFileMode SecretFileMode { get; }
        internal string ConfigurationFile =>
            Path.Combine(Paths.ConfigurationDirectory, "aethersdr.json");
        internal string StateFile =>
            Path.Combine(Paths.StateDirectory, "setup", "installation.json");
        internal string SecretFile =>
            Path.Combine(Paths.SecretDirectory, "data-protection", "key.xml");

        internal string ReadCurrentLink() =>
            new DirectoryInfo(Plan.ActivationPlan.CurrentPointerPath)
                .LinkTarget ?? string.Empty;

        internal Task<VerifiedReleaseActivationRollbackExecutionReport>
            ExecuteHealthFailureAsync() =>
            Service.ExecuteAfterHealthFailureAsync(
                RollbackReport,
                PointerReport,
                HealthFailureReport);

        private async Task InitializeAsync()
        {
            VerifiedReleaseActivationConfigurationBackupService backupService = new(
                _ => Task.FromResult(CreateStatus("aethersdr-8.1.0")),
                Directory.Move,
                Time);
            BackupReport = await backupService.ExecuteAsync(BackupPlanReport);
            Assert.True(BackupReport.Succeeded);

            MigrationReport =
                new VerifiedReleaseActivationMigrationPlanComposer().Compose(
                    PlanResult,
                    BackupReport);
            Assert.True(MigrationReport.Succeeded);
            ServiceControlReport =
                new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                    PlanResult);
            Assert.True(ServiceControlReport.Succeeded);
            HealthPlanReport =
                new VerifiedReleaseActivationHealthVerificationPlanComposer().Compose(
                    ServiceControlReport);
            Assert.True(HealthPlanReport.Succeeded);
            RollbackReport =
                new VerifiedReleaseActivationRollbackPlanComposer().Compose(
                    PlanResult,
                    BackupReport,
                    MigrationReport,
                    ServiceControlReport,
                    HealthPlanReport);
            Assert.True(RollbackReport.Succeeded);
            Plan = Assert.IsType<VerifiedReleaseActivationRollbackPlan>(
                RollbackReport.Plan);

            CreateReleaseInventoryAndTargetPointer();
            MutateLiveStateForTarget();
            VerifiedReleaseActivationServiceControlPlan servicePlan =
                Assert.IsType<VerifiedReleaseActivationServiceControlPlan>(
                    ServiceControlReport.Plan);
            VerifiedReleaseActivationServiceControlPreSwitchEvidence pre = new(
                servicePlan,
                m_topology,
                executedActionCount: 3,
                topologyNoOpActionCount: 1,
                Time.GetUtcNow());
            VerifiedReleaseActivationCurrentPointerSwitchEvidence pointerEvidence =
                new(
                    servicePlan,
                    pre,
                    Time.GetUtcNow(),
                    Time.GetUtcNow());
            PointerReport = VerifiedReleaseActivationCurrentPointerSwitchReport.Success(
                new ReleaseActivationCurrentPointerSwitchSettings
                {
                    ExecutionEnabled = true
                },
                ServiceControlReport,
                pointerEvidence);
            HealthFailureReport =
                VerifiedReleaseActivationHealthVerificationReport.Failure(
                    VerifiedReleaseActivationHealthVerificationFailureCode
                        .LoopbackHealthUnavailable,
                    "target health failed",
                    new ReleaseActivationHealthVerificationSettings
                    {
                        ExecutionEnabled = true,
                        ExpectedStationId =
                            InstallationTopologyProfile.For(m_topology)
                                .AcceptsRemoteStations
                                ? ExpectedStationId
                                : string.Empty
                    },
                    HealthPlanReport,
                    new HealthProbeTally
                    {
                        UnitActivityAttemptCount = 1,
                        LoopbackHttpAttemptCount = 1
                    },
                    exactPlanBound: true,
                    targetActiveBefore: true,
                    canonicalHostBound: true,
                    verifiedTargetCount: 1);
            Service = new VerifiedReleaseActivationRollbackExecutionService(
                _ =>
                {
                    StatusReads++;
                    string activeIdentity = m_activeIdentityFactory?.Invoke(StatusReads) ??
                        ActiveIdentityFromPointer();
                    return Task.FromResult(CreateStatus(activeIdentity));
                },
                _ => Task.FromResult(Setup),
                () => InstallationTopologyProfile.For(m_topology)
                    .AcceptsRemoteStations
                    ? CreateRemoteStationSnapshot()
                    : new RemoteStationAdministrationSnapshot(
                        Enabled: false,
                        BrokerReachable: false,
                        RefreshedAt: null,
                        Error: null,
                        Stations: [],
                        Credentials: []),
                ServiceRuntime,
                HealthRuntime,
                PointerRuntime,
                m_directoryMove,
                new ReleaseActivationRollbackSettings
                {
                    ExecutionEnabled = true,
                    ExpectedStationId =
                        InstallationTopologyProfile.For(m_topology)
                            .AcceptsRemoteStations
                            ? ExpectedStationId
                            : string.Empty
                },
                Time,
                (_, _) => Task.CompletedTask,
                m_treeDelete);
        }

        private RemoteStationAdministrationSnapshot CreateRemoteStationSnapshot()
        {
            DateTimeOffset now = Time.GetUtcNow();
            return new RemoteStationAdministrationSnapshot(
                Enabled: true,
                BrokerReachable: true,
                RefreshedAt: now,
                Error: null,
                Stations:
                [
                    new RemoteStationAdministrationEntry(
                        ExpectedStationId,
                        "instance-1",
                        "online",
                        "8.1.0",
                        now.AddMinutes(-1),
                        now,
                        HeartbeatSequence: 4,
                        InventorySequence: 3,
                        ConnectionCount: 1,
                        LastDisconnectedAt: null,
                        LastDisconnectReason: null,
                        LastRecoveredAt: null,
                        LastRecoveryMilliseconds: null,
                        Capabilities: ["receive-projection-v1"],
                        Radios: [],
                        ReceiveSessions: [],
                        ReleaseIdentity: "aethersdr-8.1.0",
                        StationEngineVersion: "8.1.0")
                ],
                Credentials: []);
        }

        private InstallationSetupState CreateSetup() => new()
        {
            SchemaVersion = InstallationSetupState.CurrentSchemaVersion,
            Revision = 7,
            CreatedAt = Time.GetUtcNow().AddMinutes(-10),
            UpdatedAt = Time.GetUtcNow().AddMinutes(-1),
            LastCompletedStep = InstallationSetupStep.Administrator,
            Lock = new InstallationSetupLock
            {
                Mode = InstallationSetupLockMode.Complete,
                ClaimedAt = Time.GetUtcNow().AddMinutes(-9),
                CompletedAt = Time.GetUtcNow().AddMinutes(-1)
            },
            Topology = m_topology,
            CanonicalPublicUrl = "https://radio.example.org",
            Paths = Paths,
            UpdateChannel = InstallationUpdateChannel.Stable,
            InstallTransmitSupport = false
        };

        private VerifiedReleaseActivationPlanCompositionResult CreatePlanResult()
        {
            string targetPath = Path.Combine(Paths.ReleaseDirectory, "aethersdr-8.2.0");
            VerifiedReleaseInstallationPackagePlan[] packages = CreatePackages(targetPath);
            VerifiedReleaseInstallationPlan installation = new(
                setupRevision: Setup.Revision,
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
                restartGatewayWeb: true,
                restartBroker: true,
                restartAetherRemoteAgent: true,
                restartStationEngine: true,
                restartHost: false,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Rollback execution fixture.");
            long bytes = installation.ManifestLength + packages.Sum(package => package.Length);
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(installation, targetPath, bytes));
            VerifiedReleaseActivationPlanCompositionResult result =
                new VerifiedReleaseActivationPlanComposer().Compose(publication);
            Assert.True(result.Succeeded);
            return result;
        }

        private ReleaseStatusReadResult CreateStatus(string activeIdentity) =>
            ReleaseStatusReadResult.Success(
                Setup,
                releaseDirectoryPresent: true,
                ["aethersdr-8.1.0", "aethersdr-8.2.0"],
                currentPointerPresent: true,
                activeIdentity);

        private string ActiveIdentityFromPointer()
        {
            string link = ReadCurrentLink();
            return string.Equals(
                    link,
                    Plan.ActivationPlan.InstalledCurrentLinkTarget,
                    StringComparison.Ordinal)
                ? Plan.ActivationPlan.InstalledReleaseIdentity
                : Plan.ActivationPlan.TargetReleaseIdentity;
        }

        private void CreateReleaseInventoryAndTargetPointer()
        {
            Directory.CreateDirectory(Plan.ActivationPlan.InstalledReleasePath);
            Directory.CreateDirectory(Plan.ActivationPlan.TargetReleasePath);
            string current = Plan.ActivationPlan.CurrentPointerPath;
            Directory.CreateSymbolicLink(
                current,
                Plan.ActivationPlan.TargetCurrentLinkTarget);
        }

        private void MutateLiveStateForTarget()
        {
            MakeWritable(new DirectoryInfo(Paths.ConfigurationDirectory));
            MakeWritable(new DirectoryInfo(Paths.StateDirectory));
            MakeWritable(new DirectoryInfo(Paths.SecretDirectory));
            File.WriteAllText(ConfigurationFile, "target-configuration");
            File.WriteAllText(StateFile, "target-state");
            File.WriteAllText(SecretFile, "target-secret");
            File.SetUnixFileMode(ConfigurationFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(StateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(SecretFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private void CreateSourceLayout()
        {
            Directory.CreateDirectory(Paths.ConfigurationDirectory);
            Directory.CreateDirectory(Path.Combine(Paths.StateDirectory, "setup"));
            Directory.CreateDirectory(Path.Combine(Paths.SecretDirectory, "data-protection"));
            Directory.CreateDirectory(Paths.BackupDirectory);
            Directory.CreateDirectory(Paths.ReleaseDirectory);
            Directory.CreateDirectory(Paths.LogDirectory);
            File.SetUnixFileMode(Paths.ConfigurationDirectory, ConfigurationDirectoryMode);
            File.SetUnixFileMode(Paths.StateDirectory, StateDirectoryMode);
            File.SetUnixFileMode(Path.Combine(Paths.StateDirectory, "setup"), StateDirectoryMode);
            File.SetUnixFileMode(Paths.SecretDirectory, SecretDirectoryMode);
            File.SetUnixFileMode(Path.Combine(Paths.SecretDirectory, "data-protection"), SecretDirectoryMode);
            File.SetUnixFileMode(Paths.BackupDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            WriteFile(ConfigurationFile, "configuration-value", ConfigurationFileMode);
            WriteFile(StateFile, "state-value", StateFileMode);
            WriteFile(SecretFile, "secret-value", SecretFileMode);
        }

        private static void WriteFile(string path, string content, UnixFileMode mode)
        {
            File.WriteAllText(path, content);
            File.SetUnixFileMode(path, mode);
        }

        private static VerifiedReleaseInstallationPackagePlan[] CreatePackages(string targetPath)
        {
            (string Identity, ReleasePackageRole Role, string Relative, long Length)[] inputs =
            [
                ("gateway", ReleasePackageRole.GatewayWeb, "packages/gateway.tar", 11),
                ("broker", ReleasePackageRole.Broker, "packages/broker.tar", 12),
                ("agent", ReleasePackageRole.AetherRemoteAgent, "packages/agent.tar", 13),
                ("engine", ReleasePackageRole.StationEngine, "packages/engine.tar", 14)
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
                    Path.GetFullPath(Path.Combine(
                        targetPath,
                        input.Relative.Replace('/', Path.DirectorySeparatorChar))));
            }).ToArray();
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
            File.SetUnixFileMode(directory.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
                    File.SetUnixFileMode(entry.FullName,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
    }

    private sealed class FakeServiceRuntime :
        IVerifiedReleaseActivationServiceControlRuntime
    {
        internal List<VerifiedReleaseActivationServiceControlAction> Actions { get; } = [];
        internal ServiceControlAttemptResult Result { get; set; } =
            ServiceControlAttemptResult.Success();

        public Task<ServiceControlAttemptResult> ControlUnitAsync(
            VerifiedReleaseActivationServiceControlAction action,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Actions.Add(action);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeHealthRuntime : IVerifiedReleaseActivationHealthProbeRuntime
    {
        internal List<string> UnitChecks { get; } = [];
        internal List<VerifiedReleaseActivationHealthVerificationTarget> HttpChecks
        { get; } = [];
        internal HealthProbeAttemptResult UnitResult { get; set; } =
            HealthProbeAttemptResult.Success();
        internal HealthProbeAttemptResult HttpResult { get; set; } =
            HealthProbeAttemptResult.Success();

        public Task<HealthProbeAttemptResult> CheckUnitActiveAsync(
            string unitIdentity,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            UnitChecks.Add(unitIdentity);
            return Task.FromResult(UnitResult);
        }

        public Task<HealthProbeAttemptResult> CheckLoopbackHealthAsync(
            VerifiedReleaseActivationHealthVerificationTarget target,
            string canonicalGatewayAuthority,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            HttpChecks.Add(target);
            return Task.FromResult(HttpResult);
        }
    }

    private sealed class AmbiguousPointerRuntime :
        IVerifiedReleaseActivationCurrentPointerRuntime
    {
        private readonly LinuxVerifiedReleaseActivationCurrentPointerRuntime m_inner = new();

        public CurrentPointerRuntimeSnapshot Read(string path) => m_inner.Read(path);

        public void CreateSymbolicLink(string path, string linkTarget) =>
            m_inner.CreateSymbolicLink(path, linkTarget);

        public void ReplaceAtomically(string temporaryPath, string currentPath)
        {
            m_inner.ReplaceAtomically(temporaryPath, currentPath);
            throw new IOException("ambiguous pointer replacement");
        }

        public void DeleteTemporary(string path) => m_inner.DeleteTemporary(path);
    }

    private sealed class StaticTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset m_now =
            new(2026, 8, 5, 0, 30, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => m_now;
    }
}
