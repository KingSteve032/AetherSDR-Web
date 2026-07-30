namespace AetherSDR.Web.Radio;

public sealed record AdminRadioCapacitySample(
    DateTimeOffset ObservedAt,
    bool Online,
    int AvailableClients,
    int LicensedClients,
    string Status);

public sealed class RadioCapacityHistoryService(
    RadioSelectionManager radioCatalog,
    ILogger<RadioCapacityHistoryService> logger)
    : BackgroundService
{
    internal const int MaximumSamplesPerRadio = 256;
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(24);
    internal static readonly TimeSpan CheckpointInterval =
        TimeSpan.FromMinutes(15);

    private readonly object m_gate = new();
    private readonly Dictionary<string, List<AdminRadioCapacitySample>>
        m_history = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AdminRadioCapacitySample> GetHistory(string radioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioId);
        lock (m_gate)
        {
            return m_history.TryGetValue(
                radioId,
                out List<AdminRadioCapacitySample>? samples)
                ? samples.ToArray()
                : [];
        }
    }

    internal void RecordSnapshot(
        IReadOnlyList<RadioSelectionOption> radios,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(radios);
        DateTimeOffset cutoff = observedAt - RetentionWindow;

        lock (m_gate)
        {
            foreach (string radioId in m_history.Keys.ToArray())
            {
                List<AdminRadioCapacitySample> existing = m_history[radioId];
                existing.RemoveAll(sample => sample.ObservedAt < cutoff);
                if (existing.Count == 0)
                {
                    m_history.Remove(radioId);
                }
            }

            foreach (RadioSelectionOption radio in radios)
            {
                if (!m_history.TryGetValue(
                        radio.RadioId,
                        out List<AdminRadioCapacitySample>? samples))
                {
                    samples = [];
                    m_history.Add(radio.RadioId, samples);
                }

                AdminRadioCapacitySample current = new(
                    observedAt,
                    radio.Online,
                    radio.AvailableClients,
                    radio.LicensedClients,
                    radio.Status);
                AdminRadioCapacitySample? previous = samples.LastOrDefault();
                bool changed = previous is null ||
                    previous.Online != current.Online ||
                    previous.AvailableClients != current.AvailableClients ||
                    previous.LicensedClients != current.LicensedClients ||
                    !string.Equals(
                        previous.Status,
                        current.Status,
                        StringComparison.OrdinalIgnoreCase);
                bool checkpointDue = previous is not null &&
                    observedAt - previous.ObservedAt >= CheckpointInterval;

                if (!changed && !checkpointDue)
                {
                    continue;
                }

                samples.Add(current);
                if (samples.Count > MaximumSamplesPerRadio)
                {
                    samples.RemoveRange(
                        0,
                        samples.Count - MaximumSamplesPerRadio);
                }
            }
        }
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(SampleInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RecordSnapshot(
                    radioCatalog.GetSnapshot().Radios,
                    DateTimeOffset.UtcNow);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Radio client-capacity history sampling failed.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
