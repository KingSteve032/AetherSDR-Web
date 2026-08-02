using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.TxWatchdog.Tests;

public sealed class WatchdogSafetyControllerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 2, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AvailableTransportDoesNotEnableArmingByItself()
    {
        ManualTimeProvider time = new(Start);
        FakeUnkeyTransport transport = new();
        WatchdogHostEngine engine = new(
            time,
            "host-a",
            transport,
            armingEnabled: false);
        WatchdogIdentity identity = WatchdogProtocolTests.Identity();
        Assert.True(engine.Process(Request(
            "register-1",
            WatchdogRequestKind.Register,
            1,
            identity)).Ok);

        WatchdogResponse arm = engine.Process(Request(
            "arm-2",
            WatchdogRequestKind.Arm,
            2,
            identity,
            1000));

        Assert.False(arm.Ok);
        Assert.Equal("arming-unavailable", arm.Error);
        Assert.Equal("Disarmed", arm.Snapshot.State);
        Assert.False(arm.Snapshot.ArmingAvailable);
        Assert.Equal(0, transport.AttemptCount);
    }

    [Fact]
    public async Task ExactArmHeartbeatDisconnectAndDeadlineUnkeyOnce()
    {
        ManualTimeProvider time = new(Start);
        FakeUnkeyTransport transport = new();
        await using WatchdogHostEngine engine = new(
            time,
            "host-a",
            transport,
            armingEnabled: true);
        WatchdogIdentity identity = WatchdogProtocolTests.Identity();
        Assert.True(engine.Process(Request(
            "register-1",
            WatchdogRequestKind.Register,
            1,
            identity)).Ok);

        WatchdogResponse armed = engine.Process(Request(
            "arm-2",
            WatchdogRequestKind.Arm,
            2,
            identity,
            1000));
        time.Advance(TimeSpan.FromMilliseconds(400));
        WatchdogResponse heartbeat = engine.Process(Request(
            "heartbeat-3",
            WatchdogRequestKind.Heartbeat,
            3,
            identity,
            1200));
        WatchdogResponse disconnected = engine.Process(Request(
            "disconnect-4",
            WatchdogRequestKind.Disconnect,
            4,
            identity));

        Assert.True(armed.Ok);
        Assert.Equal("Armed", armed.Snapshot.State);
        Assert.True(armed.Snapshot.Armed);
        Assert.True(armed.Snapshot.ArmingAvailable);
        Assert.True(heartbeat.Ok);
        Assert.Equal(
            Start.AddMilliseconds(1600),
            heartbeat.Snapshot.HeartbeatDeadlineAt);
        Assert.True(disconnected.Ok);
        Assert.False(disconnected.Snapshot.Connected);
        Assert.True(disconnected.Snapshot.Armed);
        Assert.Equal(
            "armed-disconnected-awaiting-deadline",
            disconnected.Snapshot.Reason);

        time.Advance(TimeSpan.FromMilliseconds(1200));
        await engine.EvaluateDeadlineAsync();
        await engine.EvaluateDeadlineAsync();

        WatchdogSnapshot final = engine.Snapshot;
        Assert.Equal("Disarmed", final.State);
        Assert.False(final.Armed);
        Assert.Equal("deadline-unkey-accepted", final.Reason);
        Assert.Equal(1, final.UnkeyAttemptCount);
        Assert.Equal(1, final.UnkeyAcceptedCount);
        Assert.Equal(0, final.UnkeyRejectedCount);
        Assert.Equal(0, final.UnkeyUnknownCount);
        Assert.Equal(1, transport.AttemptCount);
        Assert.Equal(identity.StationClientHandle, transport.LastHandle);
    }

    [Fact]
    public async Task KnownRejectionRequiresReconciliationAndNeverRetries()
    {
        ManualTimeProvider time = new(Start);
        FakeUnkeyTransport transport = new()
        {
            Result = WatchdogUnkeyTransportResult.Rejected("radio denied")
        };
        await using WatchdogHostEngine engine = ArmedEngine(time, transport);

        time.Advance(TimeSpan.FromSeconds(1));
        await engine.EvaluateDeadlineAsync();
        await engine.EvaluateDeadlineAsync();
        WatchdogResponse disarm = engine.Process(Request(
            "disarm-3",
            WatchdogRequestKind.Disarm,
            3,
            WatchdogProtocolTests.Identity()));

        WatchdogSnapshot snapshot = engine.Snapshot;
        Assert.Equal("ReconciliationRequired", snapshot.State);
        Assert.True(snapshot.Armed);
        Assert.Equal(1, snapshot.UnkeyAttemptCount);
        Assert.Equal(1, snapshot.UnkeyRejectedCount);
        Assert.Equal(0, snapshot.UnkeyUnknownCount);
        Assert.Equal(1, transport.AttemptCount);
        Assert.False(disarm.Ok);
        Assert.Equal("reconciliation-required", disarm.Error);
    }

    [Fact]
    public async Task UnknownOutcomeRequiresReconciliationAndNeverRetries()
    {
        ManualTimeProvider time = new(Start);
        FakeUnkeyTransport transport = new()
        {
            Result = WatchdogUnkeyTransportResult.Unknown("socket lost")
        };
        await using WatchdogHostEngine engine = ArmedEngine(time, transport);

        time.Advance(TimeSpan.FromSeconds(1));
        await engine.EvaluateDeadlineAsync();
        await engine.EvaluateDeadlineAsync();

        WatchdogSnapshot snapshot = engine.Snapshot;
        Assert.Equal("ReconciliationRequired", snapshot.State);
        Assert.Equal("deadline-unkey-outcome-unknown", snapshot.Reason);
        Assert.Equal(1, snapshot.UnkeyAttemptCount);
        Assert.Equal(1, snapshot.UnkeyUnknownCount);
        Assert.Equal(1, transport.AttemptCount);
    }

    [Fact]
    public async Task UnexpectedTransportExceptionRequiresReconciliation()
    {
        ManualTimeProvider time = new(Start);
        FakeUnkeyTransport transport = new()
        {
            Exception = new ApplicationException("unexpected transport failure")
        };
        await using WatchdogHostEngine engine = ArmedEngine(time, transport);

        time.Advance(TimeSpan.FromSeconds(1));
        await engine.EvaluateDeadlineAsync();
        await engine.EvaluateDeadlineAsync();

        WatchdogSnapshot snapshot = engine.Snapshot;
        Assert.Equal("ReconciliationRequired", snapshot.State);
        Assert.Equal("deadline-unkey-outcome-unknown", snapshot.Reason);
        Assert.Equal(1, snapshot.UnkeyAttemptCount);
        Assert.Equal(1, snapshot.UnkeyUnknownCount);
        Assert.Equal(1, transport.AttemptCount);
    }

    [Fact]
    public async Task ExactDisarmBeforeDeadlinePreventsUnkey()
    {
        ManualTimeProvider time = new(Start);
        FakeUnkeyTransport transport = new();
        await using WatchdogHostEngine engine = ArmedEngine(time, transport);
        WatchdogIdentity identity = WatchdogProtocolTests.Identity();

        WatchdogResponse disarm = engine.Process(Request(
            "disarm-3",
            WatchdogRequestKind.Disarm,
            3,
            identity));
        time.Advance(TimeSpan.FromSeconds(2));
        await engine.EvaluateDeadlineAsync();

        Assert.True(disarm.Ok);
        Assert.Equal("Disarmed", engine.Snapshot.State);
        Assert.False(engine.Snapshot.Armed);
        Assert.Equal(0, engine.Snapshot.UnkeyAttemptCount);
        Assert.Equal(0, transport.AttemptCount);
    }

    [Fact]
    public async Task MismatchedIdentityCannotHeartbeatDisarmOrReplaceArm()
    {
        ManualTimeProvider time = new(Start);
        FakeUnkeyTransport transport = new();
        await using WatchdogHostEngine engine = ArmedEngine(time, transport);
        WatchdogIdentity other = WatchdogProtocolTests.Identity() with
        {
            LeaseId = "lease-b"
        };

        WatchdogResponse heartbeat = engine.Process(Request(
            "heartbeat-3",
            WatchdogRequestKind.Heartbeat,
            3,
            other,
            1000));
        WatchdogResponse disarm = engine.Process(Request(
            "disarm-4",
            WatchdogRequestKind.Disarm,
            4,
            other));
        WatchdogResponse register = engine.Process(Request(
            "register-5",
            WatchdogRequestKind.Register,
            5,
            other));

        Assert.Equal("identity-mismatch", heartbeat.Error);
        Assert.Equal("identity-mismatch", disarm.Error);
        Assert.Equal("identity-mismatch", register.Error);
        Assert.Equal("Armed", engine.Snapshot.State);
        Assert.Equal(0, transport.AttemptCount);
    }

    private static WatchdogHostEngine ArmedEngine(
        ManualTimeProvider time,
        FakeUnkeyTransport transport)
    {
        WatchdogHostEngine engine = new(
            time,
            "host-a",
            transport,
            armingEnabled: true);
        WatchdogIdentity identity = WatchdogProtocolTests.Identity();
        Assert.True(engine.Process(Request(
            "register-1",
            WatchdogRequestKind.Register,
            1,
            identity)).Ok);
        Assert.True(engine.Process(Request(
            "arm-2",
            WatchdogRequestKind.Arm,
            2,
            identity,
            1000)).Ok);
        return engine;
    }

    private static WatchdogRequest Request(
        string requestId,
        WatchdogRequestKind kind,
        long sequence,
        WatchdogIdentity identity,
        int? heartbeatTimeoutMilliseconds = null) =>
        new(
            WatchdogProtocol.Version,
            requestId,
            kind,
            sequence,
            identity,
            heartbeatTimeoutMilliseconds);

    private sealed class FakeUnkeyTransport : IWatchdogUnkeyTransport
    {
        public bool IsAvailable { get; set; } = true;
        public int AttemptCount { get; private set; }
        public uint LastHandle { get; private set; }
        public WatchdogUnkeyTransportResult Result { get; set; } =
            WatchdogUnkeyTransportResult.Ok;
        public Exception? Exception { get; set; }

        public WatchdogUnkeyTransportDiagnostics Snapshot => new(
            Registered: true,
            ConfiguredEnabled: IsAvailable,
            Available: IsAvailable,
            RadioId: "RADIO-A",
            Port: 4992,
            CommandTimeoutMilliseconds: 2000,
            AttemptCount,
            ForwardedCount: AttemptCount,
            AcceptedCount: Result.Success ? AttemptCount : 0,
            RejectedCount:
                Result.Outcome == WatchdogUnkeyTransportOutcome.Rejected
                    ? AttemptCount
                    : 0,
            UnknownCount:
                Result.Outcome == WatchdogUnkeyTransportOutcome.Unknown
                    ? AttemptCount
                    : 0,
            LastProtectedClientHandle: LastHandle,
            LastOutcome: AttemptCount == 0
                ? "none"
                : Result.Outcome.ToString().ToLowerInvariant(),
            LastReason: AttemptCount == 0 ? "ready" : "completed",
            LastObservedAt: null);

        public Task<WatchdogUnkeyTransportResult> RequestUnkeyAsync(
            uint expectedProtectedClientHandle,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttemptCount++;
            LastHandle = expectedProtectedClientHandle;
            if (Exception is not null)
            {
                throw Exception;
            }
            return Task.FromResult(Result);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) =>
            new NoOpTimer();

        public void Advance(TimeSpan amount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
                amount,
                TimeSpan.Zero);
            m_now += amount;
        }

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose()
            {
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
