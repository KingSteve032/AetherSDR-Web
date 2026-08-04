using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Configuration;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleaseActivationHealthVerificationServiceTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsAndStateOnly()
    {
        Type type =
            typeof(VerifiedReleaseActivationHealthVerificationService);
        string[] methods = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["get_Snapshot", "get_State"], methods);
        Assert.DoesNotContain(
            methods,
            name => name.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Probe", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Observe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownConfigurationPropertiesFailClosed()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ReleaseActivationHealthVerificationSettings.SectionName}:" +
                    "ExecutonEnabled"] = "true"
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => configuration
                .GetSection(
                    ReleaseActivationHealthVerificationSettings.SectionName)
                .Get<ReleaseActivationHealthVerificationSettings>(options =>
                    options.ErrorOnUnknownConfiguration = true));

        Assert.Contains("ExecutonEnabled", exception.Message);
    }

    [Fact]
    public async Task DisabledDefaultFailsBeforeAnyObservation()
    {
        Fixture fixture = new(executionEnabled: false);

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationHealthVerificationFailureCode.ExecutionDisabled,
            report.FailureCode);
        Assert.False(report.ExecutionEnabled);
        Assert.False(report.ExecutionAvailable);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Equal(0, fixture.SetupReads);
        Assert.Empty(fixture.Runtime.UnitCalls);
        Assert.Empty(fixture.Runtime.HttpCalls);
        Assert.Equal(0, fixture.RemoteSnapshotReads);
        AssertFalseState(fixture.Service.State);
    }

    [Fact]
    public void ConfigurationIsStrictAndFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new Fixture(
                executionEnabled: false,
                expectedStationId: "station-one"));
        Assert.Throws<InvalidOperationException>(() =>
            new Fixture(
                executionEnabled: true,
                expectedStationId: " station-one"));
        Assert.Throws<InvalidOperationException>(() =>
            new Fixture(
                executionEnabled: true,
                expectedStationId: "station/one"));

        Fixture valid = new(
            executionEnabled: true,
            expectedStationId: "station-one");
        Assert.True(valid.Service.Snapshot.ExecutionEnabled);
        Assert.True(valid.Service.Snapshot.ExecutionAvailable);
        Assert.True(valid.Service.Snapshot.TopologyBindingRegistered);
        Assert.True(
            valid.Service.Snapshot.ServiceControlEvidenceInputRegistered);
        Assert.True(
            valid.Service.Snapshot.ExpectedStationIdentityConfigured);
    }

    [Fact]
    public async Task ExactActiveTargetPassesDeterministicBoundedSequence()
    {
        Fixture fixture = new();

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationHealthVerificationFailureCode.None,
            report.FailureCode);
        Assert.True(report.ExactHealthPlanBound);
        Assert.True(report.ExactActivationPlanBound);
        Assert.True(report.TargetActiveBeforeVerification);
        Assert.True(report.TargetActiveAfterVerification);
        Assert.True(report.SetupStable);
        Assert.True(report.CanonicalGatewayHostBound);
        Assert.True(report.AllUnitsActive);
        Assert.True(report.AllHealthContractsPassed);
        Assert.True(report.NetworkRequestPerformed);
        Assert.True(report.ProcessInvocationPerformed);
        Assert.True(report.SystemdCommandPerformed);
        Assert.False(report.JournalReadPerformed);
        Assert.False(report.RemoteStationSnapshotRead);
        Assert.True(report.HealthEvidenceProduced);
        Assert.True(report.ServiceHealthReady);
        Assert.True(report.ServiceControlReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
        Assert.Equal(4, report.HealthTargetCount);
        Assert.Equal(4, report.VerifiedTargetCount);
        Assert.Equal(3, report.UnitActivityAttemptCount);
        Assert.Equal(3, report.LoopbackHttpAttemptCount);
        Assert.Equal(0, report.BrokerLinkObservationCount);
        Assert.Equal(2, fixture.StatusReads);
        Assert.Equal(2, fixture.SetupReads);
        Assert.Equal(0, fixture.RemoteSnapshotReads);
        Assert.Equal(
            [
                VerifiedReleaseActivationServiceControlPlanComposer
                    .StationEngineUnitIdentity,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .BrokerUnitIdentity,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .GatewayWebUnitIdentity
            ],
            fixture.Runtime.UnitCalls);
        Assert.Equal(3, fixture.Runtime.HttpCalls.Count);
        Assert.All(
            fixture.Runtime.HttpCalls,
            call => Assert.Equal("radio.example.org", call.Authority));
        Assert.Equal(
            [
                VerifiedReleaseActivationServiceRole.StationEngine,
                VerifiedReleaseActivationServiceRole.Broker,
                VerifiedReleaseActivationServiceRole.GatewayWeb
            ],
            fixture.Runtime.HttpCalls.Select(call => call.Role).ToArray());

        VerifiedReleaseActivationHealthVerificationStateDiagnostics state =
            fixture.Service.State;
        Assert.True(state.HealthVerificationReady);
        Assert.True(state.ExactHealthPlanBound);
        Assert.True(state.ExactActivationPlanBound);
        Assert.Equal(4, state.HealthTargetCount);
        Assert.Equal(4, state.VerifiedTargetCount);
        Assert.Equal(3, state.UnitActivityCheckCount);
        Assert.Equal(3, state.LoopbackHttpCheckCount);
        Assert.Equal(0, state.FreshBrokerLinkCheckCount);
        Assert.True(state.TargetActiveBeforeVerification);
        Assert.True(state.TargetActiveAfterVerification);
        Assert.True(state.SetupStable);
        Assert.True(state.CanonicalGatewayHostBound);
        Assert.True(state.AllUnitsActive);
        Assert.True(state.AllHealthContractsPassed);
        Assert.False(state.ReconciliationRequired);
        Assert.True(state.ServiceControlReady);
        Assert.False(state.CurrentPointerChanged);
        Assert.False(state.ActivationAuthorized);
    }

    [Fact]
    public async Task MissingExactServiceControlEvidenceFailsBeforeAnyProbe()
    {
        Fixture fixture = new();
        fixture.ServiceControlObservationFactory = plan =>
            new VerifiedReleaseActivationServiceControlObservation(
                ServiceControlReady: false,
                ServiceControlRequired: plan.ServiceControlRequired,
                PlannedStopActionCount: 0,
                ExecutedStopActionCount: 0,
                TopologyNoOpStopActionCount: 0,
                PlannedStartActionCount: 0,
                ExecutedStartActionCount: 0,
                TopologyNoOpStartActionCount: 0,
                CompletedAt: null,
                ReconciliationRequired: false);

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationFailureCode
                .ServiceControlUnavailable);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Equal(0, fixture.SetupReads);
        Assert.Empty(fixture.Runtime.UnitCalls);
        Assert.Empty(fixture.Runtime.HttpCalls);
        Assert.Equal(0, fixture.RemoteSnapshotReads);
    }

    [Fact]
    public async Task LocalTopologyRejectsRemoteStationIdentityBeforeAnyProbe()
    {
        Fixture fixture = new(expectedStationId: "station-one");

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationFailureCode
                .StationIdentityMismatch);
        Assert.Empty(fixture.Runtime.UnitCalls);
        Assert.Empty(fixture.Runtime.HttpCalls);
        Assert.Equal(0, fixture.RemoteSnapshotReads);
    }

    [Fact]
    public async Task RemoteEngineTopologyFailsClosedWithoutRemoteProbeTransport()
    {
        Fixture fixture = new(
            topology: InstallationTopologyKind.RemoteStationGateway,
            expectedStationId: "station-one");

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationFailureCode
                .UnsupportedTopology);
        Assert.Empty(fixture.Runtime.UnitCalls);
        Assert.Empty(fixture.Runtime.HttpCalls);
        Assert.Equal(0, fixture.RemoteSnapshotReads);
    }

    [Fact]
    public async Task TargetMustAlreadyBeActiveBeforeAnyProbe()
    {
        Fixture fixture = new();
        fixture.StatusFactory = () =>
            ReleaseStatusReadResult.Success(
                fixture.Setup,
                releaseDirectoryPresent: true,
                [fixture.InstalledIdentity, fixture.TargetIdentity],
                currentPointerPresent: true,
                fixture.InstalledIdentity);

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationFailureCode.StatusMismatch);
        Assert.Equal(1, fixture.StatusReads);
        Assert.Equal(1, fixture.SetupReads);
        Assert.Empty(fixture.Runtime.UnitCalls);
        Assert.Empty(fixture.Runtime.HttpCalls);
        Assert.Equal(0, fixture.RemoteSnapshotReads);
    }

    [Fact]
    public async Task TamperedPlanSummaryIsRejectedBeforeObservation()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationHealthVerificationPlanReport tampered =
            fixture.HealthPlanReport with
            {
                HealthTargetCount = 3,
                CanonicalGatewayHostBindingRequired = false
            };

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(tampered);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationFailureCode
                .HealthPlanNotEligible);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Empty(fixture.Runtime.UnitCalls);
    }

    [Fact]
    public async Task UnitFailureProducesNoEvidence()
    {
        Fixture fixture = new();
        fixture.Runtime.FailedUnit =
            VerifiedReleaseActivationServiceControlPlanComposer.BrokerUnitIdentity;

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationFailureCode
                .UnitActivityUnavailable);
        Assert.Equal(2, report.UnitActivityAttemptCount);
        Assert.Equal(1, report.LoopbackHttpAttemptCount);
        Assert.Equal(0, report.BrokerLinkObservationCount);
        AssertFalseState(fixture.Service.State);
    }

    [Fact]
    public async Task LoopbackFailureProducesNoEvidence()
    {
        Fixture fixture = new();
        fixture.Runtime.FailedHttpRole =
            VerifiedReleaseActivationServiceRole.Broker;

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationFailureCode
                .LoopbackHealthUnavailable);
        Assert.Equal(2, report.UnitActivityAttemptCount);
        Assert.Equal(2, report.LoopbackHttpAttemptCount);
        Assert.Equal(0, report.BrokerLinkObservationCount);
        AssertFalseState(fixture.Service.State);
    }

    [Fact]
    public async Task FreshBrokerLinkRequiresExactConfiguredStation()
    {
        Fixture fixture = new(
            topology: InstallationTopologyKind.HybridGateway);
        fixture.RemoteSnapshotFactory = () =>
            fixture.CreateRemoteSnapshot("other-station");
        fixture.AdvanceOnRemoteSnapshot = TimeSpan.FromMinutes(2);

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationFailureCode
                .BrokerLinkUnavailable);
        Assert.True(report.BrokerLinkObservationCount > 0);
        AssertFalseState(fixture.Service.State);
    }

    [Fact]
    public async Task PostProbeReleaseDriftRejectsEvidence()
    {
        Fixture fixture = new();
        fixture.StatusFactory = () =>
        {
            bool first = fixture.StatusReads == 1;
            return ReleaseStatusReadResult.Success(
                fixture.Setup,
                releaseDirectoryPresent: true,
                [fixture.InstalledIdentity, fixture.TargetIdentity],
                currentPointerPresent: true,
                first ? fixture.TargetIdentity : fixture.InstalledIdentity);
        };

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationFailureCode
                .ObservationDrift);
        Assert.True(report.TargetActiveBeforeVerification);
        Assert.False(report.TargetActiveAfterVerification);
        AssertFalseState(fixture.Service.State);
    }

    [Fact]
    public async Task PostProbeSetupDriftRejectsEvidence()
    {
        Fixture fixture = new();
        fixture.SetupFactory = () =>
            fixture.SetupReads == 1
                ? fixture.Setup
                : fixture.Setup with
                {
                    CanonicalPublicUrl = "https://other.example.org",
                    Revision = fixture.Setup.Revision + 1,
                    UpdatedAt = fixture.Setup.UpdatedAt.AddSeconds(1)
                };

        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        AssertFailure(
            report,
            VerifiedReleaseActivationHealthVerificationFailureCode
                .ObservationDrift);
        Assert.False(report.SetupStable);
        AssertFalseState(fixture.Service.State);
    }

    [Fact]
    public async Task ObservationIsBoundToExactActivationPlanReference()
    {
        Fixture fixture = new();
        Fixture equivalent = new();
        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);
        Assert.True(report.Succeeded);

        VerifiedReleaseActivationHealthVerificationObservation exact =
            fixture.Service.Observe(fixture.ActivationPlan);
        VerifiedReleaseActivationHealthVerificationObservation other =
            fixture.Service.Observe(equivalent.ActivationPlan);

        Assert.True(exact.HealthVerificationReady);
        Assert.Equal(4, exact.VerifiedTargetCount);
        Assert.NotNull(exact.CompletedAt);
        Assert.False(other.HealthVerificationReady);
        Assert.Equal(0, other.VerifiedTargetCount);
        Assert.Null(other.CompletedAt);
    }

    [Fact]
    public async Task CompletedEvidenceCannotBeOverwritten()
    {
        Fixture fixture = new();
        Assert.True((await fixture.Service.ExecuteAsync(
            fixture.HealthPlanReport)).Succeeded);

        VerifiedReleaseActivationHealthVerificationReport second =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);

        AssertFailure(
            second,
            VerifiedReleaseActivationHealthVerificationFailureCode
                .HealthAlreadyVerified);
        Assert.True(fixture.Service.State.HealthVerificationReady);
    }

    [Fact]
    public async Task PublicReportRedactsOperationalIdentities()
    {
        Fixture fixture = new(
            topology: InstallationTopologyKind.HybridGateway);
        VerifiedReleaseActivationHealthVerificationReport report =
            await fixture.Service.ExecuteAsync(fixture.HealthPlanReport);
        Assert.True(report.Succeeded);

        string json = JsonSerializer.Serialize(report);
        foreach (string secret in new[]
                 {
                     fixture.ExpectedStationId,
                     "radio.example.org",
                     "127.0.0.1",
                     "5080",
                     "5081",
                     "5090",
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
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RuntimeUsesExactDirectSystemctlArguments()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        string root = Path.Combine(
            Path.GetTempPath(),
            $"health-systemctl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, "systemctl-probe");
        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\n" +
            "test \"$#\" -eq 4 || exit 10\n" +
            "test \"$1\" = --user || exit 11\n" +
            "test \"$2\" = is-active || exit 12\n" +
            "test \"$3\" = --quiet || exit 13\n" +
            "test \"$4\" = aethersdr-web.service || exit 14\n" +
            "test -z \"${HOME+x}\" || exit 14\n" +
            "exit 0\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        try
        {
            using HttpClient client = new(new CapturingHandler());
            LinuxVerifiedReleaseActivationHealthProbeRuntime runtime =
                new(script, client);

            HealthProbeAttemptResult result =
                await runtime.CheckUnitActiveAsync(
                    VerifiedReleaseActivationServiceControlPlanComposer
                        .GatewayWebUnitIdentity,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None);

            Assert.True(result.Succeeded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeBuildsFixedLoopbackRequestAndCanonicalHost()
    {
        CapturingHandler handler = new();
        using HttpClient client = new(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        LinuxVerifiedReleaseActivationHealthProbeRuntime runtime =
            new("/usr/bin/systemctl", client);
        VerifiedReleaseActivationHealthVerificationTarget target = new(
            Sequence: 4,
            VerifiedReleaseActivationServiceRole.GatewayWeb,
            VerifiedReleaseActivationServiceControlPlanComposer
                .GatewayWebUnitIdentity,
            VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .GatewayWebLoopbackPort,
            VerifiedReleaseActivationHealthVerificationPlanComposer.HealthPath,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .ExpectedHttpStatusCode,
            RequireCanonicalHostHeader: true,
            RequireUnitActive: true,
            RequireFreshObservation: true,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .GatewayWebDeadlineMilliseconds);

        HealthProbeAttemptResult result =
            await runtime.CheckLoopbackHealthAsync(
                target,
                "radio.example.org",
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal(
            "http://127.0.0.1:5080/healthz",
            handler.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("radio.example.org", handler.Request.Headers.Host);
        Assert.Null(handler.Request.Headers.Authorization);
    }

    private static void AssertFailure(
        VerifiedReleaseActivationHealthVerificationReport report,
        VerifiedReleaseActivationHealthVerificationFailureCode code)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(code, report.FailureCode);
        Assert.False(report.HealthEvidenceProduced);
        Assert.False(report.ServiceHealthReady);
        Assert.False(report.ServiceControlReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
        Assert.Null(report.Evidence);
    }

    private static void AssertFalseState(
        VerifiedReleaseActivationHealthVerificationStateDiagnostics state)
    {
        Assert.False(state.HealthVerificationReady);
        Assert.False(state.ExactHealthPlanBound);
        Assert.False(state.ExactActivationPlanBound);
        Assert.Equal(0, state.HealthTargetCount);
        Assert.Equal(0, state.VerifiedTargetCount);
        Assert.Equal(0, state.UnitActivityCheckCount);
        Assert.Equal(0, state.LoopbackHttpCheckCount);
        Assert.Equal(0, state.FreshBrokerLinkCheckCount);
        Assert.False(state.TargetActiveBeforeVerification);
        Assert.False(state.TargetActiveAfterVerification);
        Assert.False(state.SetupStable);
        Assert.False(state.CanonicalGatewayHostBound);
        Assert.False(state.AllUnitsActive);
        Assert.False(state.AllHealthContractsPassed);
        Assert.False(state.ReconciliationRequired);
        Assert.False(state.ServiceControlReady);
        Assert.False(state.CurrentPointerChanged);
        Assert.False(state.ActivationAuthorized);
    }

    private sealed class Fixture
    {
        private readonly ManualTimeProvider m_time =
            new(new DateTimeOffset(2026, 8, 4, 15, 0, 0, TimeSpan.Zero));

        internal Fixture(
            bool executionEnabled = true,
            InstallationTopologyKind topology =
                InstallationTopologyKind.PersonalSingleStation,
            string? expectedStationId = null)
        {
            ExpectedStationId = expectedStationId ??
                (executionEnabled &&
                 topology == InstallationTopologyKind.HybridGateway
                    ? "station-one"
                    : string.Empty);
            string root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"health-execution-{Guid.NewGuid():N}"));
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

            VerifiedReleaseInstallationPlan installation =
                CreateInstallationPlan(root);
            long publishedBytes = checked(
                installation.ManifestLength +
                installation.Packages.Sum(package => package.Length));
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(
                        installation,
                        installation.TargetReleasePath,
                        publishedBytes));
            ActivationPlanReport =
                new VerifiedReleaseActivationPlanComposer().Compose(publication);
            Assert.True(ActivationPlanReport.Succeeded);
            ActivationPlan = Assert.IsType<VerifiedReleaseActivationPlan>(
                ActivationPlanReport.Plan);
            ServiceControlPlanReport =
                new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                    ActivationPlanReport);
            Assert.True(ServiceControlPlanReport.Succeeded);
            HealthPlanReport =
                new VerifiedReleaseActivationHealthVerificationPlanComposer().Compose(
                    ServiceControlPlanReport);
            Assert.True(HealthPlanReport.Succeeded);

            Runtime = new FakeRuntime();
            StatusFactory = () => ReleaseStatusReadResult.Success(
                Setup,
                releaseDirectoryPresent: true,
                [InstalledIdentity, TargetIdentity],
                currentPointerPresent: true,
                TargetIdentity);
            SetupFactory = () => Setup;
            RemoteSnapshotFactory = () => CreateRemoteSnapshot(ExpectedStationId);
            ServiceControlObservationFactory = CreateReadyServiceControlObservation;
            ReleaseActivationHealthVerificationSettings settings = new()
            {
                ExecutionEnabled = executionEnabled,
                ExpectedStationId = ExpectedStationId
            };
            Service = new VerifiedReleaseActivationHealthVerificationService(
                _ =>
                {
                    StatusReads++;
                    return Task.FromResult(StatusFactory());
                },
                _ =>
                {
                    SetupReads++;
                    return Task.FromResult(SetupFactory());
                },
                () =>
                {
                    RemoteSnapshotReads++;
                    RemoteStationAdministrationSnapshot snapshot =
                        RemoteSnapshotFactory();
                    if (AdvanceOnRemoteSnapshot > TimeSpan.Zero)
                    {
                        m_time.Advance(AdvanceOnRemoteSnapshot);
                    }
                    return snapshot;
                },
                plan => ServiceControlObservationFactory(plan),
                Runtime,
                settings,
                m_time,
                (duration, _) =>
                {
                    m_time.Advance(duration);
                    return Task.CompletedTask;
                });
        }

        internal string InstalledIdentity => "aethersdr-8.1.0";
        internal string TargetIdentity => "aethersdr-8.2.0";
        internal string ExpectedStationId { get; }
        internal InstallationPaths Paths { get; }
        internal InstallationSetupState Setup { get; }
        internal VerifiedReleaseActivationPlanCompositionResult
            ActivationPlanReport
        {
            get;
        }
        internal VerifiedReleaseActivationPlan ActivationPlan { get; }
        internal VerifiedReleaseActivationServiceControlPlanReport
            ServiceControlPlanReport
        {
            get;
        }
        internal VerifiedReleaseActivationHealthVerificationPlanReport
            HealthPlanReport
        {
            get;
        }
        internal FakeRuntime Runtime { get; }
        internal VerifiedReleaseActivationHealthVerificationService Service
        {
            get;
        }
        internal Func<ReleaseStatusReadResult> StatusFactory { get; set; }
        internal Func<InstallationSetupState> SetupFactory { get; set; }
        internal Func<RemoteStationAdministrationSnapshot>
            RemoteSnapshotFactory
        {
            get;
            set;
        }
        internal Func<
            VerifiedReleaseActivationServiceControlPlan,
            VerifiedReleaseActivationServiceControlObservation>
            ServiceControlObservationFactory
        {
            get;
            set;
        }
        internal TimeSpan AdvanceOnRemoteSnapshot { get; set; }
        internal int StatusReads { get; private set; }
        internal int SetupReads { get; private set; }
        internal int RemoteSnapshotReads { get; private set; }

        internal VerifiedReleaseActivationServiceControlObservation
            CreateReadyServiceControlObservation(
                VerifiedReleaseActivationServiceControlPlan plan) =>
            new(
                ServiceControlReady: true,
                ServiceControlRequired: plan.ServiceControlRequired,
                PlannedStopActionCount: plan.StopActions.Count,
                ExecutedStopActionCount: plan.StopActions.Count,
                TopologyNoOpStopActionCount: 0,
                PlannedStartActionCount: plan.StartActions.Count,
                ExecutedStartActionCount: plan.StartActions.Count,
                TopologyNoOpStartActionCount: 0,
                CompletedAt: m_time.GetUtcNow(),
                ReconciliationRequired: false);

        internal RemoteStationAdministrationSnapshot CreateRemoteSnapshot(
            string stationId)
        {
            DateTimeOffset observed = m_time.GetUtcNow().AddSeconds(1);
            return new RemoteStationAdministrationSnapshot(
                Enabled: true,
                BrokerReachable: true,
                RefreshedAt: observed,
                Error: null,
                Stations:
                [
                    new RemoteStationAdministrationEntry(
                        stationId,
                        InstanceId: "instance-one",
                        State: "online",
                        SoftwareVersion: "8.2.0",
                        ConnectedAt: observed.AddMinutes(-5),
                        LastSeen: observed,
                        HeartbeatSequence: 3,
                        InventorySequence: 2,
                        ConnectionCount: 1,
                        LastDisconnectedAt: null,
                        LastDisconnectReason: null,
                        LastRecoveredAt: null,
                        LastRecoveryMilliseconds: null,
                        Capabilities: [],
                        Radios: [],
                        ReceiveSessions: [])
                ],
                Credentials: []);
        }

        private VerifiedReleaseInstallationPlan CreateInstallationPlan(
            string root)
        {
            string targetPath = Path.Combine(Paths.ReleaseDirectory, TargetIdentity);
            VerifiedReleaseInstallationPackagePlan[] packages =
                CreatePackages(targetPath);
            return new VerifiedReleaseInstallationPlan(
                setupRevision: Setup.Revision,
                installedReleaseIdentity: InstalledIdentity,
                targetReleaseIdentity: TargetIdentity,
                targetVersion: "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                pinnedReleaseIdentity: string.Empty,
                installTransmitSupport: false,
                bundleDirectory: Path.Combine(root, "bundle"),
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
                restartGatewayWeb: true,
                restartBroker: true,
                restartAetherRemoteAgent: true,
                restartStationEngine: true,
                restartHost: false,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary:
                    "Exact health-verification execution test release.");
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

    private sealed class FakeRuntime :
        IVerifiedReleaseActivationHealthProbeRuntime
    {
        internal List<string> UnitCalls { get; } = [];
        internal List<(
            VerifiedReleaseActivationServiceRole Role,
            string Authority)> HttpCalls
        {
            get;
        } = [];
        internal string? FailedUnit { get; set; }
        internal VerifiedReleaseActivationServiceRole? FailedHttpRole { get; set; }

        public Task<HealthProbeAttemptResult> CheckUnitActiveAsync(
            string unitIdentity,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(timeout > TimeSpan.Zero);
            UnitCalls.Add(unitIdentity);
            return Task.FromResult(
                string.Equals(
                    unitIdentity,
                    FailedUnit,
                    StringComparison.Ordinal)
                    ? HealthProbeAttemptResult.Reject("unit failed")
                    : HealthProbeAttemptResult.Success());
        }

        public Task<HealthProbeAttemptResult> CheckLoopbackHealthAsync(
            VerifiedReleaseActivationHealthVerificationTarget target,
            string canonicalGatewayAuthority,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(timeout > TimeSpan.Zero);
            HttpCalls.Add((target.ServiceRole, canonicalGatewayAuthority));
            return Task.FromResult(
                target.ServiceRole == FailedHttpRole
                    ? HealthProbeAttemptResult.Reject("http failed")
                    : HealthProbeAttemptResult.Success());
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        internal HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };
            foreach (var header in request.Headers)
            {
                Request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\":\"ok\"}",
                    Encoding.UTF8,
                    "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        internal void Advance(TimeSpan duration) => m_now += duration;
    }
}
