namespace AetherSDR.Web.Radio;

internal sealed class RadioTuneTracker
{
    private readonly object m_gate = new();
    private RadioTuneTimingDiagnostics m_snapshot = Idle();

    public RadioTuneTimingDiagnostics Snapshot
    {
        get
        {
            lock (m_gate)
            {
                return m_snapshot;
            }
        }
    }

    public void RecordRequest(
        string sliceId,
        int radioSliceId,
        long targetFrequencyHz,
        DateTimeOffset? requestedAt = null)
    {
        DateTimeOffset timestamp = requestedAt ?? DateTimeOffset.UtcNow;
        lock (m_gate)
        {
            m_snapshot = new RadioTuneTimingDiagnostics(
                "pending",
                sliceId,
                radioSliceId,
                targetFrequencyHz,
                timestamp,
                null,
                null,
                null);
        }
    }

    public void Observe(
        IReadOnlyList<SliceSnapshot> slices,
        DateTimeOffset? observedAt = null)
    {
        DateTimeOffset timestamp = observedAt ?? DateTimeOffset.UtcNow;
        lock (m_gate)
        {
            if (!string.Equals(
                    m_snapshot.State,
                    "pending",
                    StringComparison.Ordinal) ||
                m_snapshot.RequestedAt is null)
            {
                return;
            }

            bool confirmed = slices.Any(
                slice =>
                    slice.RadioId == m_snapshot.RadioSliceId &&
                    slice.FrequencyHz == m_snapshot.TargetFrequencyHz);
            if (!confirmed)
            {
                return;
            }

            double elapsedMilliseconds = Math.Max(
                0,
                (timestamp - m_snapshot.RequestedAt.Value).TotalMilliseconds);
            m_snapshot = m_snapshot with
            {
                State = "confirmed",
                ConfirmedAt = timestamp,
                RadioRoundTripMilliseconds = elapsedMilliseconds
            };
        }
    }

    public void RecordFailure(
        string sliceId,
        int radioSliceId,
        long targetFrequencyHz,
        string error)
    {
        lock (m_gate)
        {
            if (!string.Equals(
                    m_snapshot.State,
                    "pending",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    m_snapshot.SliceId,
                    sliceId,
                    StringComparison.OrdinalIgnoreCase) ||
                m_snapshot.RadioSliceId != radioSliceId ||
                m_snapshot.TargetFrequencyHz != targetFrequencyHz)
            {
                return;
            }

            m_snapshot = m_snapshot with
            {
                State = "failed",
                Error = error
            };
        }
    }

    private static RadioTuneTimingDiagnostics Idle() =>
        new(
            "idle",
            string.Empty,
            -1,
            0,
            null,
            null,
            null,
            null);
}
