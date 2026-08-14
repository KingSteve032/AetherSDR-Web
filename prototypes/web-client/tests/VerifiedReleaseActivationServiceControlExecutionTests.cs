using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Configuration;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleaseActivationServiceControlExecutionTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsAndStateOnly()
    {
        string[] methods =
            typeof(VerifiedReleaseActivationServiceControlExecutionService)
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(["get_Snapshot", "get_State"], methods);
        Assert.DoesNotContain(
            methods,
            name => name.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Stop", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Observe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownConfigurationPropertiesFailClosed()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ReleaseActivationServiceControlSettings.SectionName}:" +
                    "ExecutonEnabled"] = "true"
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => configuration
                .GetSection(ReleaseActivationServiceControlSettings.SectionName)
                .Get<ReleaseActivationServiceControlSettings>(options =>
                    options.ErrorOnUnknownConfiguration = true));

        Assert.Contains("ExecutonEnabled", exception.Message);
    }

    [Fact]
    public void DiagnosticsExposeBoundedCallerlessExecution()
    {
        VerifiedReleaseActivationServiceControlExecutionDiagnostics snapshot =
            new Fixture().Service.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ConfigurationRegistered);
        Assert.True(snapshot.ExecutionEnabled);
        Assert.True(snapshot.ExecutionAvailable);
        Assert.True(snapshot.ExactServiceControlPlanInputRegistered);
        Assert.True(snapshot.ExactServiceControlPlanBindingRegistered);
        Assert.True(snapshot.ExactActivationPlanBindingRegistered);
        Assert.True(snapshot.ExactCurrentPointerSwitchEvidenceInputRegistered);
        Assert.True(snapshot.ReleaseStatusDoubleReadRegistered);
        Assert.True(snapshot.SetupStateDoubleReadRegistered);
        Assert.True(snapshot.TopologyBindingRegistered);
        Assert.True(snapshot.PreSwitchStopPhaseRegistered);
        Assert.True(snapshot.PostSwitchStartPhaseRegistered);
        Assert.True(snapshot.NoOpResolutionRegistered);
        Assert.True(snapshot.DeterministicOrderingRegistered);
        Assert.True(snapshot.FixedUnitMappingRegistered);
        Assert.True(snapshot.DirectProcessRegistered);
        Assert.False(snapshot.ShellRegistered);
        Assert.True(snapshot.ClearedEnvironmentRegistered);
        Assert.True(snapshot.UserUnitScopeRegistered);
        Assert.True(snapshot.SystemUnitScopeRegistered);
        Assert.True(snapshot.BoundedOutputRegistered);
        Assert.True(snapshot.HardTimeoutRegistered);
        Assert.True(snapshot.ProcessTreeTerminationRegistered);
        Assert.True(snapshot.ExactPlanEvidenceRegistered);
        Assert.True(snapshot.PartialFailureReconciliationRegistered);
        Assert.False(snapshot.AutomaticRetryRegistered);
        Assert.False(snapshot.HostRestartExecutionRegistered);
        Assert.False(snapshot.RemoteServiceControlRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.RollbackRegistered);
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
        Assert.False(snapshot.HealthProbeCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public async Task DisabledDefaultFailsBeforeAnyObservation()
    {
        Fixture fixture = new(executionEnabled: false);

        VerifiedReleaseActivationServiceControlExecutionReport report =
            await fixture.ExecutePreAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlExecutionFailureCode
                .ExecutionDisabled);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Equal(0, fixture.SetupReads);
        Assert.Empty(fixture.Runtime.Actions);
        AssertFalseState(fixture.Service.State);
    }

    [Fact]
    public async Task ExactNoOpResolvesWithoutProcessOrObservation()
    {
        Fixture fixture = new(
            restartGateway: false,
            restartBroker: false,
            restartAgent: false,
            restartEngine: false);

        VerifiedReleaseActivationServiceControlExecutionReport report =
            await fixture.ExecutePreAsync();

        Assert.True(report.Succeeded);
        Assert.True(report.PreSwitchStopComplete);
        Assert.True(report.PostSwitchStartComplete);
        Assert.True(report.ServiceControlReady);
        Assert.Equal(0, report.PlannedActionCount);
        Assert.Equal(0, report.ExecutedActionCount);
        Assert.Equal(0, report.TopologyNoOpActionCount);
        Assert.False(report.ProcessInvocationPerformed);
        Assert.False(report.SystemdCommandPerformed);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Equal(0, fixture.SetupReads);
        Assert.Empty(fixture.Runtime.Actions);

        VerifiedReleaseActivationServiceControlObservation observation =
            fixture.Service.Observe(fixture.ActivationPlan);
        Assert.True(observation.ServiceControlReady);
        Assert.False(observation.ServiceControlRequired);
        Assert.Equal(DateTimeOffset.UnixEpoch, observation.CompletedAt);
    }

    [Fact]
    public async Task PostSwitchCannotRunBeforeExactPreSwitchPhase()
    {
        Fixture fixture = new();
        fixture.ActiveIdentity = fixture.TargetIdentity;

        VerifiedReleaseActivationServiceControlExecutionReport report =
            await fixture.ExecutePostAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlExecutionFailureCode
                .PhaseOrderInvalid);
        Assert.Empty(fixture.Runtime.Actions);
    }

    [Fact]
    public async Task PersonalTopologyStopsThreeLocalUnitsAndNoOpsAbsentAgent()
    {
        Fixture fixture = new();

        VerifiedReleaseActivationServiceControlExecutionReport report =
            await fixture.ExecutePreAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(4, report.PlannedActionCount);
        Assert.Equal(3, report.ExecutedActionCount);
        Assert.Equal(1, report.TopologyNoOpActionCount);
        Assert.True(report.ProcessInvocationPerformed);
        Assert.True(report.SystemdCommandPerformed);
        Assert.True(report.PreSwitchStopComplete);
        Assert.False(report.PostSwitchStartComplete);
        Assert.False(report.ServiceControlReady);
        Assert.Equal(2, fixture.StatusReads);
        Assert.Equal(2, fixture.SetupReads);
        Assert.Collection(
            fixture.Runtime.Actions,
            action => AssertAction(
                action,
                VerifiedReleaseActivationServiceControlActionKind.Stop,
                VerifiedReleaseActivationServiceRole.GatewayWeb),
            action => AssertAction(
                action,
                VerifiedReleaseActivationServiceControlActionKind.Stop,
                VerifiedReleaseActivationServiceRole.Broker),
            action => AssertAction(
                action,
                VerifiedReleaseActivationServiceControlActionKind.Stop,
                VerifiedReleaseActivationServiceRole.StationEngine));

        VerifiedReleaseActivationServiceControlExecutionStateDiagnostics state =
            fixture.Service.State;
        Assert.True(state.ExactServiceControlPlanBound);
        Assert.True(state.PreSwitchStopComplete);
        Assert.False(state.PostSwitchStartComplete);
        Assert.False(state.ServiceControlReady);
        Assert.Equal(3, state.ExecutedStopActionCount);
        Assert.Equal(1, state.TopologyNoOpStopActionCount);
        Assert.False(state.ReconciliationRequired);
    }

    [Fact]
    public async Task ExactPointerTransitionThenStartsLocalUnitsAndProducesEvidence()
    {
        Fixture fixture = new();
        Assert.True((await fixture.ExecutePreAsync()).Succeeded);
        fixture.ActiveIdentity = fixture.TargetIdentity;

        VerifiedReleaseActivationServiceControlExecutionReport report =
            await fixture.ExecutePostAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(4, report.PlannedActionCount);
        Assert.Equal(3, report.ExecutedActionCount);
        Assert.Equal(1, report.TopologyNoOpActionCount);
        Assert.True(report.PreSwitchStopComplete);
        Assert.True(report.PostSwitchStartComplete);
        Assert.True(report.ServiceControlReady);
        Assert.True(report.TargetReleaseActiveBefore);
        Assert.True(report.TargetReleaseActiveAfter);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
        Assert.Collection(
            fixture.Runtime.Actions.Skip(3),
            action => AssertAction(
                action,
                VerifiedReleaseActivationServiceControlActionKind.Start,
                VerifiedReleaseActivationServiceRole.StationEngine),
            action => AssertAction(
                action,
                VerifiedReleaseActivationServiceControlActionKind.Start,
                VerifiedReleaseActivationServiceRole.Broker),
            action => AssertAction(
                action,
                VerifiedReleaseActivationServiceControlActionKind.Start,
                VerifiedReleaseActivationServiceRole.GatewayWeb));

        VerifiedReleaseActivationServiceControlObservation observation =
            fixture.Service.Observe(fixture.ActivationPlan);
        Assert.True(observation.ServiceControlReady);
        Assert.True(observation.ServiceControlRequired);
        Assert.Equal(4, observation.PlannedStopActionCount);
        Assert.Equal(3, observation.ExecutedStopActionCount);
        Assert.Equal(1, observation.TopologyNoOpStopActionCount);
        Assert.Equal(4, observation.PlannedStartActionCount);
        Assert.Equal(3, observation.ExecutedStartActionCount);
        Assert.Equal(1, observation.TopologyNoOpStartActionCount);
        Assert.NotNull(observation.CompletedAt);
        Assert.False(observation.ReconciliationRequired);

        VerifiedReleaseActivationPlanCompositionResult equivalent =
            fixture.ComposeEquivalentActivation();
        VerifiedReleaseActivationServiceControlObservation distinct =
            fixture.Service.Observe(equivalent.Plan!);
        Assert.False(distinct.ServiceControlReady);
    }

    [Fact]
    public async Task HostRestartRemainsOutsideExecutionBoundary()
    {
        Fixture fixture = new(restartHost: true);

        VerifiedReleaseActivationServiceControlExecutionReport report =
            await fixture.ExecutePreAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlExecutionFailureCode
                .HostRestartUnsupported);
        Assert.Empty(fixture.Runtime.Actions);
        Assert.False(report.HostRestartPerformed);
    }

    [Fact]
    public async Task HybridGatewayKeepsStationOwnedRemoteAgentAsTopologyNoOp()
    {
        Fixture fixture = new(topology: InstallationTopologyKind.HybridGateway);

        VerifiedReleaseActivationServiceControlExecutionReport report =
            await fixture.ExecutePreAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(4, report.PlannedActionCount);
        Assert.Equal(3, report.ExecutedActionCount);
        Assert.Equal(1, report.TopologyNoOpActionCount);
        Assert.False(report.ReconciliationRequired);
        Assert.Collection(
            fixture.Runtime.Actions,
            action => AssertAction(
                action,
                VerifiedReleaseActivationServiceControlActionKind.Stop,
                VerifiedReleaseActivationServiceRole.GatewayWeb),
            action => AssertAction(
                action,
                VerifiedReleaseActivationServiceControlActionKind.Stop,
                VerifiedReleaseActivationServiceRole.Broker),
            action => AssertAction(
                action,
                VerifiedReleaseActivationServiceControlActionKind.Stop,
                VerifiedReleaseActivationServiceRole.StationEngine));
    }

    [Fact]
    public async Task RemoteStationGatewayEngineActionFailsBeforeAnyProcess()
    {
        Fixture fixture = new(
            topology: InstallationTopologyKind.RemoteStationGateway,
            restartGateway: true,
            restartBroker: true,
            restartAgent: false,
            restartEngine: true);

        VerifiedReleaseActivationServiceControlExecutionReport report =
            await fixture.ExecutePreAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlExecutionFailureCode
                .RemoteServiceControlUnavailable);
        Assert.Empty(fixture.Runtime.Actions);
    }

    [Fact]
    public async Task TamperedPlanSummaryFailsClosed()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationServiceControlPlanReport tampered =
            fixture.PlanReport with { StopActionCount = 1 };

        VerifiedReleaseActivationServiceControlExecutionReport report =
            await fixture.Service.ExecutePreSwitchStopAsync(tampered);

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlExecutionFailureCode
                .ServiceControlPlanMismatch);
        Assert.Empty(fixture.Runtime.Actions);
    }

    [Fact]
    public async Task UnknownUnitOutcomeRequiresReconciliationAndIsNotRetried()
    {
        Fixture fixture = new();
        fixture.Runtime.FailAtCall = 2;

        VerifiedReleaseActivationServiceControlExecutionReport first =
            await fixture.ExecutePreAsync();
        VerifiedReleaseActivationServiceControlExecutionReport second =
            await fixture.ExecutePreAsync();

        AssertFailure(
            first,
            VerifiedReleaseActivationServiceControlExecutionFailureCode
                .UnitControlFailed);
        Assert.True(first.ReconciliationRequired);
        Assert.Equal(2, fixture.Runtime.Actions.Count);
        AssertFailure(
            second,
            VerifiedReleaseActivationServiceControlExecutionFailureCode
                .ReconciliationRequired);
        Assert.Equal(2, fixture.Runtime.Actions.Count);
        Assert.True(fixture.Service.State.ReconciliationRequired);

        VerifiedReleaseActivationServiceControlObservation observation =
            fixture.Service.Observe(fixture.ActivationPlan);
        Assert.False(observation.ServiceControlReady);
        Assert.True(observation.ReconciliationRequired);
    }

    [Fact]
    public async Task CancellationDuringFirstActionRequiresReconciliation()
    {
        Fixture fixture = new();
        fixture.Runtime.CancelAtCall = 1;

        await Assert.ThrowsAsync<OperationCanceledException>(
            fixture.ExecutePreAsync);

        Assert.Single(fixture.Runtime.Actions);
        Assert.True(fixture.Service.State.ReconciliationRequired);
        VerifiedReleaseActivationServiceControlObservation observation =
            fixture.Service.Observe(fixture.ActivationPlan);
        Assert.False(observation.ServiceControlReady);
        Assert.True(observation.ReconciliationRequired);
    }

    [Fact]
    public async Task PostActionObservationDriftRequiresReconciliation()
    {
        Fixture fixture = new();
        fixture.StatusQueue.Enqueue(fixture.CreateStatus(fixture.InstalledIdentity));
        fixture.StatusQueue.Enqueue(fixture.CreateStatus(fixture.TargetIdentity));

        VerifiedReleaseActivationServiceControlExecutionReport report =
            await fixture.ExecutePreAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlExecutionFailureCode
                .ObservationDrift);
        Assert.True(report.ReconciliationRequired);
        Assert.Equal(3, fixture.Runtime.Actions.Count);
        Assert.True(fixture.Service.State.ReconciliationRequired);
    }

    [Fact]
    public async Task CompletedPreSwitchPhaseCannotRepeat()
    {
        Fixture fixture = new();
        Assert.True((await fixture.ExecutePreAsync()).Succeeded);

        VerifiedReleaseActivationServiceControlExecutionReport repeat =
            await fixture.ExecutePreAsync();

        AssertFailure(
            repeat,
            VerifiedReleaseActivationServiceControlExecutionFailureCode
                .PhaseAlreadyCompleted);
        Assert.Equal(3, fixture.Runtime.Actions.Count);
    }

    [Fact]
    public async Task PublicReportRedactsUnitsPathsAndTopology()
    {
        Fixture fixture = new();

        string json = JsonSerializer.Serialize(await fixture.ExecutePreAsync());

        Assert.DoesNotContain(fixture.Paths.ReleaseDirectory, json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            InstallationTopologyKind.PersonalSingleStation.ToString(),
            json,
            StringComparison.Ordinal);
        foreach (string unit in new[]
                 {
                     VerifiedReleaseActivationServiceControlPlanComposer
                         .GatewayWebUnitIdentity,
                     VerifiedReleaseActivationServiceControlPlanComposer
                         .BrokerUnitIdentity,
                     VerifiedReleaseActivationServiceControlPlanComposer
                         .AetherRemoteAgentUnitIdentity,
                     VerifiedReleaseActivationServiceControlPlanComposer
                         .StationEngineUnitIdentity
                 })
        {
            Assert.DoesNotContain(unit, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DirectRuntimeUsesExactSystemUnitArgumentVector()
    {
        string script = CreateControlScript(
            "test \"$#\" -eq 2 || exit 10\n" +
            "test \"$1\" = stop || exit 11\n" +
            "test \"$2\" = aetherremote-broker.service || exit 12\n" +
            "test -z \"${HOME+x}\" || exit 13\n");
        LinuxVerifiedReleaseActivationServiceControlRuntime runtime = new(script);
        VerifiedReleaseActivationServiceControlAction action = new(
            1,
            VerifiedReleaseActivationServiceControlActionKind.Stop,
            VerifiedReleaseActivationServiceRole.Broker,
            VerifiedReleaseActivationServiceControlPlanComposer.BrokerUnitIdentity);

        ServiceControlAttemptResult result = await runtime.ControlUnitAsync(
            action,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Reason);
        Assert.True(result.ProcessStarted);
        Assert.True(result.OutcomeKnown);
    }

    [Fact]
    public async Task DirectRuntimeUsesStandaloneGatewaySystemUnitArgumentVector()
    {
        string script = CreateControlScript(
            "test \"$#\" -eq 2 || exit 10\n" +
            "test \"$1\" = start || exit 11\n" +
            "test \"$2\" = aethersdr-web.service || exit 12\n" +
            "test -z \"${HOME+x}\" || exit 13\n");
        LinuxVerifiedReleaseActivationServiceControlRuntime runtime = new(script);
        VerifiedReleaseActivationServiceControlAction action = new(
            1,
            VerifiedReleaseActivationServiceControlActionKind.Start,
            VerifiedReleaseActivationServiceRole.GatewayWeb,
            VerifiedReleaseActivationServiceControlPlanComposer
                .GatewayWebUnitIdentity);

        ServiceControlAttemptResult result = await runtime.ControlUnitAsync(
            action,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Reason);
        Assert.True(result.ProcessStarted);
        Assert.True(result.OutcomeKnown);
    }

    [Fact]
    public async Task DirectRuntimeTimeoutReturnsUnknownOutcome()
    {
        string script = CreateControlScript(
            "test \"$#\" -eq 2 || exit 10\n" +
            "test \"$1\" = stop || exit 11\n" +
            "test \"$2\" = aetherremote-broker.service || exit 12\n" +
            "/bin/sleep 2\n");
        LinuxVerifiedReleaseActivationServiceControlRuntime runtime = new(script);
        VerifiedReleaseActivationServiceControlAction action = new(
            1,
            VerifiedReleaseActivationServiceControlActionKind.Stop,
            VerifiedReleaseActivationServiceRole.Broker,
            VerifiedReleaseActivationServiceControlPlanComposer.BrokerUnitIdentity);

        ServiceControlAttemptResult result = await runtime.ControlUnitAsync(
            action,
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.ProcessStarted);
        Assert.False(result.OutcomeKnown);
        Assert.Contains("timeout", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateControlScript(string assertions)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"service-control-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, "systemctl-fixture");
        File.WriteAllText(
            script,
            "#!/bin/sh\n" +
            assertions +
            "test \"$LANG\" = C || exit 20\n" +
            "test \"$LC_ALL\" = C || exit 21\n" +
            "exit 0\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        return script;
    }

    private static void AssertAction(
        VerifiedReleaseActivationServiceControlAction action,
        VerifiedReleaseActivationServiceControlActionKind kind,
        VerifiedReleaseActivationServiceRole role)
    {
        Assert.Equal(kind, action.Kind);
        Assert.Equal(role, action.ServiceRole);
    }

    private static void AssertFailure(
        VerifiedReleaseActivationServiceControlExecutionReport report,
        VerifiedReleaseActivationServiceControlExecutionFailureCode failureCode)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.False(report.ServiceControlReady);
        Assert.False(report.HostRestartPerformed);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
    }

    private static void AssertFalseState(
        VerifiedReleaseActivationServiceControlExecutionStateDiagnostics state)
    {
        Assert.False(state.ServiceControlReady);
        Assert.False(state.ExactServiceControlPlanBound);
        Assert.False(state.ExactActivationPlanBound);
        Assert.False(state.PreSwitchStopComplete);
        Assert.False(state.PostSwitchStartComplete);
        Assert.Equal(0, state.PlannedStopActionCount);
        Assert.Equal(0, state.ExecutedStopActionCount);
        Assert.Equal(0, state.TopologyNoOpStopActionCount);
        Assert.Equal(0, state.PlannedStartActionCount);
        Assert.Equal(0, state.ExecutedStartActionCount);
        Assert.Equal(0, state.TopologyNoOpStartActionCount);
        Assert.False(state.SetupStable);
        Assert.False(state.TopologyStable);
        Assert.False(state.InstalledReleaseActiveDuringStop);
        Assert.False(state.TargetReleaseActiveDuringStart);
        Assert.False(state.ReconciliationRequired);
        Assert.False(state.HostRestartPerformed);
        Assert.False(state.CurrentPointerChanged);
        Assert.False(state.RollbackPerformed);
        Assert.False(state.ActivationAuthorized);
    }

    private sealed class Fixture
    {
        private readonly bool m_restartGateway;
        private readonly bool m_restartBroker;
        private readonly bool m_restartAgent;
        private readonly bool m_restartEngine;
        private readonly bool m_restartHost;
        private readonly ManualTimeProvider m_time =
            new(new DateTimeOffset(2026, 8, 4, 16, 50, 0, TimeSpan.Zero));

        internal Fixture(
            bool executionEnabled = true,
            InstallationTopologyKind topology =
                InstallationTopologyKind.PersonalSingleStation,
            bool restartGateway = true,
            bool restartBroker = true,
            bool restartAgent = true,
            bool restartEngine = true,
            bool restartHost = false)
        {
            m_restartGateway = restartGateway;
            m_restartBroker = restartBroker;
            m_restartAgent = restartAgent;
            m_restartEngine = restartEngine;
            m_restartHost = restartHost;
            string root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"service-control-execution-{Guid.NewGuid():N}"));
            Paths = new InstallationPaths(
                Path.Combine(root, "config"),
                Path.Combine(root, "state"),
                Path.Combine(root, "secrets"),
                Path.Combine(root, "releases"),
                Path.Combine(root, "backups"),
                Path.Combine(root, "logs"));
            Setup = new InstallationSetupState
            {
                SchemaVersion = InstallationSetupState.CurrentSchemaVersion,
                Revision = 7,
                CreatedAt = m_time.GetUtcNow().AddMinutes(-10),
                UpdatedAt = m_time.GetUtcNow().AddMinutes(-1),
                LastCompletedStep = InstallationSetupStep.Administrator,
                Lock = new InstallationSetupLock
                {
                    Mode = InstallationSetupLockMode.Complete,
                    ClaimedAt = m_time.GetUtcNow().AddMinutes(-9),
                    CompletedAt = m_time.GetUtcNow().AddMinutes(-1)
                },
                Topology = topology,
                CanonicalPublicUrl = "https://radio.example.org",
                Paths = Paths,
                UpdateChannel = InstallationUpdateChannel.Stable,
                InstallTransmitSupport = false
            };
            ActivationPlanReport = ComposeActivation();
            ActivationPlan = Assert.IsType<VerifiedReleaseActivationPlan>(
                ActivationPlanReport.Plan);
            PlanReport =
                new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                    ActivationPlanReport);
            Assert.True(PlanReport.Succeeded);
            Runtime = new FakeRuntime();
            ActiveIdentity = InstalledIdentity;
            Service = new VerifiedReleaseActivationServiceControlExecutionService(
                _ =>
                {
                    StatusReads++;
                    ReleaseStatusReadResult status = StatusQueue.Count > 0
                        ? StatusQueue.Dequeue()
                        : CreateStatus(ActiveIdentity);
                    return Task.FromResult(status);
                },
                _ =>
                {
                    SetupReads++;
                    InstallationSetupState setup = SetupQueue.Count > 0
                        ? SetupQueue.Dequeue()
                        : Setup;
                    return Task.FromResult(setup);
                },
                Runtime,
                new ReleaseActivationServiceControlSettings
                {
                    ExecutionEnabled = executionEnabled
                },
                m_time);
        }

        internal string InstalledIdentity => "aethersdr-8.1.0";
        internal string TargetIdentity => "aethersdr-8.2.0";
        internal InstallationPaths Paths { get; }
        internal InstallationSetupState Setup { get; }
        internal VerifiedReleaseActivationPlanCompositionResult ActivationPlanReport
        {
            get;
        }
        internal VerifiedReleaseActivationPlan ActivationPlan { get; }
        internal VerifiedReleaseActivationServiceControlPlanReport PlanReport
        {
            get;
        }
        internal FakeRuntime Runtime { get; }
        internal VerifiedReleaseActivationServiceControlExecutionService Service
        {
            get;
        }
        internal string ActiveIdentity { get; set; }
        internal Queue<ReleaseStatusReadResult> StatusQueue { get; } = new();
        internal Queue<InstallationSetupState> SetupQueue { get; } = new();
        internal int StatusReads { get; private set; }
        internal int SetupReads { get; private set; }

        internal Task<VerifiedReleaseActivationServiceControlExecutionReport>
            ExecutePreAsync() =>
            Service.ExecutePreSwitchStopAsync(PlanReport);

        internal Task<VerifiedReleaseActivationServiceControlExecutionReport>
            ExecutePostAsync() =>
            Service.ExecutePostSwitchStartAsync(
                PlanReport,
                CreatePointerSwitchReport());

        private VerifiedReleaseActivationCurrentPointerSwitchReport
            CreatePointerSwitchReport()
        {
            VerifiedReleaseActivationServiceControlPreSwitchEvidence? pre =
                Service.GetPreSwitchEvidence(
                    Assert.IsType<VerifiedReleaseActivationServiceControlPlan>(
                        PlanReport.Plan));
            if (pre is null)
            {
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .PreSwitchServiceControlUnavailable,
                    "The exact pre-switch token is unavailable.",
                    new ReleaseActivationCurrentPointerSwitchSettings
                    {
                        ExecutionEnabled = true
                    },
                    PlanReport);
            }
            VerifiedReleaseActivationCurrentPointerSwitchEvidence evidence =
                new(
                    pre.Plan,
                    pre,
                    pre.CompletedAt,
                    pre.CompletedAt.AddMilliseconds(1));
            return VerifiedReleaseActivationCurrentPointerSwitchReport.Success(
                new ReleaseActivationCurrentPointerSwitchSettings
                {
                    ExecutionEnabled = true
                },
                PlanReport,
                evidence);
        }

        internal VerifiedReleaseActivationPlanCompositionResult
            ComposeEquivalentActivation() =>
            ComposeActivation();

        internal ReleaseStatusReadResult CreateStatus(string activeIdentity) =>
            ReleaseStatusReadResult.Success(
                Setup,
                releaseDirectoryPresent: true,
                [InstalledIdentity, TargetIdentity],
                currentPointerPresent: true,
                activeIdentity);

        private VerifiedReleaseActivationPlanCompositionResult ComposeActivation()
        {
            string targetPath = Path.Combine(Paths.ReleaseDirectory, TargetIdentity);
            VerifiedReleaseInstallationPackagePlan[] packages =
                CreatePackages(targetPath);
            VerifiedReleaseInstallationPlan installation = new(
                setupRevision: Setup.Revision,
                installedReleaseIdentity: InstalledIdentity,
                targetReleaseIdentity: TargetIdentity,
                targetVersion: "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                pinnedReleaseIdentity: string.Empty,
                installTransmitSupport: false,
                bundleDirectory: Path.Combine(Paths.StateDirectory, "bundle"),
                manifestLength: 37,
                manifestSha256: Enumerable.Repeat((byte)0x7A, 32).ToArray(),
                releaseRootPath: Paths.ReleaseDirectory,
                deploymentRootPath: Path.GetDirectoryName(Paths.ReleaseDirectory)!,
                targetReleasePath: targetPath,
                packages,
                targetConfigurationSchemaVersion: 1,
                ReleaseMigrationKind.None,
                migrationFromConfigurationSchemaVersion: null,
                migrationToConfigurationSchemaVersion: null,
                migrationIdentity: string.Empty,
                restartGatewayWeb: m_restartGateway,
                restartBroker: m_restartBroker,
                restartAetherRemoteAgent: m_restartAgent,
                restartStationEngine: m_restartEngine,
                restartHost: m_restartHost,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary:
                    "Exact service-control execution test release.");
            long publishedBytes = checked(
                installation.ManifestLength +
                packages.Sum(package => package.Length));
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(
                        installation,
                        installation.TargetReleasePath,
                        publishedBytes));
            VerifiedReleaseActivationPlanCompositionResult activation =
                new VerifiedReleaseActivationPlanComposer().Compose(publication);
            Assert.True(activation.Succeeded);
            return activation;
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
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        internal void Advance(TimeSpan duration) => m_now += duration;
    }

    private sealed class FakeRuntime :
        IVerifiedReleaseActivationServiceControlRuntime
    {
        internal List<VerifiedReleaseActivationServiceControlAction> Actions
        {
            get;
        } = [];
        internal int? FailAtCall { get; set; }
        internal int? CancelAtCall { get; set; }

        public Task<ServiceControlAttemptResult> ControlUnitAsync(
            VerifiedReleaseActivationServiceControlAction action,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Actions.Add(action);
            if (CancelAtCall == Actions.Count)
            {
                throw new OperationCanceledException("fixture cancellation");
            }
            return Task.FromResult(
                FailAtCall == Actions.Count
                    ? ServiceControlAttemptResult.Unknown(
                        "fixture unknown outcome")
                    : ServiceControlAttemptResult.Success());
        }
    }
}
