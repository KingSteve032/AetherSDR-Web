using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxSafetySupervisorTests
{
    private const string RadioId = "flex:safety-radio";
    private const string EngineInstanceId = "engine-instance-a";
    private const string LeaseId = "lease-a";
    private const string SessionId = "session-a";
    private const string BrowserClientId = "browser-a";
    private const uint ProtectedHandle = 0x10203040;
    private const uint ObserverHandle = 0x50607080;
    private const uint SmartSdrHandle = 0x90A0B0C0;

    [Fact]
    public async Task ArmRequiresFreshIdleAndExactLocalPttOwner()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;

        StationTxSafetyResult noObservation = await supervisor.ArmAsync(Arm());
        Assert.False(noObservation.Success);
        Assert.Equal("idle_interlock_required", noObservation.Code);

        ObserveIdle(fixture, SmartSdrHandle);
        StationTxSafetyResult wrongOwner = await supervisor.ArmAsync(Arm());
        Assert.False(wrongOwner.Success);
        Assert.Equal("local_ptt_owner_mismatch", wrongOwner.Code);
        Assert.Empty(fixture.Transport.Commands);

        ObserveIdle(fixture, ProtectedHandle);
        StationTxSafetyResult armed = await supervisor.ArmAsync(Arm());
        Assert.True(armed.Success);
        Assert.Equal("armed", armed.Code);
        Assert.Equal(StationTxSafetyState.Armed, armed.Snapshot.State);
        Assert.Equal(ProtectedHandle, armed.Snapshot.ProtectedClientHandle);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task MismatchedHeartbeatCannotExtendArm()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        StationTxSafetyResult armed = await supervisor.ArmAsync(Arm());
        DateTimeOffset deadline = armed.Snapshot.HeartbeatDeadlineAt!.Value;

        StationTxSafetyResult wrong = await supervisor.HeartbeatAsync(
            "other-engine",
            LeaseId,
            ProtectedHandle,
            TimeSpan.FromSeconds(2));

        Assert.False(wrong.Success);
        Assert.Equal("heartbeat_owner_mismatch", wrong.Code);
        Assert.Equal(deadline, wrong.Snapshot.HeartbeatDeadlineAt);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task HeartbeatExpiryWhileIdleDisarmsWithoutCommand()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await supervisor.ArmAsync(Arm(TimeSpan.FromSeconds(1)));

        fixture.Time.Advance(TimeSpan.FromSeconds(1.1));
        ObserveIdle(fixture, ProtectedHandle);
        StationTxSafetyResult result = await supervisor.EvaluateAsync();

        Assert.True(result.Success);
        Assert.Equal("disarmed", result.Code);
        Assert.Equal(StationTxSafetyState.Disarmed, result.Snapshot.State);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task HeartbeatExpiryUnkeysExactProtectedHandleFromIndependentObserver()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await supervisor.ArmAsync(Arm(TimeSpan.FromSeconds(1)));
        ObserveProtectedTx(fixture);

        RadioTxOccupancySnapshot occupancy =
            fixture.Occupancy.GetSnapshot(RadioId);
        Assert.Equal(RadioTxOccupancyState.External, occupancy.State);
        Assert.Equal(ProtectedHandle, Assert.Single(occupancy.Occupants).ClientHandle);

        fixture.Time.Advance(TimeSpan.FromSeconds(1.1));
        ObserveProtectedTx(fixture);
        StationTxSafetyResult result = await supervisor.EvaluateAsync();

        Assert.True(result.Success);
        Assert.Equal("unkey_pending", result.Code);
        Assert.Equal(StationTxSafetyState.UnkeyPending, result.Snapshot.State);
        Assert.True(result.Snapshot.SawProtectedTransmit);
        Assert.Equal(1, result.Snapshot.UnkeyAttempts);
        Assert.Single(fixture.Transport.Commands);
    }

    [Fact]
    public async Task RadioConfirmedIdleClearsEmergencyArm()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await supervisor.ArmAsync(Arm(TimeSpan.FromSeconds(1)));
        ObserveProtectedTx(fixture);
        fixture.Time.Advance(TimeSpan.FromSeconds(1.1));
        ObserveProtectedTx(fixture);
        await supervisor.EvaluateAsync();

        ObserveIdle(fixture, ProtectedHandle);
        StationTxSafetyResult idle = await supervisor.EvaluateAsync();

        Assert.True(idle.Success);
        Assert.Equal("unkeyed", idle.Code);
        Assert.Equal(StationTxSafetyState.Disarmed, idle.Snapshot.State);
        Assert.Single(fixture.Transport.Commands);
    }

    [Fact]
    public async Task BrowserOrGatewayAbortUnkeysOnlyProtectedTx()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await supervisor.ArmAsync(Arm());
        ObserveProtectedTx(fixture);

        StationTxSafetyResult result = await supervisor.AbortAsync(
            "browser-disconnected");

        Assert.True(result.Success);
        Assert.Equal("unkey_pending", result.Code);
        Assert.Single(fixture.Transport.Commands);
        Assert.Equal(
            StationTxSafetyState.UnkeyPending,
            result.Snapshot.State);
    }

    [Fact]
    public async Task ExternalSmartSdrOwnerIsNeverGloballyUnkeyed()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await supervisor.ArmAsync(Arm());
        ObserveExternalTx(fixture);

        StationTxSafetyResult result = await supervisor.AbortAsync(
            "gateway-link-lost");

        Assert.False(result.Success);
        Assert.Equal("external_tx_owner", result.Code);
        Assert.Equal(StationTxSafetyState.Faulted, result.Snapshot.State);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task AmbiguousOrStaleOwnershipNeverReceivesUnkey()
    {
        SafetyFixture ambiguous = CreateFixture();
        await using (StationTxSafetySupervisor supervisor = ambiguous.Supervisor)
        {
            ObserveIdle(ambiguous, ProtectedHandle);
            await supervisor.ArmAsync(Arm());
            ObserveAmbiguousTx(ambiguous);

            StationTxSafetyResult result = await supervisor.AbortAsync(
                "engine-lost");
            Assert.False(result.Success);
            Assert.Equal("tx_ownership_unknown", result.Code);
            Assert.Empty(ambiguous.Transport.Commands);
        }

        SafetyFixture stale = CreateFixture();
        await using (StationTxSafetySupervisor supervisor = stale.Supervisor)
        {
            ObserveIdle(stale, ProtectedHandle);
            await supervisor.ArmAsync(Arm());
            stale.Time.Advance(
                RadioTxOccupancyRegistry.ObservationLifetime +
                TimeSpan.FromMilliseconds(1));

            StationTxSafetyResult result = await supervisor.AbortAsync(
                "engine-lost");
            Assert.False(result.Success);
            Assert.Equal("tx_occupancy_stale", result.Code);
            Assert.Empty(stale.Transport.Commands);
        }
    }

    [Fact]
    public async Task UnavailableEmergencyTransportRetainsGuardedPendingArm()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await supervisor.ArmAsync(Arm());
        ObserveProtectedTx(fixture);
        fixture.Transport.IsConnected = false;

        StationTxSafetyResult unavailable = await supervisor.AbortAsync(
            "engine-shutdown");

        Assert.False(unavailable.Success);
        Assert.Equal("emergency_transport_unavailable", unavailable.Code);
        Assert.Equal(StationTxSafetyState.UnkeyPending, unavailable.Snapshot.State);
        Assert.Equal(0, unavailable.Snapshot.UnkeyAttempts);
        Assert.Empty(fixture.Transport.Commands);

        fixture.Transport.IsConnected = true;
        fixture.Time.Advance(
            StationTxSafetySupervisor.TransportRetryInterval +
            TimeSpan.FromMilliseconds(1));
        ObserveProtectedTx(fixture);
        StationTxSafetyResult retry = await supervisor.EvaluateAsync();

        Assert.True(retry.Success);
        Assert.Equal("unkey_pending", retry.Code);
        Assert.Equal(1, retry.Snapshot.UnkeyAttempts);
        Assert.Single(fixture.Transport.Commands);
    }

    [Fact]
    public async Task UnknownUnkeyOutcomeRetriesWhileExactOwnershipRemainsProven()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await supervisor.ArmAsync(Arm());
        ObserveProtectedTx(fixture);
        fixture.Transport.Results.Enqueue(
            StationTxTransportResult.Unknown("socket closed after send"));

        StationTxSafetyResult unknown = await supervisor.AbortAsync(
            "lease-expired");
        Assert.False(unknown.Success);
        Assert.Equal("emergency_unkey_outcome_unknown", unknown.Code);
        Assert.Equal(1, unknown.Snapshot.UnkeyAttempts);

        fixture.Time.Advance(
            StationTxSafetySupervisor.UnkeyConfirmationTimeout +
            TimeSpan.FromMilliseconds(1));
        ObserveProtectedTx(fixture);
        StationTxSafetyResult retry = await supervisor.EvaluateAsync();

        Assert.True(retry.Success);
        Assert.Equal("unkey_pending", retry.Code);
        Assert.Equal(2, retry.Snapshot.UnkeyAttempts);
        Assert.Equal(2, fixture.Transport.Commands.Count);
    }

    [Fact]
    public async Task EmergencyUnkeyRetriesAreBounded()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await supervisor.ArmAsync(Arm());
        ObserveProtectedTx(fixture);
        await supervisor.AbortAsync("lease-expired");

        for (int attempt = 2;
             attempt <= StationTxSafetySupervisor.MaximumUnkeyAttempts;
             attempt++)
        {
            fixture.Time.Advance(
                StationTxSafetySupervisor.UnkeyConfirmationTimeout +
                TimeSpan.FromMilliseconds(1));
            ObserveProtectedTx(fixture);
            StationTxSafetyResult retry = await supervisor.EvaluateAsync();
            Assert.True(retry.Success);
            Assert.Equal(attempt, retry.Snapshot.UnkeyAttempts);
        }

        fixture.Time.Advance(
            StationTxSafetySupervisor.UnkeyConfirmationTimeout +
            TimeSpan.FromMilliseconds(1));
        ObserveProtectedTx(fixture);
        StationTxSafetyResult exhausted = await supervisor.EvaluateAsync();

        Assert.False(exhausted.Success);
        Assert.Equal("unkey_confirmation_timeout", exhausted.Code);
        Assert.Equal(StationTxSafetyState.Faulted, exhausted.Snapshot.State);
        Assert.Equal(
            StationTxSafetySupervisor.MaximumUnkeyAttempts,
            fixture.Transport.Commands.Count);
    }

    [Fact]
    public async Task NewSupervisorNeverAssumesOwnershipOfExistingTransmit()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveProtectedTx(fixture);

        StationTxSafetyResult result = await supervisor.EvaluateAsync(
            "startup-reconciliation");

        Assert.True(result.Success);
        Assert.Equal("disarmed", result.Code);
        Assert.Equal(StationTxSafetyState.Disarmed, result.Snapshot.State);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task FaultedSupervisorCanResetOnlyAfterFreshIdle()
    {
        SafetyFixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await supervisor.ArmAsync(Arm());
        ObserveExternalTx(fixture);
        await supervisor.AbortAsync("engine-lost");

        StationTxSafetyResult blocked = await supervisor.ResetAsync();
        Assert.False(blocked.Success);
        Assert.Equal("idle_required_for_reset", blocked.Code);

        ObserveIdle(fixture, ProtectedHandle);
        StationTxSafetyResult reset = await supervisor.ResetAsync();
        Assert.True(reset.Success);
        Assert.Equal("reset", reset.Code);
        Assert.Equal(StationTxSafetyState.Disarmed, reset.Snapshot.State);
        Assert.Empty(fixture.Transport.Commands);
    }

    private static SafetyFixture CreateFixture()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero));
        RadioTxOccupancyRegistry occupancy = new(time);
        FakeEmergencyTransport transport = new();
        StationTxSafetySupervisor supervisor = new(
            RadioId,
            occupancy,
            transport,
            time);
        return new SafetyFixture(time, occupancy, transport, supervisor);
    }

    private static StationTxSafetyArm Arm(
        TimeSpan? heartbeatTimeout = null) =>
        new(
            EngineInstanceId,
            LeaseId,
            SessionId,
            BrowserClientId,
            ProtectedHandle,
            heartbeatTimeout ?? TimeSpan.FromSeconds(2));

    private static void ObserveIdle(
        SafetyFixture fixture,
        uint localPttHandle)
    {
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "READY",
            null,
            null,
            Clients(localPttHandle));
    }

    private static void ObserveProtectedTx(SafetyFixture fixture)
    {
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "TRANSMITTING",
            ProtectedHandle,
            "SW",
            Clients(ProtectedHandle));
    }

    private static void ObserveExternalTx(SafetyFixture fixture)
    {
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "TRANSMITTING",
            SmartSdrHandle,
            "SW",
            Clients(SmartSdrHandle));
    }

    private static void ObserveAmbiguousTx(SafetyFixture fixture)
    {
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "TRANSMITTING",
            null,
            "SW",
            Clients(ProtectedHandle));
    }

    private static RadioGuiClientDiagnostics[] Clients(uint localPttHandle) =>
    [
        new RadioGuiClientDiagnostics(
            ProtectedHandle,
            "engine-client",
            "AetherD",
            "AETHER-ENGINE",
            string.Empty,
            localPttHandle == ProtectedHandle,
            false),
        new RadioGuiClientDiagnostics(
            ObserverHandle,
            "safety-observer",
            "AetherSDR Safety",
            "AETHER-SAFETY",
            string.Empty,
            localPttHandle == ObserverHandle,
            true),
        new RadioGuiClientDiagnostics(
            SmartSdrHandle,
            "smartsdr-client",
            "SmartSDR-Win",
            "STEVENS-SURFACE",
            string.Empty,
            localPttHandle == SmartSdrHandle,
            false)
    ];

    private sealed record SafetyFixture(
        ManualTimeProvider Time,
        RadioTxOccupancyRegistry Occupancy,
        FakeEmergencyTransport Transport,
        StationTxSafetySupervisor Supervisor);

    private sealed class FakeEmergencyTransport
        : IStationTxEmergencyUnkeyTransport
    {
        public bool IsConnected { get; set; } = true;
        public List<bool> Commands { get; } = [];
        public Queue<StationTxTransportResult> Results { get; } = new();

        public Task<StationTxTransportResult> RequestUnkeyAsync(
            uint expectedProtectedClientHandle,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(false);
            return Task.FromResult(
                Results.Count > 0
                    ? Results.Dequeue()
                    : StationTxTransportResult.Ok);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration) =>
            m_now = m_now.Add(duration);
    }
}
