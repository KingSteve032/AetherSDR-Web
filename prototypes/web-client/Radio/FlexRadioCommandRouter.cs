using System.Globalization;

namespace AetherSDR.Web.Radio;

internal interface IStationTxFlexCommandChannel
{
    bool IsAttached { get; }
    uint ClientHandle { get; }

    Task<FlexCommandResponse> SendForClientAsync(
        uint expectedClientHandle,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class FlexRadioCommandRouter : IStationTxFlexCommandChannel
{
    private readonly object m_gate = new();
    private readonly HashSet<int> m_createdSliceIds = [];
    private readonly Dictionary<string, long> m_panCenters =
        new(StringComparer.OrdinalIgnoreCase);
    private FlexControlSession? m_control;
    private uint m_clientHandle;

    internal bool IsAttached
    {
        get
        {
            lock (m_gate)
            {
                return m_control is not null;
            }
        }
    }

    internal uint ClientHandle
    {
        get
        {
            lock (m_gate)
            {
                return m_control is null ? 0 : m_clientHandle;
            }
        }
    }

    bool IStationTxFlexCommandChannel.IsAttached => IsAttached;
    uint IStationTxFlexCommandChannel.ClientHandle => ClientHandle;

    public string? PanId
    {
        get
        {
            lock (m_gate)
            {
                return m_panCenters.Keys.FirstOrDefault();
            }
        }
    }

    public string[] PanIds
    {
        get
        {
            lock (m_gate)
            {
                return m_panCenters.Keys.ToArray();
            }
        }
    }

    public long PanCenterHz
    {
        get
        {
            lock (m_gate)
            {
                return m_panCenters.Values.FirstOrDefault();
            }
        }
    }

    internal void Attach(
        FlexControlSession control,
        uint clientHandle,
        string panId,
        long panCenterHz)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (clientHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientHandle));
        }

        lock (m_gate)
        {
            m_control = control;
            m_clientHandle = clientHandle;
            m_panCenters.Clear();
            m_panCenters[panId] = panCenterHz;
            m_createdSliceIds.Clear();
        }
    }

    internal int[] Detach(FlexControlSession control)
    {
        lock (m_gate)
        {
            if (!ReferenceEquals(m_control, control))
            {
                return [];
            }

            int[] createdSliceIds = m_createdSliceIds.ToArray();
            m_createdSliceIds.Clear();
            m_control = null;
            m_clientHandle = 0;
            m_panCenters.Clear();
            return createdSliceIds;
        }
    }

    internal void RegisterPan(string panId, long panCenterHz)
    {
        lock (m_gate)
        {
            m_panCenters[panId] = panCenterHz;
        }
    }

    internal void UnregisterPan(string panId)
    {
        lock (m_gate)
        {
            m_panCenters.Remove(panId);
        }
    }

    internal bool IsOwnedPan(uint streamId)
    {
        lock (m_gate)
        {
            return m_panCenters.Keys.Any(
                panId =>
                    FlexStatusParser.TryParseFlexUInt(
                        panId,
                        out uint candidate) &&
                    candidate == streamId);
        }
    }

    internal long PanCenterHzFor(uint streamId)
    {
        lock (m_gate)
        {
            foreach ((string panId, long centerHz) in m_panCenters)
            {
                if (FlexStatusParser.TryParseFlexUInt(
                        panId,
                        out uint candidate) &&
                    candidate == streamId)
                {
                    return centerHz;
                }
            }
            return 0;
        }
    }

    internal void ObservePanCenter(string panId, long panCenterHz)
    {
        if (panCenterHz is < 100_000 or > 60_000_000)
        {
            return;
        }

        lock (m_gate)
        {
            if (m_panCenters.ContainsKey(panId))
            {
                m_panCenters[panId] = panCenterHz;
            }
        }
    }

    internal void ObservePanCenter(long panCenterHz)
    {
        string? panId = PanId;
        if (panId is not null)
        {
            ObservePanCenter(panId, panCenterHz);
        }
    }

    internal void TrackCreatedSlice(int radioId)
    {
        lock (m_gate)
        {
            m_createdSliceIds.Add(radioId);
        }
    }

    internal Task<FlexCommandResponse> SendAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        SendCoreAsync(
            expectedClientHandle: null,
            command,
            timeout,
            cancellationToken);

    Task<FlexCommandResponse>
        IStationTxFlexCommandChannel.SendForClientAsync(
            uint expectedClientHandle,
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
        SendForClientAsync(
            expectedClientHandle,
            command,
            timeout,
            cancellationToken);

    internal Task<FlexCommandResponse> SendForClientAsync(
        uint expectedClientHandle,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (expectedClientHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedClientHandle));
        }

        return SendCoreAsync(
            expectedClientHandle,
            command,
            timeout,
            cancellationToken);
    }

    private async Task<FlexCommandResponse> SendCoreAsync(
        uint? expectedClientHandle,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        FlexControlSession control;
        lock (m_gate)
        {
            control = m_control ??
                throw new InvalidOperationException(
                    "The Flex radio control session is not connected.");
            if (expectedClientHandle is uint expected &&
                m_clientHandle != expected)
            {
                throw new InvalidOperationException(
                    "The exact Flex radio client handle is no longer connected.");
            }
        }

        FlexCommandResponse response = await control.SendCommandAsync(
            command,
            timeout,
            cancellationToken);
        if (!response.IsSuccess)
        {
            return response;
        }

        string[] parts = command.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        lock (m_gate)
        {
            if (parts is ["slice", "create", ..] &&
                int.TryParse(response.Body.Trim(), out int createdId))
            {
                m_createdSliceIds.Add(createdId);
            }
            else if (parts is ["slice", "remove", var removedText, ..] &&
                     int.TryParse(removedText, out int removedId))
            {
                m_createdSliceIds.Remove(removedId);
            }
            else if (parts is ["display", "pan", "set", var panId, ..])
            {
                string? centerText = parts
                    .FirstOrDefault(part =>
                        part.StartsWith(
                            "center=",
                            StringComparison.OrdinalIgnoreCase));
                if (centerText is not null &&
                    double.TryParse(
                        centerText["center=".Length..],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double centerMhz))
                {
                    long centerHz =
                        (long)Math.Round(centerMhz * 1_000_000d);
                    if (centerHz is >= 100_000 and <= 60_000_000 &&
                        m_panCenters.ContainsKey(panId))
                    {
                        m_panCenters[panId] = centerHz;
                    }
                }
            }
        }

        return response;
    }
}
