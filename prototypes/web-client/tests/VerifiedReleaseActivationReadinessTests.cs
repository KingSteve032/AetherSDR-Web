using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationReadinessTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsButNoReadinessCaller()
    {
        string[] methods = typeof(VerifiedReleaseActivationReadinessEvaluator)
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
    public void DiagnosticsExposeEvaluationWithoutExecutionAuthority()
    {
        VerifiedReleaseActivationReadinessDiagnostics snapshot =
            new VerifiedReleaseActivationReadinessEvaluator().Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ActivationPlanInputRegistered);
        Assert.True(snapshot.ReleaseStatusEvaluationRegistered);
        Assert.True(snapshot.TxLeaseAdmissionEvaluationRegistered);
        Assert.True(snapshot.SessionSafetyEvaluationRegistered);
        Assert.True(snapshot.RadioIdleEvaluationRegistered);
        Assert.True(snapshot.WatchdogEvaluationRegistered);
        Assert.True(snapshot.BackupReadinessEvaluationRegistered);
        Assert.True(snapshot.MigrationReadinessEvaluationRegistered);
        Assert.True(snapshot.ServiceControlReadinessEvaluationRegistered);
        Assert.True(snapshot.HealthVerificationReadinessEvaluationRegistered);
        Assert.True(snapshot.RollbackReadinessEvaluationRegistered);
        Assert.True(snapshot.OperatorApprovalEvaluationRegistered);
        Assert.False(snapshot.FileWriteRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.ActivationExecutionRegistered);
        Assert.False(snapshot.TxLeaseMutationRegistered);
        Assert.False(snapshot.RadioCommandRegistered);
        Assert.False(snapshot.WatchdogMutationRegistered);
        Assert.False(snapshot.BackupExecutionRegistered);
        Assert.False(snapshot.MigrationExecutionRegistered);
        Assert.False(snapshot.ServiceControlRegistered);
        Assert.False(snapshot.HealthProbeCallerRegistered);
        Assert.False(snapshot.RollbackExecutionRegistered);
        Assert.False(snapshot.CliCallerRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
        Assert.False(snapshot.HostedServiceCallerRegistered);
        Assert.False(snapshot.TimerCallerRegistered);
        Assert.False(snapshot.AetherRemoteCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public void CompleteNoSessionEvidenceProvesReadiness()
    {
        Fixture fixture = new();

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate();

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationReadinessFailureCode.None,
            report.FailureCode);
        Assert.Equal(7, report.SetupRevision);
        Assert.Equal("aethersdr-8.1.0", report.InstalledReleaseIdentity);
        Assert.Equal("aethersdr-8.2.0", report.TargetReleaseIdentity);
        Assert.Equal(0, report.SessionCount);
        Assert.Equal(0, report.RadioCount);
        Assert.Equal(0, report.ActiveTxLeaseCount);
        Assert.True(report.ReleaseStatusStable);
        Assert.True(report.TxLeaseAdmissionClosed);
        Assert.True(report.AllSessionsConnected);
        Assert.True(report.AllRadiosFreshIdle);
        Assert.True(report.AllSessionSafetyDisarmed);
        Assert.True(report.AllWatchdogsDisarmed);
        Assert.True(report.ConfigurationBackupReady);
        Assert.True(report.MigrationReady);
        Assert.True(report.ServiceControlReady);
        Assert.True(report.HealthVerificationReady);
        Assert.True(report.RollbackReady);
        Assert.True(report.OperatorApproved);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
        Assert.NotNull(report.Readiness);
    }

    [Fact]
    public void CompleteSessionEvidenceProvesReadiness()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationReadinessEvidence evidence =
            fixture.Evidence with
            {
                Sessions = [fixture.ReadySession()],
                Watchdogs = fixture.ReadyWatchdogs(sessionCount: 1)
            };

        VerifiedReleaseActivationReadinessReport report =
            fixture.Evaluate(evidence);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.SessionCount);
        Assert.Equal(1, report.RadioCount);
        VerifiedReleaseActivationReadiness readiness =
            Assert.IsType<VerifiedReleaseActivationReadiness>(report.Readiness);
        Assert.Single(readiness.Sessions);
        Assert.Equal(fixture.Now, readiness.CapturedAt);
    }

    [Fact]
    public void PublicReportRedactsPathsSessionsRadiosLeasesAndWatchdogDetails()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationReadinessEvidence evidence =
            fixture.Evidence with
            {
                Sessions = [fixture.ReadySession()],
                Watchdogs = fixture.ReadyWatchdogs(sessionCount: 1)
            };

        string json = JsonSerializer.Serialize(fixture.Evaluate(evidence));

        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("session-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("radio-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("lease-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("processId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("occupants", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluationPerformsNoFilesystemIo()
    {
        Fixture fixture = new();
        Assert.False(Directory.Exists(fixture.Root));

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate();

        Assert.True(report.Succeeded);
        Assert.False(Directory.Exists(fixture.Root));
        Assert.False(File.Exists(Path.Combine(fixture.DeploymentRoot, "current")));
    }

    [Fact]
    public void FailedActivationPlanCannotBeEvaluated()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationPlanCompositionResult plan = fixture.PlanResult with
        {
            Succeeded = false,
            FailureCode =
                VerifiedReleaseActivationPlanFailureCode.PublicationNotEligible
        };

        VerifiedReleaseActivationReadinessReport report =
            fixture.Evaluator.Evaluate(plan, fixture.Evidence);

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.ActivationPlanNotEligible);
    }

    [Fact]
    public void SuccessfulSummaryWithoutInternalPlanFailsClosed()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationPlanCompositionResult plan = fixture.PlanResult with
        {
            Plan = null
        };

        VerifiedReleaseActivationReadinessReport report =
            fixture.Evaluator.Evaluate(plan, fixture.Evidence);

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.ActivationPlanUnavailable);
    }

    [Fact]
    public void PublicPlanSummaryMismatchFailsClosed()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationPlanCompositionResult plan = fixture.PlanResult with
        {
            PackageCount = 3
        };

        VerifiedReleaseActivationReadinessReport report =
            fixture.Evaluator.Evaluate(plan, fixture.Evidence);

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.ActivationPlanNotEligible);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(2)]
    public void StaleOrFutureEvidenceFailsClosed(int seconds)
    {
        Fixture fixture = new();
        VerifiedReleaseActivationReadinessEvidence evidence =
            fixture.Evidence with
            {
                CapturedAt = fixture.Now.AddSeconds(seconds)
            };

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate(evidence);

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.EvidenceInvalid);
    }

    [Fact]
    public void DuplicateSessionIdentifiersFailClosed()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationSessionEvidence session = fixture.ReadySession();
        VerifiedReleaseActivationReadinessEvidence evidence =
            fixture.Evidence with
            {
                Sessions = [session, session],
                Watchdogs = fixture.ReadyWatchdogs(sessionCount: 2)
            };

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate(evidence);

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.EvidenceInvalid);
    }

    [Fact]
    public void UnavailableReleaseStatusFailsClosed()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationReadinessEvidence evidence =
            fixture.Evidence with
            {
                ReleaseStatus = fixture.Status with
                {
                    Succeeded = false,
                    FailureCode = ReleaseStatusFailureCode.StatusReadFailed
                }
            };

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate(evidence);

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.ReleaseStatusUnavailable);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("active-target")]
    [InlineData("target-missing")]
    [InlineData("inventory-duplicate")]
    [InlineData("channel")]
    [InlineData("tx-policy")]
    public void ReleaseStatusMustMatchInactivePlan(string mismatch)
    {
        Fixture fixture = new();
        ReleaseStatusReadResult status = mismatch switch
        {
            "revision" => fixture.Status with { SetupRevision = 8 },
            "active-target" => fixture.Status with
            {
                ActiveReleaseIdentity = "aethersdr-8.2.0"
            },
            "target-missing" => fixture.Status with
            {
                AvailableReleaseCount = 1,
                AvailableReleaseIdentities = ["aethersdr-8.1.0"]
            },
            "inventory-duplicate" => fixture.Status with
            {
                AvailableReleaseCount = 2,
                AvailableReleaseIdentities =
                    ["aethersdr-8.1.0", "aethersdr-8.1.0"]
            },
            "channel" => fixture.Status with
            {
                UpdateChannel = InstallationUpdateChannel.Beta
            },
            "tx-policy" => fixture.Status with
            {
                InstallTransmitSupport = true
            },
            _ => throw new InvalidOperationException()
        };

        VerifiedReleaseActivationReadinessReport report =
            fixture.Evaluate(fixture.Evidence with { ReleaseStatus = status });

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.ReleaseStatusMismatch);
    }

    [Fact]
    public void OpenTxLeaseAdmissionFailsClosed()
    {
        Fixture fixture = new();

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate(
            fixture.Evidence with { TxLeaseAdmissionClosed = false });

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.TxLeaseAdmissionOpen);
    }

    [Fact]
    public void AnyActiveTxLeaseFailsClosedAndRemainsRedacted()
    {
        Fixture fixture = new();
        TxLease lease = new(
            "lease-secret",
            "radio-secret",
            "session-secret",
            "client-secret",
            "user-secret",
            "operator",
            fixture.Now.AddSeconds(-1),
            fixture.Now.AddSeconds(-1),
            fixture.Now.AddSeconds(10));
        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate(
            fixture.Evidence with { ActiveTxLeases = [lease] });

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.ActiveTxLeasesPresent);
        Assert.Equal(1, report.ActiveTxLeaseCount);
        string json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("lease-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("unknown-occupancy")]
    [InlineData("stale-occupancy")]
    [InlineData("wrong-radio")]
    [InlineData("active-occupant")]
    [InlineData("lifecycle-unregistered")]
    [InlineData("lease-active")]
    [InlineData("gate-not-idle")]
    [InlineData("gate-intent")]
    [InlineData("safety-armed")]
    [InlineData("transaction-active")]
    [InlineData("transaction-reconciliation")]
    [InlineData("watchdog-armed")]
    [InlineData("watchdog-reconciliation")]
    public void UnsafeSessionEvidenceFailsClosed(string mismatch)
    {
        Fixture fixture = new();
        VerifiedReleaseActivationSessionEvidence session = fixture.ReadySession();
        RadioTxOccupancySnapshot occupancy = session.Occupancy;
        session = mismatch switch
        {
            "disconnected" => session with { Connected = false },
            "unknown-occupancy" => session with
            {
                Occupancy = occupancy with
                {
                    State = RadioTxOccupancyState.Unknown,
                    ObservedAt = null,
                    FreshUntil = null
                }
            },
            "stale-occupancy" => session with
            {
                Occupancy = occupancy with
                {
                    FreshUntil = fixture.Now
                }
            },
            "wrong-radio" => session with
            {
                Occupancy = occupancy with { RadioId = "another-radio" }
            },
            "active-occupant" => session with
            {
                Occupancy = occupancy with
                {
                    Occupants =
                    [new RadioTxOccupant(1, "FLEX", "station", "MIC", false)]
                }
            },
            "lifecycle-unregistered" => session with
            {
                TxLifecycleRegistered = false
            },
            "lease-active" => session with { LeaseActive = true },
            "gate-not-idle" => session with { GateState = "KeyPending" },
            "gate-intent" => session with { GateHasActiveIntent = true },
            "safety-armed" => session with
            {
                SafetyState = "Armed",
                SafetyActive = true
            },
            "transaction-active" => session with
            {
                CommandTransactionActive = true
            },
            "transaction-reconciliation" => session with
            {
                CommandTransactionReconciliationRequired = true
            },
            "watchdog-armed" => session with
            {
                IndependentWatchdogArmed = true,
                IndependentWatchdogState = "Armed"
            },
            "watchdog-reconciliation" => session with
            {
                IndependentWatchdogState = "ReconciliationRequired",
                IndependentWatchdogReconciliationRequired = true
            },
            _ => throw new InvalidOperationException()
        };
        VerifiedReleaseActivationReadinessEvidence evidence =
            fixture.Evidence with
            {
                Sessions = [session],
                Watchdogs = fixture.ReadyWatchdogs(sessionCount: 1)
            };

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate(evidence);

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.SessionEvidenceUnsafe);
    }

    [Theory]
    [InlineData("armed")]
    [InlineData("reconciliation")]
    [InlineData("degraded")]
    [InlineData("count-mismatch")]
    public void UnsafeWatchdogAggregateFailsClosed(string mismatch)
    {
        Fixture fixture = new(installTransmitSupport: true);
        StationTxIndependentWatchdogAggregate watchdogs =
            fixture.ReadyWatchdogs(sessionCount: 1);
        watchdogs = mismatch switch
        {
            "armed" => watchdogs with
            {
                ArmedProcessCount = 1,
                State = "supervised-armed"
            },
            "reconciliation" => watchdogs with
            {
                ReconciliationRequiredCount = 1,
                State = "supervised-reconciliation-required"
            },
            "degraded" => watchdogs with
            {
                RunningProcessCount = 0,
                ConnectedProcessCount = 0,
                State = "supervised-degraded-disarmed"
            },
            "count-mismatch" => watchdogs with
            {
                SessionCount = 0,
                RunningProcessCount = 0,
                ConnectedProcessCount = 0,
                RegisteredIdentityCount = 0,
                State = "supervised-empty-disarmed"
            },
            _ => throw new InvalidOperationException()
        };
        VerifiedReleaseActivationReadinessEvidence evidence =
            fixture.Evidence with
            {
                Sessions = [fixture.ReadySession()],
                Watchdogs = watchdogs
            };

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate(evidence);

        AssertFailure(
            report,
            VerifiedReleaseActivationReadinessFailureCode.WatchdogEvidenceUnsafe);
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("migration")]
    [InlineData("service")]
    [InlineData("health")]
    [InlineData("rollback")]
    [InlineData("approval")]
    public void EveryTransactionPrerequisiteFailsClosedWhenMissing(string missing)
    {
        Fixture fixture = new();
        VerifiedReleaseActivationReadinessEvidence evidence = missing switch
        {
            "backup" => fixture.Evidence with
            {
                ConfigurationBackupReady = false
            },
            "migration" => fixture.Evidence with { MigrationReady = false },
            "service" => fixture.Evidence with { ServiceControlReady = false },
            "health" => fixture.Evidence with
            {
                HealthVerificationReady = false
            },
            "rollback" => fixture.Evidence with { RollbackReady = false },
            "approval" => fixture.Evidence with { OperatorApproved = false },
            _ => throw new InvalidOperationException()
        };
        VerifiedReleaseActivationReadinessFailureCode expected = missing switch
        {
            "backup" => VerifiedReleaseActivationReadinessFailureCode
                .ConfigurationBackupNotReady,
            "migration" => VerifiedReleaseActivationReadinessFailureCode
                .MigrationNotReady,
            "service" => VerifiedReleaseActivationReadinessFailureCode
                .ServiceControlNotReady,
            "health" => VerifiedReleaseActivationReadinessFailureCode
                .HealthVerificationNotReady,
            "rollback" => VerifiedReleaseActivationReadinessFailureCode
                .RollbackNotReady,
            "approval" => VerifiedReleaseActivationReadinessFailureCode
                .OperatorApprovalMissing,
            _ => throw new InvalidOperationException()
        };

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate(evidence);

        AssertFailure(report, expected);
    }

    [Fact]
    public void ServiceControlEvidenceIsNotRequiredWhenNoRestartIsSigned()
    {
        Fixture fixture = new(
            restartGateway: false,
            restartBroker: false,
            restartAgent: false,
            restartEngine: false,
            restartHost: false);

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate(
            fixture.Evidence with { ServiceControlReady = false });

        Assert.True(report.Succeeded);
        Assert.False(report.ServiceControlReady);
    }

    [Fact]
    public void ReadinessTokenDefensivelyCopiesSessionEvidence()
    {
        Fixture fixture = new();
        List<VerifiedReleaseActivationSessionEvidence> sessions =
            [fixture.ReadySession()];
        VerifiedReleaseActivationReadinessEvidence evidence =
            fixture.Evidence with
            {
                Sessions = sessions,
                Watchdogs = fixture.ReadyWatchdogs(sessionCount: 1)
            };

        VerifiedReleaseActivationReadinessReport report = fixture.Evaluate(evidence);
        sessions.Clear();

        Assert.True(report.Succeeded);
        Assert.Single(report.Readiness!.Sessions);
    }

    private static void AssertFailure(
        VerifiedReleaseActivationReadinessReport report,
        VerifiedReleaseActivationReadinessFailureCode failureCode)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.Null(report.Readiness);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
    }

    private sealed class Fixture
    {
        private readonly ManualTimeProvider m_timeProvider;

        internal Fixture(
            bool installTransmitSupport = false,
            bool restartGateway = true,
            bool restartBroker = true,
            bool restartAgent = true,
            bool restartEngine = true,
            bool restartHost = true)
        {
            Now = new DateTimeOffset(2026, 8, 3, 23, 40, 0, TimeSpan.Zero);
            m_timeProvider = new ManualTimeProvider(Now);
            Evaluator = new VerifiedReleaseActivationReadinessEvaluator(
                m_timeProvider);
            Root = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-activation-readiness-{Guid.NewGuid():N}");
            DeploymentRoot = Path.Combine(Root, "deployment");
            ReleaseRoot = Path.Combine(DeploymentRoot, "releases");
            string targetPath = Path.Combine(ReleaseRoot, "aethersdr-8.2.0");
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
                installTransmitSupport,
                bundleDirectory: Path.Combine(Root, "bundle"),
                manifestLength: 37,
                manifestSha256: Enumerable.Repeat((byte)0x7A, 32).ToArray(),
                ReleaseRoot,
                DeploymentRoot,
                targetPath,
                packages,
                targetConfigurationSchemaVersion: 2,
                ReleaseMigrationKind.Required,
                migrationFromConfigurationSchemaVersion: 1,
                migrationToConfigurationSchemaVersion: 2,
                migrationIdentity: "schema-1-to-2",
                restartGateway,
                restartBroker,
                restartAgent,
                restartEngine,
                restartHost,
                txSupportCapable: installTransmitSupport,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Readiness test release.");
            long bytes = 37 + packages.Sum(package => package.Length);
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(installPlan, targetPath, bytes));
            PlanResult = new VerifiedReleaseActivationPlanComposer()
                .Compose(publication);
            Assert.True(PlanResult.Succeeded);

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
                InstallTransmitSupport: installTransmitSupport,
                ReleaseDirectoryPresent: true,
                AvailableReleaseCount: 2,
                AvailableReleaseIdentities:
                    ["aethersdr-8.1.0", "aethersdr-8.2.0"],
                CurrentPointerPresent: true,
                ActiveReleaseIdentity: "aethersdr-8.1.0",
                RollbackCandidateKnown: false);
            Evidence = new VerifiedReleaseActivationReadinessEvidence(
                Now,
                Status,
                TxLeaseAdmissionClosed: true,
                ActiveTxLeases: [],
                Sessions: [],
                ReadyWatchdogs(sessionCount: 0),
                ConfigurationBackupReady: true,
                MigrationReady: true,
                ServiceControlReady: true,
                HealthVerificationReady: true,
                RollbackReady: true,
                OperatorApproved: true);
        }

        internal DateTimeOffset Now { get; }
        internal string Root { get; }
        internal string DeploymentRoot { get; }
        internal string ReleaseRoot { get; }
        internal VerifiedReleaseActivationReadinessEvaluator Evaluator { get; }
        internal VerifiedReleaseActivationPlanCompositionResult PlanResult { get; }
        internal ReleaseStatusReadResult Status { get; }
        internal VerifiedReleaseActivationReadinessEvidence Evidence { get; }

        internal VerifiedReleaseActivationReadinessReport Evaluate(
            VerifiedReleaseActivationReadinessEvidence? evidence = null) =>
            Evaluator.Evaluate(PlanResult, evidence ?? Evidence);

        internal VerifiedReleaseActivationSessionEvidence ReadySession() =>
            new(
                SessionId: "session-secret",
                RadioId: "radio-secret",
                Connected: true,
                TxLifecycleRegistered: true,
                LeaseActive: false,
                GateState: "Idle",
                GateHasActiveIntent: false,
                SafetyState: "Disarmed",
                SafetyActive: false,
                CommandTransactionActive: false,
                CommandTransactionReconciliationRequired: false,
                IndependentWatchdogArmed: false,
                IndependentWatchdogState: "Disarmed",
                IndependentWatchdogReconciliationRequired: false,
                new RadioTxOccupancySnapshot(
                    "radio-secret",
                    RadioTxOccupancyState.Idle,
                    Now.AddSeconds(-1),
                    Now.AddSeconds(7),
                    Occupants: [],
                    LocalPttOwners: []));

        internal StationTxIndependentWatchdogAggregate ReadyWatchdogs(
            int sessionCount) =>
            new(
                SupervisionRegistered: true,
                SessionCount: sessionCount,
                RunningProcessCount: sessionCount,
                ConnectedProcessCount: sessionCount,
                RegisteredIdentityCount: sessionCount,
                RestartCount: 0,
                CommandTransportAvailable: false,
                ArmingAvailable: false,
                State: sessionCount == 0
                    ? "supervised-empty-disarmed"
                    : "supervised-disarmed",
                ArmedProcessCount: 0,
                ReconciliationRequiredCount: 0,
                UnkeyAttemptCount: 0);

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
