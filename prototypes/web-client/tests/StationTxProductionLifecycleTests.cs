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
