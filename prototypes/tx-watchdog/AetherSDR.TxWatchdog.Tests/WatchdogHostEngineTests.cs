using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.TxWatchdog.Tests;

public sealed class WatchdogHostEngineTests
{
    [Fact]
    public void NewHostStartsEmptyDisarmedAndCommandIncapable()
    {
        ManualTimeProvider time = NewTime();
        WatchdogHostEngine engine = new(time, "host-a");

        WatchdogSnapshot snapshot = engine.Snapshot;

        Assert.Equal("host-a", snapshot.HostInstanceId);
        Assert.Equal("Disarmed", snapshot.State);
        Assert.Equal("unkey-transport-disabled-disarmed", snapshot.Reason);
        Assert.False(snapshot.RadioCommandTransportAvailable);
        Assert.False(snapshot.ArmingAvailable);
        Assert.False(snapshot.Registered);
        Assert.False(snapshot.Connected);
        Assert.Null(snapshot.Identity);
        Assert.False(snapshot.LeaseBound);
        Assert.Equal(0, snapshot.LastSequence);
        Assert.Equal("process-started-disarmed", snapshot.LastObservation);
        Assert.Null(snapshot.LastObservedAt);
    }

    [Fact]
    public void ExactIdentityAndMonotonicSequenceAdvanceObservationOnly()
    {
        ManualTimeProvider time = NewTime();
        WatchdogHostEngine engine = new(time, "host-a");
        WatchdogIdentity identity = WatchdogProtocolTests.Identity();

        WatchdogResponse registered = engine.Process(Request(
            "register-1",
            WatchdogRequestKind.Register,
            1,
            identity));
        time.Advance(TimeSpan.FromSeconds(1));
        WatchdogResponse heartbeat = engine.Process(Request(
            "heartbeat-2",
            WatchdogRequestKind.Heartbeat,
            2,
            identity));

        Assert.True(registered.Ok);
        Assert.True(heartbeat.Ok);
        Assert.Equal("Disarmed", heartbeat.Snapshot.State);
        Assert.False(heartbeat.Snapshot.RadioCommandTransportAvailable);
        Assert.False(heartbeat.Snapshot.ArmingAvailable);
        Assert.True(heartbeat.Snapshot.Registered);
        Assert.True(heartbeat.Snapshot.Connected);
        Assert.Equal(identity, heartbeat.Snapshot.Identity);
        Assert.True(heartbeat.Snapshot.LeaseBound);
        Assert.Equal(2, heartbeat.Snapshot.LastSequence);
        Assert.Equal(
            "heartbeat-observed-disarmed",
            heartbeat.Snapshot.LastObservation);
        Assert.Equal(time.GetUtcNow(), heartbeat.Snapshot.LastObservedAt);
    }

    [Fact]
    public void MismatchedIdentityAndStaleSequenceCannotReplaceState()
    {
        WatchdogHostEngine engine = new(NewTime(), "host-a");
        WatchdogIdentity identity = WatchdogProtocolTests.Identity();
        Assert.True(engine.Process(Request(
            "register-1",
            WatchdogRequestKind.Register,
            1,
            identity)).Ok);

        WatchdogIdentity other = identity with { LeaseId = "lease-b" };
        WatchdogResponse mismatch = engine.Process(Request(
            "heartbeat-2",
            WatchdogRequestKind.Heartbeat,
            2,
            other));
        WatchdogResponse stale = engine.Process(Request(
            "heartbeat-1",
            WatchdogRequestKind.Heartbeat,
            1,
            identity));

        Assert.False(mismatch.Ok);
        Assert.Equal("identity-mismatch", mismatch.Error);
        Assert.False(stale.Ok);
        Assert.Equal("stale-sequence", stale.Error);
        Assert.Equal(identity, engine.Snapshot.Identity);
        Assert.Equal(1, engine.Snapshot.LastSequence);
        Assert.Equal("registered-disarmed", engine.Snapshot.LastObservation);
    }

    [Fact]
    public void DisconnectRequiresExactReregistrationBeforeMoreHeartbeats()
    {
        WatchdogHostEngine engine = new(NewTime(), "host-a");
        WatchdogIdentity identity = WatchdogProtocolTests.Identity();
        Assert.True(engine.Process(Request(
            "register-1",
            WatchdogRequestKind.Register,
            1,
            identity)).Ok);

        WatchdogResponse disconnected = engine.Process(Request(
            "disconnect-2",
            WatchdogRequestKind.Disconnect,
            2,
            identity));
        WatchdogResponse rejectedHeartbeat = engine.Process(Request(
            "heartbeat-3",
            WatchdogRequestKind.Heartbeat,
            3,
            identity));
        WatchdogResponse reregistered = engine.Process(Request(
            "register-3",
            WatchdogRequestKind.Register,
            3,
            identity));

        Assert.True(disconnected.Ok);
        Assert.False(disconnected.Snapshot.Connected);
        Assert.Equal(
            "disconnect-observed-disarmed",
            disconnected.Snapshot.LastObservation);
        Assert.False(rejectedHeartbeat.Ok);
        Assert.Equal(
            "disconnected-registration-required",
            rejectedHeartbeat.Error);
        Assert.True(reregistered.Ok);
        Assert.True(reregistered.Snapshot.Connected);
        Assert.Equal("registered-disarmed", reregistered.Snapshot.LastObservation);
        Assert.Equal("Disarmed", reregistered.Snapshot.State);
    }

    private static WatchdogRequest Request(
        string requestId,
        WatchdogRequestKind kind,
        long sequence,
        WatchdogIdentity identity) =>
        new(
            WatchdogProtocol.Version,
            requestId,
            kind,
            sequence,
            identity);

    private static ManualTimeProvider NewTime() =>
        new(new DateTimeOffset(2026, 7, 31, 12, 30, 0, TimeSpan.Zero));

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
