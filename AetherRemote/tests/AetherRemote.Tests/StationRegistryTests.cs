using AetherRemote.Broker;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Tests;

public sealed class StationRegistryTests
{
    [Fact]
    public void ReplacingOneStationDoesNotInterruptAnother()
    {
        ManualTimeProvider clock = new();
        StationRegistry registry = CreateRegistry(clock);
        using StationConnectionLease first =
            registry.Open("station-one", "instance-a", "0.1.0", "10.0.0.1");
        using StationConnectionLease other =
            registry.Open("station-two", "instance-b", "0.1.0", "10.0.0.2");

        using StationConnectionLease replacement =
            registry.Open("station-one", "instance-c", "0.1.1", "10.0.0.3");

        Assert.True(first.ReplacementToken.IsCancellationRequested);
        Assert.False(other.ReplacementToken.IsCancellationRequested);
        Assert.False(replacement.ReplacementToken.IsCancellationRequested);
        Assert.Equal(2, registry.GetSnapshot().Count);
    }

    [Fact]
    public void InventoryAndHeartbeatSequencesMustIncrease()
    {
        ManualTimeProvider clock = new();
        StationRegistry registry = CreateRegistry(clock);
        using StationConnectionLease lease =
            registry.Open("station-one", "instance-a", "0.1.0", "10.0.0.1");
        StationInventoryMessage inventory = Inventory(sequence: 1);

        Assert.True(lease.UpdateInventory(inventory));
        Assert.False(lease.UpdateInventory(inventory));
        Assert.True(lease.Heartbeat(1));
        Assert.False(lease.Heartbeat(1));

        RemoteStationSnapshot snapshot = Assert.Single(
            registry.GetSnapshot());
        Assert.Single(snapshot.Radios);
        Assert.Equal(1, snapshot.InventorySequence);
        Assert.Equal(1, snapshot.HeartbeatSequence);
    }

    [Fact]
    public void StationTransitionsFromOnlineToDegradedToOffline()
    {
        ManualTimeProvider clock = new();
        StationRegistry registry = CreateRegistry(clock);
        StationConnectionLease lease =
            registry.Open("station-one", "instance-a", "0.1.0", "10.0.0.1");

        Assert.Equal("online", Assert.Single(registry.GetSnapshot()).State);
        clock.Advance(TimeSpan.FromSeconds(26));
        Assert.Equal("degraded", Assert.Single(registry.GetSnapshot()).State);

        lease.Dispose();

        Assert.Equal("offline", Assert.Single(registry.GetSnapshot()).State);
    }

    [Fact]
    public void DisconnectedStationCanReconnect()
    {
        ManualTimeProvider clock = new();
        StationRegistry registry = CreateRegistry(clock);
        StationConnectionLease first =
            registry.Open(
                "station-one",
                "instance-a",
                "0.1.0",
                "10.0.0.1");

        first.Dispose();

        using StationConnectionLease replacement =
            registry.Open(
                "station-one",
                "instance-b",
                "0.1.1",
                "10.0.0.2");
        RemoteStationSnapshot snapshot =
            Assert.Single(registry.GetSnapshot());
        Assert.Equal("online", snapshot.State);
        Assert.Equal(
            replacement.ConnectionId,
            snapshot.ConnectionId);
    }

    [Fact]
    public void ReconnectRetainsDisconnectReasonAndRecoveryDuration()
    {
        ManualTimeProvider clock = new();
        StationRegistry registry = CreateRegistry(clock);
        StationConnectionLease first = registry.Open(
            "station-one",
            "instance-a",
            "0.3.3",
            "10.0.0.1");

        clock.Advance(TimeSpan.FromSeconds(10));
        first.Dispose();
        RemoteStationSnapshot offline = Assert.Single(registry.GetSnapshot());
        Assert.Equal("offline", offline.State);
        Assert.Equal(1, offline.ConnectionCount);
        Assert.Equal("connection_closed", offline.LastDisconnectReason);
        Assert.Equal(clock.GetUtcNow(), offline.LastDisconnectedAt);
        Assert.Null(offline.LastRecoveredAt);
        Assert.Null(offline.LastRecoveryMilliseconds);

        clock.Advance(TimeSpan.FromSeconds(5));
        using StationConnectionLease replacement = registry.Open(
            "station-one",
            "instance-b",
            "0.3.4",
            "10.0.0.2");
        RemoteStationSnapshot recovered = Assert.Single(registry.GetSnapshot());
        Assert.Equal("online", recovered.State);
        Assert.Equal(2, recovered.ConnectionCount);
        Assert.Equal("connection_closed", recovered.LastDisconnectReason);
        Assert.Equal(offline.LastDisconnectedAt, recovered.LastDisconnectedAt);
        Assert.Equal(clock.GetUtcNow(), recovered.LastRecoveredAt);
        Assert.Equal(5_000, recovered.LastRecoveryMilliseconds);
    }

    [Fact]
    public void SilentStationExpiresWithoutInterruptingHealthyStation()
    {
        ManualTimeProvider clock = new();
        StationRegistry registry = CreateRegistry(clock);
        using StationConnectionLease silent =
            registry.Open(
                "station-silent",
                "instance-a",
                "0.3.3",
                "10.0.0.1");
        using StationConnectionLease healthy =
            registry.Open(
                "station-healthy",
                "instance-b",
                "0.3.3",
                "10.0.0.2");

        clock.Advance(TimeSpan.FromSeconds(26));
        Assert.True(healthy.Heartbeat(1));
        Assert.Empty(registry.ExpireStaleConnections());
        Assert.Equal(
            "degraded",
            registry.GetSnapshot()
                .Single(station => station.StationId == "station-silent")
                .State);

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(healthy.Heartbeat(2));
        Assert.Equal(
            ["station-silent"],
            registry.ExpireStaleConnections());

        Assert.True(silent.LivenessToken.IsCancellationRequested);
        Assert.False(healthy.LivenessToken.IsCancellationRequested);
        RemoteStationSnapshot expired = registry.GetSnapshot()
            .Single(station => station.StationId == "station-silent");
        Assert.Equal("offline", expired.State);
        Assert.Equal("heartbeat_timeout", expired.LastDisconnectReason);
        Assert.Equal(clock.GetUtcNow(), expired.LastDisconnectedAt);
        Assert.Equal(
            "online",
            registry.GetSnapshot()
                .Single(station => station.StationId == "station-healthy")
                .State);

        silent.Dispose();
        Assert.Equal(
            "heartbeat_timeout",
            registry.GetSnapshot()
                .Single(station => station.StationId == "station-silent")
                .LastDisconnectReason);
    }

    private static StationRegistry CreateRegistry(TimeProvider timeProvider) =>
        new(
            Options.Create(
                new StationLinkSettings
                {
                    HeartbeatSeconds = 10,
                    DegradedAfterSeconds = 25,
                    DisconnectAfterSeconds = 45
                }),
            timeProvider);

    private static StationInventoryMessage Inventory(long sequence) =>
        new(
            StationMessageTypes.Inventory,
            sequence,
            [
                new StationRadioAdvertisement(
                    "flex:1234",
                    "flex",
                    "FLEX-6700",
                    "1234",
                    "Test Radio",
                    "available",
                    2,
                    2,
                    string.Empty)
            ]);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset m_now =
            new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan interval)
        {
            m_now += interval;
        }
    }
}
