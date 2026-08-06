using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationLeaseQuiescenceTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsAndStateButNoOperationalCaller()
    {
        string[] methods =
            typeof(VerifiedReleaseActivationLeaseQuiescenceBoundary)
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
    public void DiagnosticsSeparateClosureDrainMutationCallersAndActivation()
    {
        VerifiedReleaseActivationLeaseQuiescenceBoundary boundary =
            new(new TxLeaseManager());
        VerifiedReleaseActivationLeaseQuiescenceDiagnostics snapshot =
            boundary.Snapshot;
        VerifiedReleaseActivationLeaseQuiescenceStateDiagnostics state =
            boundary.State;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ActivationPlanInputRegistered);
        Assert.True(snapshot.TransactionBoundPlanCompositionRegistered);
        Assert.True(snapshot.AdmissionClosureAuthorityRegistered);
        Assert.True(snapshot.ActiveClosureStateRegistered);
        Assert.True(snapshot.AcquisitionSuppressionRegistered);
        Assert.True(snapshot.RenewalSuppressionRegistered);
        Assert.True(snapshot.ObservationOnlyLeaseSnapshotRegistered);
        Assert.True(snapshot.DrainEvaluationRegistered);
        Assert.False(snapshot.ExistingLeaseForceReleaseRegistered);
        Assert.False(snapshot.TxLeaseMutationRegistered);
        Assert.False(snapshot.RadioIdleInferenceRegistered);
        Assert.False(snapshot.RadioCommandRegistered);
        Assert.False(snapshot.WatchdogMutationRegistered);
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
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);

        Assert.False(state.AdmissionClosureActive);
        Assert.False(state.ExactTransactionBoundClosureActive);
        Assert.Equal(0, state.ObservedTxLeaseCount);
        Assert.False(state.DrainSatisfied);
        Assert.False(state.TxLeaseMutationAuthorityAvailable);
        Assert.False(state.RadioAuthoritativeIdleProven);
        Assert.False(state.ActivationAuthorized);
    }

    [Fact]
    public void CompositionBindsExactPlanWithoutClosingAdmission()
    {
        Fixture fixture = new();
        TxLeaseManager leases = new(fixture.Time);
        VerifiedReleaseActivationLeaseQuiescenceBoundary boundary = new(leases);

        VerifiedReleaseActivationLeaseQuiescenceReport report =
            boundary.Compose(fixture.PlanResult);

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationLeaseQuiescenceFailureCode.None,
            report.FailureCode);
        Assert.True(report.AdmissionClosureComposed);
        Assert.False(report.AdmissionClosed);
        Assert.False(report.DrainSatisfied);
        Assert.False(report.TxLeaseMutationPerformed);
        Assert.False(report.RadioAuthoritativeIdleProven);
        Assert.False(report.ActivationAuthorized);
        Assert.NotNull(report.Plan);
        Assert.False(boundary.State.AdmissionClosureActive);
        Assert.True(Acquire(leases, "radio-a", out _));
    }

    [Fact]
    public void ActiveClosureBlocksAcquireAndRenewButAllowsReleaseAndDrain()
    {
        Fixture fixture = new();
        TxLeaseManager leases = new(fixture.Time);
        Assert.True(Acquire(leases, "radio-a", out TxLease? existing));
        VerifiedReleaseActivationLeaseQuiescenceBoundary boundary = new(leases);
        VerifiedReleaseActivationLeaseQuiescenceReport plan =
            boundary.Compose(fixture.PlanResult);

        VerifiedReleaseActivationLeaseQuiescenceReport closed =
            boundary.CloseAdmission(plan);

        Assert.True(closed.Succeeded);
        Assert.True(closed.AdmissionClosed);
        Assert.Equal(1, closed.ObservedTxLeaseCount);
        Assert.False(closed.DrainSatisfied);
        Assert.False(leases.TryAcquire(
            "radio-b",
            "session-b",
            "client-b",
            "user-b",
            "Operator B",
            TimeSpan.FromSeconds(10),
            out TxLease? rejected,
            out string? acquireError));
        Assert.Null(rejected);
        Assert.Contains("admission is closed", acquireError, StringComparison.Ordinal);
        Assert.False(leases.TryRenew(
            "radio-a",
            existing!.LeaseId,
            existing.SessionId,
            existing.ClientId,
            TimeSpan.FromSeconds(10),
            out _,
            out string? renewError));
        Assert.Contains("renewal is closed", renewError, StringComparison.Ordinal);
        Assert.True(leases.TryValidate(
            "radio-a",
            existing.LeaseId,
            existing.SessionId,
            existing.ClientId,
            out _,
            out _));
        Assert.True(leases.TryRelease(
            "radio-a",
            existing.LeaseId,
            existing.SessionId,
            existing.ClientId,
            "operator-request",
            out _));

        VerifiedReleaseActivationLeaseQuiescenceReport drained =
            boundary.EvaluateDrain(closed);

        Assert.True(drained.Succeeded);
        Assert.True(drained.AdmissionClosed);
        Assert.Equal(0, drained.ObservedTxLeaseCount);
        Assert.True(drained.DrainSatisfied);
        Assert.False(drained.RadioAuthoritativeIdleProven);
        Assert.False(drained.ActivationAuthorized);
        Assert.True(boundary.State.AdmissionClosureActive);
        Assert.True(boundary.State.ExactTransactionBoundClosureActive);
        Assert.True(boundary.State.DrainSatisfied);
    }

    [Fact]
    public void ExactClosureAuthorityCanReopenAdmissionWithoutMutatingLeases()
    {
        Fixture fixture = new();
        TxLeaseManager leases = new(fixture.Time);
        VerifiedReleaseActivationLeaseQuiescenceBoundary boundary = new(leases);
        VerifiedReleaseActivationLeaseQuiescenceReport closed =
            boundary.CloseAdmission(boundary.Compose(fixture.PlanResult));
        Assert.True(closed.Succeeded);
        Assert.True(closed.AdmissionClosed);
        Assert.True(closed.DrainSatisfied);

        VerifiedReleaseActivationLeaseQuiescenceReport reopened =
            boundary.ReleaseAdmission(closed);

        Assert.True(reopened.Succeeded, reopened.Message);
        Assert.False(reopened.AdmissionClosed);
        Assert.False(boundary.State.AdmissionClosureActive);
        Assert.False(reopened.TxLeaseMutationPerformed);
        Assert.True(Acquire(leases, "radio-after-update", out _));

        VerifiedReleaseActivationLeaseQuiescenceReport repeated =
            boundary.ReleaseAdmission(closed);
        Assert.False(repeated.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationLeaseQuiescenceFailureCode
                .AdmissionReopenRejected,
            repeated.FailureCode);
    }

    [Fact]
    public void ClosureObservationDoesNotExpireOrForceReleaseStoredLease()
    {
        Fixture fixture = new();
        TxLeaseManager leases = new(fixture.Time);
        List<TxLeaseChange> changes = [];
        leases.Changed += changes.Add;
        Assert.True(leases.TryAcquire(
            "radio-a",
            "session-a",
            "client-a",
            "user-a",
            "Operator A",
            TimeSpan.FromSeconds(1),
            out _,
            out _));
        VerifiedReleaseActivationLeaseQuiescenceBoundary boundary = new(leases);
        VerifiedReleaseActivationLeaseQuiescenceReport closed =
            boundary.CloseAdmission(boundary.Compose(fixture.PlanResult));
        fixture.Time.Advance(TimeSpan.FromSeconds(2));

        VerifiedReleaseActivationLeaseQuiescenceReport observed =
            boundary.EvaluateDrain(closed);

        Assert.True(observed.Succeeded);
        Assert.Equal(1, observed.ObservedTxLeaseCount);
        Assert.False(observed.DrainSatisfied);
        Assert.DoesNotContain(changes, change => !change.Active);
        Assert.Equal(1, leases.SweepExpired());
        Assert.True(boundary.EvaluateDrain(closed).DrainSatisfied);
        Assert.Contains(
            changes,
            change => !change.Active && change.Reason == "expired");
    }

    [Fact]
    public void DifferentTransactionTokenCannotTakeOverActiveClosure()
    {
        Fixture fixture = new();
        TxLeaseManager leases = new(fixture.Time);
        VerifiedReleaseActivationLeaseQuiescenceBoundary boundary = new(leases);
        VerifiedReleaseActivationLeaseQuiescenceReport first =
            boundary.Compose(fixture.PlanResult);
        VerifiedReleaseActivationLeaseQuiescenceReport second =
            boundary.Compose(fixture.PlanResult);
        Assert.NotSame(first.Plan, second.Plan);
        Assert.True(boundary.CloseAdmission(first).Succeeded);

        VerifiedReleaseActivationLeaseQuiescenceReport rejected =
            boundary.CloseAdmission(second);

        Assert.False(rejected.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationLeaseQuiescenceFailureCode
                .DifferentTransactionActive,
            rejected.FailureCode);
        Assert.True(boundary.State.ExactTransactionBoundClosureActive);
    }

    [Fact]
    public async Task EvidenceUsesOnlyTheExactActivePlanClosure()
    {
        Fixture fixture = new();
        TxLeaseManager leases = new(fixture.Time);
        VerifiedReleaseActivationLeaseQuiescenceBoundary boundary = new(leases);
        VerifiedReleaseActivationLeaseQuiescenceReport closed =
            boundary.CloseAdmission(boundary.Compose(fixture.PlanResult));
        Assert.True(closed.DrainSatisfied);
        VerifiedReleaseActivationEvidenceCollector collector = new(
            _ => Task.FromResult(fixture.Status),
            boundary.Observe,
            () => [],
            ReadyWatchdogs,
            fixture.Time);

        VerifiedReleaseActivationEvidenceCollectionReport exact =
            await collector.CollectAsync(fixture.PlanResult);
        VerifiedReleaseActivationPlanCompositionResult equivalentButDistinct =
            fixture.CreatePlanResult();
        VerifiedReleaseActivationEvidenceCollectionReport distinct =
            await collector.CollectAsync(equivalentButDistinct);

        Assert.True(exact.Succeeded);
        Assert.True(exact.TxLeaseAdmissionClosed);
        Assert.Equal(0, exact.ObservedTxLeaseCount);
        Assert.True(distinct.Succeeded);
        Assert.False(distinct.TxLeaseAdmissionClosed);
        VerifiedReleaseActivationReadinessReport readiness =
            new VerifiedReleaseActivationReadinessEvaluator(fixture.Time)
                .Evaluate(fixture.PlanResult, exact.Collection!.Evidence);
        Assert.Equal(
            VerifiedReleaseActivationReadinessFailureCode
                .ConfigurationBackupNotReady,
            readiness.FailureCode);
    }

    [Fact]
    public void PublicReportsRemainLeaseIdentityRedacted()
    {
        Fixture fixture = new();
        TxLeaseManager leases = new(fixture.Time);
        Assert.True(Acquire(leases, "radio-secret", out TxLease? lease));
        VerifiedReleaseActivationLeaseQuiescenceBoundary boundary = new(leases);

        VerifiedReleaseActivationLeaseQuiescenceReport report =
            boundary.CloseAdmission(boundary.Compose(fixture.PlanResult));
        string json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain(lease!.LeaseId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(lease.RadioId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(lease.SessionId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(lease.ClientId, json, StringComparison.Ordinal);
        Assert.Contains("observedTxLeaseCount", json, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Acquire(
        TxLeaseManager leases,
        string radioId,
        out TxLease? lease) =>
        leases.TryAcquire(
            radioId,
            $"session-{radioId}",
            $"client-{radioId}",
            $"user-{radioId}",
            $"Operator {radioId}",
            TimeSpan.FromSeconds(10),
            out lease,
            out _);

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

    private sealed class Fixture
    {
        internal Fixture()
        {
            Time = new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 3, 20, 0, 0, TimeSpan.Zero));
            Root = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), $"lease-quiescence-{Guid.NewGuid():N}"));
            PlanResult = CreatePlanResult();
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
        internal VerifiedReleaseActivationPlanCompositionResult PlanResult { get; }
        internal ReleaseStatusReadResult Status { get; }

        internal VerifiedReleaseActivationPlanCompositionResult CreatePlanResult()
        {
            string deploymentRoot = Path.Combine(Root, "deployment");
            string releaseRoot = Path.Combine(deploymentRoot, "releases");
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
                releaseNotesSummary: "Lease quiescence test release.");
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

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        internal void Advance(TimeSpan duration) => m_now += duration;
    }
}
