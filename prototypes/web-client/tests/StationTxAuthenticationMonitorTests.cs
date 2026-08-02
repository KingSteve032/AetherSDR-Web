using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxAuthenticationMonitorTests
{
    private const string RadioId = "flex:authentication-loss-radio";
    private const string EngineInstanceId = "engine-instance-a";
    private const string LeaseId = "lease-a";
    private const string SessionId = "session-a";
    private const string BrowserClientId = "browser-a";
    private const uint ProtectedHandle = 0x10203040;
    private const uint ObserverHandle = 0x50607080;
    private const uint SmartSdrHandle = 0x90A0B0C0;

    [Fact]
    public async Task AuthenticatedThenUnauthenticatedWhileIdleDisarmsWithoutUnkey()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxAuthenticationMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);

        StationTxAuthenticationResult authenticated =
            await monitor.EvaluateAsync(Observation(isAuthenticated: true));
        StationTxAuthenticationResult lost =
            await monitor.EvaluateAsync(Observation(isAuthenticated: false));

        Assert.True(authenticated.Success);
        Assert.Equal("authenticated", authenticated.Code);
        Assert.True(lost.Success);
        Assert.Equal("unkeyed", lost.Code);
        Assert.True(lost.SawAuthenticated);
        Assert.True(lost.LossSignaled);
        Assert.Equal(
            StationTxSafetyState.Disarmed,
            lost.SafetySnapshot.State);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task AuthenticatedThenLostDuringProtectedTxRequestsOneObserverUnkey()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxAuthenticationMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(
            Observation(isAuthenticated: true))).Success);
        ObserveProtectedTx(fixture);

        StationTxAuthenticationResult lost =
            await monitor.EvaluateAsync(Observation(isAuthenticated: false));

        Assert.True(lost.Success);
        Assert.Equal("unkey_pending", lost.Code);
        Assert.True(lost.SawAuthenticated);
        Assert.True(lost.LossSignaled);
        Assert.Equal(
            StationTxSafetyState.UnkeyPending,
            lost.SafetySnapshot.State);
        Assert.True(lost.SafetySnapshot.SawProtectedTransmit);
        Assert.Single(fixture.Transport.Commands);
    }

    [Fact]
    public async Task AuthenticationLossNeverUnkeysAnExternalOwner()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxAuthenticationMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(
            Observation(isAuthenticated: true))).Success);
        ObserveExternalTx(fixture);

        StationTxAuthenticationResult lost =
            await monitor.EvaluateAsync(Observation(isAuthenticated: false));

        Assert.False(lost.Success);
        Assert.Equal("external_tx_owner", lost.Code);
        Assert.True(lost.SawAuthenticated);
        Assert.True(lost.LossSignaled);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(
            StationTxSafetyState.Faulted,
            lost.SafetySnapshot.State);
    }

    [Fact]
    public async Task UnknownUnkeyOutcomeReconcilesAfterRadioConfirmsIdle()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxAuthenticationMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(
            Observation(isAuthenticated: true))).Success);
        ObserveProtectedTx(fixture);
        fixture.Transport.Results.Enqueue(
            StationTxTransportResult.Unknown(
                "The FLEX command response timed out after send."));

        StationTxAuthenticationResult lost =
            await monitor.EvaluateAsync(Observation(isAuthenticated: false));

        Assert.False(lost.Success);
        Assert.Equal("emergency_unkey_outcome_unknown", lost.Code);
        Assert.True(lost.LossSignaled);
        Assert.Equal(
            StationTxSafetyState.UnkeyPending,
            lost.SafetySnapshot.State);
        Assert.Single(fixture.Transport.Commands);

        ObserveIdle(fixture);
        StationTxAuthenticationResult reconciled =
            await monitor.EvaluateAsync(Observation(isAuthenticated: false));

        Assert.True(reconciled.Success);
        Assert.Equal("unkeyed", reconciled.Code);
        Assert.Equal(
            StationTxSafetyState.Disarmed,
            reconciled.SafetySnapshot.State);
        Assert.Single(fixture.Transport.Commands);
    }

    [Theory]
    [InlineData("other-engine", LeaseId, SessionId, BrowserClientId, ProtectedHandle)]
    [InlineData(EngineInstanceId, "other-lease", SessionId, BrowserClientId, ProtectedHandle)]
    [InlineData(EngineInstanceId, LeaseId, "other-session", BrowserClientId, ProtectedHandle)]
    [InlineData(EngineInstanceId, LeaseId, SessionId, "other-browser", ProtectedHandle)]
    [InlineData(EngineInstanceId, LeaseId, SessionId, BrowserClientId, 0x99887766)]
    public async Task MismatchedAuthenticationIdentityNeverSignalsUnkey(
        string engineInstanceId,
        string leaseId,
        string sessionId,
        string browserClientId,
        uint protectedHandle)
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxAuthenticationMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        ObserveProtectedTx(fixture);

        StationTxAuthenticationResult result =
            await monitor.EvaluateAsync(new(
                engineInstanceId,
                leaseId,
                sessionId,
                browserClientId,
                protectedHandle,
                IsAuthenticated: false));

        Assert.False(result.Success);
        Assert.Equal("authentication_owner_mismatch", result.Code);
        Assert.False(result.SawAuthenticated);
        Assert.False(result.LossSignaled);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(
            StationTxSafetyState.Armed,
            result.SafetySnapshot.State);
    }

    [Fact]
    public async Task StartingUnauthenticatedNeverInventsPriorAuthorityOrOwnership()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxAuthenticationMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        ObserveProtectedTx(fixture);

        StationTxAuthenticationResult result =
            await monitor.EvaluateAsync(Observation(isAuthenticated: false));

        Assert.True(result.Success);
        Assert.Equal("authentication_not_established", result.Code);
        Assert.False(result.SawAuthenticated);
        Assert.False(result.LossSignaled);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(
            StationTxSafetyState.Armed,
            result.SafetySnapshot.State);
    }

    [Fact]
    public async Task RepeatedUnauthenticatedObservationReconcilesWithoutDuplicateImmediateUnkey()
    {
        Fixture fixture = CreateFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        await using StationTxAuthenticationMonitor monitor = new(supervisor);
        ObserveIdle(fixture);
        Assert.True((await supervisor.ArmAsync(Arm())).Success);
        Assert.True((await monitor.EvaluateAsync(
            Observation(isAuthenticated: true))).Success);
        ObserveProtectedTx(fixture);

        StationTxAuthenticationResult first =
            await monitor.EvaluateAsync(Observation(isAuthenticated: false));
        StationTxAuthenticationResult second =
            await monitor.EvaluateAsync(Observation(isAuthenticated: false));

        Assert.Equal("unkey_pending", first.Code);
        Assert.Equal("unkey_pending", second.Code);
        Assert.Single(fixture.Transport.Commands);

        ObserveIdle(fixture);
        StationTxAuthenticationResult idle =
            await monitor.EvaluateAsync(Observation(isAuthenticated: false));

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
            new DateTimeOffset(2026, 7, 30, 20, 0, 0, TimeSpan.Zero));
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

    private static StationTxAuthenticationObservation Observation(
        bool isAuthenticated) =>
        new(
            EngineInstanceId,
            LeaseId,
            SessionId,
            BrowserClientId,
            ProtectedHandle,
            isAuthenticated);

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

    private static void ObserveExternalTx(Fixture fixture)
    {
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "TRANSMITTING",
            SmartSdrHandle,
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
            IsThisSession: true),
        new RadioGuiClientDiagnostics(
            SmartSdrHandle,
            "external-client",
            "SmartSDR-Win",
            "STEVENS-SURFACE",
            string.Empty,
            LocalPtt: false,
            IsThisSession: false)
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
        public override DateTimeOffset GetUtcNow() => now;
    }
}
