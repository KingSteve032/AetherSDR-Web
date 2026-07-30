using System.Diagnostics;
using System.Text.Json;

namespace AetherSDR.TxHil;

internal sealed record HilGatewayAuthorityReady(
    string Event,
    int ProcessId,
    DateTimeOffset ProcessStartTime,
    string GatewayInstanceId,
    bool RadioConnectionCreated,
    bool KeyCapability,
    bool UnkeyCapability);

/// <summary>
/// HIL-only surrogate for the authenticated web-gateway authority process.
/// It deliberately has no radio dependency, no command transport, and no TX
/// capability. The parent observes this exact process instance alive and then
/// force-kills it to exercise the station-local gateway-loss boundary.
/// </summary>
internal static class HilGatewayAuthorityChild
{
    public static async Task<int> RunAsync(
        CancellationToken cancellationToken)
    {
        using Process process = Process.GetCurrentProcess();
        DateTimeOffset startedAt = process.StartTime.ToUniversalTime();
        string instanceId =
            $"gateway-{process.Id}-{startedAt.UtcTicks}";
        Console.WriteLine(JsonSerializer.Serialize(
            new HilGatewayAuthorityReady(
                "gateway-authority-ready",
                process.Id,
                startedAt,
                instanceId,
                RadioConnectionCreated: false,
                KeyCapability: false,
                UnkeyCapability: false)));
        await Console.Out.FlushAsync(cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}
