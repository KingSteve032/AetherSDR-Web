using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxCommandGateExecutorTests
{
    private const string RadioId = "FLEX:TEST-RADIO";
    private const string SessionId = "session-a";
    private const string BrowserClientId = "browser-a";
    private const uint AetherHandle = 0x10203040;
    private const uint ExternalHandle = 0x55667788;
    private static readonly DateTimeOffset Now =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExecutorSurfaceIsInternalAndTyped()
    {
        Assert.False(typeof(StationTxCommandGateExecutor).IsPublic);
        Assert.False(typeof(StationTxCommandGateCapabilities).IsPublic);
        Assert.Contains(
            typeof(IStationTxCommandAdapterExecutor),
            typeof(StationTxCommandGateExecutor).GetInterfaces());
    }

    [Fact]
    public async Task DisabledGateRegistersExecutorButCannotSetTransmit()
    {
        GateFixture fixture = CreateFixture(allowTransmit: false);
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);

        StationTxCommandAdapterExecutorCapabilities capabilities =
            executor.Capabilities;
        StationTxTransportResult result = await executor.ExecuteAsync(
            CreateCommand(enabled: true),
            CancellationToken.None);

        Assert.True(capabilities.Registered);
        Assert.False(capabilities.ArmingAvailable);
        Assert.False(capabilities.SetTransmitAvailable);
        Assert.Equal("transmit-disabled", capabilities.Reason);
        Assert.False(result.Success);
        Assert.True(result.OutcomeKnown);
        Assert.Contains("transmit_disabled", result.Message);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(StationTxGateState.Disabled, gate.Snapshot.State);
    }

    [Fact]
    public async Task MissingCommandTransportFailsClosedBeforeLeaseLookup()
    {
        GateFixture fixture = CreateFixture(
            allowTransmit: true,
            transportConnected: false);
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);

        StationTxCommandAdapterExecutorCapabilities capabilities =
            executor.Capabilities;
        StationTxTransportResult result = await executor.ExecuteAsync(
            CreateCommand(enabled: true),
            CancellationToken.None);

        Assert.True(capabilities.Registered);
        Assert.False(capabilities.ArmingAvailable);
        Assert.False(capabilities.SetTransmitAvailable);
        Assert.Equal("command-transport-unavailable", capabilities.Reason);
        Assert.False(result.Success);
        Assert.Contains("radio_disconnected", result.Message);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task EnabledConnectedGateReportsExecutorReady()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);

        StationTxCommandAdapterExecutorCapabilities capabilities =
            executor.Capabilities;

        Assert.True(capabilities.Registered);
        Assert.True(capabilities.ArmingAvailable);
        Assert.True(capabilities.SetTransmitAvailable);
        Assert.Equal("ready", capabilities.Reason);
    }

    [Fact]
    public async Task ValidatedKeyDelegatesExactOwnerToGateOnce()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);

        StationTxTransportResult result = await executor.ExecuteAsync(
            CreateCommand(enabled: true, leaseId),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal([true], fixture.Transport.Commands);
        Assert.Equal(StationTxGateState.KeyPending, gate.Snapshot.State);
        Assert.Equal(leaseId, gate.Snapshot.LeaseId);
        Assert.Equal(SessionId, gate.Snapshot.SessionId);
        Assert.Equal(BrowserClientId, gate.Snapshot.BrowserClientId);
        Assert.Equal(AetherHandle, gate.Snapshot.ClientHandle);
    }

    [Fact]
    public async Task ExactOwnerUnkeyDelegatesToGateOnce()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        Assert.True((await executor.ExecuteAsync(
            CreateCommand(enabled: true, leaseId),
            CancellationToken.None)).Success);
        ObserveAetherTx(fixture);
        Assert.Equal("keyed", (await gate.EvaluateAsync()).Code);

        StationTxTransportResult result = await executor.ExecuteAsync(
            CreateCommand(enabled: false, leaseId),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal([true, false], fixture.Transport.Commands);
        Assert.Equal(StationTxGateState.UnkeyPending, gate.Snapshot.State);
    }

    [Fact]
    public async Task AdapterCompositionDelegatesValidatedKeyAndUnkeyThroughGate()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        StationTxCommandAdapterComposition composition = new(
            executor,
            _ => StationTxCommandAuthorityResolution.Accepted(
                CreateAuthority(fixture, leaseId)),
            fixture.Time);

        StationTxTransportResult keyed = await composition.ExecuteAsync(
            CreateCommand(enabled: true, leaseId));
        ObserveAetherTx(fixture);
        Assert.Equal("keyed", (await gate.EvaluateAsync()).Code);
        StationTxTransportResult unkeyed = await composition.ExecuteAsync(
            CreateCommand(enabled: false, leaseId));

        Assert.True(keyed.Success);
        Assert.True(unkeyed.Success);
        Assert.Equal([true, false], fixture.Transport.Commands);
        Assert.Equal(2, composition.Snapshot.AttemptCount);
        Assert.Equal(2, composition.Snapshot.ForwardedCount);
        Assert.Equal(2, composition.Snapshot.AcceptedCount);
        Assert.Equal(StationTxGateState.UnkeyPending, gate.Snapshot.State);
    }

    [Fact]
    public async Task WrongBrowserCannotUnkeyThroughExecutor()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        await executor.ExecuteAsync(
            CreateCommand(enabled: true, leaseId),
            CancellationToken.None);
        ObserveAetherTx(fixture);
        await gate.EvaluateAsync();

        StationTxValidatedCommand command =
            CreateCommand(enabled: false, leaseId) with
            {
                BrowserClientId = "browser-b"
            };
        StationTxTransportResult result = await executor.ExecuteAsync(
            command,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("tx_owner_required", result.Message);
        Assert.Equal([true], fixture.Transport.Commands);
        Assert.Equal(StationTxGateState.Keyed, gate.Snapshot.State);
    }

    [Fact]
    public async Task ExternalTransmitOwnerBlocksExecutorWithoutCommand()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        string leaseId = AcquireLease(fixture);
        ObserveExternalTx(fixture);

        StationTxTransportResult result = await executor.ExecuteAsync(
            CreateCommand(enabled: true, leaseId),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("external_tx_owner", result.Message);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(StationTxGateState.Idle, gate.Snapshot.State);
    }

    [Fact]
    public async Task UnknownKeyOutcomeIsPreservedForGateReconciliation()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        fixture.Transport.NextResult =
            StationTxTransportResult.Unknown("socket closed after send");

        StationTxTransportResult result = await executor.ExecuteAsync(
            CreateCommand(enabled: true, leaseId),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.OutcomeKnown);
        Assert.Contains("key_command_outcome_unknown", result.Message);
        Assert.Equal([true], fixture.Transport.Commands);
        Assert.Equal(StationTxGateState.KeyPending, gate.Snapshot.State);
        Assert.True(gate.Snapshot.HasActiveIntent);
    }

    [Fact]
    public async Task RejectedKeyOutcomeRemainsRejectedWithoutRetry()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        fixture.Transport.NextResult =
            StationTxTransportResult.Rejected("radio rejected");

        StationTxTransportResult result = await executor.ExecuteAsync(
            CreateCommand(enabled: true, leaseId),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.OutcomeKnown);
        Assert.Contains("key_command_rejected", result.Message);
        Assert.Equal([true], fixture.Transport.Commands);
        Assert.Equal(StationTxGateState.Faulted, gate.Snapshot.State);
    }

    [Fact]
    public async Task UnknownUnkeyOutcomeIsPreservedWithoutExecutorRetry()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        await executor.ExecuteAsync(
            CreateCommand(enabled: true, leaseId),
            CancellationToken.None);
        ObserveAetherTx(fixture);
        await gate.EvaluateAsync();
        fixture.Transport.NextResult =
            StationTxTransportResult.Unknown("socket closed after unkey");

        StationTxTransportResult result = await executor.ExecuteAsync(
            CreateCommand(enabled: false, leaseId),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.OutcomeKnown);
        Assert.Contains("unkey_command_outcome_unknown", result.Message);
        Assert.Equal([true, false], fixture.Transport.Commands);
        Assert.Equal(StationTxGateState.UnkeyPending, gate.Snapshot.State);
        Assert.Equal(1, gate.Snapshot.UnkeyAttempts);
    }

    [Fact]
    public async Task UnsupportedCommandNeverReachesGateOrTransport()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        StationTxValidatedCommand command = CreateCommand(enabled: true) with
        {
            Action = (StationTxCommandAction)999
        };

        StationTxTransportResult result = await executor.ExecuteAsync(
            command,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("only SetTransmit", result.Message);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(StationTxGateState.Idle, gate.Snapshot.State);
    }

    [Fact]
    public async Task CancellationBeforeExecutionNeverReachesGateOrTransport()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(
                CreateCommand(enabled: true),
                cancellation.Token));

        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(StationTxGateState.Idle, gate.Snapshot.State);
    }

    [Fact]
    public async Task TransportCapabilityFaultReportsUnavailable()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        StationTxCommandGateExecutor executor = new(gate);
        fixture.Transport.ThrowOnCapabilityRead = true;

        StationTxCommandAdapterExecutorCapabilities capabilities =
            executor.Capabilities;

        Assert.True(capabilities.Registered);
        Assert.False(capabilities.ArmingAvailable);
        Assert.False(capabilities.SetTransmitAvailable);
        Assert.Equal(
            "command-transport-capabilities-faulted",
            capabilities.Reason);
        Assert.Empty(fixture.Transport.Commands);
    }

    private static GateFixture CreateFixture(
        bool allowTransmit = true,
        bool transportConnected = true)
    {
        ManualTimeProvider time = new(Now);
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        FakeTxTransport transport = new(AetherHandle)
        {
            Connected = transportConnected
        };
        StationTxCommandGate gate = new(
            allowTransmit,
            RadioId,
            leases,
            occupancy,
            transport,
            time);
        return new GateFixture(time, leases, occupancy, transport, gate);
    }

    private static string AcquireLease(GateFixture fixture)
    {
        Assert.True(fixture.Leases.TryAcquire(
            RadioId,
            SessionId,
            BrowserClientId,
            "operator-a",
            "Operator A",
            TxLeaseManager.MaximumLeaseDuration,
            out TxLease? lease,
            out string? error), error);
        return Assert.IsType<TxLease>(lease).LeaseId;
    }

    private static StationTxValidatedCommand CreateCommand(
        bool enabled,
        string leaseId = "lease-a") =>
        new(
            CommandId: Guid.NewGuid().ToString("N"),
            Sequence: 1,
            StationId: "gateway-a",
            RadioId,
            SessionId,
            BrowserClientId,
            leaseId,
            GatewayInstanceId: "gateway-a",
            EngineInstanceId: "engine-a",
            ClientHandle: AetherHandle,
            Action: StationTxCommandAction.SetTransmit,
            enabled,
            IssuedAt: Now,
            ExpiresAt: Now + TimeSpan.FromSeconds(5));

    private static StationTxCommandAuthority CreateAuthority(
        GateFixture fixture,
        string leaseId)
    {
        TxLease lease = Assert.IsType<TxLease>(
            fixture.Leases.GetCurrent(RadioId));
        return new StationTxCommandAuthority(
            StationId: "gateway-a",
            RadioId,
            SessionId,
            BrowserClientId,
            leaseId,
            lease.ExpiresAt,
            GatewayInstanceId: "gateway-a",
            EngineInstanceId: "engine-a",
            ClientHandle: AetherHandle,
            Authenticated: true,
            BrowserFresh: true,
            EngineFresh: true,
            GatewayFresh: true,
            AuthorityFresh: true,
            fixture.Occupancy.GetSnapshot(RadioId),
            new StationTxSafetySnapshot(
                RadioId,
                StationTxSafetyState.Armed,
                "armed",
                EngineInstanceId: "engine-a",
                LeaseId: leaseId,
                SessionId,
                BrowserClientId,
                ProtectedClientHandle: AetherHandle,
                ArmedAt: Now - TimeSpan.FromSeconds(1),
                LastHeartbeatAt: Now,
                HeartbeatDeadlineAt: Now + TimeSpan.FromSeconds(2),
                UnkeyDeadlineAt: null,
                UnkeyAttempts: 0,
                SawProtectedTransmit: false));
    }

    private static void ObserveIdle(GateFixture fixture) =>
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            SessionId,
            AetherHandle,
            "READY",
            null,
            null,
            [AetherClient(localPtt: true)]);

    private static void ObserveAetherTx(GateFixture fixture) =>
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            SessionId,
            AetherHandle,
            "TRANSMITTING",
            AetherHandle,
            "SW",
            [AetherClient(localPtt: true)]);

    private static void ObserveExternalTx(GateFixture fixture) =>
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            SessionId,
            AetherHandle,
            "TRANSMITTING",
            ExternalHandle,
            "SW",
            [
                AetherClient(localPtt: false),
                new RadioGuiClientDiagnostics(
                    ExternalHandle,
                    "external-client",
                    "SmartSDR-Win",
                    "EXTERNAL",
                    string.Empty,
                    LocalPtt: true,
                    IsThisSession: false)
            ]);

    private static RadioGuiClientDiagnostics AetherClient(bool localPtt) =>
        new(
            AetherHandle,
            "aether-client",
            "AetherSDR",
            "AETHER-WEB-RX",
            string.Empty,
            localPtt,
            IsThisSession: true);

    private sealed record GateFixture(
        ManualTimeProvider Time,
        TxLeaseManager Leases,
        RadioTxOccupancyRegistry Occupancy,
        FakeTxTransport Transport,
        StationTxCommandGate Gate);

    private sealed class FakeTxTransport(uint clientHandle) :
        IStationTxCommandTransport
    {
        public bool Connected { get; set; } = true;
        public uint Handle { get; set; } = clientHandle;
        public bool ThrowOnCapabilityRead { get; set; }
        public List<bool> Commands { get; } = [];
        public StationTxTransportResult NextResult { get; set; } =
            StationTxTransportResult.Ok;

        public bool IsConnected => ThrowOnCapabilityRead
            ? throw new InvalidOperationException("capability fault")
            : Connected;

        public uint ClientHandle => ThrowOnCapabilityRead
            ? throw new InvalidOperationException("capability fault")
            : Handle;

        public Task<StationTxTransportResult> SetTransmitAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(enabled);
            StationTxTransportResult result = NextResult;
            NextResult = StationTxTransportResult.Ok;
            return Task.FromResult(result);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
