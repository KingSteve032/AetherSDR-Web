using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class StationTxCommandSessionCompositionTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 1, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SubmissionSurfaceRemainsInternalAndAuthorityCannotBeSupplied()
    {
        Assert.False(typeof(StationTxCommandSessionComposition).IsPublic);
        Assert.False(typeof(StationTxCommandSessionCompositionRequest).IsPublic);
        Assert.False(typeof(StationTxCommandAuthorityResolution).IsPublic);
        Assert.False(typeof(StationTxCommandSessionCompositionResult).IsPublic);

        string[] requestProperties =
            typeof(StationTxCommandSessionCompositionRequest)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        Assert.Equal(
            [
                "ConnectionClientId",
                "Intent",
                "ObservedAt",
                "Sequence"
            ],
            requestProperties);
        System.Reflection.MethodInfo[] lifecycleMethods =
            typeof(StationTxProductionLifecycle).GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(
            lifecycleMethods,
            method => method.Name == "SubmitValidatedBrowserTxIntentAsync");
        Assert.DoesNotContain(
            lifecycleMethods.Where(method => method.IsPublic),
            method => method.Name.Contains("Submit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LifecycleRegistersCompositionWithoutCoordinatorOrIngress()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time);

        StationTxCommandSessionCompositionDiagnostics composition =
            lifecycle.Snapshot.StationCommandSessionComposition;

        Assert.True(composition.Registered);
        Assert.False(composition.CoordinatorAttached);
        Assert.True(composition.BoundaryAttached);
        Assert.False(composition.SubmissionEnabled);
        Assert.False(composition.AuthoritySnapshotAvailable);
        Assert.False(composition.SubmissionAvailable);
        Assert.Equal("coordinator-unattached", composition.Reason);
        Assert.Equal(0, composition.AttemptCount);
    }

    [Fact]
    public async Task ExactAuthorityIsDerivedFromTheOwningLifecycle()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new();
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);
        TxLease lease = await EstablishAuthorityAsync(
            lifecycle,
            leases,
            duration: TimeSpan.FromSeconds(15));
        StationTxLifecycleDiagnostics before = lifecycle.Snapshot;
        BrowserTxIntent intent = Intent(
            BrowserTxIntentKind.Mox,
            enabled: true,
            intentId: "intent-000000000000000000000000000001");

        StationTxCommandSessionCompositionResult result =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence: 7,
                intent,
                time.GetUtcNow());

        Assert.False(result.Success);
        Assert.Equal("recorded", result.Code);
        Assert.Equal(1, submitter.CallCount);
        StationTxCommandEnvelopeSubmissionRequest submission =
            Assert.IsType<StationTxCommandEnvelopeSubmissionRequest>(
                submitter.LastRequest);
        Assert.Equal(intent.IntentId, submission.Intent.IntentId);
        Assert.Equal(7, submission.Intent.Sequence);
        Assert.Equal(BrowserTxIntentKind.Mox, submission.Intent.Kind);
        Assert.True(submission.Intent.Enabled);
        Assert.Equal(time.GetUtcNow(), submission.Intent.ObservedAt);

        StationTxCommandAuthority authority = submission.Authority;
        Assert.Equal("gateway-a", authority.StationId);
        Assert.Equal("RADIO-A", authority.RadioId);
        Assert.Equal("session-a", authority.SessionId);
        Assert.Equal("browser-a", before.BrowserClientId);
        Assert.Equal("connection-a", authority.BrowserClientId);
        Assert.Equal(lease.LeaseId, authority.LeaseId);
        Assert.Equal(lease.ExpiresAt, authority.LeaseExpiresAt);
        Assert.Equal("gateway-a", authority.GatewayInstanceId);
        Assert.Equal(before.EngineInstanceId, authority.EngineInstanceId);
        Assert.Equal(0x1234abcdu, authority.ClientHandle);
        Assert.True(authority.Authenticated);
        Assert.True(authority.BrowserFresh);
        Assert.True(authority.EngineFresh);
        Assert.True(authority.GatewayFresh);
        Assert.True(authority.AuthorityFresh);
        Assert.Equal("RADIO-A", authority.Occupancy.RadioId);
        Assert.Equal(StationTxSafetyState.Disarmed, authority.Safety.State);
        Assert.NotNull(submitter.LastBoundary);
        Assert.False(submitter.LastBoundary!.Capabilities.BoundaryEnabled);

        StationTxCommandSessionCompositionDiagnostics composition =
            result.Diagnostics;
        Assert.True(composition.CoordinatorAttached);
        Assert.True(composition.AuthoritySnapshotAvailable);
        Assert.False(composition.SubmissionAvailable);
        Assert.Equal(1, composition.AttemptCount);
        Assert.Equal(1, composition.ForwardedCount);
        Assert.Equal(0, composition.AcceptedCount);
        Assert.Equal(1, composition.RejectedCount);
        Assert.Equal("recorded", composition.LastOutcome);
    }

    [Fact]
    public async Task DisabledCoordinatorRejectsBeforeSigning()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        ThrowingSigner signer = new();
        AlwaysAvailableVerifier verifier = new();
        StationTxCommandEnvelopeCoordinator coordinator = new(
            new StationTxCommandEnvelopeCoordinatorSettings
            {
                SubmissionEnabled = false
            },
            signer,
            verifier,
            NullLogger<StationTxCommandEnvelopeCoordinator>.Instance,
            time);
        await using StationTxProductionLifecycle lifecycle =
            Create(
                leases,
                occupancy,
                time,
                verifier,
                coordinator);
        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            duration: TimeSpan.FromSeconds(15));

        StationTxCommandSessionCompositionResult result =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence: 1,
                Intent(BrowserTxIntentKind.Ptt, enabled: false),
                time.GetUtcNow());

        Assert.False(result.Success);
        Assert.Equal("coordinator_disabled", result.Code);
        Assert.Equal(0, signer.CreateCount);
        Assert.Equal(1, result.Diagnostics.AttemptCount);
        Assert.Equal(1, result.Diagnostics.ForwardedCount);
        Assert.Equal(1, result.Diagnostics.RejectedCount);
        Assert.Equal("coordinator_disabled", result.Diagnostics.LastOutcome);
    }

    [Fact]
    public async Task MismatchedConnectionCannotResolveAuthority()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new();
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);
        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            duration: TimeSpan.FromSeconds(15));

        StationTxCommandSessionCompositionResult result =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-b",
                sequence: 1,
                Intent(BrowserTxIntentKind.Mox, enabled: true),
                time.GetUtcNow());

        Assert.False(result.Success);
        Assert.Equal("connection-mismatch", result.Code);
        Assert.Equal(0, submitter.CallCount);
        Assert.Equal(0, result.Diagnostics.ForwardedCount);
    }

    [Fact]
    public async Task ReplacedConnectionCannotReuseThePreviousLease()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new();
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);
        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            duration: TimeSpan.FromSeconds(15));
        lifecycle.ObserveBrowserConnection(
            "connection-b",
            connected: true,
            authenticated: true);
        await lifecycle.FlushAsync();

        StationTxCommandSessionCompositionResult oldConnection =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence: 1,
                Intent(BrowserTxIntentKind.Mox, enabled: true),
                time.GetUtcNow());
        StationTxCommandSessionCompositionResult newConnection =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-b",
                sequence: 2,
                Intent(BrowserTxIntentKind.Mox, enabled: true),
                time.GetUtcNow());

        Assert.Equal("connection-mismatch", oldConnection.Code);
        Assert.Equal("lease-mismatch", newConnection.Code);
        Assert.Equal(0, submitter.CallCount);
    }

    [Fact]
    public async Task MissingLeaseFailsBeforeCoordinator()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new();
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);
        lifecycle.ObserveBrowserConnection(
            "connection-a",
            connected: true,
            authenticated: true);
        lifecycle.ObserveEngineConnection(
            connected: true,
            clientHandle: 0x1234abcd);
        await lifecycle.FlushAsync();

        StationTxCommandSessionCompositionResult result =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence: 1,
                Intent(BrowserTxIntentKind.Mox, enabled: true),
                time.GetUtcNow());

        Assert.False(result.Success);
        Assert.Equal("authority-no-active-lease", result.Code);
        Assert.Equal(0, submitter.CallCount);
    }

    [Fact]
    public async Task StaleBrowserObservationFailsBeforeCoordinator()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new();
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);
        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            duration: TimeSpan.FromSeconds(15));
        time.Advance(StationTxProductionLifecycle.BrowserFreshnessTimeout +
            TimeSpan.FromMilliseconds(1));

        StationTxCommandSessionCompositionResult result =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence: 1,
                Intent(BrowserTxIntentKind.Mox, enabled: false),
                time.GetUtcNow());

        Assert.False(result.Success);
        Assert.Equal("authority-browser-stale", result.Code);
        Assert.Equal(0, submitter.CallCount);
    }

    [Fact]
    public async Task ExpiredLeaseFailsBeforeCoordinator()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new();
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);
        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            duration: TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(3));

        StationTxCommandSessionCompositionResult result =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence: 1,
                Intent(BrowserTxIntentKind.Ptt, enabled: false),
                time.GetUtcNow());

        Assert.False(result.Success);
        Assert.Equal("lease-unavailable", result.Code);
        Assert.Equal(0, submitter.CallCount);
    }

    [Theory]
    [InlineData((int)BrowserTxIntentKind.Tune)]
    [InlineData((int)BrowserTxIntentKind.Microphone)]
    [InlineData((int)BrowserTxIntentKind.Cw)]
    public async Task NonSetTransmitIntentNeverReachesCoordinator(int kindValue)
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new();
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);
        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            duration: TimeSpan.FromSeconds(15));

        StationTxCommandSessionCompositionResult result =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence: 1,
                Intent((BrowserTxIntentKind)kindValue, enabled: true),
                time.GetUtcNow());

        Assert.False(result.Success);
        Assert.Equal("unsupported_intent", result.Code);
        Assert.Equal(0, submitter.CallCount);
    }

    [Fact]
    public async Task MissingBooleanValueNeverReachesCoordinator()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new();
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);
        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            duration: TimeSpan.FromSeconds(15));

        StationTxCommandSessionCompositionResult result =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence: 1,
                Intent(BrowserTxIntentKind.Mox, enabled: null),
                time.GetUtcNow());

        Assert.Equal("missing_intent_value", result.Code);
        Assert.Equal(0, submitter.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RadioBrowserTxProtocol.MaximumSafeInteger + 1)]
    public async Task InvalidSequenceNeverReachesCoordinator(long sequence)
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new();
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);

        StationTxCommandSessionCompositionResult result =
            await lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence,
                Intent(BrowserTxIntentKind.Mox, enabled: true),
                time.GetUtcNow());

        Assert.Equal("invalid_intent_sequence", result.Code);
        Assert.Equal(0, submitter.CallCount);
    }

    [Fact]
    public async Task PreCancelledRequestIsNotCountedOrForwarded()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new();
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence: 1,
                Intent(BrowserTxIntentKind.Mox, enabled: true),
                time.GetUtcNow(),
                cancellation.Token));

        Assert.Equal(0, submitter.CallCount);
        Assert.Equal(
            0,
            lifecycle.Snapshot.StationCommandSessionComposition.AttemptCount);
    }

    [Fact]
    public async Task SubmitterExceptionIsRecordedAndNotRetried()
    {
        ManualTimeProvider time = new(Start);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        RecordingSubmitter submitter = new()
        {
            Exception = new InvalidOperationException("test-only")
        };
        await using StationTxProductionLifecycle lifecycle =
            Create(leases, occupancy, time, submitter: submitter);
        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            duration: TimeSpan.FromSeconds(15));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.SubmitValidatedBrowserTxIntentAsync(
                "connection-a",
                sequence: 1,
                Intent(BrowserTxIntentKind.Mox, enabled: true),
                time.GetUtcNow()));

        Assert.Equal(1, submitter.CallCount);
        StationTxCommandSessionCompositionDiagnostics diagnostics =
            lifecycle.Snapshot.StationCommandSessionComposition;
        Assert.Equal(1, diagnostics.AttemptCount);
        Assert.Equal(1, diagnostics.ForwardedCount);
        Assert.Equal(1, diagnostics.RejectedCount);
        Assert.Equal("submitter-exception", diagnostics.LastOutcome);
    }

    private static BrowserTxIntent Intent(
        BrowserTxIntentKind kind,
        bool? enabled,
        string intentId = "intent-000000000000000000000000000001") =>
        new(
            intentId,
            kind,
            kind switch
            {
                BrowserTxIntentKind.Mox => "mox.set",
                BrowserTxIntentKind.Ptt => "ptt.set",
                BrowserTxIntentKind.Tune => "tune.set",
                BrowserTxIntentKind.Microphone => "microphone.set",
                BrowserTxIntentKind.Cw => "cw.send",
                _ => "unknown"
            },
            enabled,
            Text: kind == BrowserTxIntentKind.Cw ? "CQ TEST" : null);

    private static async Task<TxLease> EstablishAuthorityAsync(
        StationTxProductionLifecycle lifecycle,
        TxLeaseManager leases,
        TimeSpan duration)
    {
        lifecycle.ObserveBrowserConnection(
            "connection-a",
            connected: true,
            authenticated: true);
        lifecycle.ObserveEngineConnection(
            connected: true,
            clientHandle: 0x1234abcd);
        Assert.True(leases.TryAcquire(
            "radio-a",
            "session-a",
            "connection-a",
            "operator-a",
            "Operator A",
            duration,
            out TxLease? lease,
            out string? error), error);
        Assert.NotNull(lease);
        lifecycle.ObserveLeaseChange(
            new TxLeaseChange(
                lease,
                Active: true,
                Reason: "test-acquired",
                OccurredAt: lease.AcquiredAt));
        await lifecycle.FlushAsync();
        return lease;
    }

    private static StationTxProductionLifecycle Create(
        TxLeaseManager leases,
        RadioTxOccupancyRegistry occupancy,
        TimeProvider timeProvider,
        IStationTxCommandSignatureVerifier? verifier = null,
        IStationTxCommandEnvelopeSubmitter? submitter = null) =>
        new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            leases,
            occupancy,
            NullLogger<StationTxProductionLifecycle>.Instance,
            timeProvider,
            independentWatchdogFactory: null,
            stationCommandVerifier: verifier,
            stationCommandSubmitter: submitter);

    private static StationTxCommandEnvelopeCoordinatorDiagnostics
        CoordinatorDiagnostics(
            bool submissionEnabled = true,
            bool signingAvailable = true,
            bool verificationAvailable = true) =>
        new(
            Registered: true,
            submissionEnabled,
            signingAvailable,
            verificationAvailable,
            BoundaryAttached: false,
            BoundaryEnabled: false,
            BoundarySignatureVerificationAvailable: false,
            CommandAdapterRegistered: false,
            ArmingAvailable: false,
            SetTransmitAvailable: false,
            SubmissionAvailable: false,
            AttemptCount: 0,
            SignedEnvelopeCount: 0,
            AcceptedCount: 0,
            RejectedCount: 0,
            LastOutcome: "none",
            LastObservedAt: null,
            Reason: submissionEnabled ? "ready-for-boundary" : "submission-disabled");

    private sealed class RecordingSubmitter : IStationTxCommandEnvelopeSubmitter
    {
        public int CallCount { get; private set; }
        public StationTxCommandEnvelopeSubmissionRequest? LastRequest { get; private set; }
        public StationTxCommandBoundary? LastBoundary { get; private set; }
        public Exception? Exception { get; set; }

        public StationTxCommandEnvelopeCoordinatorDiagnostics Snapshot =>
            CoordinatorDiagnostics();

        public Task<StationTxCommandEnvelopeCoordinatorResult> SubmitAsync(
            StationTxCommandEnvelopeSubmissionRequest request,
            StationTxCommandBoundary boundary,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            LastBoundary = boundary;
            if (Exception is not null)
            {
                throw Exception;
            }
            return Task.FromResult(
                new StationTxCommandEnvelopeCoordinatorResult(
                    Success: false,
                    Code: "recorded",
                    Message: "The test submitter recorded the request.",
                    Snapshot,
                    BoundaryResult: null));
        }
    }

    private sealed class ThrowingSigner : IStationTxCommandSigner
    {
        public int CreateCount { get; private set; }
        public bool IsAvailable => true;

        public StationTxCommandEnvelope CreateEnvelope(
            StationTxCommandSigningRequest request)
        {
            CreateCount++;
            throw new InvalidOperationException(
                "The disabled coordinator must not invoke the signer.");
        }
    }

    private sealed class AlwaysAvailableVerifier :
        IStationTxCommandSignatureVerifier
    {
        public bool IsAvailable => true;

        public bool Verify(
            string keyId,
            ReadOnlySpan<byte> payload,
            ReadOnlySpan<byte> signature) => true;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration) => m_now += duration;
    }
}
