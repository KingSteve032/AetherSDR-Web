using System.Reflection;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxProductionEmergencyUnkeyTransportTests
{
    [Fact]
    public void DefaultsAreRegisteredButDisabledAndAllowlistEmpty()
    {
        StationTxEmergencyUnkeyTransportSettings settings = new();
        StationTxEmergencyUnkeyTransportRegistrationDiagnostics diagnostics =
            StationTxEmergencyUnkeyTransportSettingsValidator.CreateDiagnostics(
                settings);

        Assert.True(diagnostics.Registered);
        Assert.False(diagnostics.ConfiguredEnabled);
        Assert.Equal(0, diagnostics.AllowedRadioCount);
        Assert.Equal(2000, diagnostics.CommandTimeoutMilliseconds);
        Assert.Equal("transport-disabled", diagnostics.Reason);
    }

    [Theory]
    [InlineData(249)]
    [InlineData(5001)]
    public void InvalidTimeoutsFailClosed(int timeoutMilliseconds)
    {
        StationTxEmergencyUnkeyTransportSettings settings = new()
        {
            CommandTimeoutMilliseconds = timeoutMilliseconds
        };

        Assert.Throws<InvalidOperationException>(() =>
            StationTxEmergencyUnkeyTransportSettingsValidator.Validate(settings));
    }

    [Fact]
    public void EnabledTransportRequiresExactNonEmptyAllowlist()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StationTxEmergencyUnkeyTransportSettingsValidator.Validate(new()
            {
                Enabled = true
            }));
        Assert.Throws<InvalidOperationException>(() =>
            StationTxEmergencyUnkeyTransportSettingsValidator.Validate(new()
            {
                Enabled = true,
                AllowedRadioIds = ["radio-a", "RADIO-A"]
            }));
    }

    [Fact]
    public void SurfaceContainsUnkeyOnlyAndNoBooleanTransmitMethod()
    {
        MethodInfo[] methods =
        [
            .. typeof(IStationTxProductionEmergencyUnkeyTransport).GetMethods(),
            .. typeof(IStationTxEmergencyUnkeyTransport).GetMethods()
        ];
        Assert.Contains(methods, method => method.Name == "RequestUnkeyAsync");
        Assert.DoesNotContain(methods, method =>
            method.Name.Contains("SetTransmit", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, method =>
            method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(bool)));
    }

    [Fact]
    public async Task DisabledTransportRejectsWithoutForwarding()
    {
        FakeChannel channel = new()
        {
            IsAttached = true,
            ClientHandle = 0x12345678
        };
        StationTxProductionEmergencyUnkeyTransport transport = new(
            new StationTxEmergencyUnkeyTransportSettings(),
            "RADIO-A",
            localFlexEligible: true,
            channel);

        StationTxTransportResult result = await transport.RequestUnkeyAsync(
            0x12345678,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Rejected, result.Outcome);
        Assert.Equal(0, channel.SendCount);
        Assert.Equal(1, transport.Snapshot.AttemptCount);
        Assert.Equal(0, transport.Snapshot.ForwardedCount);
        Assert.Equal("transport-disabled", transport.Snapshot.LastReason);
    }

    [Fact]
    public async Task EnabledTransportRejectsReplacedHandleBeforeCommand()
    {
        FakeChannel channel = new()
        {
            IsAttached = true,
            ClientHandle = 0x22222222
        };
        StationTxProductionEmergencyUnkeyTransport transport = new(
            EnabledSettings(),
            "RADIO-A",
            localFlexEligible: true,
            channel);

        StationTxTransportResult result = await transport.RequestUnkeyAsync(
            0x11111111,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Rejected, result.Outcome);
        Assert.Equal(0, channel.SendCount);
        Assert.Equal("client-handle-mismatch", transport.Snapshot.LastReason);
    }

    [Theory]
    [InlineData(0u, "", "accepted")]
    [InlineData(0x50000001u, "denied", "rejected")]
    public async Task KnownRadioOutcomesArePreservedWithoutRetry(
        uint code,
        string body,
        string expectedOutcome)
    {
        FakeChannel channel = new()
        {
            IsAttached = true,
            ClientHandle = 0x12345678,
            Response = new FlexCommandResponse(code, body)
        };
        StationTxProductionEmergencyUnkeyTransport transport = new(
            EnabledSettings(),
            "RADIO-A",
            localFlexEligible: true,
            channel);

        StationTxTransportResult result = await transport.RequestUnkeyAsync(
            0x12345678,
            CancellationToken.None);

        Assert.Equal(1, channel.SendCount);
        Assert.Equal(0x12345678u, channel.ExpectedClientHandle);
        Assert.Equal("xmit 0", channel.Command);
        Assert.Equal(TimeSpan.FromSeconds(2), channel.Timeout);
        Assert.Equal(expectedOutcome, transport.Snapshot.LastOutcome);
        Assert.Equal(code == 0, result.Success);
    }

    [Fact]
    public async Task SocketFailureIsUnknownAndNotRetried()
    {
        FakeChannel channel = new()
        {
            IsAttached = true,
            ClientHandle = 0x12345678,
            Failure = new IOException("connection lost")
        };
        StationTxProductionEmergencyUnkeyTransport transport = new(
            EnabledSettings(),
            "RADIO-A",
            localFlexEligible: true,
            channel);

        StationTxTransportResult result = await transport.RequestUnkeyAsync(
            0x12345678,
            CancellationToken.None);

        Assert.Equal(StationTxTransportOutcome.Unknown, result.Outcome);
        Assert.Equal(1, channel.SendCount);
        Assert.Equal(1, transport.Snapshot.UnknownCount);
        Assert.Equal("command-outcome-unknown", transport.Snapshot.LastReason);
    }

    [Fact]
    public void BrowserAndCoordinatorReceiveNoEmergencyTransportSurface()
    {
        Type[] forbidden =
        [
            typeof(IStationTxEmergencyUnkeyTransport),
            typeof(IStationTxProductionEmergencyUnkeyTransport),
            typeof(StationTxProductionEmergencyUnkeyTransport)
        ];
        foreach (Type type in
                 new[] { typeof(RadioWebSocketEndpoint), typeof(RadioCoordinator) })
        {
            Assert.DoesNotContain(
                type.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic),
                field => forbidden.Contains(field.FieldType));
            Assert.DoesNotContain(
                type.GetConstructors(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)
                    .SelectMany(constructor => constructor.GetParameters()),
                parameter => forbidden.Contains(parameter.ParameterType));
        }
    }

    private static StationTxEmergencyUnkeyTransportSettings EnabledSettings() =>
        new()
        {
            Enabled = true,
            AllowedRadioIds = ["RADIO-A"],
            CommandTimeoutMilliseconds = 2000
        };

    private sealed class FakeChannel : IStationTxFlexCommandChannel
    {
        public bool IsAttached { get; set; }
        public uint ClientHandle { get; set; }
        public int SendCount { get; private set; }
        public uint ExpectedClientHandle { get; private set; }
        public string? Command { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public FlexCommandResponse Response { get; set; } = new(0, string.Empty);
        public Exception? Failure { get; set; }

        public Task<FlexCommandResponse> SendForClientAsync(
            uint expectedClientHandle,
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            ExpectedClientHandle = expectedClientHandle;
            Command = command;
            Timeout = timeout;
            if (Failure is not null)
            {
                throw Failure;
            }
            return Task.FromResult(Response);
        }
    }
}
