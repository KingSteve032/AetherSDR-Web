using System.Security.Claims;
using AetherSDR.TxWatchdog.Protocol;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class RadioBrowserTxIntentTests
{
    private const uint StationHandle = 0x1234abcd;

    [Fact]
    public async Task ExactAuthorityValidatesIntentButStopsAtUnavailableTransport()
    {
        await using IntentFixture fixture = await IntentFixture.CreateAsync();
        TxLease lease = await fixture.EstablishAuthorityAsync();
        BrowserTxCapability capability =
            fixture.Coordinator.GetBrowserTxCapability(fixture.Connection);

        BrowserTxIntentResult result = fixture.Coordinator.EvaluateBrowserTxIntent(
            fixture.Connection,
            Request(lease.LeaseId, sequence: 1, "mox.set", enabled: true),
            authenticated: true);
        await fixture.Lifecycle.FlushAsync();

        Assert.True(capability.IntentValidationAvailable);
        Assert.Equal("intent-validation-ready", capability.State);
        Assert.False(capability.KeyingAvailable);
        Assert.False(capability.MicrophoneAvailable);
        Assert.False(capability.TuneAvailable);
        Assert.False(capability.CwAvailable);
        Assert.False(result.Ok);
        Assert.True(result.Validated);
        Assert.Equal("transport-unavailable", result.Outcome);
        Assert.Contains(
            "station transaction boundary",
            result.Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lease.LeaseId, fixture.Leases.GetCurrent("radio-a")?.LeaseId);

        StationTxLifecycleDiagnostics diagnostics = fixture.Lifecycle.Snapshot;
        Assert.Equal(1, diagnostics.BrowserTxIntentObservationSequence);
        Assert.Equal(1, diagnostics.LastBrowserTxIntentRequestSequence);
        Assert.Equal("mox.set", diagnostics.LastBrowserTxIntentAction);
        Assert.Equal("transport-unavailable", diagnostics.LastBrowserTxIntentOutcome);
        Assert.Contains(
            "deliberate TX intent",
            diagnostics.LastBrowserTxIntentReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(diagnostics.LastBrowserTxIntentAt);
        Assert.Equal("Disabled", diagnostics.GateState);
        Assert.Equal("Disarmed", diagnostics.SafetyState);
        Assert.False(diagnostics.CommandTransportAvailable);
        Assert.False(diagnostics.EmergencyUnkeyTransportAvailable);
    }

    [Fact]
    public async Task WrongOpaqueLeaseNeverReachesValidatedOutcome()
    {
        await using IntentFixture fixture = await IntentFixture.CreateAsync();
        TxLease lease = await fixture.EstablishAuthorityAsync();

        BrowserTxIntentResult result = fixture.Coordinator.EvaluateBrowserTxIntent(
            fixture.Connection,
            Request(
                "ffffffffffffffffffffffffffffffff",
                sequence: 1,
                "ptt.set",
                enabled: true),
            authenticated: true);

        Assert.False(result.Ok);
        Assert.False(result.Validated);
        Assert.Equal("lease-invalid", result.Outcome);
        Assert.Equal(lease.LeaseId, fixture.Leases.GetCurrent("radio-a")?.LeaseId);
    }

    [Fact]
    public async Task AuthenticationLossReleasesTheExactLeaseBeforeIntentDenial()
    {
        await using IntentFixture fixture = await IntentFixture.CreateAsync();
        TxLease lease = await fixture.EstablishAuthorityAsync();

        BrowserTxIntentResult result = fixture.Coordinator.EvaluateBrowserTxIntent(
            fixture.Connection,
            Request(lease.LeaseId, sequence: 1, "microphone.set", enabled: true),
            authenticated: false);
        await fixture.Lifecycle.FlushAsync();
        await fixture.Lifecycle.FlushAsync();

        Assert.False(result.Validated);
        Assert.Equal("authentication-required", result.Outcome);
        Assert.Null(fixture.Leases.GetCurrent("radio-a"));
        Assert.False(fixture.Lifecycle.Snapshot.LeaseActive);
        Assert.Equal(
            "authentication-lost",
            fixture.Lifecycle.Snapshot.LastLeaseChangeReason);
        Assert.Equal(
            "authentication-required",
            fixture.Lifecycle.Snapshot.LastBrowserTxIntentOutcome);
    }

    [Fact]
    public async Task ReplacedBrowserConnectionCannotReplayItsLease()
    {
        await using IntentFixture fixture = await IntentFixture.CreateAsync();
        TxLease lease = await fixture.EstablishAuthorityAsync();
        fixture.Coordinator.Unregister(fixture.Connection.ClientId);
        await fixture.Lifecycle.FlushAsync();

        BrowserTxIntentResult result = fixture.Coordinator.EvaluateBrowserTxIntent(
            fixture.Connection,
            Request(lease.LeaseId, sequence: 1, "tune.set", enabled: true),
            authenticated: true);

        Assert.False(result.Validated);
        Assert.Equal("connection-replaced", result.Outcome);
        Assert.Null(fixture.Leases.GetCurrent("radio-a"));
    }

    [Fact]
    public async Task NonIdleRadioAuthorityDeniesIntentWithoutIssuingACommand()
    {
        await using IntentFixture fixture = await IntentFixture.CreateAsync();
        TxLease lease = await fixture.EstablishAuthorityAsync();
        fixture.Occupancy.ObserveInterlock(
            "radio-a",
            "reporter-a",
            StationHandle,
            "TRANSMITTING",
            0x99999999,
            string.Empty,
            []);

        BrowserTxIntentResult result = fixture.Coordinator.EvaluateBrowserTxIntent(
            fixture.Connection,
            Request(lease.LeaseId, sequence: 1, "cw.send", text: "CQ TEST"),
            authenticated: true);

        Assert.False(result.Validated);
        Assert.Equal("occupancy-not-authorized", result.Outcome);
        Assert.False(result.Capability.OccupancyAllowsLease);
        Assert.False(result.Capability.KeyingAvailable);
        Assert.Equal(lease.LeaseId, fixture.Leases.GetCurrent("radio-a")?.LeaseId);
    }

    [Fact]
    public async Task RenewalCannotExtendLeaseAfterOccupancyStopsBeingIdle()
    {
        await using IntentFixture fixture = await IntentFixture.CreateAsync();
        TxLease lease = await fixture.EstablishAuthorityAsync();
        fixture.Occupancy.ObserveInterlock(
            "radio-a",
            "reporter-a",
            StationHandle,
            "TRANSMITTING",
            0x99999999,
            string.Empty,
            []);

        bool renewed = fixture.Coordinator.TryRenewTxLease(
            fixture.Connection,
            lease.LeaseId,
            TimeSpan.FromSeconds(10),
            authenticated: true,
            out TxLease? renewedLease,
            out string? error);
        await fixture.Lifecycle.FlushAsync();
        await fixture.Lifecycle.FlushAsync();

        Assert.False(renewed);
        Assert.Null(renewedLease);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Null(fixture.Leases.GetCurrent("radio-a"));
        Assert.False(fixture.Lifecycle.Snapshot.LeaseActive);
        Assert.Equal(
            "renewal-authority-lost",
            fixture.Lifecycle.Snapshot.LastLeaseChangeReason);
    }

    [Fact]
    public async Task LeaseExpiryMakesAFormerlyExactIntentInvalid()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 31, 16, 0, 0, TimeSpan.Zero));
        await using IntentFixture fixture = await IntentFixture.CreateAsync(time);
        TxLease lease = await fixture.EstablishAuthorityAsync(
            TxLeaseManager.MinimumLeaseDuration);
        time.Advance(TimeSpan.FromSeconds(2));

        BrowserTxIntentResult result = fixture.Coordinator.EvaluateBrowserTxIntent(
            fixture.Connection,
            Request(lease.LeaseId, sequence: 1, "mox.set", enabled: false),
            authenticated: true);
        await fixture.Lifecycle.FlushAsync();

        Assert.False(result.Validated);
        Assert.Equal("lease-invalid", result.Outcome);
        Assert.Null(fixture.Leases.GetCurrent("radio-a"));
    }

    [Fact]
    public async Task PostBarrierConfirmationRejectsLeaseRevokedByWatchdogRegistration()
    {
        await using IntentFixture fixture = await IntentFixture.CreateAsync(
            watchdogRegistrationSucceeds: false);
        Assert.True(fixture.Coordinator.TryAcquireTxLease(
            fixture.Connection,
            TimeSpan.FromSeconds(10),
            out TxLease? acquired,
            out string? acquireError), acquireError);
        Assert.NotNull(acquired);

        await fixture.Coordinator.FlushTxLifecycleAsync();

        Assert.False(fixture.Coordinator.TryConfirmTxLease(
            fixture.Connection,
            acquired!.LeaseId,
            out TxLease? confirmed,
            out string? confirmationError));
        Assert.Null(confirmed);
        Assert.NotNull(confirmationError);
        Assert.Null(fixture.Leases.GetCurrent("radio-a"));
    }

    [Fact]
    public async Task LeaseAloneCannotManufactureLifecycleAuthority()
    {
        await using IntentFixture fixture = await IntentFixture.CreateAsync(
            observeEngine: false);
        Assert.True(fixture.Coordinator.TryAcquireTxLease(
            fixture.Connection,
            TimeSpan.FromSeconds(10),
            out TxLease? lease,
            out string? error), error);
        Assert.NotNull(lease);

        BrowserTxIntentResult result = fixture.Coordinator.EvaluateBrowserTxIntent(
            fixture.Connection,
            Request(lease!.LeaseId, sequence: 1, "tune.set", enabled: false),
            authenticated: true);

        Assert.False(result.Validated);
        Assert.StartsWith("lifecycle-", result.Outcome, StringComparison.Ordinal);
        Assert.False(result.Capability.IntentValidationAvailable);
    }

    private static BrowserTxRequest Request(
        string leaseId,
        long sequence,
        string action,
        bool? enabled = null,
        string? text = null)
    {
        BrowserTxIntentKind kind = action switch
        {
            "mox.set" => BrowserTxIntentKind.Mox,
            "ptt.set" => BrowserTxIntentKind.Ptt,
            "tune.set" => BrowserTxIntentKind.Tune,
            "microphone.set" => BrowserTxIntentKind.Microphone,
            "cw.send" => BrowserTxIntentKind.Cw,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return new BrowserTxRequest(
            RequestId: sequence,
            BrowserTxRequestKind.Intent,
            sequence,
            Seconds: null,
            leaseId,
            new BrowserTxIntent(
                $"intent-{sequence}",
                kind,
                action,
                enabled,
                text));
    }

    private sealed class IntentFixture : IAsyncDisposable
    {
        private IntentFixture(
            TxLeaseManager leases,
            RadioTxOccupancyRegistry occupancy,
            StationTxProductionLifecycle lifecycle,
            RadioCoordinator coordinator,
            RadioClientConnection connection)
        {
            Leases = leases;
            Occupancy = occupancy;
            Lifecycle = lifecycle;
            Coordinator = coordinator;
            Connection = connection;
        }

        public TxLeaseManager Leases { get; }
        public RadioTxOccupancyRegistry Occupancy { get; }
        public StationTxProductionLifecycle Lifecycle { get; }
        public RadioCoordinator Coordinator { get; }
        public RadioClientConnection Connection { get; }

        public static async Task<IntentFixture> CreateAsync(
            TimeProvider? timeProvider = null,
            bool observeEngine = true,
            bool watchdogRegistrationSucceeds = true)
        {
            TxLeaseManager leases = new(timeProvider);
            RadioTxOccupancyRegistry occupancy = new(timeProvider);
            occupancy.ObserveInterlock(
                "radio-a",
                "reporter-a",
                StationHandle,
                "READY",
                null,
                string.Empty,
                []);
            FakeIndependentWatchdogFactory watchdogFactory = new(
                watchdogRegistrationSucceeds);
            StationTxProductionLifecycle lifecycle = new(
                "radio-a",
                "session-a",
                "browser-page-a",
                "gateway-a",
                leases,
                occupancy,
                NullLogger<StationTxProductionLifecycle>.Instance,
                timeProvider,
                watchdogFactory);
            await lifecycle.StartAsync();
            RadioCoordinator coordinator = new(
                NullLogger<RadioCoordinator>.Instance,
                Options.Create(new RadioSettings
                {
                    Mode = "Simulation",
                    RadioId = "radio-a",
                    SessionId = "session-a",
                    BrowserTxLeaseEnabled = true,
                    AllowTransmit = false
                }),
                leases,
                txOccupancyRegistry: occupancy,
                txLifecycle: lifecycle);
            RadioClientConnection connection = coordinator.Register(
                CreateUser());
            if (observeEngine)
            {
                lifecycle.ObserveEngineConnection(
                    connected: true,
                    clientHandle: StationHandle);
            }
            await lifecycle.FlushAsync();
            return new IntentFixture(
                leases,
                occupancy,
                lifecycle,
                coordinator,
                connection);
        }

        public async Task<TxLease> EstablishAuthorityAsync(
            TimeSpan? duration = null)
        {
            Assert.True(Coordinator.TryAcquireTxLease(
                Connection,
                duration ?? TimeSpan.FromSeconds(10),
                out TxLease? lease,
                out string? error), error);
            Assert.NotNull(lease);
            await Coordinator.FlushTxLifecycleAsync();
            Assert.True(
                Coordinator
                    .GetBrowserTxCapability(Connection)
                    .IntentValidationAvailable);
            return lease;
        }

        public async ValueTask DisposeAsync()
        {
            Coordinator.Dispose();
            await Lifecycle.DisposeAsync();
        }

        private static ClaimsPrincipal CreateUser() =>
            new(
                new ClaimsIdentity(
                    [
                        new Claim("oid", "operator-a"),
                        new Claim("name", "Operator A"),
                        new Claim(ClaimTypes.Role, "Aether.Transmit")
                    ],
                    authenticationType: "test"));
    }

    private sealed class FakeIndependentWatchdogFactory(
        bool registerSuccessfully = true) :
        IStationTxIndependentWatchdogFactory
    {
        public IStationTxIndependentWatchdog Create(
            StationTxIndependentWatchdogOwner owner,
            Func<StationTxIndependentWatchdogEvent, ValueTask> eventSink) =>
            new FakeIndependentWatchdog(registerSuccessfully);
    }

    private sealed class FakeIndependentWatchdog(
        bool registerSuccessfully = true) :
        IStationTxIndependentWatchdog
    {
        private long m_sequence;
        private WatchdogIdentity? m_identity;

        public StationTxIndependentWatchdogDiagnostics Snapshot { get; private set; } =
            NewSnapshot(
                processRunning: false,
                registered: false,
                leaseBound: false,
                lastSequence: 0,
                "fake-created");

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Snapshot = NewSnapshot(
                processRunning: true,
                registered: false,
                leaseBound: false,
                lastSequence: 0,
                "fake-ready");
            return Task.CompletedTask;
        }

        public Task<StationTxIndependentWatchdogDiagnostics> RegisterAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            if (!registerSuccessfully)
            {
                Snapshot = NewSnapshot(
                    processRunning: true,
                    registered: false,
                    leaseBound: false,
                    lastSequence: 0,
                    "registration-rejected-disarmed");
                return Task.FromResult(Snapshot);
            }

            m_identity = identity;
            m_sequence = 1;
            Snapshot = NewSnapshot(
                processRunning: true,
                registered: true,
                leaseBound: true,
                m_sequence,
                "registered-disarmed");
            return Task.FromResult(Snapshot);
        }

        public Task<StationTxIndependentWatchdogDiagnostics> HeartbeatAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(m_identity, identity);
            m_sequence++;
            Snapshot = NewSnapshot(
                processRunning: true,
                registered: true,
                leaseBound: true,
                m_sequence,
                "heartbeat-disarmed");
            return Task.FromResult(Snapshot);
        }

        public Task<StationTxIndependentWatchdogDiagnostics>
            DisconnectAndResetAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(m_identity, identity);
            m_identity = null;
            m_sequence = 0;
            Snapshot = NewSnapshot(
                processRunning: true,
                registered: false,
                leaseBound: false,
                lastSequence: 0,
                "disconnect-reset-disarmed");
            return Task.FromResult(Snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static StationTxIndependentWatchdogDiagnostics NewSnapshot(
            bool processRunning,
            bool registered,
            bool leaseBound,
            long lastSequence,
            string lastObservation) =>
            new(
                SupervisionEnabled: true,
                processRunning,
                ProcessId: processRunning ? 23456 : null,
                HostInstanceId: "fake-watchdog-intent",
                ProcessStartedAt: DateTimeOffset.UtcNow,
                State: "Disarmed",
                Reason: "unkey-transport-disabled-disarmed",
                IpcConnected: processRunning,
                registered,
                Connected: registered,
                leaseBound,
                lastSequence,
                RestartCount: 0,
                lastObservation,
                LastObservedAt: DateTimeOffset.UtcNow,
                LastError: null,
                RadioCommandTransportAvailable: false,
                ArmingAvailable: false);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan amount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
                amount,
                TimeSpan.Zero);
            m_now += amount;
        }
    }
}
