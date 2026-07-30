namespace AetherSDR.Web.Radio;

internal sealed record FlexInterlockObservation(
    string State,
    uint? TxClientHandle,
    string Source);

internal sealed class FlexInterlockTracker
{
    private readonly object m_gate = new();
    private FlexInterlockObservation? m_current;

    public FlexInterlockObservation? Current
    {
        get
        {
            lock (m_gate)
            {
                return m_current;
            }
        }
    }

    public bool Observe(
        IReadOnlyDictionary<string, string> fields,
        out FlexInterlockObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(fields);
        lock (m_gate)
        {
            string previousState = m_current?.State ?? string.Empty;
            bool stateProvided = fields.TryGetValue(
                "state",
                out string? stateValue);
            string state = stateProvided
                ? NormalizeToken(stateValue, 64)
                : previousState;
            if (state.Length == 0)
            {
                observation = m_current;
                return false;
            }

            bool ownerProvided = fields.TryGetValue(
                "tx_client_handle",
                out string? ownerValue);
            uint? txClientHandle = m_current?.TxClientHandle;
            if (ownerProvided)
            {
                if (!FlexStatusParser.TryParseFlexUInt(
                        ownerValue!,
                        out uint parsedOwner))
                {
                    observation = m_current;
                    return false;
                }
                txClientHandle = parsedOwner == 0 ? null : parsedOwner;
            }

            bool sourceProvided = fields.TryGetValue(
                "source",
                out string? sourceValue);
            string source = sourceProvided
                ? NormalizeToken(sourceValue, 32)
                : m_current?.Source ?? string.Empty;
            bool idle = IsIdleState(state);
            bool startedNewCycle =
                stateProvided &&
                IsIdleState(previousState) &&
                !idle;
            if (idle)
            {
                txClientHandle = null;
                source = string.Empty;
            }
            else if (startedNewCycle)
            {
                if (!ownerProvided)
                {
                    txClientHandle = null;
                }
                if (!sourceProvided)
                {
                    source = string.Empty;
                }
            }

            m_current = new FlexInterlockObservation(
                state,
                txClientHandle,
                source);
            observation = m_current;
            return true;
        }
    }

    public void Clear()
    {
        lock (m_gate)
        {
            m_current = null;
        }
    }

    private static bool IsIdleState(string state) =>
        state is "READY" or "RECEIVE";

    private static string NormalizeToken(string? value, int maximumLength)
    {
        string normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length <= maximumLength &&
               normalized.All(character =>
                   !char.IsControl(character) && !char.IsWhiteSpace(character))
            ? normalized
            : string.Empty;
    }
}
