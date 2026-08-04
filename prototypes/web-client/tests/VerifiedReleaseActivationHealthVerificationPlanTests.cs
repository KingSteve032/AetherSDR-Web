using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationHealthVerificationPlanTests
{
    [Fact]
    public void PublicSurfaceExposesPlanningAndDiagnosticsOnly()
    {
        Type type =
            typeof(VerifiedReleaseActivationHealthVerificationPlanComposer);
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
                name.Contains("Probe", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Request", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Restart", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnosticsRemainCallerlessAndNonExecuting()
    {
        VerifiedReleaseActivationHealthVerificationPlanDiagnostics snapshot =
            new VerifiedReleaseActivationHealthVerificationPlanComposer().Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ServiceControlPlanInputRegistered);
        Assert.True(snapshot.ExactServiceControlPlanBindingRegistered);
        Assert.True(snapshot.ExactActivationPlanBindingRegistered);
        Assert.True(snapshot.CompleteServiceCoverageRegistered);
        Assert.True(snapshot.UnitActivityPlanningRegistered);
        Assert.True(snapshot.LoopbackHttpPlanningRegistered);
        Assert.True(snapshot.FreshBrokerLinkPlanningRegistered);
        Assert.True(snapshot.CanonicalGatewayHostBindingRegistered);
        Assert.True(snapshot.FixedHealthContractMappingRegistered);
        Assert.True(snapshot.DeterministicOrderingRegistered);
        Assert.True(snapshot.BoundedDeadlinePlanningRegistered);
        Assert.True(snapshot.PostSwitchPhasePlanningRegistered);
        Assert.True(snapshot.PostHostRestartPhasePlanningRegistered);
        Assert.False(snapshot.NetworkRequestRegistered);
        Assert.False(snapshot.SocketCallerRegistered);
        Assert.False(snapshot.HttpClientCallerRegistered);
        Assert.False(snapshot.ProcessInvocationRegistered);
        Assert.False(snapshot.SystemdCommandRegistered);
        Assert.False(snapshot.JournalReadRegistered);
        Assert.False(snapshot.HealthEvidenceRegistered);
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
        Assert.False(snapshot.RollbackCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public void CompleteHealthPlanUsesFixedDependencyOrderAndContracts()
    {
        VerifiedReleaseActivationHealthVerificationPlanReport report =
            new Fixture().ComposeHealthPlan();

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationHealthVerificationPlanFailureCode.None,
            report.FailureCode);
        Assert.True(report.HealthVerificationRequired);
        Assert.Equal(4, report.HealthTargetCount);
        Assert.Equal(4, report.UnitActivityCheckCount);
        Assert.Equal(3, report.LoopbackHttpCheckCount);
        Assert.Equal(1, report.FreshBrokerLinkCheckCount);
        Assert.True(report.ExactServiceControlPlanBound);
        Assert.True(report.ExactActivationPlanBound);
        Assert.True(report.CompleteServiceCoverageBound);
        Assert.True(report.FixedHealthContractMappingBound);
        Assert.True(report.DeterministicOrderingBound);
        Assert.True(report.LoopbackOnlyHttpBound);
        Assert.True(report.CanonicalGatewayHostBindingRequired);
        Assert.True(report.BoundedDeadlinePlanningBound);
        Assert.True(report.PostSwitchVerificationPlanned);
        Assert.False(report.PostHostRestartVerificationPlanned);
        AssertNoExecution(report);

        VerifiedReleaseActivationHealthVerificationPlan plan =
            Assert.IsType<VerifiedReleaseActivationHealthVerificationPlan>(
                report.Plan);
        Assert.Collection(
            plan.Targets,
            target => AssertLoopbackTarget(
                target,
                1,
                VerifiedReleaseActivationServiceRole.StationEngine,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .StationEngineUnitIdentity,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .StationEngineLoopbackPort,
                requireCanonicalHost: false,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .StationEngineDeadlineMilliseconds),
            target => AssertLoopbackTarget(
                target,
                2,
                VerifiedReleaseActivationServiceRole.Broker,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .BrokerUnitIdentity,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .BrokerLoopbackPort,
                requireCanonicalHost: false,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .BrokerDeadlineMilliseconds),
            target => AssertAgentTarget(target, 3),
            target => AssertLoopbackTarget(
                target,
                4,
                VerifiedReleaseActivationServiceRole.GatewayWeb,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .GatewayWebUnitIdentity,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .GatewayWebLoopbackPort,
                requireCanonicalHost: true,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .GatewayWebDeadlineMilliseconds));
    }

    [Fact]
    public void SelectedRestartsStillRequireCompleteServiceHealthCoverage()
    {
        Fixture fixture = new(
            migrationKind: ReleaseMigrationKind.None,
            restartGateway: true,
            restartBroker: false,
            restartAgent: true,
            restartEngine: false,
            restartHost: false);

        VerifiedReleaseActivationHealthVerificationPlanReport report =
            fixture.ComposeHealthPlan();

        Assert.True(report.Succeeded);
        Assert.True(report.ServiceControlRequired);
        Assert.Equal(2, report.RestartServiceCount);
        Assert.Equal(4, report.HealthTargetCount);
        Assert.True(report.CompleteServiceCoverageBound);
        Assert.True(report.PostSwitchVerificationPlanned);
        Assert.False(report.PostHostRestartVerificationPlanned);
        AssertNoExecution(report);
    }

    [Fact]
    public void NoRestartStillRequiresPostSwitchHealthVerification()
    {
        Fixture fixture = new(
            migrationKind: ReleaseMigrationKind.None,
            restartGateway: false,
            restartBroker: false,
            restartAgent: false,
            restartEngine: false,
            restartHost: false);

        VerifiedReleaseActivationHealthVerificationPlanReport report =
            fixture.ComposeHealthPlan();

        Assert.True(report.Succeeded);
        Assert.False(report.ServiceControlRequired);
        Assert.Equal(0, report.RestartServiceCount);
        Assert.True(report.HealthVerificationRequired);
        Assert.Equal(4, report.HealthTargetCount);
        Assert.True(report.PostSwitchVerificationPlanned);
        Assert.False(report.PostHostRestartVerificationPlanned);
        AssertNoExecution(report);
    }

    [Fact]
    public void HostRestartUsesPostBootHealthPhaseWithoutExecutingRestart()
    {
        VerifiedReleaseActivationHealthVerificationPlanReport report =
            new Fixture(restartHost: true).ComposeHealthPlan();

        Assert.True(report.Succeeded);
        Assert.True(report.HostRestartRequired);
        Assert.True(report.ServiceControlRequired);
        Assert.Equal(4, report.RestartServiceCount);
        Assert.True(report.PostSwitchVerificationPlanned);
        Assert.True(report.PostHostRestartVerificationPlanned);
        Assert.Equal(4, report.HealthTargetCount);
        AssertNoExecution(report);
    }

    [Fact]
    public void FailedServiceControlPlanIsRejected()
    {
        VerifiedReleaseActivationServiceControlPlanReport serviceControl =
            new Fixture().ComposeServiceControl() with
            {
                Succeeded = false,
                FailureCode =
                    VerifiedReleaseActivationServiceControlPlanFailureCode
                        .RestartDeclarationInvalid
            };

        VerifiedReleaseActivationHealthVerificationPlanReport report =
            new VerifiedReleaseActivationHealthVerificationPlanComposer().Compose(
                serviceControl);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationPlanFailureCode
                .ServiceControlPlanNotEligible);
    }

    [Fact]
    public void MissingInternalServiceControlPlanIsRejected()
    {
        VerifiedReleaseActivationServiceControlPlanReport serviceControl =
            new Fixture().ComposeServiceControl() with { Plan = null };

        VerifiedReleaseActivationHealthVerificationPlanReport report =
            new VerifiedReleaseActivationHealthVerificationPlanComposer().Compose(
                serviceControl);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationPlanFailureCode
                .ServiceControlPlanUnavailable);
    }

    [Fact]
    public void TamperedPublicServiceControlSummaryIsRejected()
    {
        VerifiedReleaseActivationServiceControlPlanReport serviceControl =
            new Fixture().ComposeServiceControl() with
            {
                RestartServiceCount = 1,
                StopActionCount = 1,
                StartActionCount = 1
            };

        VerifiedReleaseActivationHealthVerificationPlanReport report =
            new VerifiedReleaseActivationHealthVerificationPlanComposer().Compose(
                serviceControl);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationPlanFailureCode
                .ServiceControlPlanMismatch);
    }

    [Fact]
    public void TamperedInternalServiceActionIsRejected()
    {
        VerifiedReleaseActivationServiceControlPlanReport original =
            new Fixture().ComposeServiceControl();
        VerifiedReleaseActivationServiceControlPlan originalPlan = original.Plan!;
        VerifiedReleaseActivationServiceControlAction[] stops =
            originalPlan.StopActions.ToArray();
        stops[0] = stops[0] with { UnitIdentity = "unexpected.service" };
        VerifiedReleaseActivationServiceControlPlan tampered = new(
            originalPlan.ActivationPlan,
            stops,
            originalPlan.StartActions,
            originalPlan.HostRestartActions);

        VerifiedReleaseActivationHealthVerificationPlanReport report =
            new VerifiedReleaseActivationHealthVerificationPlanComposer().Compose(
                original with { Plan = tampered });

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationPlanFailureCode
                .ServiceControlPlanMismatch);
    }

    [Fact]
    public void IndependentlyComposedPlansRetainDistinctExactTokens()
    {
        VerifiedReleaseActivationHealthVerificationPlanReport first =
            new Fixture().ComposeHealthPlan();
        VerifiedReleaseActivationHealthVerificationPlanReport second =
            new Fixture().ComposeHealthPlan();

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotSame(first.Plan, second.Plan);
        Assert.NotSame(
            first.Plan!.ServiceControlPlan,
            second.Plan!.ServiceControlPlan);
        Assert.NotSame(
            first.Plan.ActivationPlan,
            second.Plan.ActivationPlan);
    }

    [Fact]
    public void EveryDeadlineIsPositiveAndBounded()
    {
        VerifiedReleaseActivationHealthVerificationPlan plan =
            new Fixture().ComposeHealthPlan().Plan!;

        Assert.All(
            plan.Targets,
            target => Assert.InRange(
                target.DeadlineMilliseconds,
                1,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .MaximumDeadlineMilliseconds));
        Assert.Equal(
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .MaximumDeadlineMilliseconds,
            plan.Targets.Max(target => target.DeadlineMilliseconds));
    }

    [Fact]
    public void PublicReportRedactsUnitsPortsPathsAndContractDetails()
    {
        VerifiedReleaseActivationHealthVerificationPlanReport report =
            new Fixture().ComposeHealthPlan();
        string json = JsonSerializer.Serialize(report);

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
                     VerifiedReleaseActivationHealthVerificationPlanComposer
                         .HealthPath,
                     VerifiedReleaseActivationHealthVerificationPlanComposer
                         .GatewayWebLoopbackPort.ToString(),
                     VerifiedReleaseActivationHealthVerificationPlanComposer
                         .BrokerLoopbackPort.ToString(),
                     VerifiedReleaseActivationHealthVerificationPlanComposer
                         .StationEngineLoopbackPort.ToString()
                 })
        {
            Assert.DoesNotContain(value, json, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("systemctl", json, StringComparison.Ordinal);
        Assert.DoesNotContain("journalctl", json, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", json, StringComparison.Ordinal);
    }

    private static void AssertLoopbackTarget(
        VerifiedReleaseActivationHealthVerificationTarget target,
        int sequence,
        VerifiedReleaseActivationServiceRole role,
        string unitIdentity,
        int loopbackPort,
        bool requireCanonicalHost,
        int deadlineMilliseconds)
    {
        Assert.Equal(sequence, target.Sequence);
        Assert.Equal(role, target.ServiceRole);
        Assert.Equal(unitIdentity, target.UnitIdentity);
        Assert.Equal(
            VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
            target.ContractKind);
        Assert.Equal(loopbackPort, target.LoopbackPort);
        Assert.Equal(
            VerifiedReleaseActivationHealthVerificationPlanComposer.HealthPath,
            target.HealthPath);
        Assert.Equal(
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .ExpectedHttpStatusCode,
            target.ExpectedHttpStatusCode);
        Assert.Equal(requireCanonicalHost, target.RequireCanonicalHostHeader);
        Assert.True(target.RequireUnitActive);
        Assert.True(target.RequireFreshObservation);
        Assert.Equal(deadlineMilliseconds, target.DeadlineMilliseconds);
    }

    private static void AssertAgentTarget(
        VerifiedReleaseActivationHealthVerificationTarget target,
        int sequence)
    {
        Assert.Equal(sequence, target.Sequence);
        Assert.Equal(
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
            target.ServiceRole);
        Assert.Equal(
            VerifiedReleaseActivationServiceControlPlanComposer
                .AetherRemoteAgentUnitIdentity,
            target.UnitIdentity);
        Assert.Equal(
            VerifiedReleaseActivationHealthContractKind.FreshBrokerLink,
            target.ContractKind);
        Assert.Null(target.LoopbackPort);
        Assert.Equal(string.Empty, target.HealthPath);
        Assert.Null(target.ExpectedHttpStatusCode);
        Assert.False(target.RequireCanonicalHostHeader);
        Assert.True(target.RequireUnitActive);
        Assert.True(target.RequireFreshObservation);
        Assert.Equal(
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .AetherRemoteAgentDeadlineMilliseconds,
            target.DeadlineMilliseconds);
    }

    private static void AssertFailure(
        VerifiedReleaseActivationHealthVerificationPlanReport report,
        VerifiedReleaseActivationHealthVerificationPlanFailureCode failureCode)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.Null(report.Plan);
        Assert.False(report.ExactServiceControlPlanBound);
        Assert.False(report.ExactActivationPlanBound);
        Assert.False(report.CompleteServiceCoverageBound);
        Assert.False(report.FixedHealthContractMappingBound);
        Assert.False(report.DeterministicOrderingBound);
        Assert.False(report.ServiceHealthReady);
        AssertNoExecution(report);
    }

    private static void AssertNoExecution(
        VerifiedReleaseActivationHealthVerificationPlanReport report)
    {
        Assert.False(report.NetworkRequestPerformed);
        Assert.False(report.ProcessInvocationPerformed);
        Assert.False(report.SystemdCommandPerformed);
        Assert.False(report.JournalReadPerformed);
        Assert.False(report.HealthEvidenceProduced);
        Assert.False(report.ServiceHealthReady);
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
            bool restartHost = false)
        {
            m_migrationKind = migrationKind;
            m_restartGateway = restartGateway;
            m_restartBroker = restartBroker;
            m_restartAgent = restartAgent;
            m_restartEngine = restartEngine;
            m_restartHost = restartHost;
        }

        internal VerifiedReleaseActivationServiceControlPlanReport
            ComposeServiceControl()
        {
            VerifiedReleaseActivationPlanCompositionResult activation =
                ComposeActivation();
            VerifiedReleaseActivationServiceControlPlanReport serviceControl =
                new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                    activation);
            Assert.True(serviceControl.Succeeded);
            return serviceControl;
        }

        internal VerifiedReleaseActivationHealthVerificationPlanReport
            ComposeHealthPlan() =>
            new VerifiedReleaseActivationHealthVerificationPlanComposer().Compose(
                ComposeServiceControl());

        private VerifiedReleaseActivationPlanCompositionResult ComposeActivation()
        {
            string root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"activation-health-plan-{Guid.NewGuid():N}"));
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
                releaseNotesSummary:
                    "Exact health-verification planning test release.");
            long publishedBytes = checked(
                installationPlan.ManifestLength +
                packages.Sum(package => package.Length));
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(
                        installationPlan,
                        targetPath,
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
}
