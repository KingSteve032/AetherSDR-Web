using AetherSDR.TxWatchdog.Protocol;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxIndependentSafetyArmParticipantTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 2, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SnapshotRequiresIndependentArmingForKeyAndHeartbeat()
    {
        List<string> calls = [];
        FakeInner inner = new(calls)
        {
            Diagnostics = Diagnostics(
                armAvailable: true,
                heartbeatAvailable: true,
                abortAvailable: false)
        };
        FakeWatchdog watchdog = new(calls)
        {
            Current = Watchdog(
                armingAvailable: false,
                armed: false,
                state: "Disarmed")
        };
        StationTxIndependentSafetyArmParticipant subject = Subject(
            inner,
            watchdog);

        StationTxSafetyArmCompositionDiagnostics snapshot = subject.Snapshot;

        Assert.False(snapshot.ArmAvailable);
        Assert.False(snapshot.HeartbeatAvailable);
        Assert.False(snapshot.AbortAvailable);
        Assert.Equal(
            "independent-watchdog-arming-unavailable",
            snapshot.Reason);
    }

    [Fact]
    public async Task ArmOrdersIndependentBeforeLocalAndPreservesExactIdentity()
    {
        List<string> calls = [];
        FakeInner inner = new(calls)
        {
            ArmResult = Result(
                success: true,
                StationTxSafetyState.Armed)
        };
        FakeWatchdog watchdog = new(calls);
        WatchdogIdentity identity = Identity();
        StationTxIndependentSafetyArmParticipant subject = Subject(
            inner,
            watchdog,
            identity);

        StationTxSafetyArmCompositionResult result = await subject.ArmAsync(
            new(
                ConnectionClientId: "connection-a",
                HeartbeatTimeout: TimeSpan.FromSeconds(1)));

        Assert.True(result.Success);
        Assert.Equal(["watchdog-arm", "inner-arm"], calls);
        Assert.Equal(identity, watchdog.LastIdentity);
        Assert.Equal(TimeSpan.FromSeconds(1), watchdog.LastTimeout);
        Assert.Equal(1, watchdog.ArmCount);
        Assert.Equal(1, inner.ArmCount);
    }

    [Fact]
    public async Task LocalArmFailureDisarmsIndependentArm()
    {
        List<string> calls = [];
        FakeInner inner = new(calls)
        {
            ArmResult = Result(
                success: false,
                StationTxSafetyState.Disarmed)
        };
        FakeWatchdog watchdog = new(calls);
        StationTxIndependentSafetyArmParticipant subject = Subject(
            inner,
            watchdog);

        StationTxSafetyArmCompositionResult result = await subject.ArmAsync(
            new("connection-a", TimeSpan.FromSeconds(1)));

        Assert.False(result.Success);
        Assert.Equal(
            ["watchdog-arm", "inner-arm", "watchdog-disarm"],
            calls);
        Assert.Equal(1, watchdog.DisarmCount);
    }

    [Fact]
    public async Task HeartbeatOrdersIndependentBeforeLocal()
    {
        List<string> calls = [];
        FakeInner inner = new(calls)
        {
            HeartbeatResult = Result(
                success: true,
                StationTxSafetyState.Armed)
        };
        FakeWatchdog watchdog = new(calls)
        {
            Current = Watchdog(
                armingAvailable: true,
                armed: true,
                state: "Armed")
        };
        StationTxIndependentSafetyArmParticipant subject = Subject(
            inner,
            watchdog);

        StationTxSafetyArmCompositionResult result =
            await subject.HeartbeatAsync(
                new("connection-a", TimeSpan.FromMilliseconds(750)));

        Assert.True(result.Success);
        Assert.Equal(
            ["watchdog-heartbeat", "inner-heartbeat"],
            calls);
        Assert.Equal(TimeSpan.FromMilliseconds(750), watchdog.LastTimeout);
    }

    [Fact]
    public async Task AbortDisarmsIndependentOnlyAfterLocalDisarmed()
    {
        List<string> calls = [];
        FakeInner inner = new(calls)
        {
            AbortResult = Result(
                success: true,
                StationTxSafetyState.UnkeyPending)
        };
        FakeWatchdog watchdog = new(calls)
        {
            Current = Watchdog(
                armingAvailable: true,
                armed: true,
                state: "Armed")
        };
        StationTxIndependentSafetyArmParticipant subject = Subject(
            inner,
            watchdog);

        StationTxSafetyArmCompositionResult pending =
            await subject.AbortAsync(
                new("connection-a", "transaction-cleanup"));
        Assert.True(pending.Success);
        Assert.Equal(["inner-abort"], calls);
        Assert.Equal(0, watchdog.DisarmCount);

        calls.Clear();
        inner.AbortResult = Result(
            success: true,
            StationTxSafetyState.Disarmed);
        StationTxSafetyArmCompositionResult disarmed =
            await subject.AbortAsync(
                new("connection-a", "radio-confirmed-idle"));

        Assert.True(disarmed.Success);
        Assert.Equal(["inner-abort", "watchdog-disarm"], calls);
        Assert.Equal(1, watchdog.DisarmCount);
    }

    [Fact]
    public async Task RadioConfirmedAbortDisarmsIndependentDespiteCallerCancellation()
    {
        List<string> calls = [];
        using CancellationTokenSource cancellation = new();
        FakeInner inner = new(calls)
        {
            AbortResult = Result(
                success: true,
                StationTxSafetyState.Disarmed),
            BeforeAbortReturn = cancellation.Cancel
        };
        FakeWatchdog watchdog = new(calls)
        {
            Current = Watchdog(
                armingAvailable: true,
                armed: true,
                state: "Armed")
        };
        StationTxIndependentSafetyArmParticipant subject = Subject(
            inner,
            watchdog);

        StationTxSafetyArmCompositionResult result =
            await subject.AbortAsync(
                new("connection-a", "radio-confirmed-idle"),
                cancellation.Token);

        Assert.True(result.Success);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(["inner-abort", "watchdog-disarm"], calls);
        Assert.False(watchdog.LastDisarmCancellationRequested);
    }

    [Fact]
    public async Task MissingRegisteredIdentityRejectsBeforeAnyArm()
    {
        List<string> calls = [];
        FakeInner inner = new(calls);
        FakeWatchdog watchdog = new(calls);
        StationTxIndependentSafetyArmParticipant subject = Subject(
            inner,
            watchdog,
            resolveIdentity: false);

        StationTxSafetyArmCompositionResult result = await subject.ArmAsync(
            new("connection-a", TimeSpan.FromSeconds(1)));

        Assert.False(result.Success);
        Assert.Equal(
            "independent_watchdog_identity_unavailable",
            result.Code);
        Assert.Empty(calls);
    }

    private static StationTxIndependentSafetyArmParticipant Subject(
        FakeInner inner,
        FakeWatchdog watchdog,
        WatchdogIdentity? identity = null,
        bool resolveIdentity = true)
    {
        StationTxCommandAuthority authority = Authority();
        WatchdogIdentity? resolved = resolveIdentity
            ? identity ?? Identity()
            : null;
        return new(
            inner,
            watchdog,
            _ => StationTxCommandAuthorityResolution.Accepted(authority),
            _ => resolved);
    }

    private static StationTxCommandAuthority Authority()
    {
        RadioTxOccupant owner = new(
            ClientHandle: 0x12345678,
            Program: "AetherSDR",
            Station: "WEB",
            Source: "SW",
            AetherOwned: true);
        RadioTxOccupancySnapshot occupancy = new(
            "RADIO-A",
            RadioTxOccupancyState.Idle,
            Now,
            Now.AddSeconds(8),
            Occupants: [],
            LocalPttOwners: [owner]);
        StationTxSafetySnapshot safety = new(
            "RADIO-A",
            StationTxSafetyState.Disarmed,
            "disarmed",
            EngineInstanceId: null,
            LeaseId: null,
            SessionId: null,
            BrowserClientId: null,
            ProtectedClientHandle: 0,
            ArmedAt: null,
            LastHeartbeatAt: null,
            HeartbeatDeadlineAt: null,
            UnkeyDeadlineAt: null,
            UnkeyAttempts: 0,
            SawProtectedTransmit: false);
        return new(
            StationId: "station-a",
            RadioId: "RADIO-A",
            SessionId: "session-a",
            BrowserClientId: "browser-a",
            LeaseId: "lease-a",
            LeaseExpiresAt: Now.AddMinutes(1),
            GatewayInstanceId: "gateway-a",
            EngineInstanceId: "engine-a",
            ClientHandle: 0x12345678,
            Authenticated: true,
            BrowserFresh: true,
            EngineFresh: true,
            GatewayFresh: true,
            AuthorityFresh: true,
            occupancy,
            safety);
    }

    private static WatchdogIdentity Identity() =>
        new(
            "RADIO-A",
            "session-a",
            "browser-a",
            "gateway-a",
            "engine-a",
            "connection-a",
            "lease-a",
            0x12345678);

    private static StationTxSafetyArmCompositionDiagnostics Diagnostics(
        bool armAvailable = true,
        bool heartbeatAvailable = true,
        bool abortAvailable = true) =>
        new(
            Registered: true,
            ArmAuthorityAttached: true,
            ArmAuthorityRegistered: true,
            ArmAuthorityArmAvailable: armAvailable,
            ArmAuthorityHeartbeatAvailable: heartbeatAvailable,
            ArmAuthorityAbortAvailable: abortAvailable,
            SessionAuthoritySnapshotAvailable: true,
            ArmAvailable: armAvailable,
            HeartbeatAvailable: heartbeatAvailable,
            AbortAvailable: abortAvailable,
            AttemptCount: 0,
            ForwardedCount: 0,
            AcceptedCount: 0,
            RejectedCount: 0,
            LastOperation: "none",
            LastOutcome: "none",
            LastObservedAt: null,
            Reason: "ready");

    private static StationTxSafetyArmCompositionResult Result(
        bool success,
        StationTxSafetyState state)
    {
        StationTxSafetySnapshot safety = Authority().Safety with
        {
            State = state,
            Reason = state.ToString().ToLowerInvariant(),
            EngineInstanceId = state == StationTxSafetyState.Disarmed
                ? null
                : "engine-a",
            LeaseId = state == StationTxSafetyState.Disarmed
                ? null
                : "lease-a",
            SessionId = state == StationTxSafetyState.Disarmed
                ? null
                : "session-a",
            BrowserClientId = state == StationTxSafetyState.Disarmed
                ? null
                : "browser-a",
            ProtectedClientHandle = state == StationTxSafetyState.Disarmed
                ? 0u
                : 0x12345678u,
            ArmedAt = state == StationTxSafetyState.Disarmed ? null : Now,
            LastHeartbeatAt =
                state == StationTxSafetyState.Disarmed ? null : Now,
            HeartbeatDeadlineAt = state == StationTxSafetyState.Disarmed
                ? null
                : Now.AddSeconds(1)
        };
        StationTxSafetyResult safetyResult = new(
            success,
            success ? "accepted" : "rejected",
            success ? "accepted" : "rejected",
            safety);
        return new(
            success,
            success ? "accepted" : "rejected",
            success ? "accepted" : "rejected",
            Diagnostics(),
            safetyResult);
    }

    private static StationTxIndependentWatchdogDiagnostics Watchdog(
        bool armingAvailable,
        bool armed,
        string state) =>
        new(
            SupervisionEnabled: true,
            ProcessRunning: true,
            ProcessId: 123,
            HostInstanceId: "watchdog-a",
            ProcessStartedAt: Now,
            state,
            Reason: armed ? "armed-heartbeat-current" : "ready",
            IpcConnected: true,
            Registered: true,
            Connected: true,
            LeaseBound: true,
            LastSequence: 2,
            RestartCount: 0,
            LastObservation: armed
                ? "armed-exact-authority"
                : "registered-disarmed",
            LastObservedAt: Now,
            LastError: null,
            RadioCommandTransportAvailable: true,
            armingAvailable,
            armed,
            ArmedAt: armed ? Now : null,
            LastSafetyHeartbeatAt: armed ? Now : null,
            HeartbeatDeadlineAt: armed ? Now.AddSeconds(1) : null,
            HeartbeatTimeoutMilliseconds: armed ? 1000 : null);

    private sealed class FakeInner(List<string> calls) :
        IStationTxSafetyArmTransactionParticipant
    {
        public StationTxSafetyArmCompositionDiagnostics Diagnostics { get; set; } =
            StationTxIndependentSafetyArmParticipantTests.Diagnostics();
        public StationTxSafetyArmCompositionResult ArmResult { get; set; } =
            Result(true, StationTxSafetyState.Armed);
        public StationTxSafetyArmCompositionResult HeartbeatResult { get; set; } =
            Result(true, StationTxSafetyState.Armed);
        public StationTxSafetyArmCompositionResult AbortResult { get; set; } =
            Result(true, StationTxSafetyState.Disarmed);
        public Action? BeforeAbortReturn { get; set; }
        public int ArmCount { get; private set; }
        public StationTxSafetyArmCompositionDiagnostics Snapshot => Diagnostics;

        public Task<StationTxSafetyArmCompositionResult> ArmAsync(
            StationTxSafetyArmCompositionArmRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("inner-arm");
            ArmCount++;
            return Task.FromResult(ArmResult);
        }

        public Task<StationTxSafetyArmCompositionResult> HeartbeatAsync(
            StationTxSafetyArmCompositionHeartbeatRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("inner-heartbeat");
            return Task.FromResult(HeartbeatResult);
        }

        public Task<StationTxSafetyArmCompositionResult> AbortAsync(
            StationTxSafetyArmCompositionAbortRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("inner-abort");
            BeforeAbortReturn?.Invoke();
            return Task.FromResult(AbortResult);
        }
    }

    private sealed class FakeWatchdog(List<string> calls) :
        IStationTxIndependentWatchdog
    {
        public StationTxIndependentWatchdogDiagnostics Current { get; set; } =
            Watchdog(
                armingAvailable: true,
                armed: false,
                state: "Disarmed");
        public WatchdogIdentity? LastIdentity { get; private set; }
        public TimeSpan? LastTimeout { get; private set; }
        public int ArmCount { get; private set; }
        public int DisarmCount { get; private set; }
        public bool LastDisarmCancellationRequested { get; private set; }
        public StationTxIndependentWatchdogDiagnostics Snapshot => Current;

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<StationTxIndependentWatchdogDiagnostics> RegisterAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<StationTxIndependentWatchdogDiagnostics> HeartbeatAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<StationTxIndependentWatchdogDiagnostics> ArmAsync(
            WatchdogIdentity identity,
            TimeSpan heartbeatTimeout,
            CancellationToken cancellationToken = default)
        {
            calls.Add("watchdog-arm");
            LastIdentity = identity;
            LastTimeout = heartbeatTimeout;
            ArmCount++;
            Current = Watchdog(true, true, "Armed");
            return Task.FromResult(Current);
        }

        public Task<StationTxIndependentWatchdogDiagnostics>
            SafetyHeartbeatAsync(
                WatchdogIdentity identity,
                TimeSpan heartbeatTimeout,
                CancellationToken cancellationToken = default)
        {
            calls.Add("watchdog-heartbeat");
            LastIdentity = identity;
            LastTimeout = heartbeatTimeout;
            Current = Watchdog(true, true, "Armed");
            return Task.FromResult(Current);
        }

        public Task<StationTxIndependentWatchdogDiagnostics> DisarmAsync(
            WatchdogIdentity identity,
            CancellationToken cancellationToken = default)
        {
            calls.Add("watchdog-disarm");
            LastIdentity = identity;
            LastDisarmCancellationRequested =
                cancellationToken.IsCancellationRequested;
            DisarmCount++;
            Current = Watchdog(true, false, "Disarmed");
            return Task.FromResult(Current);
        }

        public Task<StationTxIndependentWatchdogDiagnostics>
            DisconnectAndResetAsync(
                WatchdogIdentity identity,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
