namespace AetherSDR.Web.Radio;

public enum RadioTxOccupancyState
{
    Unknown,
    Idle,
    AetherOwned,
    External,
    Ambiguous
}

public sealed record RadioTxOccupant(
    uint ClientHandle,
    string Program,
    string Station,
    string Source,
    bool AetherOwned);

public sealed record RadioTxOccupancySnapshot(
    string RadioId,
    RadioTxOccupancyState State,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? FreshUntil,
    IReadOnlyList<RadioTxOccupant> Occupants,
    IReadOnlyList<RadioTxOccupant> LocalPttOwners)
{
    public string StateName => State switch
    {
        RadioTxOccupancyState.AetherOwned => "aether-owned",
        RadioTxOccupancyState.External => "external",
        RadioTxOccupancyState.Ambiguous => "ambiguous",
        RadioTxOccupancyState.Idle => "idle",
        _ => "unknown"
    };

    public bool BrowserLeaseAllowed => State == RadioTxOccupancyState.Idle;

    public bool HasExclusiveLocalPttAuthority(uint clientHandle) =>
        clientHandle != 0 &&
        LocalPttOwners.Count == 1 &&
        LocalPttOwners[0].AetherOwned &&
        LocalPttOwners[0].ClientHandle == clientHandle;
}

public sealed class RadioTxOccupancyRegistry(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan ObservationLifetime = TimeSpan.FromSeconds(8);

    private static readonly HashSet<string> IdleStates =
        new(StringComparer.Ordinal)
        {
            "READY",
            "RECEIVE"
        };
    private static readonly HashSet<string> HardwarePttSources =
        new(StringComparer.Ordinal)
        {
            "MIC",
            "ACC",
            "RCA",
            "HW"
        };

    private readonly object m_gate = new();
    private readonly TimeProvider m_timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Dictionary<string, Observation>> m_observations =
        new(StringComparer.OrdinalIgnoreCase);

    public RadioTxOccupancySnapshot ObserveInterlock(
        string radioId,
        string reporterId,
        uint reporterClientHandle,
        string interlockState,
        uint? txClientHandle,
        string? pttSource,
        IReadOnlyList<RadioGuiClientDiagnostics> clients)
    {
        string radio = NormalizeRadioId(radioId);
        string reporter = NormalizeReporterId(reporterId);
        string state = NormalizeToken(interlockState, 64);
        string source = NormalizeToken(pttSource, 32);
        ArgumentException.ThrowIfNullOrWhiteSpace(radio);
        ArgumentException.ThrowIfNullOrWhiteSpace(reporter);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        if (reporterClientHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reporterClientHandle));
        }
        ArgumentNullException.ThrowIfNull(clients);
        if (clients.Count > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(clients));
        }

        uint? normalizedOwner = txClientHandle is > 0
            ? txClientHandle
            : null;
        RadioGuiClientDiagnostics[] roster = clients
            .Where(client => client.ClientHandle != 0)
            .GroupBy(client => client.ClientHandle)
            .Select(group => group.Last())
            .OrderBy(client => client.ClientHandle)
            .ToArray();
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        lock (m_gate)
        {
            PruneLocked(now);
            if (!m_observations.TryGetValue(
                    radio,
                    out Dictionary<string, Observation>? reports))
            {
                reports = new Dictionary<string, Observation>(StringComparer.Ordinal);
                m_observations.Add(radio, reports);
            }
            reports[reporter] = new Observation(
                now,
                reporterClientHandle,
                state,
                normalizedOwner,
                source,
                roster);
            return BuildSnapshotLocked(radio, now);
        }
    }

    public RadioTxOccupancySnapshot RemoveReporter(string radioId, string reporterId)
    {
        string radio = NormalizeRadioId(radioId);
        string reporter = NormalizeReporterId(reporterId);
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        lock (m_gate)
        {
            if (m_observations.TryGetValue(
                    radio,
                    out Dictionary<string, Observation>? reports))
            {
                reports.Remove(reporter);
                if (reports.Count == 0)
                {
                    m_observations.Remove(radio);
                }
            }
            PruneLocked(now);
            return BuildSnapshotLocked(radio, now);
        }
    }

    public RadioTxOccupancySnapshot GetSnapshot(string radioId)
    {
        string radio = NormalizeRadioId(radioId);
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        lock (m_gate)
        {
            PruneLocked(now);
            return BuildSnapshotLocked(radio, now);
        }
    }

    private RadioTxOccupancySnapshot BuildSnapshotLocked(
        string radio,
        DateTimeOffset now)
    {
        if (!m_observations.TryGetValue(
                radio,
                out Dictionary<string, Observation>? reports) ||
            reports.Count == 0)
        {
            return Unknown(radio);
        }

        Observation[] fresh = reports.Values
            .Where(report => report.ObservedAt + ObservationLifetime > now)
            .ToArray();
        if (fresh.Length == 0)
        {
            return Unknown(radio);
        }

        DateTimeOffset observedAt = fresh.Max(report => report.ObservedAt);
        DateTimeOffset freshUntil = fresh.Max(
            report => report.ObservedAt + ObservationLifetime);
        HashSet<uint> knownAetherHandles = fresh
            .Select(report => report.ReporterClientHandle)
            .Where(handle => handle != 0)
            .ToHashSet();
        RadioTxOccupant[] localPttOwners = BuildLocalPttOwners(
            fresh,
            knownAetherHandles);
        Observation[] active = fresh
            .Where(report => !IdleStates.Contains(report.InterlockState))
            .ToArray();
        if (active.Length == 0)
        {
            return new RadioTxOccupancySnapshot(
                radio,
                RadioTxOccupancyState.Idle,
                observedAt,
                freshUntil,
                [],
                localPttOwners);
        }

        RadioTxOccupant[] occupants = BuildOccupants(
            active,
            fresh,
            knownAetherHandles);

        // Reporters share one reliable radio status stream per GUI client. If
        // one still says READY while another says TX or a transition/fault,
        // ownership is not safe to infer until they converge.
        if (active.Length != fresh.Length)
        {
            return new RadioTxOccupancySnapshot(
                radio,
                RadioTxOccupancyState.Ambiguous,
                observedAt,
                freshUntil,
                occupants,
                localPttOwners);
        }

        uint?[] owners = active
            .Select(report => report.TxClientHandle)
            .Distinct()
            .ToArray();
        if (owners.Length != 1)
        {
            return new RadioTxOccupancySnapshot(
                radio,
                RadioTxOccupancyState.Ambiguous,
                observedAt,
                freshUntil,
                occupants,
                localPttOwners);
        }

        uint? owner = owners[0];
        RadioTxOccupancyState state;
        if (owner is uint handle)
        {
            state = knownAetherHandles.Contains(handle)
                ? RadioTxOccupancyState.AetherOwned
                : RadioTxOccupancyState.External;
        }
        else
        {
            // Hardware MIC/ACC/RCA keying commonly has no GUI owner. Other
            // ownerless software/interlock states remain ambiguous so an
            // AetherSDR cleanup path can never unkey an unproven owner.
            state = active.All(report =>
                    HardwarePttSources.Contains(report.PttSource))
                ? RadioTxOccupancyState.External
                : RadioTxOccupancyState.Ambiguous;
        }

        return new RadioTxOccupancySnapshot(
            radio,
            state,
            observedAt,
            freshUntil,
            occupants,
            localPttOwners);
    }

    private static RadioTxOccupant[] BuildLocalPttOwners(
        IReadOnlyList<Observation> fresh,
        IReadOnlySet<uint> knownAetherHandles) =>
        fresh
            .SelectMany(report => report.Clients)
            .Where(client => client.LocalPtt)
            .GroupBy(client => client.ClientHandle)
            .Select(group => group.Last())
            .OrderBy(client => client.ClientHandle)
            .Select(client => new RadioTxOccupant(
                client.ClientHandle,
                client.Program,
                client.Station,
                client.Source,
                knownAetherHandles.Contains(client.ClientHandle)))
            .ToArray();

    private static RadioTxOccupant[] BuildOccupants(
        IReadOnlyList<Observation> active,
        IReadOnlyList<Observation> fresh,
        IReadOnlySet<uint> knownAetherHandles)
    {
        Dictionary<uint, RadioGuiClientDiagnostics> roster = fresh
            .SelectMany(report => report.Clients)
            .GroupBy(client => client.ClientHandle)
            .ToDictionary(group => group.Key, group => group.Last());
        uint[] handles = active
            .Select(report => report.TxClientHandle)
            .Where(handle => handle.HasValue)
            .Select(handle => handle!.Value)
            .Distinct()
            .OrderBy(handle => handle)
            .ToArray();
        if (handles.Length == 0)
        {
            string source = active
                .Select(report => report.PttSource)
                .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
            return
            [
                new RadioTxOccupant(
                    0,
                    "Radio interlock",
                    source.Length > 0 ? source : "Owner not reported",
                    source,
                    AetherOwned: false)
            ];
        }

        return handles
            .Select(handle =>
            {
                roster.TryGetValue(
                    handle,
                    out RadioGuiClientDiagnostics? client);
                string source = active
                    .Where(report => report.TxClientHandle == handle)
                    .Select(report => report.PttSource)
                    .FirstOrDefault(value => value.Length > 0) ??
                    client?.Source ??
                    string.Empty;
                return new RadioTxOccupant(
                    handle,
                    client?.Program ?? "FLEX client",
                    client?.Station ?? string.Empty,
                    source,
                    knownAetherHandles.Contains(handle));
            })
            .ToArray();
    }

    private void PruneLocked(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, Dictionary<string, Observation>> radio in
            m_observations.ToArray())
        {
            foreach (KeyValuePair<string, Observation> report in radio.Value.ToArray())
            {
                if (report.Value.ObservedAt + ObservationLifetime <= now)
                {
                    radio.Value.Remove(report.Key);
                }
            }
            if (radio.Value.Count == 0)
            {
                m_observations.Remove(radio.Key);
            }
        }
    }

    private static RadioTxOccupancySnapshot Unknown(string radio) =>
        new(radio, RadioTxOccupancyState.Unknown, null, null, [], []);

    private static string NormalizeRadioId(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 128 &&
               normalized.All(character => !char.IsControl(character))
            ? normalized.ToUpperInvariant()
            : string.Empty;
    }

    private static string NormalizeReporterId(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 128 &&
               normalized.All(character =>
                   !char.IsControl(character) && !char.IsWhiteSpace(character))
            ? normalized
            : string.Empty;
    }

    private static string NormalizeToken(string? value, int maximumLength)
    {
        string normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length <= maximumLength &&
               normalized.All(character =>
                   !char.IsControl(character) && !char.IsWhiteSpace(character))
            ? normalized
            : string.Empty;
    }

    private sealed record Observation(
        DateTimeOffset ObservedAt,
        uint ReporterClientHandle,
        string InterlockState,
        uint? TxClientHandle,
        string PttSource,
        IReadOnlyList<RadioGuiClientDiagnostics> Clients);
}
