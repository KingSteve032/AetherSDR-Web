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

    private static StationTxProductionLifecycle Create(
        TxLeaseManager leases,
        RadioTxOccupancyRegistry occupancy) =>
        new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            leases,
            occupancy,
            NullLogger<StationTxProductionLifecycle>.Instance);
}
