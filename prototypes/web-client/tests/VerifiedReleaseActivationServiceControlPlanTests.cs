using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationServiceControlPlanTests
{
    [Fact]
    public void PublicSurfaceExposesPlanningAndDiagnosticsOnly()
    {
        Type type = typeof(VerifiedReleaseActivationServiceControlPlanComposer);
        string[] publicMethods = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Compose", "get_Snapshot"], publicMethods);
        Assert.DoesNotContain(
            publicMethods,
            name => name.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Restart", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Stop", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Start", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnosticsRemainCallerlessAndNonExecuting()
    {
        VerifiedReleaseActivationServiceControlPlanDiagnostics snapshot =
            new VerifiedReleaseActivationServiceControlPlanComposer().Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ActivationPlanInputRegistered);
        Assert.True(snapshot.ExactActivationPlanBindingRegistered);
        Assert.True(snapshot.NoOpResolutionRegistered);
        Assert.True(snapshot.ServiceRestartPlanningRegistered);
        Assert.True(snapshot.HostRestartPlanningRegistered);
        Assert.True(snapshot.FixedServiceMappingRegistered);
        Assert.True(snapshot.DeterministicStopOrderingRegistered);
        Assert.True(snapshot.DeterministicStartOrderingRegistered);
        Assert.True(snapshot.HostRestartSupersessionRegistered);
        Assert.True(snapshot.PreSwitchPhasePlanningRegistered);
        Assert.True(snapshot.PostSwitchPhasePlanningRegistered);
        Assert.False(snapshot.ProcessInvocationRegistered);
        Assert.False(snapshot.SystemdCommandRegistered);
        Assert.False(snapshot.HostRestartExecutionRegistered);
        Assert.False(snapshot.ServiceControlEvidenceRegistered);
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
        Assert.False(snapshot.HealthProbeCallerRegistered);
        Assert.False(snapshot.RollbackCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public void NoRestartDeclarationResolvesAsExactNoOp()
    {
        Fixture fixture = new(
            migrationKind: ReleaseMigrationKind.None,
            restartGateway: false,
            restartBroker: false,
            restartAgent: false,
            restartEngine: false,
            restartHost: false);

        VerifiedReleaseActivationServiceControlPlanReport report =
            fixture.ComposeServiceControl();

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationServiceControlPlanFailureCode.None,
            report.FailureCode);
        Assert.False(report.ServiceControlRequired);
        Assert.True(report.NoOpServiceControlResolved);
        Assert.Equal(0, report.RestartServiceCount);
        Assert.Equal(0, report.StopActionCount);
        Assert.Equal(0, report.StartActionCount);
        Assert.Equal(0, report.HostRestartActionCount);
        Assert.True(report.ExactActivationPlanBound);
        Assert.True(report.FixedServiceMappingBound);
        Assert.True(report.DeterministicOrderingBound);
        Assert.True(report.ServiceControlReady);
        AssertNoExecution(report);
        VerifiedReleaseActivationServiceControlPlan plan =
            Assert.IsType<VerifiedReleaseActivationServiceControlPlan>(report.Plan);
        Assert.Empty(plan.StopActions);
        Assert.Empty(plan.StartActions);
        Assert.Empty(plan.HostRestartActions);
    }

    [Fact]
    public void SelectedServicesUseFixedDeterministicStopAndStartOrders()
    {
        Fixture fixture = new(
            migrationKind: ReleaseMigrationKind.None,
            restartGateway: true,
            restartBroker: false,
            restartAgent: true,
            restartEngine: false,
            restartHost: false);

        VerifiedReleaseActivationServiceControlPlanReport report =
            fixture.ComposeServiceControl();

        Assert.True(report.Succeeded);
        Assert.True(report.ServiceControlRequired);
        Assert.False(report.NoOpServiceControlResolved);
        Assert.Equal(2, report.RestartServiceCount);
        Assert.Equal(2, report.StopActionCount);
        Assert.Equal(2, report.StartActionCount);
        Assert.Equal(0, report.HostRestartActionCount);
        Assert.True(report.PreSwitchStopPlanned);
        Assert.True(report.PostSwitchStartPlanned);
        Assert.False(report.HostRestartPlanned);
        Assert.False(report.HostRestartSupersedesServiceActions);
        Assert.False(report.ServiceControlReady);
        AssertNoExecution(report);

        VerifiedReleaseActivationServiceControlPlan plan = report.Plan!;
        Assert.Collection(
            plan.StopActions,
            action => AssertAction(
                action,
                1,
                VerifiedReleaseActivationServiceControlActionKind.Stop,
                VerifiedReleaseActivationServiceRole.GatewayWeb,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .GatewayWebUnitIdentity),
            action => AssertAction(
                action,
                2,
                VerifiedReleaseActivationServiceControlActionKind.Stop,
                VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .AetherRemoteAgentUnitIdentity));
        Assert.Collection(
            plan.StartActions,
            action => AssertAction(
                action,
                1,
                VerifiedReleaseActivationServiceControlActionKind.Start,
                VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .AetherRemoteAgentUnitIdentity),
            action => AssertAction(
                action,
                2,
                VerifiedReleaseActivationServiceControlActionKind.Start,
                VerifiedReleaseActivationServiceRole.GatewayWeb,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .GatewayWebUnitIdentity));
    }

    [Fact]
    public void AllServiceRestartsUseCompleteDependencyOrder()
    {
        Fixture fixture = new(restartHost: false);

        VerifiedReleaseActivationServiceControlPlanReport report =
            fixture.ComposeServiceControl();

        Assert.True(report.Succeeded);
        VerifiedReleaseActivationServiceControlPlan plan = report.Plan!;
        Assert.Equal(
            [
                VerifiedReleaseActivationServiceRole.GatewayWeb,
                VerifiedReleaseActivationServiceRole.Broker,
                VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
                VerifiedReleaseActivationServiceRole.StationEngine
            ],
            plan.StopActions.Select(action => action.ServiceRole!.Value).ToArray());
        Assert.Equal(
            [
                VerifiedReleaseActivationServiceRole.StationEngine,
                VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
                VerifiedReleaseActivationServiceRole.Broker,
                VerifiedReleaseActivationServiceRole.GatewayWeb
            ],
            plan.StartActions.Select(action => action.ServiceRole!.Value).ToArray());
        Assert.Equal(4, report.RestartServiceCount);
        Assert.Equal(4, report.StopActionCount);
        Assert.Equal(4, report.StartActionCount);
        AssertNoExecution(report);
    }

    [Fact]
    public void HostRestartSupersedesIndividualServiceActions()
    {
        Fixture fixture = new(restartHost: true);

        VerifiedReleaseActivationServiceControlPlanReport report =
            fixture.ComposeServiceControl();

        Assert.True(report.Succeeded);
        Assert.True(report.HostRestartRequired);
        Assert.True(report.HostRestartPlanned);
        Assert.True(report.HostRestartSupersedesServiceActions);
        Assert.Equal(4, report.RestartServiceCount);
        Assert.Equal(0, report.StopActionCount);
        Assert.Equal(0, report.StartActionCount);
        Assert.Equal(1, report.HostRestartActionCount);
        Assert.False(report.ServiceControlReady);
        AssertNoExecution(report);

        VerifiedReleaseActivationServiceControlAction action =
            Assert.Single(report.Plan!.HostRestartActions);
        Assert.Equal(1, action.Sequence);
        Assert.Equal(
            VerifiedReleaseActivationServiceControlActionKind.RestartHost,
            action.Kind);
        Assert.Null(action.ServiceRole);
        Assert.Equal(
            VerifiedReleaseActivationServiceControlPlanComposer.HostRestartIdentity,
            action.UnitIdentity);
    }

    [Fact]
    public void HostRestartRequiresAllSignedServiceRestarts()
    {
        Fixture fixture = new(
            restartGateway: true,
            restartBroker: false,
            restartAgent: true,
            restartEngine: true,
            restartHost: true);

        VerifiedReleaseActivationServiceControlPlanReport report =
            fixture.ComposeServiceControl();

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlPlanFailureCode
                .RestartDeclarationInvalid);
    }

    [Fact]
    public void RequiredMigrationRequiresGatewayRestart()
    {
        Fixture fixture = new(
            migrationKind: ReleaseMigrationKind.Required,
            restartGateway: false,
            restartBroker: true,
            restartAgent: true,
            restartEngine: true,
            restartHost: false);

        VerifiedReleaseActivationServiceControlPlanReport report =
            fixture.ComposeServiceControl();

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlPlanFailureCode
                .RestartDeclarationInvalid);
    }

    [Fact]
    public void FailedActivationPlanIsRejected()
    {
        VerifiedReleaseActivationPlanCompositionResult activationPlan =
            new Fixture().ComposeActivation() with
            {
                Succeeded = false,
                FailureCode =
                    VerifiedReleaseActivationPlanFailureCode.InvalidActivationPaths
            };

        VerifiedReleaseActivationServiceControlPlanReport report =
            new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                activationPlan);

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlPlanFailureCode
                .ActivationPlanNotEligible);
    }

    [Fact]
    public void MissingInternalActivationPlanIsRejected()
    {
        VerifiedReleaseActivationPlanCompositionResult activationPlan =
            new Fixture().ComposeActivation() with { Plan = null };

        VerifiedReleaseActivationServiceControlPlanReport report =
            new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                activationPlan);

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlPlanFailureCode
                .ActivationPlanUnavailable);
    }

    [Fact]
    public void TamperedPublicSummaryIsRejected()
    {
        VerifiedReleaseActivationPlanCompositionResult activationPlan =
            new Fixture(restartHost: false).ComposeActivation() with
            {
                RestartServiceCount = 1,
                HostRestartRequired = true
            };

        VerifiedReleaseActivationServiceControlPlanReport report =
            new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                activationPlan);

        AssertFailure(
            report,
            VerifiedReleaseActivationServiceControlPlanFailureCode
                .ActivationPlanMismatch);
    }

    [Fact]
    public void IndependentlyComposedPlansRetainDistinctExactTokens()
    {
        VerifiedReleaseActivationServiceControlPlanReport first =
            new Fixture(restartHost: false).ComposeServiceControl();
        VerifiedReleaseActivationServiceControlPlanReport second =
            new Fixture(restartHost: false).ComposeServiceControl();

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotSame(first.Plan, second.Plan);
        Assert.NotSame(first.Plan!.ActivationPlan, second.Plan!.ActivationPlan);
    }

    [Fact]
    public void PublicReportRedactsUnitAndHostActionIdentities()
    {
        VerifiedReleaseActivationServiceControlPlanReport serviceReport =
            new Fixture(restartHost: false).ComposeServiceControl();
        VerifiedReleaseActivationServiceControlPlanReport hostReport =
            new Fixture(restartHost: true).ComposeServiceControl();

        string serviceJson = JsonSerializer.Serialize(serviceReport);
        string hostJson = JsonSerializer.Serialize(hostReport);
        foreach (string value in new[]
                 {
                     VerifiedReleaseActivationServiceControlPlanComposer
                         .GatewayWebUnitIdentity,
                     VerifiedReleaseActivationServiceControlPlanComposer
                         .BrokerUnitIdentity,
                     VerifiedReleaseActivationServiceControlPlanComposer
                         .AetherRemoteAgentUnitIdentity,
                     VerifiedReleaseActivationServiceControlPlanComposer
                         .StationEngineUnitIdentity,
                     VerifiedReleaseActivationServiceControlPlanComposer
                         .HostRestartIdentity
                 })
        {
            Assert.DoesNotContain(value, serviceJson, StringComparison.Ordinal);
            Assert.DoesNotContain(value, hostJson, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("systemctl", serviceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("systemctl", hostJson, StringComparison.Ordinal);
    }

    private static void AssertAction(
        VerifiedReleaseActivationServiceControlAction action,
        int sequence,
        VerifiedReleaseActivationServiceControlActionKind kind,
        VerifiedReleaseActivationServiceRole role,
        string unitIdentity)
    {
        Assert.Equal(sequence, action.Sequence);
        Assert.Equal(kind, action.Kind);
        Assert.Equal(role, action.ServiceRole);
        Assert.Equal(unitIdentity, action.UnitIdentity);
    }

    private static void AssertFailure(
        VerifiedReleaseActivationServiceControlPlanReport report,
        VerifiedReleaseActivationServiceControlPlanFailureCode failureCode)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.Null(report.Plan);
        Assert.False(report.ExactActivationPlanBound);
        Assert.False(report.FixedServiceMappingBound);
        Assert.False(report.DeterministicOrderingBound);
        Assert.False(report.NoOpServiceControlResolved);
        Assert.False(report.ServiceControlReady);
        AssertNoExecution(report);
    }

    private static void AssertNoExecution(
        VerifiedReleaseActivationServiceControlPlanReport report)
    {
        Assert.False(report.ProcessInvocationPerformed);
        Assert.False(report.SystemdCommandPerformed);
        Assert.False(report.HostRestartPerformed);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
    }

    private sealed class Fixture
    {
        private readonly ReleaseMigrationKind m_migrationKind;
        private readonly bool m_restartGateway;
        private readonly bool m_restartBroker;
        private readonly bool m_restartAgent;
        private readonly bool m_restartEngine;
        private readonly bool m_restartHost;

        internal Fixture(
            ReleaseMigrationKind migrationKind = ReleaseMigrationKind.Required,
            bool restartGateway = true,
            bool restartBroker = true,
            bool restartAgent = true,
            bool restartEngine = true,
            bool restartHost = true)
        {
            m_migrationKind = migrationKind;
            m_restartGateway = restartGateway;
            m_restartBroker = restartBroker;
            m_restartAgent = restartAgent;
            m_restartEngine = restartEngine;
            m_restartHost = restartHost;
        }

        internal VerifiedReleaseActivationPlanCompositionResult ComposeActivation()
        {
            string root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"activation-service-control-{Guid.NewGuid():N}"));
            string deploymentRoot = Path.Combine(root, "deployment");
            string releaseRoot = Path.Combine(deploymentRoot, "releases");
            string targetPath = Path.Combine(releaseRoot, "aethersdr-8.2.0");
            VerifiedReleaseInstallationPackagePlan[] packages =
                CreatePackages(targetPath);
            bool migrationRequired =
                m_migrationKind == ReleaseMigrationKind.Required;
            VerifiedReleaseInstallationPlan installationPlan = new(
                setupRevision: 7,
                installedReleaseIdentity: "aethersdr-8.1.0",
                targetReleaseIdentity: "aethersdr-8.2.0",
                targetVersion: "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                pinnedReleaseIdentity: string.Empty,
                installTransmitSupport: false,
                bundleDirectory: Path.Combine(root, "bundle"),
                manifestLength: 37,
                manifestSha256: Enumerable.Repeat((byte)0x7A, 32).ToArray(),
                releaseRoot,
                deploymentRoot,
                targetPath,
                packages,
                targetConfigurationSchemaVersion: migrationRequired ? 2 : 1,
                m_migrationKind,
                migrationFromConfigurationSchemaVersion:
                    migrationRequired ? 1 : null,
                migrationToConfigurationSchemaVersion:
                    migrationRequired ? 2 : null,
                migrationIdentity:
                    migrationRequired ? "schema-1-to-2" : string.Empty,
                restartGatewayWeb: m_restartGateway,
                restartBroker: m_restartBroker,
                restartAetherRemoteAgent: m_restartAgent,
                restartStationEngine: m_restartEngine,
                restartHost: m_restartHost,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Exact service-control planning test release.");
            long publishedBytes = checked(
                installationPlan.ManifestLength +
                packages.Sum(package => package.Length));
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(
                        installationPlan,
                        targetPath,
                        publishedBytes));
            VerifiedReleaseActivationPlanCompositionResult activationPlan =
                new VerifiedReleaseActivationPlanComposer().Compose(publication);
            Assert.True(activationPlan.Succeeded);
            return activationPlan;
        }

        internal VerifiedReleaseActivationServiceControlPlanReport
            ComposeServiceControl() =>
            new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                ComposeActivation());

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
