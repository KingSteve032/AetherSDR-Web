using Microsoft.Extensions.Options;

namespace AetherRemote.Broker;

public sealed class StationLivenessMonitor(
    IOptions<StationLinkSettings> settings,
    StationRegistry registry,
    ILogger<StationLivenessMonitor> logger) : BackgroundService
{
    private readonly TimeSpan m_checkInterval = TimeSpan.FromSeconds(
        Math.Max(1, settings.Value.HeartbeatSeconds));

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(m_checkInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (string stationId in
                         registry.ExpireStaleConnections())
                {
                    logger.LogWarning(
                        "Station {StationId} exceeded its heartbeat " +
                        "disconnect timeout; the link and its receive " +
                        "sessions were closed.",
                        stationId);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
