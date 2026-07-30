namespace AetherSDR.Web.Radio;

public interface IRadioConnectionSelection
{
    SelectedRadioEndpoint Selected { get; }
    bool LowBandwidth { get; }
    IReadOnlyDictionary<string, int> NormalDisplayRatesToRestore { get; }

    Task WaitForChangeAsync(
        long observedRevision,
        CancellationToken cancellationToken);

    void MarkNormalDisplayRatesRestored();
}

public sealed class SessionRadioSelection(
    SelectedRadioEndpoint endpoint,
    bool lowBandwidth)
    : IRadioConnectionSelection
{
    private readonly object m_gate = new();
    private readonly string m_radioId = endpoint.RadioId;
    private readonly string m_host = endpoint.Host;
    private readonly int m_port = endpoint.Port;
    private readonly string m_source = endpoint.Source;
    private readonly string m_stationId = endpoint.StationId;
    private readonly string m_sourceRadioId = endpoint.SourceRadioId;
    private readonly Dictionary<string, int> m_normalDisplayRates =
        new(StringComparer.OrdinalIgnoreCase);
    private bool m_lowBandwidth = lowBandwidth;
    private bool m_restoreNormalDisplayRates;
    private long m_revision = 1;
    private TaskCompletionSource<long> m_changed = NewChangeSource();

    public SelectedRadioEndpoint Selected
    {
        get
        {
            lock (m_gate)
            {
                return new SelectedRadioEndpoint(
                    m_radioId,
                    m_host,
                    m_port,
                    m_revision,
                    m_source,
                    m_stationId,
                    m_sourceRadioId);
            }
        }
    }

    public bool LowBandwidth
    {
        get
        {
            lock (m_gate)
            {
                return m_lowBandwidth;
            }
        }
    }

    public IReadOnlyDictionary<string, int> NormalDisplayRatesToRestore
    {
        get
        {
            lock (m_gate)
            {
                return !m_lowBandwidth && m_restoreNormalDisplayRates
                    ? new Dictionary<string, int>(
                        m_normalDisplayRates,
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(
                        StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public bool SetLowBandwidth(
        bool enabled,
        IReadOnlyList<PanadapterSnapshot>? panadapters = null)
    {
        TaskCompletionSource<long>? changed = null;
        long revision = 0;
        lock (m_gate)
        {
            if (m_lowBandwidth == enabled)
            {
                return false;
            }

            if (enabled)
            {
                m_normalDisplayRates.Clear();
                foreach (PanadapterSnapshot pan in panadapters ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(pan.Id) &&
                        pan.FramesPerSecond is >= 1 and <= 30)
                    {
                        m_normalDisplayRates[pan.Id] =
                            pan.FramesPerSecond;
                    }
                }
                m_restoreNormalDisplayRates = false;
            }
            else
            {
                m_restoreNormalDisplayRates =
                    m_normalDisplayRates.Count > 0;
            }

            m_lowBandwidth = enabled;
            changed = m_changed;
            m_changed = NewChangeSource();
            revision = ++m_revision;
        }

        changed.TrySetResult(revision);
        return true;
    }

    public void MarkNormalDisplayRatesRestored()
    {
        lock (m_gate)
        {
            m_restoreNormalDisplayRates = false;
            m_normalDisplayRates.Clear();
        }
    }

    public Task WaitForChangeAsync(
        long observedRevision,
        CancellationToken cancellationToken)
    {
        Task<long> waitTask;
        lock (m_gate)
        {
            if (m_revision != observedRevision)
            {
                return Task.CompletedTask;
            }

            waitTask = m_changed.Task;
        }

        return waitTask.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource<long> NewChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
