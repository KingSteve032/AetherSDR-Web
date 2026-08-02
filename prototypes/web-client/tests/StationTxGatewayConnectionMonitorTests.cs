using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxGatewayConnectionMonitorTests
{
    private const string RadioId = "flex:gateway-loss-radio";
    private const string GatewayInstanceId = "gateway-pid-420";
    private const string EngineInstanceId = "engine-instance-a";
    private const string LeaseId = "lease-a";
    private const string SessionId = "session-a";
    private const string BrowserClientId = "browser-a";
    private const uint ProtectedHandle = 0x10203040;
    private const uint ObserverHandle = 0x50607080;
    private const uint ExternalHandle = 0x90A0B0C0;

    [Fact]
    public async Task ConnectedThenLostWhileIdleDisarmsWithoutUnkey()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxGatewayConnectionMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.Equal(
            "gateway_connected",
            (await monitor.EvaluateAsync(Observation(true))).Code);

        StationTxGatewayConnectionResult lost =
            await monitor.EvaluateAsync(Observation(false));

        Assert.True(lost.Success);
        Assert.Equal("unkeyed", lost.Code);
        Assert.True(lost.SawConnected);
        Assert.True(lost.LossSignaled);
        Assert.Equal(StationTxSafetyState.Disarmed, lost.SafetySnapshot.State);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task ConnectedThenProcessLostDuringProtectedTxRequestsOneUnkey()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxGatewayConnectionMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(Observation(true))).Success);
        ObserveProtectedTx(fixture);

        StationTxGatewayConnectionResult lost =
            await monitor.EvaluateAsync(Observation(false));

        Assert.True(lost.Success);
        Assert.Equal("unkey_pending", lost.Code);
        Assert.Equal(
            StationTxSafetyState.UnkeyPending,
            lost.SafetySnapshot.State);
        Assert.True(lost.SafetySnapshot.SawProtectedTransmit);
        Assert.Single(fixture.Transport.Commands);
    }

    [Fact]
    public async Task StartingDisconnectedNeverInventsGatewayOwnership()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxGatewayConnectionMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        ObserveProtectedTx(fixture);

        StationTxGatewayConnectionResult result =
            await monitor.EvaluateAsync(Observation(false));

        Assert.True(result.Success);
        Assert.Equal("gateway_connection_not_established", result.Code);
        Assert.False(result.SawConnected);
        Assert.False(result.LossSignaled);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task ReplacementGatewayInstanceCannotClaimPriorConnection()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxGatewayConnectionMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(Observation(true))).Success);
        ObserveProtectedTx(fixture);

        StationTxGatewayConnectionResult result =
            await monitor.EvaluateAsync(
                Observation(false) with
                {
                    GatewayInstanceId = "replacement-gateway-pid-421"
                });

        Assert.False(result.Success);
        Assert.Equal("gateway_instance_mismatch", result.Code);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(StationTxSafetyState.Armed, result.SafetySnapshot.State);
    }

    [Theory]
    [InlineData("other-engine", LeaseId, SessionId, BrowserClientId, ProtectedHandle)]
    [InlineData(EngineInstanceId, "other-lease", SessionId, BrowserClientId, ProtectedHandle)]
    [InlineData(EngineInstanceId, LeaseId, "other-session", BrowserClientId, ProtectedHandle)]
    [InlineData(EngineInstanceId, LeaseId, SessionId, "other-browser", ProtectedHandle)]
    [InlineData(EngineInstanceId, LeaseId, SessionId, BrowserClientId, 0x99887766)]
    public async Task MismatchedOwnerIdentityNeverSignalsUnkey(
        string engineInstanceId,
        string leaseId,
        string sessionId,
        string browserClientId,
        uint protectedHandle)
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxGatewayConnectionMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        ObserveProtectedTx(fixture);

        StationTxGatewayConnectionResult result =
            await monitor.EvaluateAsync(new(
                GatewayInstanceId,
                engineInstanceId,
                leaseId,
                sessionId,
                browserClientId,
                protectedHandle,
                IsConnected: false));

        Assert.False(result.Success);
        Assert.Equal("gateway_connection_owner_mismatch", result.Code);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task RepeatedLossReconcilesWithoutDuplicateImmediateUnkey()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxGatewayConnectionMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(Observation(true))).Success);
        ObserveProtectedTx(fixture);

        StationTxGatewayConnectionResult first =
            await monitor.EvaluateAsync(Observation(false));
        StationTxGatewayConnectionResult second =
            await monitor.EvaluateAsync(Observation(false));

        Assert.Equal("unkey_pending", first.Code);
        Assert.Equal("unkey_pending", second.Code);
        Assert.Single(fixture.Transport.Commands);

        ObserveIdle(fixture);
        StationTxGatewayConnectionResult idle =
            await monitor.EvaluateAsync(Observation(false));
        Assert.True(idle.Success);
        Assert.Equal("unkeyed", idle.Code);
        Assert.Single(fixture.Transport.Commands);
    }

    [Fact]
    public async Task ExternalOwnerIsNeverUnkeyedAfterGatewayLoss()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxGatewayConnectionMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(Observation(true))).Success);
        ObserveExternalTx(fixture);

        StationTxGatewayConnectionResult result =
            await monitor.EvaluateAsync(Observation(false));

        Assert.False(result.Success);
        Assert.Equal("external_tx_owner", result.Code);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(StationTxSafetyState.Faulted, result.SafetySnapshot.State);
    }

    private static Fixture CreateFixture()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 23, 0, 0, TimeSpan.Zero));
        RadioTxOccupancyRegistry occupancy = new(time);
        FakeEmergencyTransport transport = new();
        StationTxSafetySupervisor supervisor = new(
            RadioId,
            occupancy,
            transport,
            time);
        return new Fixture(occupancy, transport, supervisor);
    }

    private static StationTxSafetyArm Arm() => new(
        EngineInstanceId,
        LeaseId,
        SessionId,
        BrowserClientId,
        ProtectedHandle,
        TimeSpan.FromSeconds(2));

    private static StationTxGatewayConnectionObservation Observation(
        bool connected) => new(
            GatewayInstanceId,
            EngineInstanceId,
            LeaseId,
            SessionId,
            BrowserClientId,
            ProtectedHandle,
            connected);

    private static void ObserveIdle(Fixture fixture) =>
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "READY",
            null,
            null,
            Clients(ProtectedHandle));

    private static void ObserveProtectedTx(Fixture fixture) =>
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "TRANSMITTING",
            ProtectedHandle,
            "SW",
            Clients(ProtectedHandle));

    private static void ObserveExternalTx(Fixture fixture) =>
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "TRANSMITTING",
            ExternalHandle,
            "SW",
            Clients(ExternalHandle));

    private static RadioGuiClientDiagnostics[] Clients(uint localPttHandle) =>
    [
        new(
            ProtectedHandle,
            "engine-client",
            "AetherD",
            "AETHER-ENGINE",
            string.Empty,
            localPttHandle == ProtectedHandle,
            false),
        new(
            ObserverHandle,
            "safety-observer",
            "AetherSDR Safety",
            "AETHER-SAFETY",
            string.Empty,
            false,
            true),
        new(
            ExternalHandle,
            "external-client",
            "SmartSDR-Win",
            "STEVENS-SURFACE",
            string.Empty,
            localPttHandle == ExternalHandle,
            false)
    ];

    private sealed record Fixture(
        RadioTxOccupancyRegistry Occupancy,
        FakeEmergencyTransport Transport,
        StationTxSafetySupervisor Supervisor);

    private sealed class FakeEmergencyTransport
        : IStationTxEmergencyUnkeyTransport
    {
        public bool IsConnected { get; set; } = true;
        public List<bool> Commands { get; } = [];

        public Task<StationTxTransportResult> RequestUnkeyAsync(
            uint expectedProtectedClientHandle,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(false);
            return Task.FromResult(StationTxTransportResult.Ok);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
