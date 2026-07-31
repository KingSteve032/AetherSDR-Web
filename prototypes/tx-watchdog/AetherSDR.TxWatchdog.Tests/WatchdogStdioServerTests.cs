using System.Text.Json;
using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.TxWatchdog.Tests;

public sealed class WatchdogStdioServerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task OversizedLineIsConsumedAndTheNextBoundedRequestStillWorks()
    {
        string oversized = new(
            'x',
            WatchdogProtocol.MaximumMessageCharacters + 1);
        string inputText = string.Join(
            '\n',
            oversized,
            WatchdogProtocolTests.StatusJson("status-1"),
            string.Empty);
        using StringReader input = new(inputText);
        using StringWriter output = new();
        WatchdogHostEngine engine = new(
            hostInstanceId: "host-a");

        await WatchdogStdioServer.RunAsync(input, output, engine);

        string[] lines = output.ToString()
            .Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        WatchdogResponse oversizedResponse = Deserialize(lines[0]);
        WatchdogResponse statusResponse = Deserialize(lines[1]);
        Assert.False(oversizedResponse.Ok);
        Assert.Equal("message-too-large", oversizedResponse.Error);
        Assert.True(statusResponse.Ok);
        Assert.Equal("status-1", statusResponse.RequestId);
        Assert.Equal("Disarmed", statusResponse.Snapshot.State);
        Assert.False(statusResponse.Snapshot.Registered);
        Assert.False(statusResponse.Snapshot.LeaseBound);
    }

    [Fact]
    public async Task MalformedRequestDoesNotMutateOrStopTheHost()
    {
        string inputText = string.Join(
            '\n',
            "{not-json}",
            WatchdogProtocolTests.StatusJson("status-2"),
            string.Empty);
        using StringReader input = new(inputText);
        using StringWriter output = new();
        WatchdogHostEngine engine = new(
            hostInstanceId: "host-a");

        await WatchdogStdioServer.RunAsync(input, output, engine);

        string[] lines = output.ToString()
            .Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("invalid-json", Deserialize(lines[0]).Error);
        WatchdogResponse status = Deserialize(lines[1]);
        Assert.True(status.Ok);
        Assert.False(status.Snapshot.Registered);
        Assert.False(status.Snapshot.LeaseBound);
        Assert.False(status.Snapshot.RadioCommandTransportAvailable);
        Assert.False(status.Snapshot.ArmingAvailable);
    }

    [Fact]
    public async Task RegistrationResponseDoesNotEchoTheOpaqueLeaseIdentity()
    {
        string inputText = WatchdogProtocolTests.RequestJson(
            "register",
            "register-1",
            1,
            WatchdogProtocolTests.IdentityJson("radio-a")) + '\n';
        using StringReader input = new(inputText);
        using StringWriter output = new();
        WatchdogHostEngine engine = new(
            hostInstanceId: "host-a");

        await WatchdogStdioServer.RunAsync(input, output, engine);

        string responseJson = output.ToString().Trim();
        Assert.DoesNotContain("lease-a", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"identity\"", responseJson, StringComparison.Ordinal);
        WatchdogResponse response = Deserialize(responseJson);
        Assert.True(response.Ok);
        Assert.True(response.Snapshot.Registered);
        Assert.True(response.Snapshot.Connected);
        Assert.True(response.Snapshot.LeaseBound);
        Assert.Null(response.Snapshot.Identity);
    }

    private static WatchdogResponse Deserialize(string json) =>
        JsonSerializer.Deserialize<WatchdogResponse>(json, JsonOptions) ??
        throw new InvalidOperationException("A watchdog response was required.");
}
