using System.Security.Claims;
using AetherSDR.TxWatchdog.Protocol;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class RadioBrowserTxProductionBindingTests
{
    private const uint StationHandle = 0x1234abcd;

    [Fact]
    public async Task BrowserKeyHeartbeatAndUnkeyTraverseTheBoundTransaction()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        ObserveIdle(occupancy);
        RecordingProductionTransport transport = new(occupancy);
        AvailableEmergencyTransport emergency = new();
        AvailableWatchdogFactory watchdogFactory = new();
        CompleteSubmitter submitter = new();
        StationTxProductionActivationConfigurationDiagnostics configuration =
            CompleteConfiguration();

        await using StationTxProductionLifecycle lifecycle = new(
            "radio-a",
            "session-a",
            "browser-page-a",
            "gateway-a",
            leases,
            occupancy,
            NullLogger<StationTxProductionLifecycle>.Instance,
            independentWatchdogFactory: watchdogFactory,
            stationCommandVerifier: new AlwaysAvailableVerifier(),
            stationCommandSubmitter: submitter,
            productionReadinessConfiguration: new(
                AllowTransmitConfigured: true,
                BrowserTxLeaseConfigured: true),
            productionCommandTransport: transport,
            productionEmergencyUnkeyTransport: emergency,
            independentWatchdogLocalFlexEligible: true,
            productionActivationConfiguration: configuration);
        await lifecycle.StartAsync();

        using RadioCoordinator coordinator = new(
            NullLogger<RadioCoordinator>.Instance,
            Options.Create(new RadioSettings
            {
                Mode = "FlexRx",
                RadioId = "radio-a",
                SessionId = "session-a",
                BrowserTxLeaseEnabled = true,
                AllowTransmit = true
            }),
            leases,
            txOccupancyRegistry: occupancy,
            txLifecycle: lifecycle);
        RadioClientConnection connection = coordinator.Register(CreateUser());
        coordinator.SetRadioConnection(
            connected: true,
            radioModel: "FLEX-TEST",
            serial: "TEST-SERIAL",
            stationClientHandle: StationHandle);
        await lifecycle.FlushAsync();

        Assert.True(coordinator.TryAcquireTxLease(
            connection,
            TimeSpan.FromSeconds(10),
            authenticated: true,
            out TxLease? lease,
            out string? acquireError), acquireError);
        Assert.NotNull(lease);
        await coordinator.FlushTxLifecycleAsync();

        BrowserTxCapability ready = coordinator.GetBrowserTxCapability(
            connection,
            authenticated: true);
        Assert.True(ready.KeyingAvailable);
        Assert.Equal("keying-ready", ready.State);

        BrowserTxIntentResult key = await coordinator.ExecuteBrowserTxIntentAsync(
            connection,
            Request(lease!.LeaseId, sequence: 1, enabled: true),
            authenticated: true);

        Assert.True(key.Ok, key.Error);
        Assert.Equal("key_confirmed", key.Outcome);
        Assert.Equal([true], transport.EnabledValues);
        Assert.True(lifecycle.Snapshot.StationCommandTransactionComposition.Active);
        Assert.True(watchdogFactory.Watchdog.Snapshot.Armed);
        Assert.Equal("transmit-active", key.Capability.State);

        BrowserTxHeartbeatResult heartbeat =
            await coordinator.HeartbeatBrowserTxAsync(
                connection,
                Heartbeat(lease.LeaseId, sequence: 2),
                authenticated: true);

        Assert.True(heartbeat.Ok, heartbeat.Error);
        Assert.Equal("heartbeat_accepted", heartbeat.Outcome);
        Assert.Equal(1, watchdogFactory.Watchdog.SafetyHeartbeatCount);

        Assert.True(coordinator.TryRenewTxLease(
            connection,
            lease.LeaseId,
            TimeSpan.FromSeconds(10),
            authenticated: true,
            out TxLease? renewed,
            out string? renewError), renewError);
        Assert.NotNull(renewed);
        await coordinator.FlushTxLifecycleAsync();
        StationTxSafetyArmAuthorityDiagnostics preUnkeyAuthority =
            lifecycle.Snapshot.StationCommandSafetyArmAuthority;
        Assert.True(
            preUnkeyAuthority.HeartbeatAvailable,
            preUnkeyAuthority.Reason);
        Assert.True(
            preUnkeyAuthority.AbortAvailable,
            preUnkeyAuthority.Reason);

        BrowserTxIntentResult unkey = await coordinator.ExecuteBrowserTxIntentAsync(
            connection,
            Request(lease.LeaseId, sequence: 3, enabled: false),
            authenticated: true);

        StationTxLifecycleDiagnostics failedSnapshot = lifecycle.Snapshot;
        Assert.True(
            unkey.Ok,
            $"{unkey.Outcome}: {unkey.Error}; " +
            $"authority={failedSnapshot.StationCommandSafetyArmAuthority.Reason}; " +
            $"composition={failedSnapshot.StationCommandSafetyArmComposition.Reason}; " +
            $"transaction={failedSnapshot.StationCommandTransactionComposition.Reason}");
        Assert.Equal("unkey_confirmed", unkey.Outcome);
        Assert.Equal([true, false], transport.EnabledValues);
        Assert.False(lifecycle.Snapshot.StationCommandTransactionComposition.Active);
        Assert.False(watchdogFactory.Watchdog.Snapshot.Armed);
        Assert.Equal(RadioTxOccupancyState.Idle, occupancy.GetSnapshot("radio-a").State);
    }

    private static BrowserTxRequest Request(
        string leaseId,
        long sequence,
        bool enabled) =>
        new(
            RequestId: sequence,
            BrowserTxRequestKind.Intent,
            sequence,
            Seconds: null,
            leaseId,
            new BrowserTxIntent(
                $"intent-{sequence:000000000000000000000000000000}",
                BrowserTxIntentKind.Mox,
                "mox.set",
                enabled,
                Text: null));

    private static BrowserTxRequest Heartbeat(string leaseId, long sequence) =>
        new(
            RequestId: sequence,
            BrowserTxRequestKind.Heartbeat,
            sequence,
            Seconds: null,
            leaseId,
            Intent: null);

    private static StationTxProductionActivationConfigurationDiagnostics
        CompleteConfiguration() =>
        StationTxProductionActivationConfigurationInterlock.Evaluate(new(
            ActivationRequested: true,
            LocalFlexModeConfigured: true,
            AllowTransmitConfigured: true,
            BrowserTxLeaseConfigured: true,
            CommandTrustVerificationEnabled: true,
            CommandTrustKeyConfigured: true,
            CommandSigningEnabled: true,
            CommandSigningKeyConfigured: true,
            CommandSubmissionEnabled: true,
            CommandTransportEnabled: true,
            CommandTransportAllowlistConfigured: true,
            EmergencyUnkeyTransportEnabled: true,
            EmergencyUnkeyTransportAllowlistConfigured: true,
            WatchdogSupervisionEnabled: true,
            WatchdogCommandTransportEnabled: true,
            WatchdogRadioAllowlistConfigured: true,
            WatchdogArmingEnabled: true));

    private static ClaimsPrincipal CreateUser() =>
        new(
            new ClaimsIdentity(
                [
                    new Claim("oid", "operator-a"),
                    new Claim("name", "Operator A"),
                    new Claim(ClaimTypes.Role, "Aether.Transmit")
                ],
                authenticationType: "test"));

    private static RadioGuiClientDiagnostics AetherClient(bool localPtt) =>
        new(
            StationHandle,
            "aether-client",
            "AetherSDR",
            "AETHER-WEB-RX",
            string.Empty,
            localPtt,
            IsThisSession: true);

    private static void ObserveIdle(RadioTxOccupancyRegistry occupancy) =>
        occupancy.ObserveInterlock(
            "radio-a",
            "session-a",
            StationHandle,
            "READY",
            txClientHandle: null,
            pttSource: null,
            [AetherClient(localPtt: true)]);

    private static void ObserveTransmitting(RadioTxOccupancyRegistry occupancy) =>
        occupancy.ObserveInterlock(
            "radio-a",
            "session-a",
            StationHandle,
            "TRANSMITTING",
            StationHandle,
            "SW",
            [AetherClient(localPtt: true)]);

    private sealed class RecordingProductionTransport(
        RadioTxOccupancyRegistry occupancy) :
        IStationTxProductionCommandTransport
    {
        public List<bool> EnabledValues { get; } = [];
        public bool IsConnected => true;
        public uint ClientHandle => StationHandle;
        public StationTxProductionCommandTransportDiagnostics Snapshot =>
            new(
                Registered: true,
                ConfiguredEnabled: true,
                LocalFlexEligible: true,
                RadioAllowed: true,
                CommandChannelAttached: true,
                ClientHandleAvailable: true,
                Available: true,
                SetTransmitAvailable: true,
                CommandTimeoutMilliseconds: 2000,
                AttemptCount: EnabledValues.Count,
                ForwardedCount: EnabledValues.Count,
                KeyAttemptCount: EnabledValues.Count(value => value),
                UnkeyAttemptCount: EnabledValues.Count(value => !value),
                AcceptedCount: EnabledValues.Count,
                RejectedCount: 0,
                UnknownCount: 0,
                LastOperation: EnabledValues.LastOrDefault() ? "key" : "unkey",
                LastOutcome: EnabledValues.Count == 0 ? "none" : "accepted",
                LastReason: "ready",
                LastObservedAt: EnabledValues.Count == 0
                    ? null
                    : DateTimeOffset.UtcNow);

        public Task<StationTxTransportResult> SetTransmitAsync(
            bool enabled,
            uint expectedClientHandle,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(StationHandle, expectedClientHandle);
            EnabledValues.Add(enabled);
            if (enabled)
            {
                ObserveTransmitting(occupancy);
            }
            else
            {
                ObserveIdle(occupancy);
            }
            return Task.FromResult(StationTxTransportResult.Ok);
        }
    }

    private sealed class AvailableEmergencyTransport :
        IStationTxProductionEmergencyUnkeyTransport
    {
        public bool IsConnected => true;
        public StationTxProductionEmergencyUnkeyTransportDiagnostics Snapshot =>
            new(
                Registered: true,
                ConfiguredEnabled: true,
                LocalFlexEligible: true,
                RadioAllowed: true,
                CommandChannelAttached: true,
                ClientHandleAvailable: true,
                Available: true,
                UnkeyAvailable: true,
                CommandTimeoutMilliseconds: 2000,
                AttemptCount: 0,
                ForwardedCount: 0,
                AcceptedCount: 0,
                RejectedCount: 0,
                UnknownCount: 0,
                LastOutcome: "none",
                LastReason: "ready",
                LastObservedAt: null);

        public Task<StationTxTransportResult> RequestUnkeyAsync(
            uint expectedProtectedClientHandle,
            CancellationToken cancellationToken) =>
            Task.FromResult(StationTxTransportResult.Ok);
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

    private sealed class CompleteSubmitter : IStationTxCommandEnvelopeSubmitter
    {
        public StationTxCommandEnvelopeCoordinatorDiagnostics Snapshot =>
            Diagnostics(boundary: null, success: false);

        public async Task<StationTxCommandEnvelopeCoordinatorResult> SubmitAsync(
            StationTxCommandEnvelopeSubmissionRequest request,
            StationTxCommandBoundary boundary,
            CancellationToken cancellationToken = default)
        {
            StationTxCommandAuthority authority = request.Authority;
            StationTxValidatedOperatorIntent intent = request.Intent;
            DateTimeOffset expiresAt = intent.ObservedAt + TimeSpan.FromSeconds(5);
            if (expiresAt > authority.LeaseExpiresAt)
            {
                expiresAt = authority.LeaseExpiresAt;
            }
            StationTxCommandEnvelope envelope = new(
                StationTxCommandBoundary.ProtocolVersion,
                "test-key",
                Guid.NewGuid().ToString("N"),
                intent.Sequence,
                intent.ObservedAt,
                expiresAt,
                authority.StationId,
                authority.RadioId,
                authority.SessionId,
                authority.BrowserClientId,
                authority.LeaseId,
                authority.GatewayInstanceId,
                authority.EngineInstanceId,
                authority.ClientHandle,
                StationTxCommandAction.SetTransmit,
                intent.Enabled,
                Convert.ToBase64String(new byte[64]));
            StationTxCommandBoundaryResult boundaryResult =
                await boundary.ValidateAndExecuteAsync(
                    envelope,
                    authority,
                    cancellationToken);
            return new StationTxCommandEnvelopeCoordinatorResult(
                boundaryResult.Success,
                boundaryResult.Code,
                boundaryResult.Message,
                Diagnostics(boundaryResult.Capabilities, boundaryResult.Success),
                boundaryResult);
        }

        private static StationTxCommandEnvelopeCoordinatorDiagnostics Diagnostics(
            StationTxCommandCapabilities? boundary,
            bool success) =>
            new(
                Registered: true,
                SubmissionEnabled: true,
                SigningAvailable: true,
                SignatureVerificationAvailable: true,
                BoundaryAttached: boundary is not null,
                BoundaryEnabled: boundary?.BoundaryEnabled == true,
                BoundarySignatureVerificationAvailable:
                    boundary?.SignatureVerificationAvailable == true,
                CommandAdapterRegistered:
                    boundary?.CommandAdapterRegistered == true,
                ArmingAvailable: boundary?.ArmingAvailable ?? true,
                SetTransmitAvailable: boundary?.SetTransmitAvailable ?? true,
                SubmissionAvailable: true,
                AttemptCount: success ? 1 : 0,
                SignedEnvelopeCount: success ? 1 : 0,
                AcceptedCount: success ? 1 : 0,
                RejectedCount: 0,
                LastOutcome: success ? "accepted" : "ready",
                LastObservedAt: success ? DateTimeOffset.UtcNow : null,
                Reason: "ready");
    }

    private sealed class AvailableWatchdogFactory :
        IStationTxIndependentWatchdogFactory
    {
        public AvailableWatchdog Watchdog { get; } = new();

        public IStationTxIndependentWatchdog Create(
            StationTxIndependentWatchdogOwner owner,
            Func<StationTxIndependentWatchdogEvent, ValueTask> eventSink) =>
            Watchdog;
    }

    private sealed class AvailableWatchdog : IStationTxIndependentWatchdog
    {
        private WatchdogIdentity? m_identity;
        private long m_sequence;
        public int SafetyHeartbeatCount { get; private set; }

        public StationTxIndependentWatchdogDiagnostics Snapshot { get; private set; } =
            CreateSnapshot(
                registered: false,
                leaseBound: false,
                armed: false,
                sequence: 0,
                observation: "created");

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Snapshot = CreateSnapshot(
                registered: false,
                leaseBound: false,
                armed: false,
                sequence: 0,
                observation: "ready");
            return Task.CompletedTask;
        }

        public Task<StationTxIndependentWatchdogDiagnostics> RegisterAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            m_identity = identity;
            Snapshot = CreateSnapshot(
                registered: true,
                leaseBound: true,
                armed: false,
                sequence: ++m_sequence,
                observation: "registered");
            return Task.FromResult(Snapshot);
        }

        public Task<StationTxIndependentWatchdogDiagnostics> HeartbeatAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(m_identity, identity);
            Snapshot = CreateSnapshot(
                registered: true,
                leaseBound: true,
                armed: Snapshot.Armed,
                sequence: ++m_sequence,
                observation: "heartbeat");
            return Task.FromResult(Snapshot);
        }

        public Task<StationTxIndependentWatchdogDiagnostics> ArmAsync(
            WatchdogIdentity identity,
            TimeSpan heartbeatTimeout,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(m_identity, identity);
            Snapshot = CreateSnapshot(
                registered: true,
                leaseBound: true,
                armed: true,
                sequence: ++m_sequence,
                observation: "armed",
                heartbeatTimeout);
            return Task.FromResult(Snapshot);
        }

        public Task<StationTxIndependentWatchdogDiagnostics> SafetyHeartbeatAsync(
            WatchdogIdentity identity,
            TimeSpan heartbeatTimeout,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(m_identity, identity);
            SafetyHeartbeatCount++;
            Snapshot = CreateSnapshot(
                registered: true,
                leaseBound: true,
                armed: true,
                sequence: ++m_sequence,
                observation: "safety-heartbeat",
                heartbeatTimeout);
            return Task.FromResult(Snapshot);
        }

        public Task<StationTxIndependentWatchdogDiagnostics> DisarmAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(m_identity, identity);
            Snapshot = CreateSnapshot(
                registered: true,
                leaseBound: true,
                armed: false,
                sequence: ++m_sequence,
                observation: "disarmed");
            return Task.FromResult(Snapshot);
        }

        public Task<StationTxIndependentWatchdogDiagnostics> DisconnectAndResetAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            m_identity = null;
            Snapshot = CreateSnapshot(
                registered: false,
                leaseBound: false,
                armed: false,
                sequence: 0,
                observation: "reset");
            return Task.FromResult(Snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static StationTxIndependentWatchdogDiagnostics CreateSnapshot(
            bool registered,
            bool leaseBound,
            bool armed,
            long sequence,
            string observation,
            TimeSpan? heartbeatTimeout = null) =>
            new(
                SupervisionEnabled: true,
                ProcessRunning: true,
                ProcessId: 43210,
                HostInstanceId: "test-watchdog",
                ProcessStartedAt: DateTimeOffset.UtcNow,
                State: armed ? "Armed" : "Disarmed",
                Reason: armed ? "armed" : "ready",
                IpcConnected: true,
                Registered: registered,
                Connected: registered,
                LeaseBound: leaseBound,
                LastSequence: sequence,
                RestartCount: 0,
                LastObservation: observation,
                LastObservedAt: DateTimeOffset.UtcNow,
                LastError: null,
                RadioCommandTransportAvailable: true,
                ArmingAvailable: true,
                Armed: armed,
                ArmedAt: armed ? DateTimeOffset.UtcNow : null,
                LastSafetyHeartbeatAt: armed ? DateTimeOffset.UtcNow : null,
                HeartbeatDeadlineAt: armed
                    ? DateTimeOffset.UtcNow +
                        (heartbeatTimeout ?? TimeSpan.FromSeconds(5))
                    : null,
                HeartbeatTimeoutMilliseconds: heartbeatTimeout.HasValue
                    ? (int)heartbeatTimeout.Value.TotalMilliseconds
                    : null);
    }
}
