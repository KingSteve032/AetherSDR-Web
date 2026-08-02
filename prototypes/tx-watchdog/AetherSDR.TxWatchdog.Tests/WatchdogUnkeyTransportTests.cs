using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.TxWatchdog.Tests;

public sealed class WatchdogUnkeyTransportTests
{
    [Fact]
    public void ProgramOptionsDefaultToDisabledUnkeyTransport()
    {
        Assert.True(WatchdogProgramOptions.TryParse(
            ["--stdio"],
            out WatchdogProgramOptions? options,
            out string error), error);
        Assert.NotNull(options);
        Assert.False(options.UnkeyTransport.Enabled);
        Assert.False(options.ArmingEnabled);
        Assert.Null(options.UnkeyTransport.Address);
    }

    [Fact]
    public void ProgramOptionsAcceptOneStrictIpv4UnkeyEndpoint()
    {
        Assert.True(WatchdogProgramOptions.TryParse(
            [
                "--stdio",
                "--unkey-enabled",
                "--radio-id",
                "radio-a",
                "--radio-host",
                "192.0.2.15",
                "--radio-port",
                "4992",
                "--command-timeout-ms",
                "750"
            ],
            out WatchdogProgramOptions? options,
            out string error), error);
        Assert.NotNull(options);
        Assert.True(options.UnkeyTransport.Enabled);
        Assert.Equal("RADIO-A", options.UnkeyTransport.RadioId);
        Assert.Equal(IPAddress.Parse("192.0.2.15"), options.UnkeyTransport.Address);
        Assert.Equal(4992, options.UnkeyTransport.Port);
        Assert.Equal(TimeSpan.FromMilliseconds(750), options.UnkeyTransport.CommandTimeout);
        Assert.False(options.ArmingEnabled);
    }

    [Fact]
    public void ProgramOptionsRequireAnExplicitArmingFlag()
    {
        Assert.True(WatchdogProgramOptions.TryParse(
            [
                "--stdio",
                "--unkey-enabled",
                "--arming-enabled",
                "--radio-id",
                "radio-a",
                "--radio-host",
                "192.0.2.15",
                "--radio-port",
                "4992",
                "--command-timeout-ms",
                "750"
            ],
            out WatchdogProgramOptions? options,
            out string error), error);
        Assert.NotNull(options);
        Assert.True(options.UnkeyTransport.Enabled);
        Assert.True(options.ArmingEnabled);

        Assert.False(WatchdogProgramOptions.TryParse(
            ["--stdio", "--arming-enabled"],
            out _,
            out string missingTransportError));
        Assert.Equal("invalid-arguments", missingTransportError);
    }

