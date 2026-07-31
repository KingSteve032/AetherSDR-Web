using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class StationTxProductionLifecycleTests
{
    [Fact]
    public async Task ProductionLifecycleRegistersOnlyDisabledCommandIncapableStateMachines()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy);

        await lifecycle.FlushAsync();
        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.False(snapshot.ProductionTransmitEnabled);
        Assert.False(snapshot.CommandTransportAvailable);
        Assert.False(snapshot.EmergencyUnkeyTransportAvailable);
        Assert.True(snapshot.GatewayConnected);
        Assert.False(snapshot.EngineConnected);
        Assert.False(snapshot.BrowserConnected);
        Assert.False(snapshot.Authenticated);
        Assert.False(snapshot.LeaseActive);
        Assert.Equal("Disabled", snapshot.GateState);
        Assert.Equal("transmit-disabled", snapshot.GateReason);
        Assert.False(snapshot.GateHasActiveIntent);
        Assert.Equal("Disarmed", snapshot.SafetyState);
        Assert.Equal("disarmed", snapshot.SafetyReason);
        Assert.False(snapshot.SafetyActive);
        Assert.False(snapshot.ObservationFaulted);
        Assert.Equal(0, snapshot.BrowserObservationSequence);
        Assert.Null(snapshot.LastBrowserObservedAt);
        Assert.Equal(0, snapshot.EngineObservationSequence);
        Assert.Null(snapshot.LastEngineObservedAt);
        Assert.Equal(1, snapshot.GatewayObservationSequence);
        Assert.NotNull(snapshot.LastGatewayObservedAt);
        Assert.Equal(0, snapshot.LeaseObservationSequence);
        Assert.Null(snapshot.LastLeaseObservedAt);
        Assert.True(snapshot.WatchdogRunning);
        Assert.Equal(0, snapshot.WatchdogEvaluationSequence);
        Assert.Null(snapshot.LastWatchdogEvaluatedAt);
        Assert.False(snapshot.BrowserFresh);
        Assert.False(snapshot.EngineFresh);
        Assert.True(snapshot.GatewayFresh);
        Assert.False(snapshot.AuthorityFresh);
        Assert.Equal("no-active-lease", snapshot.AuthorityReason);
    }

    [Fact]
    public async Task ExactBrowserEngineAndLeaseObservationsNeverEnableKeying()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy);
        leases.Changed += lifecycle.ObserveLeaseChange;

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
            TimeSpan.FromSeconds(5),
            out TxLease? lease,
            out string? error), error);
        Assert.NotNull(lease);

        await lifecycle.FlushAsync();
        StationTxLifecycleDiagnostics active = lifecycle.Snapshot;

        Assert.True(active.BrowserConnected);
        Assert.True(active.Authenticated);
        Assert.Equal("connection-a", active.ConnectionClientId);
        Assert.True(active.EngineConnected);
        Assert.Equal(0x1234abcdu, active.StationClientHandle);
        Assert.True(active.LeaseActive);
        Assert.Equal(lease!.LeaseId, active.LeaseId);
        Assert.Equal("Disabled", active.GateState);
        Assert.False(active.GateHasActiveIntent);
        Assert.Equal("Disarmed", active.SafetyState);
        Assert.False(active.ProductionTransmitEnabled);
        Assert.Equal(1, active.BrowserObservationSequence);
        Assert.NotNull(active.LastBrowserObservedAt);
        Assert.Equal(1, active.EngineObservationSequence);
        Assert.NotNull(active.LastEngineObservedAt);
        Assert.Equal(1, active.LeaseObservationSequence);
        Assert.NotNull(active.LastLeaseObservedAt);
        Assert.True(active.BrowserFresh);
        Assert.True(active.EngineFresh);
        Assert.True(active.GatewayFresh);
        Assert.True(active.AuthorityFresh);
        Assert.Equal("fresh", active.AuthorityReason);

        Assert.True(leases.TryRelease(
            "radio-a",
            lease.LeaseId,
            "session-a",
            "connection-a",
            "test-release",
            out _));
        lifecycle.ObserveBrowserConnection(
            "connection-a",
            connected: false,
            authenticated: false);
        lifecycle.ObserveEngineConnection(
            connected: false,
            clientHandle: 0x1234abcd);
        lifecycle.ObserveGatewayConnection(connected: false);

        await lifecycle.FlushAsync();
        StationTxLifecycleDiagnostics released = lifecycle.Snapshot;

        Assert.False(released.GatewayConnected);
        Assert.False(released.EngineConnected);
        Assert.False(released.BrowserConnected);
        Assert.False(released.Authenticated);
        Assert.Null(released.ConnectionClientId);
        Assert.Equal(0u, released.StationClientHandle);
        Assert.False(released.LeaseActive);
        Assert.Null(released.LeaseId);
        Assert.Equal("Disabled", released.GateState);
        Assert.Equal("Disarmed", released.SafetyState);
        Assert.False(released.ObservationFaulted);
        Assert.False(released.AuthorityFresh);
        Assert.Equal("no-active-lease", released.AuthorityReason);
    }

    [Fact]
    public async Task BrowserStalenessReleasesTheExactTrackedLease()
    {
        ManualTimeProvider time = NewTime();
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            time);
        leases.Changed += lifecycle.ObserveLeaseChange;

        TxLease lease = await EstablishAuthorityAsync(
            lifecycle,
            leases,
            TimeSpan.FromSeconds(15));
        Assert.True(lifecycle.Snapshot.AuthorityFresh);

        time.Advance(
            StationTxProductionLifecycle.BrowserFreshnessTimeout +
            TimeSpan.FromMilliseconds(1));
        await lifecycle.EvaluateWatchdogAsync();
        await lifecycle.FlushAsync();

        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;
        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.False(snapshot.LeaseActive);
        Assert.Null(snapshot.LeaseId);
        Assert.False(snapshot.BrowserFresh);
        Assert.True(snapshot.EngineFresh);
        Assert.True(snapshot.GatewayFresh);
        Assert.False(snapshot.AuthorityFresh);
        Assert.Equal("no-active-lease", snapshot.AuthorityReason);
        Assert.Equal(1, snapshot.WatchdogEvaluationSequence);
        Assert.NotNull(snapshot.LastWatchdogEvaluatedAt);
        Assert.Equal(
            "lease-released-watchdog-browser-stale",
            snapshot.LastObservation);
        Assert.NotEqual(lease.LeaseId, snapshot.LeaseId);
        Assert.Equal("Disabled", snapshot.GateState);
        Assert.Equal("Disarmed", snapshot.SafetyState);
    }

    [Fact]
    public async Task EngineStalenessReleasesLeaseWhileOtherObservationsRemainFresh()
    {
        ManualTimeProvider time = NewTime();
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            time);
        leases.Changed += lifecycle.ObserveLeaseChange;

        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            TimeSpan.FromSeconds(15));
        time.Advance(TimeSpan.FromSeconds(9));
        lifecycle.ObserveBrowserActivity(
            "connection-a",
            authenticated: true);
        lifecycle.ObserveGatewayHeartbeat();
        await lifecycle.FlushAsync();
        time.Advance(TimeSpan.FromSeconds(2));

        await lifecycle.EvaluateWatchdogAsync();
        await lifecycle.FlushAsync();

        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;
        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.True(snapshot.BrowserFresh);
        Assert.False(snapshot.EngineFresh);
        Assert.True(snapshot.GatewayFresh);
        Assert.Equal(
            "lease-released-watchdog-engine-stale",
            snapshot.LastObservation);
        Assert.False(snapshot.ProductionTransmitEnabled);
        Assert.False(snapshot.CommandTransportAvailable);
        Assert.False(snapshot.EmergencyUnkeyTransportAvailable);
    }

    [Fact]
    public async Task GatewayStalenessReleasesLeaseWithoutTrustingOtherFreshSignals()
    {
        ManualTimeProvider time = NewTime();
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            time);
        leases.Changed += lifecycle.ObserveLeaseChange;

        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            TimeSpan.FromSeconds(15));
        time.Advance(TimeSpan.FromSeconds(9));
        lifecycle.ObserveBrowserActivity(
            "connection-a",
            authenticated: true);
        lifecycle.ObserveEngineHeartbeat(0x1234abcd);
        await lifecycle.FlushAsync();
        time.Advance(TimeSpan.FromSeconds(2));

        await lifecycle.EvaluateWatchdogAsync();
        await lifecycle.FlushAsync();

        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;
        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.True(snapshot.BrowserFresh);
        Assert.True(snapshot.EngineFresh);
        Assert.False(snapshot.GatewayFresh);
        Assert.Equal(
            "lease-released-watchdog-gateway-stale",
            snapshot.LastObservation);
        Assert.Equal("Disabled", snapshot.GateState);
        Assert.Equal("Disarmed", snapshot.SafetyState);
    }

    [Fact]
    public async Task FreshObservationsAfterWatchdogRevocationCannotRestoreTheLease()
    {
        ManualTimeProvider time = NewTime();
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            time);
        leases.Changed += lifecycle.ObserveLeaseChange;

        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            TimeSpan.FromSeconds(15));
        time.Advance(
            StationTxProductionLifecycle.BrowserFreshnessTimeout +
            TimeSpan.FromMilliseconds(1));
        await lifecycle.EvaluateWatchdogAsync();
        await lifecycle.FlushAsync();

        lifecycle.ObserveBrowserActivity(
            "connection-a",
            authenticated: true);
        lifecycle.ObserveEngineHeartbeat(0x1234abcd);
        lifecycle.ObserveGatewayHeartbeat();
        await lifecycle.EvaluateWatchdogAsync();
        await lifecycle.FlushAsync();

        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;
        Assert.True(snapshot.BrowserFresh);
        Assert.True(snapshot.EngineFresh);
        Assert.True(snapshot.GatewayFresh);
        Assert.False(snapshot.LeaseActive);
        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.False(snapshot.AuthorityFresh);
        Assert.Equal("no-active-lease", snapshot.AuthorityReason);
        Assert.Equal("gateway-heartbeat", snapshot.LastObservation);
    }

    [Fact]
    public async Task EngineDisconnectImmediatelyReleasesTheTrackedLease()
    {
        ManualTimeProvider time = NewTime();
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            time);
        leases.Changed += lifecycle.ObserveLeaseChange;

        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            TimeSpan.FromSeconds(15));
        lifecycle.ObserveEngineConnection(
            connected: false,
            clientHandle: 0x1234abcd);
        await lifecycle.FlushAsync();

        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;
        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.False(snapshot.LeaseActive);
        Assert.False(snapshot.EngineConnected);
        Assert.Equal(
            "lease-released-engine-disconnected",
            snapshot.LastObservation);
    }

    [Fact]
    public async Task AnotherSessionLeaseDoesNotEnterLifecycleAuthority()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy);
        leases.Changed += lifecycle.ObserveLeaseChange;

        Assert.True(leases.TryAcquire(
            "radio-a",
            "another-session",
            "another-client",
            "operator-b",
            "Operator B",
            TimeSpan.FromSeconds(5),
            out _,
            out string? error), error);

        await lifecycle.FlushAsync();
        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;

        Assert.False(snapshot.LeaseActive);
        Assert.Null(snapshot.LeaseId);
        Assert.Equal("Disabled", snapshot.GateState);
        Assert.Equal("Disarmed", snapshot.SafetyState);
    }

    [Fact]
    public async Task AnotherBrowserLeaseInTheSameSessionIsNotAuthority()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy);
        leases.Changed += lifecycle.ObserveLeaseChange;

        lifecycle.ObserveBrowserConnection(
            "connection-a",
            connected: true,
            authenticated: true);
        Assert.True(leases.TryAcquire(
            "radio-a",
            "session-a",
            "connection-b",
            "operator-b",
            "Operator B",
            TimeSpan.FromSeconds(5),
            out _,
            out string? error), error);

        await lifecycle.FlushAsync();
        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;

        Assert.True(snapshot.BrowserConnected);
        Assert.Equal("connection-a", snapshot.ConnectionClientId);
        Assert.False(snapshot.LeaseActive);
        Assert.Null(snapshot.LeaseId);
        Assert.Equal("Disabled", snapshot.GateState);
        Assert.Equal("Disarmed", snapshot.SafetyState);
    }

    [Fact]
    public async Task ExactActivityAndHeartbeatAdvanceDiagnosticsWhileMismatchesAreIgnored()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy);

        lifecycle.ObserveBrowserConnection(
            "connection-a",
            connected: true,
            authenticated: true);
        lifecycle.ObserveEngineConnection(
            connected: true,
            clientHandle: 0x1234abcd);
        await lifecycle.FlushAsync();
        StationTxLifecycleDiagnostics connected = lifecycle.Snapshot;

        lifecycle.ObserveBrowserActivity(
            "connection-b",
            authenticated: false);
        lifecycle.ObserveEngineHeartbeat(0x22222222);
        await lifecycle.FlushAsync();
        StationTxLifecycleDiagnostics mismatched = lifecycle.Snapshot;

        Assert.Equal(
            connected.BrowserObservationSequence,
            mismatched.BrowserObservationSequence);
        Assert.Equal(
            connected.EngineObservationSequence,
            mismatched.EngineObservationSequence);
        Assert.True(mismatched.Authenticated);
        Assert.Equal("Disabled", mismatched.GateState);
        Assert.Equal("Disarmed", mismatched.SafetyState);

        lifecycle.ObserveBrowserActivity(
            "connection-a",
            authenticated: true);
        lifecycle.ObserveEngineHeartbeat(0x1234abcd);
        lifecycle.ObserveGatewayHeartbeat();
        await lifecycle.FlushAsync();
        StationTxLifecycleDiagnostics exact = lifecycle.Snapshot;

        Assert.Equal(
            connected.BrowserObservationSequence + 1,
            exact.BrowserObservationSequence);
        Assert.Equal(
            connected.EngineObservationSequence + 1,
            exact.EngineObservationSequence);
        Assert.Equal(
            connected.GatewayObservationSequence + 1,
            exact.GatewayObservationSequence);
        Assert.Equal("gateway-heartbeat", exact.LastObservation);
        Assert.False(exact.ObservationFaulted);
        Assert.False(exact.ProductionTransmitEnabled);
        Assert.False(exact.CommandTransportAvailable);
        Assert.False(exact.EmergencyUnkeyTransportAvailable);
    }

    [Fact]
    public async Task ExactUnauthenticatedActivityReleasesOnlyTheCurrentBrowserLease()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy);
        leases.Changed += lifecycle.ObserveLeaseChange;

        lifecycle.ObserveBrowserConnection(
            "connection-a",
            connected: true,
            authenticated: true);
        Assert.True(leases.TryAcquire(
            "radio-a",
            "session-a",
            "connection-a",
            "operator-a",
            "Operator A",
            TimeSpan.FromSeconds(5),
            out TxLease? lease,
            out string? error), error);
        Assert.NotNull(lease);
        await lifecycle.FlushAsync();
        Assert.True(lifecycle.Snapshot.LeaseActive);

        lifecycle.ObserveBrowserActivity(
            "connection-b",
            authenticated: false);
        await lifecycle.FlushAsync();
        Assert.True(lifecycle.Snapshot.LeaseActive);
        Assert.NotNull(leases.GetCurrent("radio-a"));

        lifecycle.ObserveBrowserActivity(
            "connection-a",
            authenticated: false);
        await lifecycle.FlushAsync();

        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;
        Assert.False(snapshot.Authenticated);
        Assert.False(snapshot.LeaseActive);
        Assert.Null(snapshot.LeaseId);
        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.Equal("Disabled", snapshot.GateState);
        Assert.Equal("Disarmed", snapshot.SafetyState);
        Assert.False(snapshot.ObservationFaulted);
    }

    [Fact]
    public async Task GatewayDisconnectImmediatelyReleasesTheTrackedLease()
    {
        ManualTimeProvider time = NewTime();
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            time);
        leases.Changed += lifecycle.ObserveLeaseChange;

        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            TimeSpan.FromSeconds(15));
        lifecycle.ObserveGatewayConnection(connected: false);
        await lifecycle.FlushAsync();

        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;
        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.False(snapshot.LeaseActive);
        Assert.False(snapshot.GatewayConnected);
        Assert.Equal(
            "lease-released-gateway-disconnected",
            snapshot.LastObservation);
    }

    [Fact]
    public async Task WatchdogNeverReleasesAnotherBrowserLeaseInTheSameSession()
    {
        ManualTimeProvider time = NewTime();
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            time);
        leases.Changed += lifecycle.ObserveLeaseChange;

        lifecycle.ObserveBrowserConnection(
            "connection-a",
            connected: true,
            authenticated: true);
        Assert.True(leases.TryAcquire(
            "radio-a",
            "session-a",
            "connection-b",
            "operator-b",
            "Operator B",
            TimeSpan.FromSeconds(15),
            out TxLease? lease,
            out string? error), error);
        Assert.NotNull(lease);
        await lifecycle.FlushAsync();
        Assert.False(lifecycle.Snapshot.LeaseActive);

        time.Advance(
            StationTxProductionLifecycle.GatewayFreshnessTimeout +
            TimeSpan.FromMilliseconds(1));
        await lifecycle.EvaluateWatchdogAsync();
        await lifecycle.FlushAsync();

        TxLease? current = leases.GetCurrent("radio-a");
        Assert.NotNull(current);
        Assert.Equal(lease!.LeaseId, current.LeaseId);
        Assert.Equal("connection-b", current.ClientId);
        Assert.False(lifecycle.Snapshot.LeaseActive);
        Assert.Equal("no-active-lease", lifecycle.Snapshot.AuthorityReason);
    }

    [Fact]
    public async Task ProductionTransportsRejectEveryCommandSurface()
    {
        StationTxUnavailableCommandTransport command = new();
        StationTxUnavailableEmergencyUnkeyTransport emergency = new();

        StationTxTransportResult key = await command.SetTransmitAsync(
            enabled: true,
            CancellationToken.None);
        StationTxTransportResult unkey = await command.SetTransmitAsync(
            enabled: false,
            CancellationToken.None);
        StationTxTransportResult emergencyUnkey =
            await emergency.RequestUnkeyAsync(CancellationToken.None);

        Assert.False(command.IsConnected);
        Assert.Equal(0u, command.ClientHandle);
        Assert.False(emergency.IsConnected);
        Assert.Equal(StationTxTransportOutcome.Rejected, key.Outcome);
        Assert.Equal(StationTxTransportOutcome.Rejected, unkey.Outcome);
        Assert.Equal(
            StationTxTransportOutcome.Rejected,
            emergencyUnkey.Outcome);
    }

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
        await lifecycle.FlushAsync();
        return lease;
    }

    private static ManualTimeProvider NewTime() =>
        new(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));

    private static StationTxProductionLifecycle Create(
        TxLeaseManager leases,
        RadioTxOccupancyRegistry occupancy,
        TimeProvider? timeProvider = null) =>
        new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            leases,
            occupancy,
            NullLogger<StationTxProductionLifecycle>.Instance,
            timeProvider);

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
