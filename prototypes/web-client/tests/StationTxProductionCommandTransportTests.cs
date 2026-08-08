using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class StationTxProductionCommandTransportTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 2, 0, 15, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultSettingsRegisterDisabledWithNoAllowlist()
    {
        StationTxCommandTransportRegistrationDiagnostics diagnostics =
            StationTxCommandTransportSettingsValidator.CreateDiagnostics(
                new StationTxCommandTransportSettings());

        Assert.True(diagnostics.Registered);
        Assert.False(diagnostics.ConfiguredEnabled);
        Assert.Equal(0, diagnostics.AllowedRadioCount);
        Assert.Equal(2000, diagnostics.CommandTimeoutMilliseconds);
        Assert.Equal("transport-disabled", diagnostics.Reason);
    }

    [Fact]
    public void EnabledSettingsRequireExactRadioAllowlist()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => StationTxCommandTransportSettingsValidator.Validate(new()
            {
                Enabled = true,
                AllowedRadioIds = []
            }));

        Assert.Contains("AllowedRadioIds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowedRadioIdsAreCanonicalAndUnique()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => StationTxCommandTransportSettingsValidator.Validate(new()
            {
                Enabled = true,
                AllowedRadioIds = ["radio-a", " RADIO-A "]
            }));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(249)]
    [InlineData(5001)]
    public void CommandTimeoutIsBounded(int timeoutMilliseconds)
    {
        Assert.Throws<InvalidOperationException>(
            () => StationTxCommandTransportSettingsValidator.Validate(new()
            {
                CommandTimeoutMilliseconds = timeoutMilliseconds
            }));
    }

    [Fact]
    public void AllowedRadioCountIsBounded()
    {
        Assert.Throws<InvalidOperationException>(
            () => StationTxCommandTransportSettingsValidator.Validate(new()
            {
                AllowedRadioIds = Enumerable.Range(0, 17)
                    .Select(index => $"radio-{index}")
                    .ToArray()
            }));
    }

    [Fact]
    public async Task DisabledTransportRejectsBeforeCommandChannel()
    {
        ManualTimeProvider time = new(Start);
        FakeCommandChannel channel = ReadyChannel();
        StationTxProductionCommandTransport transport = new(
            new StationTxCommandTransportSettings(),
            "radio-a",
            localFlexEligible: true,
            channel,
            time);

        StationTxTransportResult result = await transport.SetTransmitAsync(
            enabled: true,
            expectedClientHandle: channel.ClientHandle,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Rejected, result.Outcome);
        Assert.Empty(channel.Commands);
        StationTxProductionCommandTransportDiagnostics diagnostics =
            transport.Snapshot;
        Assert.True(diagnostics.Registered);
        Assert.False(diagnostics.ConfiguredEnabled);
        Assert.True(diagnostics.CommandChannelAttached);
        Assert.True(diagnostics.ClientHandleAvailable);
        Assert.False(diagnostics.Available);
        Assert.Equal("transport-disabled", diagnostics.LastReason);
        Assert.Equal(1, diagnostics.AttemptCount);
        Assert.Equal(0, diagnostics.ForwardedCount);
        Assert.Equal(1, diagnostics.RejectedCount);
    }

    [Fact]
    public async Task RadioMustBeExplicitlyAllowed()
    {
        FakeCommandChannel channel = ReadyChannel();
        StationTxProductionCommandTransport transport = CreateEnabled(
            channel,
            radioId: "radio-b",
            allowedRadioIds: ["radio-a"]);

        StationTxTransportResult result = await transport.SetTransmitAsync(
            enabled: true,
            expectedClientHandle: channel.ClientHandle,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Rejected, result.Outcome);
        Assert.Equal("radio-not-allowed", transport.Snapshot.LastReason);
        Assert.Empty(channel.Commands);
    }

    [Fact]
    public async Task RemoteOrSimulationSessionIsNeverEligible()
    {
        FakeCommandChannel channel = ReadyChannel();
        StationTxProductionCommandTransport transport = new(
            EnabledSettings("radio-a"),
            "radio-a",
            localFlexEligible: false,
            channel);

        StationTxTransportResult result = await transport.SetTransmitAsync(
            enabled: true,
            expectedClientHandle: channel.ClientHandle,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Rejected, result.Outcome);
        Assert.Equal("local-flex-ineligible", transport.Snapshot.LastReason);
        Assert.Empty(channel.Commands);
    }

    [Fact]
    public async Task AttachedChannelAndNonzeroHandleAreRequired()
    {
        FakeCommandChannel channel = new()
        {
            IsAttached = false,
            ClientHandle = 0x1234
        };
        StationTxProductionCommandTransport transport = CreateEnabled(channel);

        StationTxTransportResult result = await transport.SetTransmitAsync(
            enabled: true,
            expectedClientHandle: 0x1234,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Rejected, result.Outcome);
        Assert.Equal("command-channel-unattached", transport.Snapshot.LastReason);
        Assert.Empty(channel.Commands);
    }

    [Fact]
    public async Task ExactExpectedHandleIsRequiredBeforeForwarding()
    {
        FakeCommandChannel channel = ReadyChannel();
        StationTxProductionCommandTransport transport = CreateEnabled(channel);

        StationTxTransportResult result = await transport.SetTransmitAsync(
            enabled: true,
            expectedClientHandle: channel.ClientHandle + 1,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Rejected, result.Outcome);
        Assert.Equal("client-handle-mismatch", transport.Snapshot.LastReason);
        Assert.Empty(channel.Commands);
    }

    [Fact]
    public async Task KeyUsesExactCommandHandleAndBoundedTimeoutOnce()
    {
        FakeCommandChannel channel = ReadyChannel();
        StationTxProductionCommandTransport transport = CreateEnabled(channel);

        StationTxTransportResult result = await transport.SetTransmitAsync(
            enabled: true,
            expectedClientHandle: channel.ClientHandle,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Accepted, result.Outcome);
        CommandCall command = Assert.Single(channel.Commands);
        Assert.Equal(StationTxProductionCommandTransport.KeyCommand, command.Command);
        Assert.Equal(channel.ClientHandle, command.ExpectedClientHandle);
        Assert.Equal(TimeSpan.FromSeconds(2), command.Timeout);
        StationTxProductionCommandTransportDiagnostics diagnostics =
            transport.Snapshot;
        Assert.Equal(1, diagnostics.AttemptCount);
        Assert.Equal(1, diagnostics.ForwardedCount);
        Assert.Equal(1, diagnostics.KeyAttemptCount);
        Assert.Equal(0, diagnostics.UnkeyAttemptCount);
        Assert.Equal(1, diagnostics.AcceptedCount);
        Assert.Equal("accepted", diagnostics.LastReason);
    }

    [Fact]
    public async Task UnkeyPreservesKnownRadioRejectionWithoutRetry()
    {
        FakeCommandChannel channel = ReadyChannel();
        channel.Handler = (_, _, _, _) =>
            Task.FromResult(new FlexCommandResponse(0x50001000, "denied"));
        StationTxProductionCommandTransport transport = CreateEnabled(channel);

        StationTxTransportResult result = await transport.SetTransmitAsync(
            enabled: false,
            expectedClientHandle: channel.ClientHandle,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Rejected, result.Outcome);
        Assert.Contains("0x50001000", result.Message, StringComparison.Ordinal);
        CommandCall command = Assert.Single(channel.Commands);
        Assert.Equal(StationTxProductionCommandTransport.UnkeyCommand, command.Command);
        Assert.Equal(1, transport.Snapshot.RejectedCount);
        Assert.Equal("radio-rejected", transport.Snapshot.LastReason);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task SocketAndTimeoutFailuresRemainUnknownWithoutRetry(
        string failure)
    {
        FakeCommandChannel channel = ReadyChannel();
        channel.Handler = (_, _, _, _) => failure switch
        {
            "io" => throw new IOException("socket closed"),
            "timeout" => throw new TimeoutException("response timeout"),
            _ => throw new InvalidOperationException("unsupported test failure")
        };
        StationTxProductionCommandTransport transport = CreateEnabled(channel);

        StationTxTransportResult result = await transport.SetTransmitAsync(
            enabled: true,
            expectedClientHandle: channel.ClientHandle,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Unknown, result.Outcome);
        Assert.False(result.OutcomeKnown);
        Assert.Single(channel.Commands);
        Assert.Equal(1, transport.Snapshot.UnknownCount);
        Assert.Equal("command-outcome-unknown", transport.Snapshot.LastReason);
    }

    [Fact]
    public async Task ReplacedChannelHandleIsKnownRejectionWithoutRetry()
    {
        FakeCommandChannel channel = ReadyChannel();
        channel.Handler = (expected, _, _, _) =>
        {
            channel.ClientHandle = expected + 1;
            throw new InvalidOperationException(
                "The exact Flex radio client handle is no longer connected.");
        };
        StationTxProductionCommandTransport transport = CreateEnabled(channel);

        StationTxTransportResult result = await transport.SetTransmitAsync(
            enabled: true,
            expectedClientHandle: channel.ClientHandle,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Rejected, result.Outcome);
        Assert.True(result.OutcomeKnown);
        Assert.Single(channel.Commands);
        Assert.Equal("command-channel-rejected", transport.Snapshot.LastReason);
    }

    [Fact]
    public async Task PreCancelledOperationIsNotCountedOrForwarded()
    {
        FakeCommandChannel channel = ReadyChannel();
        StationTxProductionCommandTransport transport = CreateEnabled(channel);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.SetTransmitAsync(
                enabled: true,
                expectedClientHandle: channel.ClientHandle,
                cancellation.Token));

        Assert.Equal(0, transport.Snapshot.AttemptCount);
        Assert.Empty(channel.Commands);
    }

    [Fact]
    public async Task RadioMessagesAreBoundedAndControlCharactersAreRemoved()
    {
        FakeCommandChannel channel = ReadyChannel();
        string body = new string('x', 400) + "\r\nsecret";
        channel.Handler = (_, _, _, _) =>
            Task.FromResult(new FlexCommandResponse(1, body));
        StationTxProductionCommandTransport transport = CreateEnabled(channel);

        StationTxTransportResult result = await transport.SetTransmitAsync(
            enabled: false,
            expectedClientHandle: channel.ClientHandle,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Rejected, result.Outcome);
        Assert.True(result.Message.Length <= 256);
        Assert.DoesNotContain('\r', result.Message);
        Assert.DoesNotContain('\n', result.Message);
    }

    [Fact]
    public async Task RouterRejectsMismatchedHandleBeforeUsingControlSession()
    {
        FlexRadioCommandRouter router = new();
        await using FlexControlSession control = new(NullLogger.Instance);
        router.Attach(control, 0x1234, "0x40000000", 14_200_000);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => router.SendForClientAsync(
                    0x5678,
                    StationTxProductionCommandTransport.KeyCommand,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None));

        Assert.Contains("exact", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0x1234u, router.ClientHandle);
        router.Detach(control);
        Assert.Equal(0u, router.ClientHandle);
    }

    [Fact]
    public void BrowserAndExternalExecutionTypesReceiveNoProductionTransport()
    {
        Type[] forbidden =
        [
            typeof(StationTxProductionCommandTransport),
            typeof(IStationTxProductionCommandTransport),
            typeof(IStationTxCommandTransport),
            typeof(IStationTxFlexCommandChannel)
        ];
        Type[] externalTypes =
        [
            typeof(RadioWebSocketEndpoint),
            typeof(RadioCoordinator),
            typeof(RemoteRadioProjectionService),
            typeof(StationTxIndependentWatchdogRegistry),
            typeof(StationTxIndependentWatchdogClient)
        ];

        foreach (Type externalType in externalTypes)
        {
            IEnumerable<Type> exposedTypes =
                externalType.GetConstructors(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance)
                    .SelectMany(constructor => constructor.GetParameters())
                    .Select(parameter => parameter.ParameterType)
                    .Concat(
                        externalType.GetMethods(
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic |
                                System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.DeclaredOnly)
                            .SelectMany(method => method.GetParameters())
                            .Select(parameter => parameter.ParameterType))
                    .Concat(
                        externalType.GetFields(
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic |
                                System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.DeclaredOnly)
                            .Select(field => field.FieldType));

            Assert.DoesNotContain(
                exposedTypes,
                type => forbidden.Any(candidate => ContainsType(type, candidate)));
        }
    }

    [Fact]
    public async Task LifecycleRegistersTransportButGateRemainsTransmitDisabled()
    {
        FakeCommandChannel channel = ReadyChannel();
        StationTxProductionCommandTransport transport = CreateEnabled(channel);
        ManualTimeProvider time = new(Start);
        await using StationTxProductionLifecycle lifecycle = new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            new TxLeaseManager(time),
            new RadioTxOccupancyRegistry(time),
            NullLogger<StationTxProductionLifecycle>.Instance,
            timeProvider: time,
            productionReadinessConfiguration: new(
                AllowTransmitConfigured: false,
                BrowserTxLeaseConfigured: false),
            productionCommandTransport: transport);

        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;
        Assert.True(snapshot.ProductionCommandTransport.Registered);
        Assert.True(snapshot.ProductionCommandTransport.Available);
        Assert.True(snapshot.CommandTransportAvailable);
        Assert.Equal("Disabled", snapshot.GateState);
        Assert.False(snapshot.StationCommandSetTransmitAvailable);
        Assert.False(snapshot.ProductionTransmitEnabled);
        Assert.DoesNotContain(
            "command-transport-unavailable",
            snapshot.ProductionReadiness.MissingPrerequisites);

        StationTxCommandTransactionResult result =
            await lifecycle.ExecuteStationCommandTransactionAsync(new(
                "connection-a",
                Sequence: 1,
                new BrowserTxIntent(
                    "intent-000000000000000000000000000001",
                    BrowserTxIntentKind.Mox,
                    "mox.set",
                    Enabled: true,
                    Text: null),
                time.GetUtcNow(),
                TimeSpan.FromSeconds(1)));

        Assert.Equal(StationTxCommandTransactionOutcome.Rejected, result.Outcome);
        Assert.Empty(channel.Commands);
    }

    private static bool ContainsType(Type type, Type candidate)
    {
        if (type == candidate)
        {
            return true;
        }
        if (type.IsArray)
        {
            return ContainsType(type.GetElementType()!, candidate);
        }
        return type.IsGenericType &&
            type.GetGenericArguments().Any(argument => ContainsType(argument, candidate));
    }

    private static StationTxProductionCommandTransport CreateEnabled(
        FakeCommandChannel channel,
        string radioId = "radio-a",
        string[]? allowedRadioIds = null) =>
        new(
            EnabledSettings(allowedRadioIds ?? ["radio-a"]),
            radioId,
            localFlexEligible: true,
            channel);

    private static StationTxCommandTransportSettings EnabledSettings(
        params string[] allowedRadioIds) =>
        new()
        {
            Enabled = true,
            AllowedRadioIds = allowedRadioIds,
            CommandTimeoutMilliseconds = 2000
        };

    private static FakeCommandChannel ReadyChannel() =>
        new()
        {
            IsAttached = true,
            ClientHandle = 0x1234
        };

    private sealed record CommandCall(
        uint ExpectedClientHandle,
        string Command,
        TimeSpan Timeout);

    private sealed class FakeCommandChannel : IStationTxFlexCommandChannel
    {
        public bool IsAttached { get; set; }
        public uint ClientHandle { get; set; }
        public List<CommandCall> Commands { get; } = [];
        public Func<uint, string, TimeSpan, CancellationToken,
            Task<FlexCommandResponse>>
        Handler
        { get; set; } =
            (_, _, _, _) => Task.FromResult(new FlexCommandResponse(0, string.Empty));

        public Task<FlexCommandResponse> SendForClientAsync(
            uint expectedClientHandle,
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(new(expectedClientHandle, command, timeout));
            return Handler(
                expectedClientHandle,
                command,
                timeout,
                cancellationToken);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