    [Theory]
    [InlineData("127.0.0.1", "0", "750")]
    [InlineData("127.0.0.1", "65536", "750")]
    [InlineData("not-an-ip", "4992", "750")]
    [InlineData("0.0.0.0", "4992", "750")]
    [InlineData("224.0.0.1", "4992", "750")]
    [InlineData("127.0.0.1", "4992", "249")]
    [InlineData("127.0.0.1", "4992", "5001")]
    public void ProgramOptionsRejectInvalidUnkeyEndpoints(
        string host,
        string port,
        string timeout)
    {
        Assert.False(WatchdogProgramOptions.TryParse(
            [
                "--stdio",
                "--unkey-enabled",
                "--radio-id",
                "RADIO-A",
                "--radio-host",
                host,
                "--radio-port",
                port,
                "--command-timeout-ms",
                timeout
            ],
            out _,
            out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void HostWithAvailableTransportStillStartsEmptyAndDisarmed()
    {
        FakeUnkeyTransport transport = new(isAvailable: true);
        WatchdogHostEngine engine = new(
            TimeProvider.System,
            "watchdog-ready",
            transport);

        WatchdogSnapshot snapshot = engine.Snapshot;

        Assert.Equal("Disarmed", snapshot.State);
        Assert.Equal("watchdog-arming-disabled-disarmed", snapshot.Reason);
        Assert.True(snapshot.RadioCommandTransportAvailable);
        Assert.False(snapshot.ArmingAvailable);
        Assert.False(snapshot.Registered);
        Assert.False(snapshot.Connected);
        Assert.False(snapshot.LeaseBound);
        Assert.Equal(0, transport.AttemptCount);
    }

    [Fact]
    public void WatchdogProtocolHasArmAndDisarmButNoKeyOrUnkeyRequestKind()
    {
        string[] names = Enum.GetNames<WatchdogRequestKind>();
        Assert.Contains(nameof(WatchdogRequestKind.Arm), names);
        Assert.Contains(nameof(WatchdogRequestKind.Disarm), names);
        Assert.DoesNotContain(names, name =>
            string.Equals(name, "Key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name =>
            string.Equals(name, "Unkey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TransportSurfaceHasNoKeyOrArbitraryCommandMethod()
    {
        string[] methods = typeof(IWatchdogUnkeyTransport)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .ToArray();
        Assert.Contains("RequestUnkeyAsync", methods);
        Assert.DoesNotContain(methods, method =>
            method.Contains("key", StringComparison.OrdinalIgnoreCase) &&
            !method.Contains("unkey", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methods, method =>
            method.Contains("command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DisabledTransportRejectsBeforeForwarding()
    {
        FakeChannel channel = new();
        FlexWatchdogUnkeyTransport transport = new(
            WatchdogUnkeyTransportConfiguration.Disabled,
            channel);

        WatchdogUnkeyTransportResult result =
            await transport.RequestUnkeyAsync(0x12345678, CancellationToken.None);

        Assert.Equal(WatchdogUnkeyTransportOutcome.Rejected, result.Outcome);
        Assert.Equal(0, channel.SendCount);
        Assert.Equal(1, transport.Snapshot.AttemptCount);
        Assert.Equal(0, transport.Snapshot.ForwardedCount);
    }

    [Fact]
    public async Task EnabledTransportRequiresExactNonZeroProtectedHandle()
    {
        FakeChannel channel = new();
        FlexWatchdogUnkeyTransport transport = new(EnabledConfiguration(), channel);

        WatchdogUnkeyTransportResult result =
            await transport.RequestUnkeyAsync(0, CancellationToken.None);

        Assert.Equal(WatchdogUnkeyTransportOutcome.Rejected, result.Outcome);
        Assert.Equal(0, channel.SendCount);
        Assert.Equal("expected-client-handle-required", transport.Snapshot.LastReason);
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
            Response = new WatchdogFlexUnkeyResponse(code, body)
        };
        FlexWatchdogUnkeyTransport transport = new(EnabledConfiguration(), channel);

        WatchdogUnkeyTransportResult result =
            await transport.RequestUnkeyAsync(0x12345678, CancellationToken.None);

        Assert.Equal(1, channel.SendCount);
        Assert.Equal(1, transport.Snapshot.ForwardedCount);
        Assert.Equal(expectedOutcome, transport.Snapshot.LastOutcome);
        Assert.Equal(code == 0, result.Success);
    }

    [Fact]
    public async Task SocketFailureIsUnknownAndIsNotRetried()
    {
        FakeChannel channel = new()
        {
            Failure = new IOException("connection lost")
        };
        FlexWatchdogUnkeyTransport transport = new(EnabledConfiguration(), channel);

        WatchdogUnkeyTransportResult result =
            await transport.RequestUnkeyAsync(0x12345678, CancellationToken.None);

        Assert.Equal(WatchdogUnkeyTransportOutcome.Unknown, result.Outcome);
        Assert.Equal(1, channel.SendCount);
        Assert.Equal(1, transport.Snapshot.UnknownCount);
        Assert.Equal("command-outcome-unknown", transport.Snapshot.LastReason);
    }

    [Fact]
    public async Task TcpChannelWritesExactlyOneUnkeyCommand()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<string[]> server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = accepted.GetStream();
            using StreamReader reader = new(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            await using StreamWriter writer = new(
                stream,
                Encoding.ASCII,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            await writer.WriteLineAsync("H12345678");
            string clientSubscription =
                await reader.ReadLineAsync() ?? string.Empty;
            string txSubscription =
                await reader.ReadLineAsync() ?? string.Empty;
            await writer.WriteLineAsync("R1|0|");
            await writer.WriteLineAsync("R2|0|");
            await writer.WriteLineAsync(
                "S1|client 0x11111111 connected local_ptt=1");
            await writer.WriteLineAsync(
                "S1|interlock state=TRANSMITTING tx_client_handle=0x11111111 source=SW");
            string command = await reader.ReadLineAsync() ?? string.Empty;
            await writer.WriteLineAsync("R3|0|");
            await writer.WriteLineAsync(
                "S1|interlock state=READY tx_client_handle=0 source=SW");
            return new[] { clientSubscription, txSubscription, command };
        });
        FlexWatchdogUnkeyTransport transport = new(new(
            Enabled: true,
            RadioId: "RADIO-A",
            Address: IPAddress.Loopback,
            Port: port,
            CommandTimeout: TimeSpan.FromSeconds(2)));

        WatchdogUnkeyTransportResult result =
            await transport.RequestUnkeyAsync(0x11111111, CancellationToken.None);
        string[] commands = await server.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Success);
        Assert.Equal(
            ["C1|sub client all", "C2|sub tx all", "C3|xmit 0"],
            commands);
    }

    [Fact]
    public async Task AcceptedCommandWithoutIdleConfirmationIsUnknown()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<string> server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = accepted.GetStream();
            using StreamReader reader = new(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            await using StreamWriter writer = new(
                stream,
                Encoding.ASCII,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            await writer.WriteLineAsync("H12345678");
            _ = await reader.ReadLineAsync();
            _ = await reader.ReadLineAsync();
            await writer.WriteLineAsync("R1|0|");
            await writer.WriteLineAsync("R2|0|");
            await writer.WriteLineAsync(
                "S1|client 0x11111111 connected local_ptt=1");
            await writer.WriteLineAsync(
                "S1|interlock state=TRANSMITTING tx_client_handle=0x11111111 source=SW");
            string command = await reader.ReadLineAsync() ?? string.Empty;
            await writer.WriteLineAsync("R3|0|");
            return command;
        });
        FlexWatchdogUnkeyTransport transport = new(new(
            Enabled: true,
            RadioId: "RADIO-A",
            Address: IPAddress.Loopback,
            Port: port,
            CommandTimeout: TimeSpan.FromSeconds(2)));

        WatchdogUnkeyTransportResult result =
            await transport.RequestUnkeyAsync(0x11111111, CancellationToken.None);
        string command = await server.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("C3|xmit 0", command);
        Assert.Equal(WatchdogUnkeyTransportOutcome.Unknown, result.Outcome);
        Assert.Equal(1, transport.Snapshot.UnknownCount);
        Assert.Equal("command-outcome-unknown", transport.Snapshot.LastReason);
    }

    [Fact]
    public async Task IdleRadioCompletesWithoutSendingUnkey()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<string?[]> server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = accepted.GetStream();
            using StreamReader reader = new(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            await using StreamWriter writer = new(
                stream,
                Encoding.ASCII,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            await writer.WriteLineAsync("H12345678");
            string? clientSubscription = await reader.ReadLineAsync();
            string? txSubscription = await reader.ReadLineAsync();
            await writer.WriteLineAsync("R1|0|");
            await writer.WriteLineAsync("R2|0|");
            await writer.WriteLineAsync(
                "S1|interlock state=READY tx_client_handle=0");
            string? unkey = await reader.ReadLineAsync();
            return new[] { clientSubscription, txSubscription, unkey };
        });
        FlexWatchdogUnkeyTransport transport = new(new(
            Enabled: true,
            RadioId: "RADIO-A",
            Address: IPAddress.Loopback,
            Port: port,
            CommandTimeout: TimeSpan.FromSeconds(2)));

        WatchdogUnkeyTransportResult result =
            await transport.RequestUnkeyAsync(0x11111111, CancellationToken.None);
        string?[] commands = await server.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Success);
        Assert.Equal("C1|sub client all", commands[0]);
        Assert.Equal("C2|sub tx all", commands[1]);
        Assert.Null(commands[2]);
    }

    [Fact]
    public async Task DifferentFreshTxOwnerRejectsBeforeSendingUnkey()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<string?> server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = accepted.GetStream();
            using StreamReader reader = new(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            await using StreamWriter writer = new(
                stream,
                Encoding.ASCII,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            await writer.WriteLineAsync("H12345678");
            _ = await reader.ReadLineAsync();
            _ = await reader.ReadLineAsync();
            await writer.WriteLineAsync("R1|0|");
            await writer.WriteLineAsync("R2|0|");
            await writer.WriteLineAsync(
                "S1|client 0x11111111 connected local_ptt=1");
            await writer.WriteLineAsync(
                "S1|interlock state=TRANSMITTING tx_client_handle=0x22222222 source=SW");
            return await reader.ReadLineAsync();
        });
        FlexWatchdogUnkeyTransport transport = new(new(
            Enabled: true,
            RadioId: "RADIO-A",
            Address: IPAddress.Loopback,
            Port: port,
            CommandTimeout: TimeSpan.FromSeconds(2)));

        WatchdogUnkeyTransportResult result =
            await transport.RequestUnkeyAsync(0x11111111, CancellationToken.None);
        string? unkey = await server.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WatchdogUnkeyTransportOutcome.Rejected, result.Outcome);
        Assert.Null(unkey);
        Assert.Equal(1, transport.Snapshot.RejectedCount);
    }

    private static WatchdogUnkeyTransportConfiguration EnabledConfiguration() =>
        new(
            Enabled: true,
            RadioId: "RADIO-A",
            Address: IPAddress.Parse("192.0.2.10"),
            Port: 4992,
            CommandTimeout: TimeSpan.FromSeconds(2));

    private sealed class FakeChannel : IWatchdogFlexUnkeyChannel
    {
        public int SendCount { get; private set; }
        public WatchdogFlexUnkeyResponse Response { get; set; } = new(0, string.Empty);
        public Exception? Failure { get; set; }

        public Task<WatchdogFlexUnkeyResponse> SendUnkeyAsync(
            WatchdogUnkeyTransportConfiguration configuration,
            uint expectedProtectedClientHandle,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotEqual(0u, expectedProtectedClientHandle);
            SendCount++;
            if (Failure is not null)
            {
                throw Failure;
            }
            return Task.FromResult(Response);
        }
    }

    private sealed class FakeUnkeyTransport(bool isAvailable) : IWatchdogUnkeyTransport
    {
        public int AttemptCount { get; private set; }
        public bool IsAvailable { get; } = isAvailable;
        public WatchdogUnkeyTransportDiagnostics Snapshot => new(
            Registered: true,
            ConfiguredEnabled: IsAvailable,
            Available: IsAvailable,
            RadioId: IsAvailable ? "RADIO-A" : string.Empty,
            Port: IsAvailable ? 4992 : 0,
            CommandTimeoutMilliseconds: 2000,
            AttemptCount,
            ForwardedCount: 0,
            AcceptedCount: 0,
            RejectedCount: 0,
            UnknownCount: 0,
            LastProtectedClientHandle: 0,
            LastOutcome: "none",
            LastReason: IsAvailable ? "ready" : "transport-disabled",
            LastObservedAt: null);

        public Task<WatchdogUnkeyTransportResult> RequestUnkeyAsync(
            uint expectedProtectedClientHandle,
            CancellationToken cancellationToken)
        {
            AttemptCount++;
            return Task.FromResult(WatchdogUnkeyTransportResult.Ok);
        }
    }
}
