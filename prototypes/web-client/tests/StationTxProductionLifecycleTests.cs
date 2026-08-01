using AetherSDR.TxWatchdog.Protocol;
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
        Assert.Equal(1, snapshot.StationCommandProtocolVersion);
        Assert.True(snapshot.StationCommandBoundaryRegistered);
        Assert.False(snapshot.StationCommandBoundaryEnabled);
        Assert.False(snapshot.StationCommandSignatureVerificationAvailable);
        Assert.False(snapshot.StationCommandAdapterRegistered);
        Assert.False(snapshot.StationCommandArmingAvailable);
        Assert.False(snapshot.StationCommandSetTransmitAvailable);
        Assert.Equal(0, snapshot.StationCommandAuditCount);
        StationTxCommandAdapterCompositionDiagnostics adapterComposition =
            snapshot.StationCommandAdapterComposition;
        Assert.True(adapterComposition.Registered);
        Assert.False(adapterComposition.ExecutorAttached);
        Assert.False(adapterComposition.ExecutorRegistered);
        Assert.False(adapterComposition.AuthoritySnapshotAvailable);
        Assert.False(adapterComposition.CommandAdapterRegistered);
        Assert.False(adapterComposition.ArmingAvailable);
        Assert.False(adapterComposition.SetTransmitAvailable);
        Assert.Equal(0, adapterComposition.AttemptCount);
        Assert.Equal(0, adapterComposition.ForwardedCount);
        Assert.Equal("none", adapterComposition.LastOutcome);
        Assert.Equal("executor-unattached", adapterComposition.Reason);
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
    public async Task ReadySignatureVerifierDoesNotEnableCommands()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            stationCommandVerifier: new AlwaysAvailableSignatureVerifier());

        await lifecycle.FlushAsync();
        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;

        Assert.True(snapshot.StationCommandBoundaryRegistered);
        Assert.False(snapshot.StationCommandBoundaryEnabled);
        Assert.True(snapshot.StationCommandSignatureVerificationAvailable);
        Assert.False(snapshot.StationCommandAdapterRegistered);
        Assert.False(snapshot.StationCommandArmingAvailable);
        Assert.False(snapshot.StationCommandSetTransmitAvailable);
        Assert.Equal(0, snapshot.StationCommandAuditCount);
        Assert.True(snapshot.StationCommandAdapterComposition.Registered);
        Assert.False(
            snapshot.StationCommandAdapterComposition.ExecutorAttached);
        Assert.False(
            snapshot.StationCommandAdapterComposition.CommandAdapterRegistered);
        Assert.Equal(
            "executor-unattached",
            snapshot.StationCommandAdapterComposition.Reason);
        Assert.Equal("Disabled", snapshot.GateState);
        Assert.Equal("Disarmed", snapshot.SafetyState);
    }

    [Fact]
    public async Task AttachedExecutorStillCannotArmOrSetTransmit()
    {
        ManualTimeProvider time = NewTime();
        TxLeaseManager leases = new(time);
        RadioTxOccupancyRegistry occupancy = new(time);
        ReadyAdapterExecutor executor = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            time,
            stationCommandAdapterExecutor: executor);
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
        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;
        StationTxCommandAdapterCompositionDiagnostics adapter =
            snapshot.StationCommandAdapterComposition;

        Assert.True(adapter.Registered);
        Assert.True(adapter.ExecutorAttached);
        Assert.True(adapter.ExecutorRegistered);
        Assert.True(adapter.ExecutorArmingAvailable);
        Assert.True(adapter.ExecutorSetTransmitAvailable);
        Assert.True(adapter.AuthoritySnapshotAvailable);
        Assert.True(adapter.CommandAdapterRegistered);
        Assert.False(adapter.ArmingAvailable);
        Assert.False(adapter.SetTransmitAvailable);
        Assert.Equal("safety-not-armed", adapter.Reason);
        Assert.True(snapshot.StationCommandAdapterRegistered);
        Assert.False(snapshot.StationCommandArmingAvailable);
        Assert.False(snapshot.StationCommandSetTransmitAvailable);
        Assert.False(snapshot.StationCommandBoundaryEnabled);
        Assert.Equal("Disarmed", snapshot.SafetyState);
        Assert.Equal(0, executor.ExecuteCount);
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
        Assert.True(
            active.StationCommandAdapterComposition.AuthoritySnapshotAvailable);
        Assert.False(active.StationCommandAdapterComposition.ExecutorAttached);
        Assert.False(
            active.StationCommandAdapterComposition.CommandAdapterRegistered);
        Assert.False(active.StationCommandAdapterComposition.ArmingAvailable);
        Assert.False(
            active.StationCommandAdapterComposition.SetTransmitAvailable);
        Assert.Equal(
            "executor-unattached",
            active.StationCommandAdapterComposition.Reason);

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
    public async Task IndependentWatchdogBindsExactLeaseAndResetsAfterRelease()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        FakeIndependentWatchdogFactory watchdogFactory = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            independentWatchdogFactory: watchdogFactory);
        leases.Changed += lifecycle.ObserveLeaseChange;

        await lifecycle.StartAsync();
        TxLease lease = await EstablishAuthorityAsync(
            lifecycle,
            leases,
            TimeSpan.FromSeconds(15));

        WatchdogIdentity registered = Assert.IsType<WatchdogIdentity>(
            watchdogFactory.Watchdog.Identity);
        Assert.Equal("RADIO-A", registered.RadioId);
        Assert.Equal("session-a", registered.SessionId);
        Assert.Equal("browser-a", registered.BrowserClientId);
        Assert.Equal("gateway-a", registered.GatewayInstanceId);
        Assert.Equal("connection-a", registered.ConnectionClientId);
        Assert.Equal(lease.LeaseId, registered.LeaseId);
        Assert.Equal(0x1234abcdu, registered.StationClientHandle);
        Assert.Equal(1, watchdogFactory.Watchdog.RegisterCount);
        Assert.True(lifecycle.Snapshot.IndependentWatchdog.Registered);
        Assert.False(
            lifecycle.Snapshot.IndependentWatchdog
                .RadioCommandTransportAvailable);
        Assert.False(lifecycle.Snapshot.IndependentWatchdog.ArmingAvailable);

        lifecycle.ObserveBrowserActivity(
            "connection-a",
            authenticated: true);
        lifecycle.ObserveEngineHeartbeat(0x1234abcd);
        lifecycle.ObserveGatewayHeartbeat();
        await lifecycle.FlushAsync();

        Assert.True(watchdogFactory.Watchdog.HeartbeatCount >= 3);
        Assert.True(
            lifecycle.Snapshot.IndependentWatchdog.LastSequence >= 4);

        Assert.True(leases.TryRelease(
            "radio-a",
            lease.LeaseId,
            "session-a",
            "connection-a",
            "operator-release",
            out _));
        await lifecycle.FlushAsync();

        Assert.Equal(1, watchdogFactory.Watchdog.DisconnectCount);
        Assert.Null(watchdogFactory.Watchdog.Identity);
        Assert.False(lifecycle.Snapshot.IndependentWatchdog.Registered);
        Assert.False(lifecycle.Snapshot.IndependentWatchdog.LeaseBound);
        Assert.Equal(0, lifecycle.Snapshot.IndependentWatchdog.LastSequence);
    }

    [Fact]
    public async Task IndependentWatchdogRegistrationWithoutConfirmationReleasesLease()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        FakeIndependentWatchdogFactory watchdogFactory = new();
        watchdogFactory.Watchdog.FailRegistration = true;
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            independentWatchdogFactory: watchdogFactory);
        leases.Changed += lifecycle.ObserveLeaseChange;

        await lifecycle.StartAsync();
        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            TimeSpan.FromSeconds(15));
        await lifecycle.FlushAsync();

        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.False(lifecycle.Snapshot.LeaseActive);
        Assert.False(lifecycle.Snapshot.IndependentWatchdog.Registered);
        Assert.Equal(
            "lease-released-independent-watchdog-registration-failed",
            lifecycle.Snapshot.LastObservation);
    }

    [Fact]
    public async Task IndependentWatchdogNonAdvancingHeartbeatReleasesLease()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        FakeIndependentWatchdogFactory watchdogFactory = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            independentWatchdogFactory: watchdogFactory);
        leases.Changed += lifecycle.ObserveLeaseChange;

        await lifecycle.StartAsync();
        await EstablishAuthorityAsync(
            lifecycle,
            leases,
            TimeSpan.FromSeconds(15));
        watchdogFactory.Watchdog.FailHeartbeat = true;
        lifecycle.ObserveGatewayHeartbeat();
        await lifecycle.FlushAsync();
        await lifecycle.FlushAsync();

        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.False(lifecycle.Snapshot.LeaseActive);
        Assert.Equal(
            "lease-released-independent-watchdog-heartbeat-failed",
            lifecycle.Snapshot.LastObservation);
    }

    [Fact]
    public async Task IndependentWatchdogLossReleasesLeaseAndReadyCannotRestoreIt()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        FakeIndependentWatchdogFactory watchdogFactory = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            independentWatchdogFactory: watchdogFactory);
        leases.Changed += lifecycle.ObserveLeaseChange;

        await lifecycle.StartAsync();
        TxLease lease = await EstablishAuthorityAsync(
            lifecycle,
            leases,
            TimeSpan.FromSeconds(15));
        Assert.Equal(lease.LeaseId, lifecycle.Snapshot.LeaseId);

        await watchdogFactory.PublishAsync(
            StationTxIndependentWatchdogEventKind.Lost,
            "watchdog-process-exited");
        await lifecycle.FlushAsync();

        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.False(lifecycle.Snapshot.LeaseActive);
        Assert.Null(lifecycle.Snapshot.LeaseId);
        Assert.Contains(
            "independent-watchdog-process-exited",
            lifecycle.Snapshot.LastObservation,
            StringComparison.Ordinal);

        await watchdogFactory.PublishAsync(
            StationTxIndependentWatchdogEventKind.Ready,
            "watchdog-process-ready-disarmed");
        await lifecycle.FlushAsync();

        Assert.Null(leases.GetCurrent("radio-a"));
        Assert.False(lifecycle.Snapshot.LeaseActive);
        Assert.Equal(1, watchdogFactory.Watchdog.RegisterCount);
        Assert.Equal("no-active-lease", lifecycle.Snapshot.AuthorityReason);
    }

    [Fact]
    public async Task IndependentWatchdogLossDoesNotReleaseAnotherSessionLease()
    {
        TxLeaseManager leases = new();
        RadioTxOccupancyRegistry occupancy = new();
        FakeIndependentWatchdogFactory watchdogFactory = new();
        await using StationTxProductionLifecycle lifecycle = Create(
            leases,
            occupancy,
            independentWatchdogFactory: watchdogFactory);

        Assert.True(leases.TryAcquire(
            "radio-a",
            "session-b",
            "connection-b",
            "operator-b",
            "Operator B",
            TimeSpan.FromSeconds(15),
            out TxLease? otherLease,
            out string? error), error);
        Assert.NotNull(otherLease);

        await watchdogFactory.PublishAsync(
            StationTxIndependentWatchdogEventKind.Lost,
            "watchdog-process-exited");
        await lifecycle.FlushAsync();

        Assert.Equal(
            otherLease!.LeaseId,
            leases.GetCurrent("radio-a")?.LeaseId);
        Assert.False(lifecycle.Snapshot.LeaseActive);
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
        TimeProvider? timeProvider = null,
        IStationTxIndependentWatchdogFactory? independentWatchdogFactory = null,
        IStationTxCommandSignatureVerifier? stationCommandVerifier = null,
        IStationTxCommandAdapterExecutor? stationCommandAdapterExecutor = null) =>
        new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            leases,
            occupancy,
            NullLogger<StationTxProductionLifecycle>.Instance,
            timeProvider,
            independentWatchdogFactory,
            stationCommandVerifier,
            stationCommandSubmitter: null,
            stationCommandAdapterExecutor: stationCommandAdapterExecutor);

    private sealed class ReadyAdapterExecutor : IStationTxCommandAdapterExecutor
    {
        public StationTxCommandAdapterExecutorCapabilities Capabilities { get; } =
            new(
                Registered: true,
                ArmingAvailable: true,
                SetTransmitAvailable: true,
                Reason: "ready");

        public int ExecuteCount { get; private set; }

        public Task<StationTxTransportResult> ExecuteAsync(
            StationTxValidatedCommand command,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(StationTxTransportResult.Ok);
        }
    }

    private sealed class AlwaysAvailableSignatureVerifier :
        IStationTxCommandSignatureVerifier
    {
        public bool IsAvailable => true;

        public bool Verify(
            string keyId,
            ReadOnlySpan<byte> payload,
            ReadOnlySpan<byte> signature) => true;
    }

    private sealed class FakeIndependentWatchdogFactory :
        IStationTxIndependentWatchdogFactory
    {
        private Func<StationTxIndependentWatchdogEvent, ValueTask>? m_eventSink;

        public FakeIndependentWatchdog Watchdog { get; } = new();

        public IStationTxIndependentWatchdog Create(
            StationTxIndependentWatchdogOwner owner,
            Func<StationTxIndependentWatchdogEvent, ValueTask> eventSink)
        {
            m_eventSink = eventSink;
            Watchdog.Owner = owner;
            return Watchdog;
        }

        public ValueTask PublishAsync(
            StationTxIndependentWatchdogEventKind kind,
            string reason) =>
            m_eventSink is null
                ? throw new InvalidOperationException(
                    "The fake watchdog was not attached to a lifecycle.")
                : m_eventSink(new StationTxIndependentWatchdogEvent(
                    kind,
                    reason,
                    Watchdog.Snapshot.HostInstanceId,
                    DateTimeOffset.UtcNow));
    }

    private sealed class FakeIndependentWatchdog :
        IStationTxIndependentWatchdog
    {
        private long m_sequence;

        public StationTxIndependentWatchdogOwner? Owner { get; set; }
        public WatchdogIdentity? Identity { get; private set; }
        public int RegisterCount { get; private set; }
        public int HeartbeatCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public bool FailRegistration { get; set; }
        public bool FailHeartbeat { get; set; }

        public StationTxIndependentWatchdogDiagnostics Snapshot { get; private set; } =
            NewSnapshot(
                processRunning: false,
                ipcConnected: false,
                registered: false,
                connected: false,
                leaseBound: false,
                lastSequence: 0,
                lastObservation: "fake-created");

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Snapshot = NewSnapshot(
                processRunning: true,
                ipcConnected: true,
                registered: false,
                connected: false,
                leaseBound: false,
                lastSequence: 0,
                lastObservation: "fake-ready");
            return Task.CompletedTask;
        }

        public Task<StationTxIndependentWatchdogDiagnostics> RegisterAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            RegisterCount++;
            if (FailRegistration)
            {
                Identity = null;
                m_sequence = 0;
                Snapshot = NewSnapshot(
                    processRunning: true,
                    ipcConnected: true,
                    registered: false,
                    connected: false,
                    leaseBound: false,
                    lastSequence: 0,
                    lastObservation: "registration-not-confirmed");
                return Task.FromResult(Snapshot);
            }

            Identity = identity;
            m_sequence = 1;
            Snapshot = NewSnapshot(
                processRunning: true,
                ipcConnected: true,
                registered: true,
                connected: true,
                leaseBound: true,
                lastSequence: m_sequence,
                lastObservation: "registered-disarmed");
            return Task.FromResult(Snapshot);
        }

        public Task<StationTxIndependentWatchdogDiagnostics> HeartbeatAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Identity, identity);
            HeartbeatCount++;
            if (!FailHeartbeat)
            {
                m_sequence++;
            }
            Snapshot = NewSnapshot(
                processRunning: true,
                ipcConnected: true,
                registered: true,
                connected: true,
                leaseBound: true,
                lastSequence: m_sequence,
                lastObservation: "heartbeat-observed-disarmed");
            return Task.FromResult(Snapshot);
        }

        public Task<StationTxIndependentWatchdogDiagnostics>
            DisconnectAndResetAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Identity, identity);
            DisconnectCount++;
            Identity = null;
            m_sequence = 0;
            Snapshot = NewSnapshot(
                processRunning: true,
                ipcConnected: true,
                registered: false,
                connected: false,
                leaseBound: false,
                lastSequence: 0,
                lastObservation: "disconnect-reset-disarmed");
            return Task.FromResult(Snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static StationTxIndependentWatchdogDiagnostics NewSnapshot(
            bool processRunning,
            bool ipcConnected,
            bool registered,
            bool connected,
            bool leaseBound,
            long lastSequence,
            string lastObservation) =>
            new(
                SupervisionEnabled: true,
                processRunning,
                ProcessId: processRunning ? 12345 : null,
                HostInstanceId: "fake-watchdog",
                ProcessStartedAt: DateTimeOffset.UtcNow,
                State: "Disarmed",
                Reason: "command-incapable-skeleton",
                ipcConnected,
                registered,
                connected,
                leaseBound,
                lastSequence,
                RestartCount: 0,
                lastObservation,
                LastObservedAt: DateTimeOffset.UtcNow,
                LastError: null,
                RadioCommandTransportAvailable: false,
                ArmingAvailable: false);
    }

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
