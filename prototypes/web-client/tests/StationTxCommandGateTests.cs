using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxCommandGateTests
{
    private const string RadioId = "flex:test-radio";
    private const string SessionId = "session-a";
    private const string BrowserClientId = "browser-a";
    private const string UserId = "user-a";
    private const string DisplayName = "Operator A";
    private const uint AetherHandle = 0x10203040;
    private const uint SmartSdrHandle = 0x55667788;

    [Fact]
    public async Task DisabledGateRejectsBeforeSendingAnyCommand()
    {
        ManualTimeProvider time = NewTime();
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        FakeTxTransport transport = new(AetherHandle);
        await using StationTxCommandGate gate = new(
            allowTransmit: false,
            RadioId,
            leases,
            occupancy,
            transport,
            time);

        StationTxGateResult result = await gate.RequestKeyAsync(
            "missing",
            SessionId,
            BrowserClientId);

        Assert.False(result.Success);
        Assert.Equal("transmit_disabled", result.Code);
        Assert.Equal(StationTxGateState.Disabled, result.Snapshot.State);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task ValidLeaseAndIdleInterlockCreateOnlyAPendingIntent()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);

        StationTxGateResult result = await gate.RequestKeyAsync(
            leaseId,
            SessionId,
            BrowserClientId);

        Assert.True(result.Success);
        Assert.Equal("key_pending", result.Code);
        Assert.Equal(StationTxGateState.KeyPending, result.Snapshot.State);
        Assert.Equal(AetherHandle, result.Snapshot.ClientHandle);
        Assert.Equal([true], fixture.Transport.Commands);
    }

    [Fact]
    public async Task UnknownKeyCommandOutcomeRemainsGuardedForWatchdogReconciliation()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        fixture.Transport.NextResult =
            StationTxTransportResult.Unknown("socket closed after send");

        StationTxGateResult result = await gate.RequestKeyAsync(
            leaseId,
            SessionId,
            BrowserClientId);

        Assert.False(result.Success);
        Assert.Equal("key_command_outcome_unknown", result.Code);
        Assert.Equal(StationTxGateState.KeyPending, result.Snapshot.State);
        Assert.True(result.Snapshot.HasActiveIntent);
        Assert.Equal([true], fixture.Transport.Commands);

        ObserveExternalTx(fixture);
        StationTxGateResult reconciled = await gate.EvaluateAsync();
        Assert.False(reconciled.Success);
        Assert.Equal("external_tx_owner", reconciled.Code);
        Assert.Equal([true], fixture.Transport.Commands);
    }

    [Fact]
    public async Task RadioMustConfirmExactAetherClientHandleBeforeKeyed()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        await gate.RequestKeyAsync(leaseId, SessionId, BrowserClientId);

        ObserveAetherTx(fixture);
        StationTxGateResult confirmed = await gate.EvaluateAsync();

        Assert.True(confirmed.Success);
        Assert.Equal("keyed", confirmed.Code);
        Assert.Equal(StationTxGateState.Keyed, confirmed.Snapshot.State);
        Assert.Equal([true], fixture.Transport.Commands);
    }

    [Fact]
    public async Task SmartSdrLocalPttOwnershipBlocksKeyWhileInterlockIsIdle()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        RadioGuiClientDiagnostics[] clients =
        [
            AetherClient(localPtt: false),
            SmartSdrClient(localPtt: true)
        ];
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            SessionId,
            AetherHandle,
            "READY",
            null,
            null,
            clients);

        StationTxGateResult result = await gate.RequestKeyAsync(
            leaseId,
            SessionId,
            BrowserClientId);

        Assert.False(result.Success);
        Assert.Equal("external_local_ptt_owner", result.Code);
        Assert.Equal(StationTxGateState.Idle, result.Snapshot.State);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task AetherLocalPttOwnershipCanCoexistWithIdleSmartSdrClient()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        RadioGuiClientDiagnostics[] clients =
        [
            AetherClient(localPtt: true),
            SmartSdrClient(localPtt: false)
        ];
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            SessionId,
            AetherHandle,
            "READY",
            null,
            null,
            clients);

        StationTxGateResult result = await gate.RequestKeyAsync(
            leaseId,
            SessionId,
            BrowserClientId);

        Assert.True(result.Success);
        Assert.Equal("key_pending", result.Code);
        Assert.Equal([true], fixture.Transport.Commands);
    }

    [Fact]
    public async Task ExternalSmartSdrTransmitBlocksKeyWithoutAnyCommand()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        ObserveExternalTx(fixture);

        StationTxGateResult result = await gate.RequestKeyAsync(
            leaseId,
            SessionId,
            BrowserClientId);

        Assert.False(result.Success);
        Assert.Equal("external_tx_owner", result.Code);
        Assert.Equal(StationTxGateState.Idle, result.Snapshot.State);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task ExternalOwnerAfterKeyRequestIsNeverUnkeyed()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        await gate.RequestKeyAsync(leaseId, SessionId, BrowserClientId);

        ObserveExternalTx(fixture);
        StationTxGateResult result = await gate.EvaluateAsync();

        Assert.False(result.Success);
        Assert.Equal("external_tx_owner", result.Code);
        Assert.Equal(StationTxGateState.Faulted, result.Snapshot.State);
        Assert.Equal([true], fixture.Transport.Commands);
        Assert.DoesNotContain(false, fixture.Transport.Commands);
    }

    [Fact]
    public async Task LeaseExpiryForceUnkeysOnlyProvenAetherOwnedTx()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(
            fixture,
            TxLeaseManager.MinimumLeaseDuration);
        ObserveIdle(fixture);
        await gate.RequestKeyAsync(leaseId, SessionId, BrowserClientId);
        ObserveAetherTx(fixture);
        Assert.Equal("keyed", (await gate.EvaluateAsync()).Code);

        fixture.Time.Advance(TimeSpan.FromSeconds(1.1));
        StationTxGateResult unkey = await gate.EvaluateAsync("lease-watchdog");

        Assert.True(unkey.Success);
        Assert.Equal("unkey_pending", unkey.Code);
        Assert.Equal(StationTxGateState.UnkeyPending, unkey.Snapshot.State);
        Assert.Equal([true, false], fixture.Transport.Commands);

        ObserveIdle(fixture);
        StationTxGateResult complete = await gate.EvaluateAsync();
        Assert.True(complete.Success);
        Assert.Equal("unkeyed", complete.Code);
        Assert.Equal(StationTxGateState.Idle, complete.Snapshot.State);
    }

    [Fact]
    public async Task LeaseExpiryProtectsExternalOwnerFromGlobalUnkey()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(
            fixture,
            TxLeaseManager.MinimumLeaseDuration);
        ObserveIdle(fixture);
        await gate.RequestKeyAsync(leaseId, SessionId, BrowserClientId);
        ObserveAetherTx(fixture);
        await gate.EvaluateAsync();

        ObserveExternalTx(fixture);
        fixture.Time.Advance(TimeSpan.FromSeconds(1.1));
        StationTxGateResult result = await gate.EvaluateAsync("lease-watchdog");

        Assert.False(result.Success);
        Assert.Equal("external_tx_owner", result.Code);
        Assert.Equal("external-owner-protected", result.Snapshot.Reason);
        Assert.Equal([true], fixture.Transport.Commands);
    }

    [Fact]
    public async Task WrongBrowserCannotUnkeyActiveIntent()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        await gate.RequestKeyAsync(leaseId, SessionId, BrowserClientId);
        ObserveAetherTx(fixture);
        await gate.EvaluateAsync();

        StationTxGateResult result = await gate.RequestUnkeyAsync(
            leaseId,
            SessionId,
            "browser-b");

        Assert.False(result.Success);
        Assert.Equal("tx_owner_required", result.Code);
        Assert.Equal(StationTxGateState.Keyed, result.Snapshot.State);
        Assert.Equal([true], fixture.Transport.Commands);
    }

    [Fact]
    public async Task KeyConfirmationTimeoutDoesNotSendBlindUnkey()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        await gate.RequestKeyAsync(leaseId, SessionId, BrowserClientId);

        fixture.Time.Advance(
            StationTxCommandGate.KeyConfirmationTimeout +
            TimeSpan.FromMilliseconds(1));
        ObserveIdle(fixture);
        StationTxGateResult result = await gate.EvaluateAsync();

        Assert.False(result.Success);
        Assert.Equal("key_confirmation_timeout", result.Code);
        Assert.Equal(StationTxGateState.Faulted, result.Snapshot.State);
        Assert.Equal([true], fixture.Transport.Commands);
    }

    [Fact]
    public async Task RejectedUnkeyRemainsGuardedAndRetriesWhileOwnershipIsProven()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        await gate.RequestKeyAsync(leaseId, SessionId, BrowserClientId);
        ObserveAetherTx(fixture);
        await gate.EvaluateAsync();
        fixture.Transport.NextResult =
            StationTxTransportResult.Rejected("radio busy");

        StationTxGateResult rejected = await gate.RequestUnkeyAsync(
            leaseId,
            SessionId,
            BrowserClientId);

        Assert.False(rejected.Success);
        Assert.Equal("unkey_command_rejected", rejected.Code);
        Assert.Equal(StationTxGateState.UnkeyPending, rejected.Snapshot.State);
        Assert.Equal(1, rejected.Snapshot.UnkeyAttempts);

        fixture.Time.Advance(
            StationTxCommandGate.UnkeyConfirmationTimeout +
            TimeSpan.FromMilliseconds(1));
        ObserveAetherTx(fixture);
        StationTxGateResult retry = await gate.EvaluateAsync();

        Assert.True(retry.Success);
        Assert.Equal("unkey_pending", retry.Code);
        Assert.Equal(2, retry.Snapshot.UnkeyAttempts);
        Assert.Equal([true, false, false], fixture.Transport.Commands);
    }

    [Fact]
    public async Task UnkeyRetriesAreBoundedAndRequireFreshExactOwnership()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        await gate.RequestKeyAsync(leaseId, SessionId, BrowserClientId);
        ObserveAetherTx(fixture);
        await gate.EvaluateAsync();

        StationTxGateResult first = await gate.RequestUnkeyAsync(
            leaseId,
            SessionId,
            BrowserClientId);
        Assert.Equal("unkey_pending", first.Code);

        for (int attempt = 2;
             attempt <= StationTxCommandGate.MaximumUnkeyAttempts;
             attempt++)
        {
            fixture.Time.Advance(
                StationTxCommandGate.UnkeyConfirmationTimeout +
                TimeSpan.FromMilliseconds(1));
            ObserveAetherTx(fixture);
            StationTxGateResult retry = await gate.EvaluateAsync();
            Assert.True(retry.Success);
            Assert.Equal("unkey_pending", retry.Code);
            Assert.Equal(attempt, retry.Snapshot.UnkeyAttempts);
        }

        fixture.Time.Advance(
            StationTxCommandGate.UnkeyConfirmationTimeout +
            TimeSpan.FromMilliseconds(1));
        ObserveAetherTx(fixture);
        StationTxGateResult exhausted = await gate.EvaluateAsync();

        Assert.False(exhausted.Success);
        Assert.Equal("unkey_confirmation_timeout", exhausted.Code);
        Assert.Equal(StationTxGateState.Faulted, exhausted.Snapshot.State);
        Assert.Equal(
            [true, false, false, false],
            fixture.Transport.Commands);
    }

    [Fact]
    public async Task FlexClientHandleChangeInvalidatesIntentWithoutGlobalUnkey()
    {
        GateFixture fixture = CreateFixture();
        await using StationTxCommandGate gate = fixture.Gate;
        string leaseId = AcquireLease(fixture);
        ObserveIdle(fixture);
        await gate.RequestKeyAsync(leaseId, SessionId, BrowserClientId);
        ObserveAetherTx(fixture);
        await gate.EvaluateAsync();

        fixture.Transport.ClientHandle = 0xABCDEF01;
        StationTxGateResult result = await gate.EvaluateAsync();

        Assert.False(result.Success);
        Assert.Equal("flex_client_lost", result.Code);
        Assert.Equal(StationTxGateState.Faulted, result.Snapshot.State);
        Assert.Equal([true], fixture.Transport.Commands);
    }

    private static GateFixture CreateFixture()
    {
        ManualTimeProvider time = NewTime();
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        FakeTxTransport transport = new(AetherHandle);
        StationTxCommandGate gate = new(
            allowTransmit: true,
            RadioId,
            leases,
            occupancy,
            transport,
            time);
        return new GateFixture(time, leases, occupancy, transport, gate);
    }

    private static string AcquireLease(
        GateFixture fixture,
        TimeSpan? duration = null)
    {
        Assert.True(fixture.Leases.TryAcquire(
            RadioId,
            SessionId,
            BrowserClientId,
            UserId,
            DisplayName,
            duration ?? TxLeaseManager.MaximumLeaseDuration,
            out TxLease? lease,
            out string? error), error);
        return Assert.IsType<TxLease>(lease).LeaseId;
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
            SmartSdrHandle,
            "SW",
            [
                AetherClient(localPtt: false),
                SmartSdrClient(localPtt: true)
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

    private static RadioGuiClientDiagnostics SmartSdrClient(bool localPtt) =>
        new(
            SmartSdrHandle,
            "smartsdr-client",
            "SmartSDR-Win",
            "STEVENS-SURFACE",
            string.Empty,
            localPtt,
            IsThisSession: false);

    private static ManualTimeProvider NewTime() =>
        new(new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero));

    private sealed record GateFixture(
        ManualTimeProvider Time,
        TxLeaseManager Leases,
        RadioTxOccupancyRegistry Occupancy,
        FakeTxTransport Transport,
        StationTxCommandGate Gate);

    private sealed class FakeTxTransport(uint clientHandle)
        : IStationTxCommandTransport
    {
        public bool IsConnected { get; set; } = true;
        public uint ClientHandle { get; set; } = clientHandle;
        public List<bool> Commands { get; } = [];
        public StationTxTransportResult NextResult { get; set; } =
            StationTxTransportResult.Ok;

        public Task<StationTxTransportResult> SetTransmitAsync(
            bool enabled,
            uint expectedClientHandle,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ClientHandle, expectedClientHandle);
            Commands.Add(enabled);
            StationTxTransportResult result = NextResult;
            NextResult = StationTxTransportResult.Ok;
            return Task.FromResult(result);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration) => m_now += duration;
    }
}
