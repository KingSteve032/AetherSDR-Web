using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxCommandAdapterCompositionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 1, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AdapterCompositionSurfaceIsInternalAndTyped()
    {
        Assert.False(typeof(StationTxCommandAdapterComposition).IsPublic);
        Assert.False(typeof(IStationTxCommandAdapterExecutor).IsPublic);
        Assert.False(typeof(StationTxCommandAdapterExecutorCapabilities).IsPublic);
        Assert.False(typeof(IStationTxCommandAdapter).IsPublic);
        Assert.True(typeof(StationTxCommandAdapterCompositionDiagnostics).IsPublic);
        Assert.Contains(
            typeof(IStationTxCommandAdapter),
            typeof(StationTxCommandAdapterComposition).GetInterfaces());
    }

    [Fact]
    public void ProductionShapeHasNoRegisteredAdapterOrExecutor()
    {
        StationTxCommandAuthority authority = CreateAuthority();
        StationTxCommandAdapterComposition composition = new(
            executor: null,
            _ => StationTxCommandAuthorityResolution.Accepted(authority),
            new ManualTimeProvider(Now));
        IStationTxCommandAdapter adapter = composition;

        StationTxCommandAdapterCompositionDiagnostics snapshot =
            composition.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.False(snapshot.ExecutorAttached);
        Assert.False(snapshot.ExecutorRegistered);
        Assert.False(snapshot.ExecutorArmingAvailable);
        Assert.False(snapshot.ExecutorSetTransmitAvailable);
        Assert.True(snapshot.AuthoritySnapshotAvailable);
        Assert.False(snapshot.CommandAdapterRegistered);
        Assert.False(snapshot.ArmingAvailable);
        Assert.False(snapshot.SetTransmitAvailable);
        Assert.Equal("executor-unattached", snapshot.Reason);
        Assert.False(adapter.IsRegistered);
        Assert.False(adapter.ArmingAvailable);
        Assert.False(adapter.SupportsSetTransmit);
    }

    [Fact]
    public void UnregisteredExecutorDoesNotRegisterTheAdapter()
    {
        RecordingExecutor executor = new()
        {
            Capabilities = new(
                Registered: false,
                ArmingAvailable: true,
                SetTransmitAvailable: true,
                Reason: "executor-staged-disabled")
        };
        StationTxCommandAdapterComposition composition = CreateComposition(executor);
        IStationTxCommandAdapter adapter = composition;

        StationTxCommandAdapterCompositionDiagnostics snapshot =
            composition.Snapshot;

        Assert.True(snapshot.ExecutorAttached);
        Assert.False(snapshot.ExecutorRegistered);
        Assert.False(snapshot.CommandAdapterRegistered);
        Assert.False(snapshot.ArmingAvailable);
        Assert.False(snapshot.SetTransmitAvailable);
        Assert.Equal("executor-staged-disabled", snapshot.Reason);
        Assert.False(adapter.IsRegistered);
    }

    [Fact]
    public void RegisteredExecutorWithoutArmingRemainsCommandIncapable()
    {
        RecordingExecutor executor = new()
        {
            Capabilities = ReadyCapabilities with
            {
                ArmingAvailable = false,
                Reason = "arming-disabled"
            }
        };
        StationTxCommandAdapterComposition composition = CreateComposition(executor);
        IStationTxCommandAdapter adapter = composition;

        StationTxCommandAdapterCompositionDiagnostics snapshot =
            composition.Snapshot;

        Assert.True(snapshot.CommandAdapterRegistered);
        Assert.False(snapshot.ArmingAvailable);
        Assert.False(snapshot.SetTransmitAvailable);
        Assert.Equal("executor-arming-unavailable", snapshot.Reason);
        Assert.True(adapter.IsRegistered);
        Assert.False(adapter.ArmingAvailable);
        Assert.False(adapter.SupportsSetTransmit);
    }

    [Fact]
    public void ReadyExecutorAndExactFreshAuthorityReportAvailability()
    {
        RecordingExecutor executor = new();
        StationTxCommandAdapterComposition composition = CreateComposition(executor);
        IStationTxCommandAdapter adapter = composition;

        StationTxCommandAdapterCompositionDiagnostics snapshot =
            composition.Snapshot;

        Assert.True(snapshot.ExecutorRegistered);
        Assert.True(snapshot.ExecutorArmingAvailable);
        Assert.True(snapshot.ExecutorSetTransmitAvailable);
        Assert.True(snapshot.AuthoritySnapshotAvailable);
        Assert.True(snapshot.CommandAdapterRegistered);
        Assert.True(snapshot.ArmingAvailable);
        Assert.True(snapshot.SetTransmitAvailable);
        Assert.Equal("ready", snapshot.Reason);
        Assert.True(adapter.IsRegistered);
        Assert.True(adapter.ArmingAvailable);
        Assert.True(adapter.SupportsSetTransmit);
    }

    [Fact]
    public async Task ExactCommandIsForwardedOnce()
    {
        RecordingExecutor executor = new();
        StationTxCommandAdapterComposition composition = CreateComposition(executor);
        StationTxValidatedCommand command = CreateCommand();

        StationTxTransportResult result = await composition.ExecuteAsync(command);

        Assert.Same(StationTxTransportResult.Ok, result);
        Assert.Single(executor.Commands);
        Assert.Same(command, executor.Commands[0]);
        StationTxCommandAdapterCompositionDiagnostics snapshot =
            composition.Snapshot;
        Assert.Equal(1, snapshot.AttemptCount);
        Assert.Equal(1, snapshot.ForwardedCount);
        Assert.Equal(1, snapshot.AcceptedCount);
        Assert.Equal(0, snapshot.RejectedCount);
        Assert.Equal("accepted", snapshot.LastOutcome);
    }

    [Theory]
    [InlineData(0, "station_mismatch")]
    [InlineData(1, "radio_mismatch")]
    [InlineData(2, "session_mismatch")]
    [InlineData(3, "browser_client_mismatch")]
    [InlineData(4, "lease_mismatch")]
    [InlineData(5, "gateway_instance_mismatch")]
    [InlineData(6, "engine_instance_mismatch")]
    [InlineData(7, "client_handle_mismatch")]
    public async Task IdentityMismatchNeverReachesExecutor(
        int mismatch,
        string expectedOutcome)
    {
        RecordingExecutor executor = new();
        StationTxCommandAdapterComposition composition = CreateComposition(executor);
        StationTxValidatedCommand command = CreateCommand();
        command = mismatch switch
        {
            0 => command with { StationId = "station-b" },
            1 => command with { RadioId = "RADIO-B" },
            2 => command with { SessionId = "session-b" },
            3 => command with { BrowserClientId = "browser-b" },
            4 => command with { LeaseId = "lease-b" },
            5 => command with { GatewayInstanceId = "gateway-b" },
            6 => command with { EngineInstanceId = "engine-b" },
            7 => command with { ClientHandle = 0x22222222 },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        StationTxTransportResult result = await composition.ExecuteAsync(command);

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal(expectedOutcome, composition.Snapshot.LastOutcome);
        Assert.Equal(1, composition.Snapshot.RejectedCount);
    }

    [Fact]
    public async Task UnsupportedCommandNeverReachesExecutor()
    {
        RecordingExecutor executor = new();
        StationTxCommandAdapterComposition composition = CreateComposition(executor);
        StationTxValidatedCommand command = CreateCommand() with
        {
            Action = (StationTxCommandAction)999
        };

        StationTxTransportResult result = await composition.ExecuteAsync(command);

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal("unsupported_command", composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task ExpiredCommandNeverReachesExecutor()
    {
        RecordingExecutor executor = new();
        StationTxCommandAdapterComposition composition = CreateComposition(executor);
        StationTxValidatedCommand command = CreateCommand() with
        {
            IssuedAt = Now - TimeSpan.FromSeconds(10),
            ExpiresAt = Now
        };

        StationTxTransportResult result = await composition.ExecuteAsync(command);

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal("command_stale", composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task AuthorityResolutionFailureNeverReachesExecutor()
    {
        RecordingExecutor executor = new();
        StationTxCommandAdapterComposition composition = new(
            executor,
            _ => throw new InvalidOperationException("resolver fault"),
            new ManualTimeProvider(Now));

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal(
            "authority_resolution_failed",
            composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task RejectedAuthorityNeverReachesExecutor()
    {
        RecordingExecutor executor = new();
        StationTxCommandAdapterComposition composition = new(
            executor,
            _ => StationTxCommandAuthorityResolution.Rejected(
                "lease-unavailable",
                "No exact lease is available."),
            new ManualTimeProvider(Now));

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal("lease-unavailable", composition.Snapshot.LastOutcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task StaleAuthorityFlagsNeverReachExecutor(int staleFlag)
    {
        RecordingExecutor executor = new();
        StationTxCommandAuthority authority = CreateAuthority();
        authority = staleFlag switch
        {
            0 => authority with { Authenticated = false },
            1 => authority with { BrowserFresh = false },
            2 => authority with { EngineFresh = false },
            3 => authority with { GatewayFresh = false },
            4 => authority with { AuthorityFresh = false },
            _ => throw new ArgumentOutOfRangeException(nameof(staleFlag))
        };
        StationTxCommandAdapterComposition composition =
            CreateComposition(executor, authority);

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal(
            staleFlag == 0 ? "authentication_stale" : "authority_stale",
            composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task ExpiredLeaseNeverReachesExecutor()
    {
        RecordingExecutor executor = new();
        StationTxCommandAuthority authority = CreateAuthority() with
        {
            LeaseExpiresAt = Now
        };
        StationTxCommandAdapterComposition composition =
            CreateComposition(executor, authority);

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal("lease_mismatch", composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task StaleOccupancyNeverReachesExecutor()
    {
        RecordingExecutor executor = new();
        StationTxCommandAuthority authority = CreateAuthority();
        authority = authority with
        {
            Occupancy = authority.Occupancy with { FreshUntil = Now }
        };
        StationTxCommandAdapterComposition composition =
            CreateComposition(executor, authority);

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal("occupancy_stale", composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task NonIdleOccupancyNeverReachesExecutor()
    {
        RecordingExecutor executor = new();
        StationTxCommandAuthority authority = CreateAuthority();
        authority = authority with
        {
            Occupancy = authority.Occupancy with
            {
                State = RadioTxOccupancyState.External
            }
        };
        StationTxCommandAdapterComposition composition =
            CreateComposition(executor, authority);

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal("radio_not_idle", composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task LocalPttMismatchNeverReachesExecutor()
    {
        RecordingExecutor executor = new();
        StationTxCommandAuthority authority = CreateAuthority();
        authority = authority with
        {
            Occupancy = authority.Occupancy with
            {
                LocalPttOwners =
                [
                    new RadioTxOccupant(
                        0x22222222,
                        "AetherSDR",
                        "AETHER-WEB-RX",
                        string.Empty,
                        AetherOwned: true)
                ]
            }
        };
        StationTxCommandAdapterComposition composition =
            CreateComposition(executor, authority);

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal(
            "local_ptt_authority_mismatch",
            composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task DisarmedSafetyNeverReachesExecutor()
    {
        RecordingExecutor executor = new();
        StationTxCommandAuthority authority = CreateAuthority();
        authority = authority with
        {
            Safety = authority.Safety with
            {
                State = StationTxSafetyState.Disarmed,
                Reason = "disarmed",
                EngineInstanceId = null,
                LeaseId = null,
                SessionId = null,
                BrowserClientId = null,
                ProtectedClientHandle = 0,
                HeartbeatDeadlineAt = null
            }
        };
        StationTxCommandAdapterComposition composition =
            CreateComposition(executor, authority);

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());

        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal("safety_not_armed", composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task ExecutorRejectionIsNotRetried()
    {
        RecordingExecutor executor = new()
        {
            Result = StationTxTransportResult.Rejected("executor rejected")
        };
        StationTxCommandAdapterComposition composition = CreateComposition(executor);

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());

        Assert.False(result.Success);
        Assert.True(result.OutcomeKnown);
        Assert.Single(executor.Commands);
        Assert.Equal(1, composition.Snapshot.ForwardedCount);
        Assert.Equal(1, composition.Snapshot.RejectedCount);
        Assert.Equal("executor-rejected", composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task UnknownExecutorOutcomeIsPreservedAndNotRetried()
    {
        RecordingExecutor executor = new()
        {
            Result = StationTxTransportResult.Unknown("outcome unknown")
        };
        StationTxCommandAdapterComposition composition = CreateComposition(executor);

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());

        Assert.False(result.Success);
        Assert.False(result.OutcomeKnown);
        Assert.Single(executor.Commands);
        Assert.Equal(1, composition.Snapshot.ForwardedCount);
        Assert.Equal(1, composition.Snapshot.RejectedCount);
        Assert.Equal(
            "executor-outcome-unknown",
            composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotStartAnAttempt()
    {
        RecordingExecutor executor = new();
        StationTxCommandAdapterComposition composition = CreateComposition(executor);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => composition.ExecuteAsync(
                CreateCommand(),
                cancellation.Token));

        Assert.Empty(executor.Commands);
        Assert.Equal(0, composition.Snapshot.AttemptCount);
        Assert.Equal("none", composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task ExecutorCancellationIsRecordedAndPropagated()
    {
        RecordingExecutor executor = new() { WaitForCancellation = true };
        StationTxCommandAdapterComposition composition = CreateComposition(executor);
        using CancellationTokenSource cancellation = new();

        Task<StationTxTransportResult> pending = composition.ExecuteAsync(
            CreateCommand(),
            cancellation.Token);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(executor.Commands);
        Assert.Equal(1, composition.Snapshot.ForwardedCount);
        Assert.Equal(1, composition.Snapshot.RejectedCount);
        Assert.Equal("cancelled", composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task ExecutorExceptionIsRecordedAndPropagated()
    {
        RecordingExecutor executor = new()
        {
            Exception = new InvalidOperationException("executor fault")
        };
        StationTxCommandAdapterComposition composition = CreateComposition(executor);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => composition.ExecuteAsync(CreateCommand()));

        Assert.Single(executor.Commands);
        Assert.Equal(1, composition.Snapshot.ForwardedCount);
        Assert.Equal(1, composition.Snapshot.RejectedCount);
        Assert.Equal("executor-exception", composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task FaultedExecutorCapabilitiesFailClosed()
    {
        RecordingExecutor executor = new() { ThrowOnCapabilities = true };
        StationTxCommandAdapterComposition composition = CreateComposition(executor);
        IStationTxCommandAdapter adapter = composition;

        Assert.False(adapter.IsRegistered);
        Assert.False(adapter.ArmingAvailable);
        Assert.False(adapter.SupportsSetTransmit);
        Assert.Equal(
            "executor-capabilities-faulted",
            composition.Snapshot.Reason);

        StationTxTransportResult result =
            await composition.ExecuteAsync(CreateCommand());
        Assert.False(result.Success);
        Assert.Empty(executor.Commands);
        Assert.Equal("executor_unregistered", composition.Snapshot.LastOutcome);
    }

    private static StationTxCommandAdapterComposition CreateComposition(
        RecordingExecutor executor,
        StationTxCommandAuthority? authority = null) =>
        new(
            executor,
            _ => StationTxCommandAuthorityResolution.Accepted(
                authority ?? CreateAuthority()),
            new ManualTimeProvider(Now));

    private static StationTxValidatedCommand CreateCommand() =>
        new(
            Guid.NewGuid().ToString("N"),
            Sequence: 1,
            StationId: "station-a",
            RadioId: "RADIO-A",
            SessionId: "session-a",
            BrowserClientId: "browser-a",
            LeaseId: "lease-a",
            GatewayInstanceId: "gateway-a",
            EngineInstanceId: "engine-a",
            ClientHandle: 0x11111111,
            StationTxCommandAction.SetTransmit,
            Enabled: true,
            IssuedAt: Now,
            ExpiresAt: Now + TimeSpan.FromSeconds(5));

    private static StationTxCommandAuthority CreateAuthority()
    {
        StationTxValidatedCommand command = CreateCommand();
        RadioTxOccupant localPttOwner = new(
            command.ClientHandle,
            "AetherSDR",
            "AETHER-WEB-RX",
            string.Empty,
            AetherOwned: true);
        RadioTxOccupancySnapshot occupancy = new(
            command.RadioId,
            RadioTxOccupancyState.Idle,
            Now,
            Now + TimeSpan.FromSeconds(8),
            Occupants: [],
            LocalPttOwners: [localPttOwner]);
        StationTxSafetySnapshot safety = new(
            command.RadioId,
            StationTxSafetyState.Armed,
            "armed",
            command.EngineInstanceId,
            command.LeaseId,
            command.SessionId,
            command.BrowserClientId,
            command.ClientHandle,
            ArmedAt: Now - TimeSpan.FromSeconds(1),
            LastHeartbeatAt: Now,
            HeartbeatDeadlineAt: Now + TimeSpan.FromSeconds(2),
            UnkeyDeadlineAt: null,
            UnkeyAttempts: 0,
            SawProtectedTransmit: false);
        return new StationTxCommandAuthority(
            command.StationId,
            command.RadioId,
            command.SessionId,
            command.BrowserClientId,
            command.LeaseId,
            LeaseExpiresAt: Now + TimeSpan.FromSeconds(20),
            command.GatewayInstanceId,
            command.EngineInstanceId,
            command.ClientHandle,
            Authenticated: true,
            BrowserFresh: true,
            EngineFresh: true,
            GatewayFresh: true,
            AuthorityFresh: true,
            occupancy,
            safety);
    }

    private static readonly StationTxCommandAdapterExecutorCapabilities
        ReadyCapabilities = new(
            Registered: true,
            ArmingAvailable: true,
            SetTransmitAvailable: true,
            Reason: "ready");

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingExecutor : IStationTxCommandAdapterExecutor
    {
        private StationTxCommandAdapterExecutorCapabilities m_capabilities =
            ReadyCapabilities;

        public StationTxCommandAdapterExecutorCapabilities Capabilities
        {
            get
            {
                if (ThrowOnCapabilities)
                {
                    throw new InvalidOperationException("capability fault");
                }
                return m_capabilities;
            }
            set => m_capabilities = value;
        }

        public List<StationTxValidatedCommand> Commands { get; } = [];
        public StationTxTransportResult Result { get; set; } =
            StationTxTransportResult.Ok;
        public Exception? Exception { get; set; }
        public bool WaitForCancellation { get; set; }
        public bool ThrowOnCapabilities { get; set; }
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<StationTxTransportResult> ExecuteAsync(
            StationTxValidatedCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            Entered.TrySetResult();
            if (Exception is not null)
            {
                throw Exception;
            }
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return Result;
        }
    }
}
