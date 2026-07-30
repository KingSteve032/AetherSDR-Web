namespace AetherSDR.Web.Radio;

public static class AdminRadioHealthStates
{
    public const string Healthy = "healthy";
    public const string Busy = "busy";
    public const string Degraded = "degraded";
    public const string Reconnecting = "reconnecting";
    public const string Offline = "offline";
}

public sealed record AdminRadioHealthSnapshot(
    string State,
    string Summary,
    int SessionCount,
    DateTimeOffset? OldestSessionAt,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? LastStreamAt,
    int QueueDepth,
    int QueueCapacity,
    long DroppedMessages);

internal static class RadioHealthClassifier
{
    private static readonly TimeSpan StaleHeartbeat =
        TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StaleStream =
        TimeSpan.FromSeconds(10);

    public static AdminRadioHealthSnapshot Classify(
        RadioSelectionOption radio,
        IReadOnlyList<RadioSessionDiagnostics> sessions,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(sessions);

        int queueDepth = sessions
            .SelectMany(session => session.WebClients)
            .Sum(client => Math.Max(0, client.QueueDepth));
        int queueCapacity = sessions
            .SelectMany(session => session.WebClients)
            .Sum(client => Math.Max(0, client.QueueCapacity));
        long droppedMessages = sessions
            .SelectMany(session => session.WebClients)
            .Sum(client => Math.Max(0, client.DroppedMessages));
        DateTimeOffset? oldestSessionAt = sessions.Count == 0
            ? null
            : sessions.Min(session => session.CreatedAt);
        DateTimeOffset? lastActivityAt = sessions.Count == 0
            ? null
            : sessions.Max(session => session.LastActivity);
        DateTimeOffset? lastStreamAt = Latest(
            sessions.SelectMany(session => new DateTimeOffset?[]
            {
                session.Transport.LastDatagramAt,
                session.Transport.LastSpectrumFrameAt,
                session.Transport.LastAudioFrameAt
            }));

        AdminRadioHealthSnapshot Snapshot(string state, string summary) =>
            new(
                state,
                summary,
                sessions.Count,
                oldestSessionAt,
                lastActivityAt,
                lastStreamAt,
                queueDepth,
                queueCapacity,
                droppedMessages);

        if (!radio.Online)
        {
            return Snapshot(
                AdminRadioHealthStates.Offline,
                "The radio path is not currently reachable.");
        }

        RadioSessionDiagnostics? reconnecting = sessions.FirstOrDefault(
            session => IsConnectionState(
                session,
                "connecting",
                "reconnecting"));
        if (reconnecting is not null)
        {
            return Snapshot(
                AdminRadioHealthStates.Reconnecting,
                $"Browser session {ShortId(reconnecting.SessionId)} is " +
                $"{reconnecting.ConnectionState}.");
        }

        RadioSessionDiagnostics? failed = sessions.FirstOrDefault(
            session => !session.Connected &&
                !IsConnectionState(session, "radio-busy"));
        if (failed is not null)
        {
            return Snapshot(
                AdminRadioHealthStates.Degraded,
                failed.ConnectionError ??
                $"Browser session {ShortId(failed.SessionId)} is not connected.");
        }

        string? staleReason = FindStaleTransportReason(sessions, now);
        if (staleReason is not null)
        {
            return Snapshot(AdminRadioHealthStates.Degraded, staleReason);
        }

        bool queuePressured = sessions
            .SelectMany(session => session.WebClients)
            .Any(client =>
                client.QueueCapacity > 0 &&
                client.QueueDepth >= Math.Ceiling(
                    client.QueueCapacity * 0.75));
        if (queuePressured || droppedMessages > 0)
        {
            string dropped = droppedMessages > 0
                ? $"; {droppedMessages} message(s) dropped"
                : string.Empty;
            return Snapshot(
                AdminRadioHealthStates.Degraded,
                $"Browser queue pressure is {queueDepth} of " +
                $"{queueCapacity}{dropped}.");
        }

        bool busy = radio.AvailableClients == 0 ||
            sessions.Any(session => IsConnectionState(session, "radio-busy")) ||
            string.Equals(
                radio.Status.Replace('-', '_'),
                "in_use",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                radio.Status,
                "busy",
                StringComparison.OrdinalIgnoreCase);
        if (busy)
        {
            return Snapshot(
                AdminRadioHealthStates.Busy,
                "No GUI client slots are currently available.");
        }

        if (sessions.Count == 0)
        {
            return Snapshot(
                AdminRadioHealthStates.Healthy,
                "Reachable and ready for a browser session.");
        }

        return Snapshot(
            AdminRadioHealthStates.Healthy,
            $"{sessions.Count} browser session(s) connected with healthy " +
            "transport and queue signals.");
    }

    private static string? FindStaleTransportReason(
        IReadOnlyList<RadioSessionDiagnostics> sessions,
        DateTimeOffset now)
    {
        foreach (RadioSessionDiagnostics session in sessions.Where(
                     candidate => candidate.Connected))
        {
            DateTimeOffset baseline =
                session.Transport.ConnectedAt ?? session.CreatedAt;
            if (string.Equals(
                    session.Transport.Transport,
                    "FlexRx",
                    StringComparison.OrdinalIgnoreCase) &&
                IsStale(
                    session.Transport.LastHeartbeatAt,
                    baseline,
                    now,
                    StaleHeartbeat))
            {
                return $"Heartbeat for session {ShortId(session.SessionId)} " +
                    "is stale.";
            }

            if (session.Panadapters.Count > 0 &&
                IsStale(
                    session.Transport.LastSpectrumFrameAt,
                    baseline,
                    now,
                    StaleStream))
            {
                return $"Spectrum for session {ShortId(session.SessionId)} " +
                    "is stale.";
            }

            if (session.Slices.Count > 0 &&
                IsStale(
                    session.Transport.LastAudioFrameAt,
                    baseline,
                    now,
                    StaleStream))
            {
                return $"Audio for session {ShortId(session.SessionId)} is stale.";
            }
        }

        return null;
    }

    private static bool IsStale(
        DateTimeOffset? observedAt,
        DateTimeOffset baseline,
        DateTimeOffset now,
        TimeSpan threshold) =>
        now - (observedAt ?? baseline) > threshold;

    private static bool IsConnectionState(
        RadioSessionDiagnostics session,
        params string[] states) =>
        states.Any(state => string.Equals(
            session.ConnectionState,
            state,
            StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? Latest(
        IEnumerable<DateTimeOffset?> values) =>
        values.Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max() is DateTimeOffset maximum && maximum != default
                ? maximum
                : null;

    private static string ShortId(string value) =>
        value.Length <= 8 ? value : value[..8];
}
