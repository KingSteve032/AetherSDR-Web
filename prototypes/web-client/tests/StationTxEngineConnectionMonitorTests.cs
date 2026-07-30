using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxEngineConnectionMonitorTests
{
    private const string RadioId = "flex:engine-loss-radio";
    private const string EngineInstanceId = "engine-instance-a";
    private const string LeaseId = "lease-a";
    private const string SessionId = "session-a";
    private const string BrowserClientId = "browser-a";
    private const uint ProtectedHandle = 0x10203040;
    private const uint ObserverHandle = 0x50607080;

    [Fact]
    public async Task ConnectedThenDisconnectedWhileIdleDisarmsWithoutUnkey()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxEngineConnectionMonitor monitor =
            new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);

        StationTxEngineConnectionResult connected =
            await monitor.EvaluateAsync(Observation(isConnected: true));
        StationTxEngineConnectionResult disconnected =
            await monitor.EvaluateAsync(Observation(isConnected: false));

        Assert.True(connected.Success);
        Assert.Equal("engine_connected", connected.Code);
        Assert.True(disconnected.Success);
        Assert.Equal("unkeyed", disconnected.Code);
        Assert.True(disconnected.SawConnected);
        Assert.True(disconnected.LossSignaled);
        Assert.Equal(
            StationTxSafetyState.Disarmed,
            disconnected.SafetySnapshot.State);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task ConnectedThenLostDuringProtectedTxRequestsOneObserverUnkey()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxEngineConnectionMonitor monitor =
            new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(
            Observation(isConnected: true))).Success);
        ObserveProtectedTx(fixture);

        StationTxEngineConnectionResult lost =
            await monitor.EvaluateAsync(Observation(isConnected: false));

        Assert.True(lost.Success);
        Assert.Equal("unkey_pending", lost.Code);
        Assert.True(lost.SawConnected);
        Assert.True(lost.LossSignaled);
        Assert.Equal(
            StationTxSafetyState.UnkeyPending,
            lost.SafetySnapshot.State);
        Assert.True(lost.SafetySnapshot.SawProtectedTransmit);
        Assert.Single(fixture.Transport.Commands);
    }

    [Fact]
    public async Task UnknownUnkeyOutcomeReconcilesAfterRadioConfirmsIdle()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxEngineConnectionMonitor monitor =
            new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(
            Observation(isConnected: true))).Success);
        ObserveProtectedTx(fixture);
        fixture.Transport.Results.Enqueue(
            StationTxTransportResult.Unknown(
                "The FLEX command response timed out after send."));

        StationTxEngineConnectionResult lost =
            await monitor.EvaluateAsync(Observation(isConnected: false));

        Assert.False(lost.Success);
        Assert.Equal("emergency_unkey_outcome_unknown", lost.Code);
        Assert.True(lost.LossSignaled);
        Assert.Equal(
            StationTxSafetyState.UnkeyPending,
            lost.SafetySnapshot.State);
        Assert.Single(fixture.Transport.Commands);

        ObserveIdle(fixture);
        StationTxEngineConnectionResult reconciled =
            await monitor.EvaluateAsync(Observation(isConnected: false));

        Assert.True(reconciled.Success);
        Assert.Equal("unkeyed", reconciled.Code);
        Assert.Equal(
            StationTxSafetyState.Disarmed,
            reconciled.SafetySnapshot.State);
        Assert.Single(fixture.Transport.Commands);
    }

    [Theory]
    [InlineData("other-engine", LeaseId, ProtectedHandle)]
    [InlineData(EngineInstanceId, "other-lease", ProtectedHandle)]
    [InlineData(EngineInstanceId, LeaseId, 0x99887766)]
    public async Task MismatchedConnectionIdentityNeverSignalsUnkey(
        string engineInstanceId,
        string leaseId,
        uint protectedHandle)
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxEngineConnectionMonitor monitor =
            new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        ObserveProtectedTx(fixture);

        StationTxEngineConnectionResult result =
            await monitor.EvaluateAsync(new(
                engineInstanceId,
                leaseId,
                protectedHandle,
                IsConnected: false));

        Assert.False(result.Success);
        Assert.Equal("engine_connection_owner_mismatch", result.Code);
        Assert.False(result.SawConnected);
        Assert.False(result.LossSignaled);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(
            StationTxSafetyState.Armed,
            result.SafetySnapshot.State);
    }

    [Fact]
    public async Task StartingDisconnectedNeverInventsPriorConnectionOrOwnership()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxEngineConnectionMonitor monitor =
            new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        ObserveProtectedTx(fixture);

        StationTxEngineConnectionResult result =
            await monitor.EvaluateAsync(Observation(isConnected: false));

        Assert.True(result.Success);
        Assert.Equal("engine_connection_not_established", result.Code);
        Assert.False(result.SawConnected);
        Assert.False(result.LossSignaled);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(
            StationTxSafetyState.Armed,
            result.SafetySnapshot.State);
    }

    [Fact]
    public async Task RepeatedDisconnectedObservationReconcilesWithoutDuplicateImmediateUnkey()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxEngineConnectionMonitor monitor =
            new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(
            Observation(isConnected: true))).Success);
        ObserveProtectedTx(fixture);

        StationTxEngineConnectionResult first =
            await monitor.EvaluateAsync(Observation(isConnected: false));
        StationTxEngineConnectionResult second =
            await monitor.EvaluateAsync(Observation(isConnected: false));

        Assert.Equal("unkey_pending", first.Code);
        Assert.Equal("unkey_pending", second.Code);
        Assert.Single(fixture.Transport.Commands);

        ObserveIdle(fixture);
        StationTxEngineConnectionResult idle =
            await monitor.EvaluateAsync(Observation(isConnected: false));

        Assert.True(idle.Success);
        Assert.Equal("unkeyed", idle.Code);
        Assert.Equal(
            StationTxSafetyState.Disarmed,
            idle.SafetySnapshot.State);
        Assert.Single(fixture.Transport.Commands);
    }

    private static Fixture CreateFixture()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 13, 0, 0, TimeSpan.Zero));
        RadioTxOccupancyRegistry occupancy = new(time);
        FakeEmergencyTransport transport = new();
        StationTxSafetySupervisor supervisor = new(
            RadioId,
            occupancy,
            transport,
            time);
        return new Fixture(time, occupancy, transport, supervisor);
    }

    private static StationTxSafetyArm Arm() =>
        new(
            EngineInstanceId,
            LeaseId,
            SessionId,
            BrowserClientId,
            ProtectedHandle,
            TimeSpan.FromSeconds(2));

    private static StationTxEngineConnectionObservation Observation(
        bool isConnected) =>
        new(
            EngineInstanceId,
            LeaseId,
            ProtectedHandle,
            isConnected);

    private static void ObserveIdle(Fixture fixture)
    {
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "READY",
            null,
            null,
            Clients());
    }

    private static void ObserveProtectedTx(Fixture fixture)
    {
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "TRANSMITTING",
            ProtectedHandle,
            "SW",
            Clients());
    }

    private static RadioGuiClientDiagnostics[] Clients() =>
    [
        new RadioGuiClientDiagnostics(
            ProtectedHandle,
            "engine-client",
            "AetherD",
            "AETHER-ENGINE",
            string.Empty,
            LocalPtt: true,
            IsThisSession: false),
        new RadioGuiClientDiagnostics(
            ObserverHandle,
            "safety-observer",
            "AetherSDR Safety",
            "AETHER-SAFETY",
            string.Empty,
            LocalPtt: false,
            IsThisSession: true)
    ];

    private sealed record Fixture(
        ManualTimeProvider Time,
        RadioTxOccupancyRegistry Occupancy,
        FakeEmergencyTransport Transport,
        StationTxSafetySupervisor Supervisor);

    private sealed class FakeEmergencyTransport
        : IStationTxEmergencyUnkeyTransport
    {
        public bool IsConnected { get; set; } = true;
        public List<bool> Commands { get; } = [];
        public Queue<StationTxTransportResult> Results { get; } = [];

        public Task<StationTxTransportResult> RequestUnkeyAsync(
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
        public override DateTimeOffset GetUtcNow() => now;
    }
}
