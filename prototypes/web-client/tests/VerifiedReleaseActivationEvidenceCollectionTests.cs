using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationEvidenceCollectionTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsButNoCollectionCaller()
    {
        string[] methods = typeof(VerifiedReleaseActivationEvidenceCollector)
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
    public void DiagnosticsExposeObservationWithoutMutationAuthority()
    {
        VerifiedReleaseActivationEvidenceCollectionDiagnostics snapshot =
            new Fixture().Collector.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ActivationPlanInputRegistered);
        Assert.True(snapshot.ReleaseStatusDoubleReadRegistered);
        Assert.True(snapshot.ObservationOnlyTxLeaseSnapshotRegistered);
        Assert.True(snapshot.SessionDiagnosticsSnapshotRegistered);
        Assert.True(snapshot.RadioOccupancySnapshotRegistered);
        Assert.True(snapshot.WatchdogAggregateSnapshotRegistered);
        Assert.True(snapshot.BoundedCollectionWindowRegistered);
        Assert.True(snapshot.MissingPrerequisitesFailClosedRegistered);
        Assert.True(snapshot.TxLeaseAdmissionClosureEvidenceRegistered);
        Assert.True(snapshot.ConfigurationBackupEvidenceRegistered);
        Assert.True(snapshot.MigrationExecutionEvidenceRegistered);
        Assert.True(snapshot.ServiceControlEvidenceRegistered);
        Assert.True(snapshot.HealthVerificationEvidenceRegistered);
        Assert.False(snapshot.RollbackEvidenceRegistered);
        Assert.True(snapshot.OperatorApprovalEvidenceRegistered);
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
    public async Task EmptyRuntimeSourcesProduceBoundedFailClosedEvidence()
    {
        Fixture fixture = new();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationEvidenceCollectionFailureCode.None,
            report.FailureCode);
        Assert.Equal(7, report.SetupRevision);
        Assert.Equal("aethersdr-8.1.0", report.InstalledReleaseIdentity);
        Assert.Equal("aethersdr-8.2.0", report.TargetReleaseIdentity);
        Assert.Equal(0, report.ObservedTxLeaseCount);
        Assert.Equal(0, report.SessionCount);
        Assert.Equal(0, report.RadioCount);
        Assert.Equal(0, report.WatchdogSessionCount);
        Assert.True(report.ReleaseStatusCollected);
        Assert.True(report.ReleaseStatusStable);
        Assert.True(report.ReleaseStatusSucceeded);
        Assert.True(report.ObservationOnlyTxLeaseSnapshot);
        Assert.True(report.SessionSafetyEvidenceCollected);
        Assert.True(report.WatchdogEvidenceCollected);
        Assert.False(report.TxLeaseAdmissionClosed);
        Assert.False(report.ConfigurationBackupReady);
        Assert.False(report.MigrationReady);
        Assert.False(report.ServiceControlReady);
        Assert.False(report.HealthVerificationReady);
        Assert.False(report.RollbackReady);
        Assert.False(report.OperatorApproved);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);

        VerifiedReleaseActivationEvidenceCollection collection =
            Assert.IsType<VerifiedReleaseActivationEvidenceCollection>(
                report.Collection);
        Assert.Equal(fixture.Now, collection.Evidence.CapturedAt);
        Assert.Empty(collection.Evidence.ActiveTxLeases);
        Assert.Empty(collection.Evidence.Sessions);
    }

    [Fact]
    public async Task StableFailedStatusIsCollectedForEvaluatorRejection()
    {
        Fixture fixture = new();
        fixture.Status = fixture.Status with
        {
            Succeeded = false,
            FailureCode = ReleaseStatusFailureCode.StatusReadFailed
        };

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        Assert.True(report.Succeeded);
        Assert.False(report.ReleaseStatusSucceeded);
        VerifiedReleaseActivationReadinessReport readiness =
            fixture.Evaluator.Evaluate(
                fixture.PlanResult,
                report.Collection!.Evidence);
        Assert.False(readiness.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationReadinessFailureCode.ReleaseStatusUnavailable,
            readiness.FailureCode);
    }

    [Fact]
    public async Task RequiredTransactionPrerequisitesRemainUnavailable()
    {
        Fixture fixture = new();

        VerifiedReleaseActivationReadinessEvidence evidence =
            (await fixture.CollectAsync()).Collection!.Evidence;

        Assert.False(evidence.TxLeaseAdmissionClosed);
        Assert.False(evidence.ConfigurationBackupReady);
        Assert.False(evidence.MigrationReady);
        Assert.False(evidence.ServiceControlReady);
        Assert.False(evidence.HealthVerificationReady);
        Assert.False(evidence.RollbackReady);
        Assert.False(evidence.OperatorApproved);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReadyConfigurationBackupAcceptsSupportedSourceRootCounts(
        int sourceDirectoryCount)
    {
        Fixture fixture = new();
        fixture.ConfigurationBackupReader = _ =>
            new VerifiedReleaseActivationConfigurationBackupObservation(
                ConfigurationBackupReady: true,
                SourceDirectoryCount: sourceDirectoryCount,
                DirectoryCount: sourceDirectoryCount + 2,
                FileCount: 3,
                BackupBytes: 4096,
                CompletedAt: fixture.Now,
                ReconciliationRequired: false);
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        Assert.True(report.Succeeded);
        Assert.True(report.ConfigurationBackupReady);
        Assert.True(report.Collection!.Evidence.ConfigurationBackupReady);
    }

    [Fact]
    public async Task ExactServiceControlAndHealthObservationsCompleteBothFields()
    {
        Fixture fixture = new(restartHost: false);
        fixture.Status = fixture.Status with
        {
            ActiveReleaseIdentity = "aethersdr-8.2.0"
        };
        fixture.ServiceControlReader = _ =>
            fixture.ReadyServiceControlObservation();
        fixture.HealthVerificationReader = _ =>
            fixture.ReadyHealthVerificationObservation();
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        Assert.True(report.Succeeded);
        Assert.True(report.HealthVerificationReady);
        Assert.True(report.ServiceControlReady);
        Assert.False(report.RollbackReady);
        Assert.False(report.OperatorApproved);
        Assert.True(report.Collection!.Evidence.ServiceControlReady);
        Assert.True(report.Collection.Evidence.HealthVerificationReady);
    }

    [Fact]
    public async Task HealthReadyWithoutServiceControlEvidenceFailsClosed()
    {
        Fixture fixture = new(restartHost: false);
        fixture.Status = fixture.Status with
        {
            ActiveReleaseIdentity = "aethersdr-8.2.0"
        };
        fixture.HealthVerificationReader = _ =>
            fixture.ReadyHealthVerificationObservation();
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed,
            report.FailureCode);
        Assert.Null(report.Collection);
    }

    [Fact]
    public async Task MalformedHealthObservationFailsClosed()
    {
        Fixture fixture = new();
        fixture.HealthVerificationReader = _ =>
            new VerifiedReleaseActivationHealthVerificationObservation(
                HealthVerificationReady: true,
                HealthTargetCount: 4,
                VerifiedTargetCount: 3,
                UnitActivityCheckCount: 4,
                LoopbackHttpCheckCount: 3,
                FreshBrokerLinkCheckCount: 1,
                CompletedAt: fixture.Now,
                ReconciliationRequired: false);
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed,
            report.FailureCode);
        Assert.Null(report.Collection);
    }

    [Fact]
    public async Task ExactOperatorApprovalObservationCompletesApprovalEvidence()
    {
        Fixture fixture = new();
        fixture.OperatorApprovalReader = _ =>
            new VerifiedReleaseActivationOperatorApprovalObservation(
                OperatorApproved: true,
                ApprovedAt: fixture.Now,
                ExpiresAt: fixture.Now.AddMinutes(5),
                Revoked: false);
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        Assert.True(report.Succeeded);
        Assert.True(report.OperatorApproved);
        Assert.True(report.Collection!.Evidence.OperatorApproved);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
    }

    [Fact]
    public async Task ApprovalExpiringDuringCollectionFailsReadinessClosed()
    {
        Fixture fixture = new();
        fixture.OperatorApprovalReader = _ =>
            new VerifiedReleaseActivationOperatorApprovalObservation(
                OperatorApproved: true,
                ApprovedAt: fixture.Now,
                ExpiresAt: fixture.Now.AddSeconds(1),
                Revoked: false);
        fixture.AfterStatusRead = count =>
        {
            if (count == 2)
            {
                fixture.Time.Advance(TimeSpan.FromSeconds(2));
            }
        };
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        Assert.True(report.Succeeded);
        Assert.False(report.OperatorApproved);
        Assert.False(report.Collection!.Evidence.OperatorApproved);
    }

    [Fact]
    public async Task MalformedOperatorApprovalObservationFailsClosed()
    {
        Fixture fixture = new();
        fixture.OperatorApprovalReader = _ =>
            new VerifiedReleaseActivationOperatorApprovalObservation(
                OperatorApproved: true,
                ApprovedAt: fixture.Now.AddMinutes(5),
                ExpiresAt: fixture.Now.AddMinutes(10),
                Revoked: false);
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed);
    }

    [Fact]
    public async Task NoOpMigrationAndServiceControlAreDerivedFromSignedPlan()
    {
        Fixture fixture = new(
            migrationKind: ReleaseMigrationKind.None,
            restartServices: false,
            restartHost: false);

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        Assert.True(report.Succeeded);
        Assert.True(report.MigrationReady);
        Assert.True(report.ServiceControlReady);
        Assert.False(report.ConfigurationBackupReady);
        Assert.False(report.HealthVerificationReady);
        Assert.False(report.RollbackReady);
        Assert.False(report.OperatorApproved);
    }

    [Fact]
    public async Task CollectedEvidenceFailsClosedAtLeaseAdmissionBoundary()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationReadinessEvidence evidence =
            (await fixture.CollectAsync()).Collection!.Evidence;

        VerifiedReleaseActivationReadinessReport report =
            fixture.Evaluator.Evaluate(fixture.PlanResult, evidence);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationReadinessFailureCode.TxLeaseAdmissionOpen,
            report.FailureCode);
    }

    [Fact]
    public async Task FutureReviewedPrerequisitesCanCompleteCollectedEvidence()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationReadinessEvidence evidence =
            (await fixture.CollectAsync()).Collection!.Evidence with
            {
                ReleaseStatus = fixture.Status with
                {
                    ActiveReleaseIdentity = "aethersdr-8.2.0"
                },
                TxLeaseAdmissionClosed = true,
                ConfigurationBackupReady = true,
                MigrationReady = true,
                ServiceControlReady = true,
                HealthVerificationReady = true,
                RollbackReady = true,
                OperatorApproved = true
            };

        VerifiedReleaseActivationReadinessReport report =
            fixture.Evaluator.Evaluate(fixture.PlanResult, evidence);

        Assert.True(report.Succeeded);
        Assert.NotNull(report.Readiness);
    }

    [Fact]
    public async Task SessionAndWatchdogEvidenceAreCapturedWithoutIdentityLeakage()
    {
        Fixture fixture = new();
        fixture.Sessions.Add(fixture.Session("session-secret", "radio-secret"));
        fixture.Watchdogs = fixture.ReadyWatchdogs(sessionCount: 1);
        fixture.SessionCapture = _ => fixture.ReadySessionEvidence(
            "session-secret",
            "radio-secret");
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.SessionCount);
        Assert.Equal(1, report.RadioCount);
        Assert.Equal(1, report.WatchdogSessionCount);
        Assert.Single(report.Collection!.Evidence.Sessions);

        string json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("session-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("radio-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("processId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("occupants", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeaseIdentitiesAndPathsAreRedactedFromPublicReport()
    {
        Fixture fixture = new();
        fixture.Leases.Add(fixture.Lease("lease-secret", "radio-secret"));

        string json = JsonSerializer.Serialize(await fixture.CollectAsync());

        Assert.DoesNotContain("lease-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("radio-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("availableReleaseIdentities", json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetainedEvidenceDefensivelyCopiesMutableSourceLists()
    {
        Fixture fixture = new();
        List<string> identities =
            ["aethersdr-8.1.0", "aethersdr-8.2.0"];
        fixture.Status = fixture.Status with
        {
            AvailableReleaseIdentities = identities
        };
        fixture.Leases.Add(fixture.Lease("lease-1", "radio-1"));
        fixture.Sessions.Add(fixture.Session("session-1", "radio-1"));
        fixture.SessionCapture = _ => fixture.ReadySessionEvidence(
            "session-1",
            "radio-1");
        fixture.Watchdogs = fixture.ReadyWatchdogs(sessionCount: 1);
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollection collection =
            (await fixture.CollectAsync()).Collection!;
        identities.Clear();
        fixture.Leases.Clear();
        fixture.Sessions.Clear();

        Assert.Equal(2,
            collection.Evidence.ReleaseStatus.AvailableReleaseIdentities.Count);
        Assert.Single(collection.Evidence.ActiveTxLeases);
        Assert.Single(collection.Evidence.Sessions);
    }

    [Fact]
    public async Task StatusReaderIsInvokedExactlyTwice()
    {
        Fixture fixture = new();

        await fixture.CollectAsync();

        Assert.Equal(2, fixture.StatusReadCount);
    }

    [Fact]
    public async Task ReleaseStatusDriftFailsClosed()
    {
        Fixture fixture = new();
        fixture.StatusReads.Enqueue(fixture.Status);
        fixture.StatusReads.Enqueue(fixture.Status with
        {
            ActiveReleaseIdentity = "aethersdr-8.2.0"
        });

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .ReleaseStatusDrift);
    }

    [Fact]
    public async Task ReleaseInventoryOrderDriftFailsClosed()
    {
        Fixture fixture = new();
        fixture.StatusReads.Enqueue(fixture.Status);
        fixture.StatusReads.Enqueue(fixture.Status with
        {
            AvailableReleaseIdentities =
                ["aethersdr-8.2.0", "aethersdr-8.1.0"]
        });

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .ReleaseStatusDrift);
    }

    [Fact]
    public async Task CollectionLongerThanFreshnessWindowFailsClosed()
    {
        Fixture fixture = new();
        fixture.AfterStatusRead = count =>
        {
            if (count == 2)
            {
                fixture.Time.Advance(
                    VerifiedReleaseActivationEvidenceCollector
                        .MaximumCollectionDuration + TimeSpan.FromMilliseconds(1));
            }
        };

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .CollectionWindowExceeded);
    }

    [Fact]
    public async Task BackwardClockMovementFailsClosed()
    {
        Fixture fixture = new();
        fixture.AfterStatusRead = count =>
        {
            if (count == 2)
            {
                fixture.Time.Advance(TimeSpan.FromSeconds(-1));
            }
        };

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .CollectionWindowExceeded);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("leases")]
    [InlineData("sessions")]
    [InlineData("watchdogs")]
    public async Task SourceFailuresReturnTypedFailure(string source)
    {
        Fixture fixture = new();
        fixture.ThrowingSource = source;
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceSourceUnavailable);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("leases")]
    [InlineData("sessions")]
    [InlineData("watchdogs")]
    public async Task NullSourceSnapshotsReturnMalformedFailure(string source)
    {
        Fixture fixture = new();
        fixture.NullSource = source;
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed);
    }

    [Fact]
    public async Task OversizedLeaseSnapshotFailsClosed()
    {
        Fixture fixture = new();
        for (int index = 0;
             index <= VerifiedReleaseActivationReadinessEvaluator.MaximumLeaseCount;
             index++)
        {
            fixture.Leases.Add(
                fixture.Lease($"lease-{index}", $"radio-{index}"));
        }

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed);
    }

    [Fact]
    public async Task DuplicateLeaseIdentifiersFailClosed()
    {
        Fixture fixture = new();
        fixture.Leases.Add(fixture.Lease("lease-1", "radio-1"));
        fixture.Leases.Add(fixture.Lease("lease-1", "radio-2"));

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed);
    }

    [Fact]
    public async Task MalformedLeaseIdentityFailsClosed()
    {
        Fixture fixture = new();
        fixture.Leases.Add(fixture.Lease(" lease-1 ", "radio-1"));

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed);
    }

    [Fact]
    public async Task OversizedSessionSnapshotFailsClosed()
    {
        Fixture fixture = new();
        for (int index = 0;
             index <= VerifiedReleaseActivationReadinessEvaluator.MaximumSessionCount;
             index++)
        {
            fixture.Sessions.Add(
                fixture.Session($"session-{index}", $"radio-{index}"));
        }

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed);
    }

    [Fact]
    public async Task DuplicateSessionIdentifiersFailClosed()
    {
        Fixture fixture = new();
        fixture.Sessions.Add(fixture.Session("session-1", "radio-1"));
        fixture.Sessions.Add(fixture.Session("session-1", "radio-2"));

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed);
    }

    [Fact]
    public async Task MalformedSessionIdentityFailsClosed()
    {
        Fixture fixture = new();
        fixture.Sessions.Add(fixture.Session(" session-1 ", "radio-1"));

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed);
    }

    [Fact]
    public async Task SessionCaptureFailureReturnsMalformedFailure()
    {
        Fixture fixture = new();
        fixture.Sessions.Add(fixture.Session("session-1", "radio-1"));
        fixture.SessionCapture = _ =>
            throw new InvalidOperationException("capture failed");
        fixture.RebuildCollector();

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed);
    }

    [Fact]
    public async Task MalformedWatchdogCountsFailClosed()
    {
        Fixture fixture = new();
        fixture.Watchdogs = fixture.ReadyWatchdogs(sessionCount: 0) with
        {
            ArmedProcessCount = 1
        };

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.CollectAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .EvidenceMalformed);
    }

    [Fact]
    public async Task FailedActivationPlanCannotCollectEvidence()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationPlanCompositionResult plan =
            fixture.PlanResult with
            {
                Succeeded = false,
                FailureCode =
                    VerifiedReleaseActivationPlanFailureCode.PublicationNotEligible
            };

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.Collector.CollectAsync(plan);

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .ActivationPlanNotEligible);
        Assert.Equal(0, fixture.StatusReadCount);
    }

    [Fact]
    public async Task SuccessfulSummaryWithoutInternalPlanFailsClosed()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationPlanCompositionResult plan =
            fixture.PlanResult with { Plan = null };

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.Collector.CollectAsync(plan);

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .ActivationPlanUnavailable);
    }

    [Fact]
    public async Task ActivationPlanSummaryMismatchFailsClosed()
    {
        Fixture fixture = new();
        VerifiedReleaseActivationPlanCompositionResult plan =
            fixture.PlanResult with { TargetVersion = "8.2.1" };

        VerifiedReleaseActivationEvidenceCollectionReport report =
            await fixture.Collector.CollectAsync(plan);

        AssertFailure(
            report,
            VerifiedReleaseActivationEvidenceCollectionFailureCode
                .ActivationPlanMismatch);
    }

    [Fact]
    public async Task PreCancelledCollectionThrowsWithoutReadingSources()
    {
        Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Collector.CollectAsync(
                fixture.PlanResult,
                cancellation.Token));
        Assert.Equal(0, fixture.StatusReadCount);
    }

    [Fact]
    public void ObservationOnlyLeaseSnapshotRetainsExpiredLeaseWithoutEvent()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));
        TxLeaseManager manager = new(time);
        List<TxLeaseChange> changes = [];
        manager.Changed += changes.Add;
        Assert.True(manager.TryAcquire(
            "radio-1",
            "session-1",
            "client-1",
            "user-1",
            "Operator",
            TxLeaseManager.MinimumLeaseDuration,
            out _,
            out _));
        changes.Clear();
        time.Advance(TxLeaseManager.MinimumLeaseDuration + TimeSpan.FromSeconds(1));

        IReadOnlyList<TxLease> observed = manager.GetObservationSnapshot();

        Assert.Single(observed);
        Assert.Empty(changes);
        Assert.Empty(manager.GetSnapshot());
        TxLeaseChange expired = Assert.Single(changes);
        Assert.Equal("expired", expired.Reason);
    }

    [Fact]
    public void ObservationOnlyLeaseSnapshotIsSortedAndDetached()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));
        TxLeaseManager manager = new(time);
        Assert.True(manager.TryAcquire(
            "radio-b", "session-b", "client-b", "user-b", "B",
            TxLeaseManager.MaximumLeaseDuration, out _, out _));
        Assert.True(manager.TryAcquire(
            "radio-a", "session-a", "client-a", "user-a", "A",
            TxLeaseManager.MaximumLeaseDuration, out _, out _));

        IReadOnlyList<TxLease> first = manager.GetObservationSnapshot();
        IReadOnlyList<TxLease> second = manager.GetObservationSnapshot();

        Assert.Equal(["RADIO-A", "RADIO-B"],
            first.Select(lease => lease.RadioId));
        Assert.NotSame(first, second);
    }

    private static void AssertFailure(
        VerifiedReleaseActivationEvidenceCollectionReport report,
        VerifiedReleaseActivationEvidenceCollectionFailureCode failureCode)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.Null(report.Collection);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
    }

    private sealed class Fixture
    {
        internal Fixture(
            ReleaseMigrationKind migrationKind = ReleaseMigrationKind.Required,
            bool restartServices = true,
            bool restartHost = true)
        {
            Now = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
            Time = new ManualTimeProvider(Now);
            Root = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-evidence-collection-{Guid.NewGuid():N}");
            string deploymentRoot = Path.Combine(Root, "deployment");
            string releaseRoot = Path.Combine(deploymentRoot, "releases");
            string targetPath = Path.Combine(releaseRoot, "aethersdr-8.2.0");
            VerifiedReleaseInstallationPackagePlan[] packages =
                CreatePackages(targetPath);
            bool migrationRequired =
                migrationKind == ReleaseMigrationKind.Required;
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
                targetConfigurationSchemaVersion: migrationRequired ? 2 : 1,
                migrationKind,
                migrationFromConfigurationSchemaVersion:
                    migrationRequired ? 1 : null,
                migrationToConfigurationSchemaVersion:
                    migrationRequired ? 2 : null,
                migrationIdentity:
                    migrationRequired ? "schema-1-to-2" : string.Empty,
                restartGatewayWeb: restartServices,
                restartBroker: restartServices,
                restartAetherRemoteAgent: restartServices,
                restartStationEngine: restartServices,
                restartHost,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Evidence collection test release.");
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
                InstallTransmitSupport: false,
                ReleaseDirectoryPresent: true,
                AvailableReleaseCount: 2,
                AvailableReleaseIdentities:
                    ["aethersdr-8.1.0", "aethersdr-8.2.0"],
                CurrentPointerPresent: true,
                ActiveReleaseIdentity: "aethersdr-8.1.0",
                RollbackCandidateKnown: false);
            Watchdogs = ReadyWatchdogs(sessionCount: 0);
            Evaluator = new VerifiedReleaseActivationReadinessEvaluator(Time);
            SessionCapture = diagnostics =>
                VerifiedReleaseActivationSessionEvidence.Capture(diagnostics);
            RebuildCollector();
        }

        internal DateTimeOffset Now { get; }
        internal ManualTimeProvider Time { get; }
        internal string Root { get; }
        internal VerifiedReleaseActivationPlanCompositionResult PlanResult { get; }
        internal VerifiedReleaseActivationReadinessEvaluator Evaluator { get; }
        internal ReleaseStatusReadResult Status { get; set; }
        internal Queue<ReleaseStatusReadResult> StatusReads { get; } = new();
        internal List<TxLease> Leases { get; } = [];
        internal List<RadioSessionDiagnostics> Sessions { get; } = [];
        internal StationTxIndependentWatchdogAggregate Watchdogs { get; set; }
        internal Action<int>? AfterStatusRead { get; set; }
        internal string ThrowingSource { get; set; } = string.Empty;
        internal string NullSource { get; set; } = string.Empty;
        internal int StatusReadCount { get; private set; }
        internal Func<RadioSessionDiagnostics,
            VerifiedReleaseActivationSessionEvidence>
        SessionCapture
        { get; set; }
        internal Func<
            VerifiedReleaseActivationPlan,
            VerifiedReleaseActivationConfigurationBackupObservation>
        ConfigurationBackupReader
        { get; set; } = _ =>
            new VerifiedReleaseActivationConfigurationBackupObservation(
                ConfigurationBackupReady: false,
                SourceDirectoryCount: 0,
                DirectoryCount: 0,
                FileCount: 0,
                BackupBytes: 0,
                CompletedAt: null,
                ReconciliationRequired: false);
        internal Func<
            VerifiedReleaseActivationPlan,
            VerifiedReleaseActivationServiceControlObservation>
        ServiceControlReader
        { get; set; } = plan =>
            new VerifiedReleaseActivationServiceControlObservation(
                ServiceControlReady:
                    plan.RestartServiceCount == 0 && !plan.RestartHost,
                ServiceControlRequired:
                    plan.RestartServiceCount > 0 || plan.RestartHost,
                PlannedStopActionCount: 0,
                ExecutedStopActionCount: 0,
                TopologyNoOpStopActionCount: 0,
                PlannedStartActionCount: 0,
                ExecutedStartActionCount: 0,
                TopologyNoOpStartActionCount: 0,
                CompletedAt:
                    plan.RestartServiceCount == 0 && !plan.RestartHost
                        ? DateTimeOffset.UnixEpoch
                        : null,
                ReconciliationRequired: false);
        internal Func<
            VerifiedReleaseActivationPlan,
            VerifiedReleaseActivationHealthVerificationObservation>
        HealthVerificationReader
        { get; set; } = _ =>
            new VerifiedReleaseActivationHealthVerificationObservation(
                HealthVerificationReady: false,
                HealthTargetCount: 0,
                VerifiedTargetCount: 0,
                UnitActivityCheckCount: 0,
                LoopbackHttpCheckCount: 0,
                FreshBrokerLinkCheckCount: 0,
                CompletedAt: null,
                ReconciliationRequired: false);
        internal Func<
            VerifiedReleaseActivationPlan,
            VerifiedReleaseActivationOperatorApprovalObservation>
        OperatorApprovalReader
        { get; set; } = _ =>
            new VerifiedReleaseActivationOperatorApprovalObservation(
                OperatorApproved: false,
                ApprovedAt: null,
                ExpiresAt: null,
                Revoked: false);
        internal VerifiedReleaseActivationEvidenceCollector Collector { get; private set; }
            = null!;

        internal void RebuildCollector()
        {
            Collector = new VerifiedReleaseActivationEvidenceCollector(
                ReadStatusAsync,
                _ =>
                {
                    if (ThrowingSource == "leases")
                    {
                        throw new InvalidOperationException("lease source failed");
                    }
                    return new VerifiedReleaseActivationLeaseQuiescenceObservation(
                        AdmissionClosed: false,
                        NullSource == "leases" ? null! : Leases);
                },
                () =>
                {
                    if (ThrowingSource == "sessions")
                    {
                        throw new InvalidOperationException("session source failed");
                    }
                    return NullSource == "sessions" ? null! : Sessions;
                },
                () =>
                {
                    if (ThrowingSource == "watchdogs")
                    {
                        throw new InvalidOperationException("watchdog source failed");
                    }
                    return NullSource == "watchdogs" ? null! : Watchdogs;
                },
                Time,
                SessionCapture,
                configurationBackupReader: ConfigurationBackupReader,
                healthVerificationReader: HealthVerificationReader,
                serviceControlReader: ServiceControlReader,
                operatorApprovalReader: OperatorApprovalReader);
        }

        internal Task<VerifiedReleaseActivationEvidenceCollectionReport>
            CollectAsync() => Collector.CollectAsync(PlanResult);

        internal VerifiedReleaseActivationServiceControlObservation
            ReadyServiceControlObservation()
        {
            VerifiedReleaseActivationPlan plan = PlanResult.Plan!;
            return new VerifiedReleaseActivationServiceControlObservation(
                ServiceControlReady: true,
                ServiceControlRequired: true,
                PlannedStopActionCount: plan.RestartServiceCount,
                ExecutedStopActionCount: plan.RestartServiceCount,
                TopologyNoOpStopActionCount: 0,
                PlannedStartActionCount: plan.RestartServiceCount,
                ExecutedStartActionCount: plan.RestartServiceCount,
                TopologyNoOpStartActionCount: 0,
                CompletedAt: Now,
                ReconciliationRequired: false);
        }

        internal VerifiedReleaseActivationHealthVerificationObservation
            ReadyHealthVerificationObservation() =>
            new(
                HealthVerificationReady: true,
                HealthTargetCount: 4,
                VerifiedTargetCount: 4,
                UnitActivityCheckCount: 3,
                LoopbackHttpCheckCount: 3,
                FreshBrokerLinkCheckCount: 0,
                CompletedAt: Now,
                ReconciliationRequired: false);

        internal TxLease Lease(string leaseId, string radioId) =>
            new(
                leaseId,
                radioId,
                $"session-{radioId}",
                $"client-{radioId}",
                $"user-{radioId}",
                "Operator",
                Now,
                Now,
                Now.AddSeconds(10));

        internal RadioSessionDiagnostics Session(
            string sessionId,
            string radioId) =>
            new(
                sessionId,
                "gui-client",
                "user-1",
                "Operator",
                radioId,
                "127.0.0.1",
                4992,
                Now.AddMinutes(-1),
                Now,
                BrowserConnections: 0,
                new RadioBrowserReconnectDiagnostics(
                    0, 0, 0, 0, null, null, null),
                LowBandwidth: false,
                SnapshotVersion: 1,
                Connected: true,
                ConnectionState: "connected",
                ConnectionError: null,
                RadioModel: "FLEX",
                Serial: "serial",
                new RadioTransportDiagnostics(
                    "test",
                    1,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    []),
                WebClients: [],
                Panadapters: [],
                Slices: [],
                new RadioTxOccupancySnapshot(
                    radioId,
                    RadioTxOccupancyState.Idle,
                    Now.AddSeconds(-1),
                    Now.AddSeconds(7),
                    Occupants: [],
                    LocalPttOwners: []),
                new RadioTuneTimingDiagnostics(
                    "idle", string.Empty, -1, 0, null, null, null, null),
                TxLifecycle: null);

        internal VerifiedReleaseActivationSessionEvidence ReadySessionEvidence(
            string sessionId,
            string radioId) =>
            new(
                sessionId,
                radioId,
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
                    radioId,
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

        private Task<ReleaseStatusReadResult> ReadStatusAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowingSource == "status")
            {
                throw new InvalidOperationException("status source failed");
            }
            StatusReadCount++;
            ReleaseStatusReadResult result = NullSource == "status"
                ? null!
                : StatusReads.Count > 0
                    ? StatusReads.Dequeue()
                    : Status;
            AfterStatusRead?.Invoke(StatusReadCount);
            return Task.FromResult(result);
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
