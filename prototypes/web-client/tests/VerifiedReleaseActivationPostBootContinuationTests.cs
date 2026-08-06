using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleaseActivationPostBootContinuationTests
{
    [Fact]
    public void DiagnosticsRegisterContinuationWithoutAuthorityReconstruction()
    {
        using Fixture fixture = new(executionEnabled: false);

        VerifiedReleaseActivationPostBootContinuationDiagnostics snapshot =
            fixture.Service.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.False(snapshot.ExecutionEnabled);
        Assert.True(snapshot.OwnerOnlyMarkerReadRegistered);
        Assert.True(snapshot.StrictMarkerSchemaRegistered);
        Assert.True(snapshot.MarkerFreshnessRegistered);
        Assert.True(snapshot.ReleaseStatusDoubleReadRegistered);
        Assert.True(snapshot.SetupStateDoubleReadRegistered);
        Assert.True(snapshot.ExactActiveReleaseBindingRegistered);
        Assert.True(snapshot.FixedUnitActivityRegistered);
        Assert.True(snapshot.LoopbackHealthRegistered);
        Assert.True(snapshot.FreshBrokerLinkRegistered);
        Assert.True(snapshot.DurableTerminalResultRegistered);
        Assert.True(snapshot.IdempotentMarkerConsumptionRegistered);
        Assert.False(snapshot.ApprovalAuthorityReconstructionRegistered);
        Assert.False(snapshot.RollbackAuthorityReconstructionRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.ServiceControlRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public async Task MissingMarkerIsSafeNoOp()
    {
        using Fixture fixture = new();

        VerifiedReleaseActivationPostBootContinuationReport report =
            await fixture.Service.ContinueAsync();

        Assert.True(report.Succeeded);
        Assert.False(report.MarkerPresent);
        Assert.False(report.HealthVerified);
        Assert.False(report.MarkerConsumed);
        Assert.Equal(0, fixture.Runtime.UnitAttempts);
        Assert.Equal(0, fixture.Runtime.HttpAttempts);
        AssertNoAuthority(report);
    }

    [Fact]
    public async Task MarkerFailsClosedWhenExecutionIsDisabled()
    {
        using Fixture fixture = new(executionEnabled: false);
        fixture.WriteMarker();

        VerifiedReleaseActivationPostBootContinuationReport report =
            await fixture.Service.ContinueAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPostBootContinuationFailureCode
                .ExecutionDisabled,
            report.FailureCode);
        Assert.True(report.MarkerPresent);
        Assert.True(report.ReconciliationRequired);
        Assert.True(File.Exists(fixture.MarkerPath));
        Assert.False(File.Exists(fixture.ResultPath));
        AssertNoAuthority(report);
    }

    [Fact]
    public async Task HealthyExactMarkerWritesTerminalResultAndIsConsumed()
    {
        using Fixture fixture = new();
        fixture.WriteMarker();

        VerifiedReleaseActivationPostBootContinuationReport report =
            await fixture.Service.ContinueAsync();

        Assert.True(report.Succeeded, report.Message);
        Assert.True(report.MarkerPresent);
        Assert.True(report.TargetActiveBeforeVerification);
        Assert.True(report.TargetActiveAfterVerification);
        Assert.True(report.SetupStable);
        Assert.Equal(3, report.UnitActivityAttemptCount);
        Assert.Equal(3, report.LoopbackHttpAttemptCount);
        Assert.Equal(0, report.BrokerLinkObservationCount);
        Assert.True(report.HealthVerified);
        Assert.True(report.TerminalResultWritten);
        Assert.True(report.MarkerConsumed);
        Assert.False(report.ReconciliationRequired);
        Assert.False(File.Exists(fixture.MarkerPath));
        Assert.True(File.Exists(fixture.ResultPath));
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(fixture.ResultPath));
        ReleaseUpdateJournalDocument journal = fixture.ReadJournal();
        Assert.Equal(ReleaseUpdateTransactionPhase.Completed, journal.Phase);
        Assert.False(journal.RestartPending);
        Assert.False(journal.ReconciliationRequired);
        Assert.Equal(Fixture.TransactionId, journal.TransactionId);
        AssertNoAuthority(report);

        VerifiedReleaseActivationPostBootContinuationStateDiagnostics state =
            fixture.Service.State;
        Assert.True(state.ContinuationCompleted);
        Assert.True(state.HealthVerified);
        Assert.True(state.MarkerConsumed);
        Assert.False(state.ReconciliationRequired);
        Assert.False(state.ApprovalAuthorityReconstructed);
        Assert.False(state.RollbackAuthorityReconstructed);
        Assert.False(state.CurrentPointerMutationPerformed);
        Assert.False(state.RadioCommandIssued);
        Assert.False(state.TxActionPerformed);
    }

    [Fact]
    public async Task SuccessfulContinuationDoesNotRunAgainAfterMarkerConsumption()
    {
        using Fixture fixture = new();
        fixture.WriteMarker();
        VerifiedReleaseActivationPostBootContinuationReport first =
            await fixture.Service.ContinueAsync();
        int unitAttempts = fixture.Runtime.UnitAttempts;
        int httpAttempts = fixture.Runtime.HttpAttempts;

        VerifiedReleaseActivationPostBootContinuationReport second =
            await fixture.Service.ContinueAsync();

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.False(second.MarkerPresent);
        Assert.Equal(unitAttempts, fixture.Runtime.UnitAttempts);
        Assert.Equal(httpAttempts, fixture.Runtime.HttpAttempts);
        AssertNoAuthority(second);
    }

    [Fact]
    public async Task UnhealthyServiceWritesReconciliationAndRetainsMarker()
    {
        using Fixture fixture = new();
        fixture.Runtime.UnitResult =
            HealthProbeAttemptResult.Reject("unit unavailable");
        fixture.WriteMarker();

        VerifiedReleaseActivationPostBootContinuationReport report =
            await fixture.Service.ContinueAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPostBootContinuationFailureCode
                .UnitActivityUnavailable,
            report.FailureCode);
        Assert.True(report.TerminalResultWritten);
        Assert.True(report.ReconciliationRequired);
        Assert.False(report.HealthVerified);
        Assert.True(File.Exists(fixture.MarkerPath));
        Assert.True(File.Exists(fixture.ResultPath));
        Assert.Equal(1, fixture.Runtime.UnitAttempts);
        Assert.Equal(0, fixture.Runtime.HttpAttempts);
        ReleaseUpdateJournalDocument journal = fixture.ReadJournal();
        Assert.Equal(
            ReleaseUpdateTransactionPhase.ReconciliationRequired,
            journal.Phase);
        Assert.False(journal.RestartPending);
        Assert.True(journal.ReconciliationRequired);
        AssertNoAuthority(report);
    }

    [Fact]
    public async Task ReconciliationResultPreventsAutomaticProbeRetry()
    {
        using Fixture fixture = new();
        fixture.Runtime.UnitResult =
            HealthProbeAttemptResult.Reject("unit unavailable");
        fixture.WriteMarker();
        VerifiedReleaseActivationPostBootContinuationReport first =
            await fixture.Service.ContinueAsync();
        int unitAttempts = fixture.Runtime.UnitAttempts;
        fixture.Runtime.UnitResult = HealthProbeAttemptResult.Success();

        VerifiedReleaseActivationPostBootContinuationReport second =
            await fixture.Service.ContinueAsync();

        Assert.False(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPostBootContinuationFailureCode
                .PriorReconciliationRequired,
            second.FailureCode);
        Assert.True(second.ExistingTerminalResultRead);
        Assert.Equal(unitAttempts, fixture.Runtime.UnitAttempts);
        Assert.Equal(0, second.UnitActivityAttemptCount);
        Assert.True(File.Exists(fixture.MarkerPath));
        ReleaseUpdateJournalDocument journal = fixture.ReadJournal();
        Assert.Equal(
            ReleaseUpdateTransactionPhase.ReconciliationRequired,
            journal.Phase);
        Assert.False(journal.RestartPending);
        Assert.True(journal.ReconciliationRequired);
        AssertNoAuthority(second);
    }

    [Fact]
    public async Task MarkerMustMatchExactPendingJournalBeforeAnyProbe()
    {
        using Fixture fixture = new();
        fixture.WriteMarker();
        fixture.WriteJournal(
            fixture.ReadJournal() with
            {
                TransactionId = "fedcba9876543210fedcba9876543210"
            });

        VerifiedReleaseActivationPostBootContinuationReport report =
            await fixture.Service.ContinueAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPostBootContinuationFailureCode
                .TransactionJournalUpdateFailed,
            report.FailureCode);
        Assert.True(report.ReconciliationRequired);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Equal(0, fixture.SetupReads);
        Assert.Equal(0, fixture.Runtime.UnitAttempts);
        Assert.Equal(0, fixture.Runtime.HttpAttempts);
        Assert.True(File.Exists(fixture.MarkerPath));
        Assert.False(File.Exists(fixture.ResultPath));
        AssertNoAuthority(report);
    }

    [Fact]
    public async Task WrongActiveReleaseRequiresReconciliationBeforeProbes()
    {
        using Fixture fixture = new();
        fixture.StatusFactory = () => ReleaseStatusReadResult.Success(
            fixture.Setup,
            releaseDirectoryPresent: true,
            [Fixture.InstalledIdentity, Fixture.TargetIdentity],
            currentPointerPresent: true,
            Fixture.InstalledIdentity);
        fixture.WriteMarker();

        VerifiedReleaseActivationPostBootContinuationReport report =
            await fixture.Service.ContinueAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPostBootContinuationFailureCode
                .StatusMismatch,
            report.FailureCode);
        Assert.True(report.TerminalResultWritten);
        Assert.True(report.ReconciliationRequired);
        Assert.Equal(0, fixture.Runtime.UnitAttempts);
        Assert.Equal(0, fixture.Runtime.HttpAttempts);
        Assert.True(File.Exists(fixture.MarkerPath));
        AssertNoAuthority(report);
    }

    [Fact]
    public async Task StaleMarkerIsRejectedWithoutWritingResult()
    {
        using Fixture fixture = new();
        fixture.WriteMarker(
            fixture.Time.GetUtcNow() -
                VerifiedReleaseActivationPostBootContinuationService
                    .MaximumMarkerAge -
                TimeSpan.FromSeconds(1));

        VerifiedReleaseActivationPostBootContinuationReport report =
            await fixture.Service.ContinueAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPostBootContinuationFailureCode.MarkerStale,
            report.FailureCode);
        Assert.True(report.ReconciliationRequired);
        Assert.True(File.Exists(fixture.MarkerPath));
        Assert.False(File.Exists(fixture.ResultPath));
        Assert.Equal(0, fixture.Runtime.UnitAttempts);
        AssertNoAuthority(report);
    }

    [Fact]
    public async Task AlteredMarkerIdentityIsRejectedBeforeStatusRead()
    {
        using Fixture fixture = new();
        fixture.WriteMarker(targetIdentity: "not-a-release-identity");

        VerifiedReleaseActivationPostBootContinuationReport report =
            await fixture.Service.ContinueAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPostBootContinuationFailureCode.MarkerInvalid,
            report.FailureCode);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Equal(0, fixture.SetupReads);
        Assert.Equal(0, fixture.Runtime.UnitAttempts);
        Assert.True(File.Exists(fixture.MarkerPath));
        AssertNoAuthority(report);
    }

    [Fact]
    public async Task WritableMarkerIsRejectedBeforeStatusRead()
    {
        using Fixture fixture = new();
        fixture.WriteMarker();
        File.SetUnixFileMode(
            fixture.MarkerPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        VerifiedReleaseActivationPostBootContinuationReport report =
            await fixture.Service.ContinueAsync();

        Assert.False(report.Succeeded);
        Assert.Contains(
            report.FailureCode,
            new[]
            {
                VerifiedReleaseActivationPostBootContinuationFailureCode
                    .MarkerInvalid,
                VerifiedReleaseActivationPostBootContinuationFailureCode
                    .MarkerUnsafe
            });
        Assert.True(report.ReconciliationRequired);
        Assert.Equal(0, fixture.StatusReads);
        Assert.Equal(0, fixture.Runtime.UnitAttempts);
        AssertNoAuthority(report);
    }

    [Fact]
    public async Task HybridTopologyRequiresFreshExactBrokerLink()
    {
        using Fixture fixture = new(
            topology: InstallationTopologyKind.HybridGateway,
            expectedStationId: "station-one");
        fixture.WriteMarker();

        VerifiedReleaseActivationPostBootContinuationReport report =
            await fixture.Service.ContinueAsync();

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal(3, report.UnitActivityAttemptCount);
        Assert.Equal(3, report.LoopbackHttpAttemptCount);
        Assert.Equal(1, report.BrokerLinkObservationCount);
        Assert.Equal(1, fixture.RemoteSnapshotReads);
        AssertNoAuthority(report);
    }

    private static void AssertNoAuthority(
        VerifiedReleaseActivationPostBootContinuationReport report)
    {
        Assert.False(report.ApprovalAuthorityReconstructed);
        Assert.False(report.RollbackAuthorityReconstructed);
        Assert.False(report.CurrentPointerMutationPerformed);
        Assert.False(report.RadioCommandIssued);
        Assert.False(report.TxActionPerformed);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string TransactionId =
            "0123456789abcdef0123456789abcdef";
        internal const string InstalledIdentity = "aethersdr-8.1.0";
        internal const string TargetIdentity = "aethersdr-8.2.0";

        internal Fixture(
            bool executionEnabled = true,
            InstallationTopologyKind topology =
                InstallationTopologyKind.PersonalSingleStation,
            string expectedStationId = "")
        {
            Time = new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 6, 9, 0, 0, TimeSpan.Zero));
            Root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"post-boot-continuation-{Guid.NewGuid():N}"));
            Paths = new InstallationPaths(
                Path.Combine(Root, "config"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(Root, "deployment", "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            Setup = new InstallationSetupState
            {
                SchemaVersion = InstallationSetupState.CurrentSchemaVersion,
                Revision = 7,
                CreatedAt = Time.GetUtcNow().AddMinutes(-20),
                UpdatedAt = Time.GetUtcNow().AddMinutes(-2),
                LastCompletedStep = InstallationSetupStep.Administrator,
                Lock = new InstallationSetupLock
                {
                    Mode = InstallationSetupLockMode.Complete,
                    ClaimedAt = Time.GetUtcNow().AddMinutes(-19),
                    CompletedAt = Time.GetUtcNow().AddMinutes(-2)
                },
                Topology = topology,
                CanonicalPublicUrl = "https://radio.example.org",
                Paths = Paths,
                UpdateChannel = InstallationUpdateChannel.Stable,
                PinnedRelease = string.Empty,
                InstallTransmitSupport = false
            };
            StatusFactory = () => ReleaseStatusReadResult.Success(
                Setup,
                releaseDirectoryPresent: true,
                [InstalledIdentity, TargetIdentity],
                currentPointerPresent: true,
                TargetIdentity);
            SetupFactory = () => Setup;
            ExpectedStationId = expectedStationId;
            Runtime = new FakeRuntime();
            Service = new VerifiedReleaseActivationPostBootContinuationService(
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
                    return CreateRemoteSnapshot();
                },
                Runtime,
                new ReleaseActivationHostRestartSettings
                {
                    ExecutionEnabled = executionEnabled
                },
                new ReleaseActivationHealthVerificationSettings
                {
                    ExecutionEnabled = executionEnabled,
                    ExpectedStationId =
                        executionEnabled ? expectedStationId : string.Empty
                },
                Paths,
                Time,
                (duration, _) =>
                {
                    Time.Advance(duration);
                    return Task.CompletedTask;
                });
            HostRestartContinuationPaths storage =
                HostRestartContinuationStorage.Resolve(Paths);
            MarkerPath = storage.Marker;
            ResultPath = storage.Result;
        }

        internal string Root { get; }
        internal InstallationPaths Paths { get; }
        internal InstallationSetupState Setup { get; }
        internal ManualTimeProvider Time { get; }
        internal FakeRuntime Runtime { get; }
        internal VerifiedReleaseActivationPostBootContinuationService Service
        {
            get;
        }
        internal string MarkerPath { get; }
        internal string ResultPath { get; }
        internal string ExpectedStationId { get; }
        internal Func<ReleaseStatusReadResult> StatusFactory { get; set; }
        internal Func<InstallationSetupState> SetupFactory { get; set; }
        internal int StatusReads { get; private set; }
        internal int SetupReads { get; private set; }
        internal int RemoteSnapshotReads { get; private set; }

        internal void WriteMarker(
            DateTimeOffset? requestedAt = null,
            string targetIdentity = TargetIdentity)
        {
            HostRestartContinuationPaths storage =
                HostRestartContinuationStorage.Resolve(Paths);
            Directory.CreateDirectory(storage.Root);
            File.SetUnixFileMode(
                storage.Root,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            string current = Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(Paths.ReleaseDirectory)!,
                    "current"));
            HostRestartMarker marker = new(
                HostRestartContinuationStorage.MarkerSchemaVersion,
                TransactionId,
                Setup.Revision,
                InstalledIdentity,
                targetIdentity,
                Paths.ReleaseDirectory,
                current,
                Setup.UpdateChannel,
                Setup.PinnedRelease,
                Setup.InstallTransmitSupport,
                requestedAt ?? Time.GetUtcNow().AddMinutes(-1),
                PostBootVerificationRequired: true);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                marker,
                HostRestartContinuationStorage.JsonOptions);
            File.WriteAllBytes(storage.Marker, bytes);
            File.SetUnixFileMode(storage.Marker, UnixFileMode.UserRead);

            ReleaseUpdateJournalDocument journal = new(
                ReleaseUpdateTransactionJournal.SchemaVersion,
                TransactionId,
                ReleaseUpdateTransactionPhase.RestartPending,
                Setup.Revision,
                InstalledIdentity,
                targetIdentity,
                "8.2.0",
                Time.GetUtcNow().AddSeconds(-30),
                CurrentPointerChanged: true,
                RollbackPerformed: false,
                RestartPending: true,
                ReconciliationRequired: false);
            WriteJournal(journal);
        }

        internal ReleaseUpdateJournalDocument ReadJournal() =>
            Assert.IsType<ReleaseUpdateJournalDocument>(
                new ReleaseUpdateTransactionJournal(Paths).Read());

        internal void WriteJournal(ReleaseUpdateJournalDocument journal)
        {
            JsonSerializerOptions options = new()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                Converters =
                {
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                }
            };
            HostRestartContinuationPaths storage =
                HostRestartContinuationStorage.Resolve(Paths);
            string path = Path.Combine(storage.Root, "active.json");
            if (File.Exists(path))
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.WriteAllBytes(
                path,
                JsonSerializer.SerializeToUtf8Bytes(journal, options));
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
        }

        private RemoteStationAdministrationSnapshot CreateRemoteSnapshot()
        {
            DateTimeOffset now = Time.GetUtcNow();
            IReadOnlyList<RemoteStationAdministrationEntry> stations =
                string.IsNullOrEmpty(ExpectedStationId)
                    ? []
                    :
                    [
                        new RemoteStationAdministrationEntry(
                            ExpectedStationId,
                            "instance-one",
                            "online",
                            "8.2.0",
                            now.AddMinutes(-2),
                            now,
                            HeartbeatSequence: 2,
                            InventorySequence: 2,
                            ConnectionCount: 1,
                            LastDisconnectedAt: null,
                            LastDisconnectReason: null,
                            LastRecoveredAt: null,
                            LastRecoveryMilliseconds: null,
                            Capabilities: ["release-service-control-v1"],
                            Radios: [],
                            ReceiveSessions: [])
                    ];
            return new RemoteStationAdministrationSnapshot(
                Enabled: !string.IsNullOrEmpty(ExpectedStationId),
                BrokerReachable: !string.IsNullOrEmpty(ExpectedStationId),
                RefreshedAt: now,
                Error: null,
                stations,
                Credentials: []);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FakeRuntime :
        IVerifiedReleaseActivationHealthProbeRuntime
    {
        internal HealthProbeAttemptResult UnitResult { get; set; } =
            HealthProbeAttemptResult.Success();
        internal HealthProbeAttemptResult HttpResult { get; set; } =
            HealthProbeAttemptResult.Success();
        internal int UnitAttempts { get; private set; }
        internal int HttpAttempts { get; private set; }

        public Task<HealthProbeAttemptResult> CheckUnitActiveAsync(
            string unitIdentity,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            UnitAttempts++;
            return Task.FromResult(UnitResult);
        }

        public Task<HealthProbeAttemptResult> CheckLoopbackHealthAsync(
            VerifiedReleaseActivationHealthVerificationTarget target,
            string canonicalGatewayAuthority,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            HttpAttempts++;
            return Task.FromResult(HttpResult);
        }
    }

    internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        internal void Advance(TimeSpan duration) => m_now += duration;
    }
}
