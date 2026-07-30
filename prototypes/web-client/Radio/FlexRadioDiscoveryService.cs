using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed record DiscoveredFlexRadio(
    string RadioId,
    string Name,
    string Model,
    string Serial,
    string Nickname,
    string Callsign,
    string Host,
    int Port,
    string Status,
    string Version,
    bool InUse,
    bool MultiFlexEnabled,
    DateTimeOffset LastSeen,
    int AvailableClients = -1,
    int LicensedClients = -1);

public sealed record RadioSelectionOption(
    string RadioId,
    string Label,
    string Model,
    string Serial,
    string Host,
    int Port,
    string Status,
    string Version,
    bool Online,
    bool MultiFlexEnabled,
    bool CanSelect,
    bool IsSelected,
    bool IsConfiguredFallback,
    int AvailableClients,
    int LicensedClients,
    string Source = "local",
    string StationId = "",
    bool TunnelReady = true);

public sealed record SelectedRadioEndpoint(
    string RadioId,
    string Host,
    int Port,
    long Revision,
    string Source = "local",
    string StationId = "",
    string SourceRadioId = "");

public sealed record RadioSelectionSnapshot(
    string SelectedRadioId,
    IReadOnlyList<RadioSelectionOption> Radios,
    bool LowBandwidth);

public sealed record SelectRadioRequest(
    string RadioId,
    string? CurrentSessionId = null,
    string? BrowserClientId = null,
    bool? LowBandwidth = null);

public sealed record SetLowBandwidthRequest(
    bool Enabled,
    string SessionId);

public sealed class RadioSelectionManager : IRadioConnectionSelection
{
    private const string ConfiguredRadioId = "configured";
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RemoteOnlineWindow =
        TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, int>
        EmptyDisplayRates = new Dictionary<string, int>();
    private readonly object m_gate = new();
    private readonly Dictionary<string, DiscoveredFlexRadio> m_radios =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RemoteRadioCatalogEntry>
        m_remoteRadios = new(StringComparer.OrdinalIgnoreCase);
    private readonly string m_configuredHost;
    private readonly int m_configuredPort;
    private string m_selectedRadioId = ConfiguredRadioId;
    private bool m_lowBandwidth;
    private long m_revision = 1;
    private TaskCompletionSource<long> m_changed = NewChangeSource();

    public RadioSelectionManager(IOptions<RadioSettings> settings)
    {
        m_configuredHost = settings.Value.Host;
        m_configuredPort = settings.Value.TcpPort;
        m_lowBandwidth = settings.Value.LowBandwidthConnect;
    }

    public SelectedRadioEndpoint Selected
    {
        get
        {
            lock (m_gate)
            {
                return ResolveSelected();
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

    public IReadOnlyDictionary<string, int> NormalDisplayRatesToRestore =>
        EmptyDisplayRates;

    public void MarkNormalDisplayRatesRestored()
    {
    }

    public void Upsert(DiscoveredFlexRadio radio)
    {
        TaskCompletionSource<long>? changed = null;
        long revision = 0;
        lock (m_gate)
        {
            string previousHost =
                m_radios.TryGetValue(
                    radio.RadioId,
                    out DiscoveredFlexRadio? previous)
                    ? previous.Host
                    : string.Empty;
            int previousPort = previous?.Port ?? 0;
            m_radios[radio.RadioId] = radio;

            if (string.Equals(
                    m_selectedRadioId,
                    ConfiguredRadioId,
                    StringComparison.OrdinalIgnoreCase) &&
                EndpointsEqual(
                    radio.Host,
                    radio.Port,
                    m_configuredHost,
                    m_configuredPort))
            {
                // Associate the configured endpoint with its discovery identity
                // without restarting an already-correct receive session.
                m_selectedRadioId = radio.RadioId;
            }

            if (string.Equals(
                    m_selectedRadioId,
                    radio.RadioId,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(previousHost) &&
                !EndpointsEqual(
                    previousHost,
                    previousPort,
                    radio.Host,
                    radio.Port))
            {
                (changed, revision) = MarkChanged();
            }
        }
        changed?.TrySetResult(revision);
    }

    public void ReplaceRemoteRadios(
        IReadOnlyList<RemoteRadioCatalogEntry> radios)
    {
        ArgumentNullException.ThrowIfNull(radios);
        Dictionary<string, RemoteRadioCatalogEntry> replacement =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (RemoteRadioCatalogEntry radio in radios)
        {
            if (!replacement.TryAdd(radio.SelectorId, radio))
            {
                throw new InvalidDataException(
                    "The remote radio catalog contains a duplicate selector.");
            }
        }

        lock (m_gate)
        {
            m_remoteRadios.Clear();
            foreach ((string selectorId, RemoteRadioCatalogEntry radio) in
                     replacement)
            {
                m_remoteRadios.Add(selectorId, radio);
            }
        }
    }

    public RadioSelectionSnapshot GetSnapshot(
        string? selectedRadioId = null,
        bool? lowBandwidth = null)
    {
        lock (m_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            SelectedRadioEndpoint selected =
                ResolveSelection(selectedRadioId);
            List<RadioSelectionOption> options = m_radios.Values
                .Select(radio => ToOption(radio, selected, now))
                .Concat(
                    m_remoteRadios.Values.Select(
                        radio => ToRemoteOption(radio, selected, now)))
                .OrderByDescending(radio => radio.IsSelected)
                .ThenByDescending(radio => radio.Online)
                .ThenBy(radio => radio.Source, StringComparer.Ordinal)
                .ThenBy(radio => radio.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool configuredIsDiscovered = options.Any(
                option => EndpointsEqual(
                    option.Host,
                    option.Port,
                    m_configuredHost,
                    m_configuredPort));
            if (!configuredIsDiscovered)
            {
                options.Add(
                    new RadioSelectionOption(
                        ConfiguredRadioId,
                        $"Configured radio · {m_configuredHost}",
                        "FLEX",
                        string.Empty,
                        m_configuredHost,
                        m_configuredPort,
                        "Configured",
                        string.Empty,
                        true,
                        true,
                        true,
                        string.Equals(
                            selected.RadioId,
                            ConfiguredRadioId,
                            StringComparison.OrdinalIgnoreCase),
                        true,
                        -1,
                        -1));
            }

            string selectedId =
                options.FirstOrDefault(option => option.IsSelected)?.RadioId ??
                selected.RadioId;
            return new RadioSelectionSnapshot(
                selectedId,
                options,
                lowBandwidth ?? m_lowBandwidth);
        }
    }

    public bool TryResolve(
        string radioId,
        out SelectedRadioEndpoint selected,
        out string? error)
    {
        lock (m_gate)
        {
            string normalizedId = radioId?.Trim() ?? string.Empty;
            if (!TryResolveEndpoint(
                    normalizedId,
                    out selected,
                    out error))
            {
                selected = ResolveSelected();
                return false;
            }
            return true;
        }
    }

    public bool SetLowBandwidth(bool enabled)
    {
        TaskCompletionSource<long>? changed = null;
        long revision = 0;
        lock (m_gate)
        {
            if (m_lowBandwidth == enabled)
            {
                return false;
            }
            m_lowBandwidth = enabled;
            (changed, revision) = MarkChanged();
        }
        changed?.TrySetResult(revision);
        return true;
    }

    public bool TrySelect(
        string radioId,
        out SelectedRadioEndpoint selected,
        out bool connectionChanged,
        out string? error)
    {
        TaskCompletionSource<long>? changed = null;
        long revision = 0;
        lock (m_gate)
        {
            string normalizedId = radioId?.Trim() ?? string.Empty;
            if (!TryResolveEndpoint(
                    normalizedId,
                    out SelectedRadioEndpoint next,
                    out error))
            {
                selected = ResolveSelected();
                connectionChanged = false;
                return false;
            }

            SelectedRadioEndpoint current = ResolveSelected();
            connectionChanged = !string.Equals(
                current.RadioId,
                next.RadioId,
                StringComparison.OrdinalIgnoreCase);
            m_selectedRadioId = next.RadioId;
            if (connectionChanged)
            {
                (changed, revision) = MarkChanged();
            }
            selected = ResolveSelected();
            error = null;
        }

        changed?.TrySetResult(revision);
        return true;
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

    private SelectedRadioEndpoint ResolveSelected()
    {
        if (m_radios.TryGetValue(
                m_selectedRadioId,
                out DiscoveredFlexRadio? radio))
        {
            return new SelectedRadioEndpoint(
                radio.RadioId,
                radio.Host,
                radio.Port,
                m_revision);
        }
        if (m_remoteRadios.TryGetValue(
                m_selectedRadioId,
                out RemoteRadioCatalogEntry? remote))
        {
            return ToRemoteEndpoint(remote);
        }
        return new SelectedRadioEndpoint(
            ConfiguredRadioId,
            m_configuredHost,
            m_configuredPort,
            m_revision);
    }

    private SelectedRadioEndpoint ResolveSelection(string? radioId)
    {
        string normalizedId = radioId?.Trim() ?? string.Empty;
        if (string.Equals(
                normalizedId,
                ConfiguredRadioId,
                StringComparison.OrdinalIgnoreCase))
        {
            return new SelectedRadioEndpoint(
                ConfiguredRadioId,
                m_configuredHost,
                m_configuredPort,
                m_revision);
        }
        if (m_radios.TryGetValue(
                normalizedId,
                out DiscoveredFlexRadio? radio))
        {
            // An established session remains selected even if discovery now
            // reports it busy or stale. Availability only gates new sessions.
            return new SelectedRadioEndpoint(
                radio.RadioId,
                radio.Host,
                radio.Port,
                m_revision);
        }
        if (m_remoteRadios.TryGetValue(
                normalizedId,
                out RemoteRadioCatalogEntry? remote))
        {
            return ToRemoteEndpoint(remote);
        }

        return ResolveSelected();
    }

    private bool TryResolveEndpoint(
        string normalizedId,
        out SelectedRadioEndpoint selected,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(normalizedId) ||
            normalizedId.Length > 128)
        {
            selected = ResolveSelected();
            error = "A valid discovered radio ID is required.";
            return false;
        }

        if (string.Equals(
                normalizedId,
                ConfiguredRadioId,
                StringComparison.OrdinalIgnoreCase))
        {
            selected = new SelectedRadioEndpoint(
                ConfiguredRadioId,
                m_configuredHost,
                m_configuredPort,
                m_revision);
            error = null;
            return true;
        }

        if (m_remoteRadios.TryGetValue(
                normalizedId,
                out RemoteRadioCatalogEntry? remote))
        {
            bool online =
                remote.StationOnline &&
                DateTimeOffset.UtcNow - remote.LastSeen <=
                    RemoteOnlineWindow;
            if (!online)
            {
                selected = ResolveSelected();
                error = "That remote station is no longer online.";
                return false;
            }
            if (!remote.ReceiveProjectionReady)
            {
                selected = ResolveSelected();
                error =
                    "This station agent does not support the receive tunnel.";
                return false;
            }
            selected = ToRemoteEndpoint(remote);
            error = null;
            return true;
        }

        if (!m_radios.TryGetValue(
                normalizedId,
                out DiscoveredFlexRadio? radio))
        {
            selected = ResolveSelected();
            error = "That radio was not discovered by this web server.";
            return false;
        }

        if (DateTimeOffset.UtcNow - radio.LastSeen > OnlineWindow)
        {
            selected = ResolveSelected();
            error = "That radio is no longer present on the network.";
            return false;
        }

        // Discovery capacity is a useful hint, but it is a time-delayed UDP
        // snapshot. Let the live `client gui` command be the admission
        // authority so a browser never replaces another GUI client or rejects
        // a slot that the radio has just made available.
        selected = new SelectedRadioEndpoint(
            radio.RadioId,
            radio.Host,
            radio.Port,
            m_revision);
        error = null;
        return true;
    }

    private (TaskCompletionSource<long> Changed, long Revision) MarkChanged()
    {
        TaskCompletionSource<long> changed = m_changed;
        m_changed = NewChangeSource();
        m_revision++;
        return (changed, m_revision);
    }

    private static TaskCompletionSource<long> NewChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RadioSelectionOption ToOption(
        DiscoveredFlexRadio radio,
        SelectedRadioEndpoint selected,
        DateTimeOffset now)
    {
        string identity =
            !string.IsNullOrWhiteSpace(radio.Nickname)
                ? radio.Nickname
                : !string.IsNullOrWhiteSpace(radio.Callsign)
                    ? radio.Callsign
                    : radio.Model;
        string label = $"{identity} · {radio.Model} · {radio.Host}";
        bool online = now - radio.LastSeen <= OnlineWindow;
        bool canSelect = online;
        return new RadioSelectionOption(
            radio.RadioId,
            label,
            radio.Model,
            radio.Serial,
            radio.Host,
            radio.Port,
            radio.Status,
            radio.Version,
            online,
            radio.MultiFlexEnabled,
            canSelect,
            string.Equals(
                radio.RadioId,
                selected.RadioId,
                StringComparison.OrdinalIgnoreCase) ||
            EndpointsEqual(
                radio.Host,
                radio.Port,
                selected.Host,
                selected.Port),
            false,
            radio.AvailableClients,
            radio.LicensedClients);
    }

    private static RadioSelectionOption ToRemoteOption(
        RemoteRadioCatalogEntry radio,
        SelectedRadioEndpoint selected,
        DateTimeOffset now)
    {
        string identity = !string.IsNullOrWhiteSpace(radio.Nickname)
            ? radio.Nickname
            : radio.Model;
        bool online =
            radio.StationOnline &&
            now - radio.LastSeen <= RemoteOnlineWindow;
        return new RadioSelectionOption(
            radio.SelectorId,
            $"{identity} · {radio.Model} · Remote",
            radio.Model,
            radio.Serial,
            $"Via {radio.StationId}",
            0,
            radio.Status,
            string.Empty,
            online,
            radio.LicensedClients > 1,
            online && radio.ReceiveProjectionReady,
            string.Equals(
                radio.SelectorId,
                selected.RadioId,
                StringComparison.OrdinalIgnoreCase),
            false,
            radio.AvailableClients,
            radio.LicensedClients,
            "remote",
            radio.StationId,
            radio.ReceiveProjectionReady);
    }

    private SelectedRadioEndpoint ToRemoteEndpoint(
        RemoteRadioCatalogEntry remote) =>
        new(
            remote.SelectorId,
            remote.StationId,
            0,
            m_revision,
            "remote",
            remote.StationId,
            remote.SourceRadioId);

    private static bool EndpointsEqual(
        string firstHost,
        int firstPort,
        string secondHost,
        int secondPort) =>
        firstPort == secondPort &&
        string.Equals(
            firstHost,
            secondHost,
            StringComparison.OrdinalIgnoreCase);
}

public sealed class FlexRadioDiscoveryService(
    RadioSelectionManager selectionManager,
    IOptions<RadioSettings> settings,
    ILogger<FlexRadioDiscoveryService> logger)
    : BackgroundService
{
    public const int DiscoveryPort = 4992;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(
                settings.Value.Mode,
                "FlexRx",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException exception)
            {
                logger.LogWarning(
                    exception,
                    "Flex discovery could not listen on UDP {DiscoveryPort}; retrying",
                    DiscoveryPort);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        using UdpClient udp = new(AddressFamily.InterNetwork);
        udp.Client.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress,
            true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        logger.LogInformation(
            "Listening for Flex radio discovery on UDP {DiscoveryPort}",
            DiscoveryPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram =
                await udp.ReceiveAsync(cancellationToken);
            DiscoveredFlexRadio? radio = FlexDiscoveryParser.TryParse(
                datagram.Buffer,
                datagram.RemoteEndPoint.Address);
            if (radio is not null)
            {
                selectionManager.Upsert(radio);
            }
        }
    }
}

public static class FlexDiscoveryParser
{
    private const int MaximumPacketBytes = 8 * 1024;

    public static DiscoveredFlexRadio? TryParse(
        ReadOnlySpan<byte> packet,
        IPAddress senderAddress)
    {
        if (packet.IsEmpty || packet.Length > MaximumPacketBytes)
        {
            return null;
        }

        string text = Encoding.UTF8.GetString(packet);
        Dictionary<string, string> fields =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string token in text.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            int separator = token.IndexOf('=');
            if (separator <= 0 || separator == token.Length - 1)
            {
                continue;
            }
            string key = Clean(token[..separator], decodeSpaces: false)
                .ToLowerInvariant();
            string value = Clean(token[(separator + 1)..], decodeSpaces: true);
            if (key.Length is > 0 and <= 64 && value.Length <= 256)
            {
                fields[key] = value;
            }
        }

        string serial = Read(fields, "serial", 64);
        string model = Read(fields, "model", 64);
        if (string.IsNullOrWhiteSpace(serial) ||
            string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        string hostText = Read(fields, "ip", 64);
        IPAddress address =
            IPAddress.TryParse(hostText, out IPAddress? parsedAddress) &&
            parsedAddress.AddressFamily == AddressFamily.InterNetwork
                ? parsedAddress
                : senderAddress;
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        int port = FlexRadioDiscoveryService.DiscoveryPort;
        if (fields.TryGetValue("port", out string? portText) &&
            (!int.TryParse(portText, out port) ||
             port is < 1 or > 65_535))
        {
            return null;
        }

        int availableClients = ReadNonNegativeInt(
            fields,
            "available_clients");
        int licensedClients = ReadNonNegativeInt(
            fields,
            "licensed_clients");
        bool multiFlexEnabled =
            fields.TryGetValue("mf_enable", out string? multiFlex)
                ? multiFlex != "0"
                : licensedClients > 1 ||
                  (licensedClients < 0 && availableClients != 0);

        string radioId = $"flex:{serial}";
        return new DiscoveredFlexRadio(
            radioId,
            Read(fields, "name", 64),
            model,
            serial,
            Read(fields, "nickname", 64),
            Read(fields, "callsign", 32),
            address.ToString(),
            port,
            Read(fields, "status", 32),
            Read(fields, "version", 32),
            fields.TryGetValue("inuse", out string? inUse) &&
            inUse == "1",
            multiFlexEnabled,
            DateTimeOffset.UtcNow,
            availableClients,
            licensedClients);
    }

    private static string Read(
        IReadOnlyDictionary<string, string> fields,
        string key,
        int maximumLength) =>
        fields.TryGetValue(key, out string? value)
            ? value[..Math.Min(value.Length, maximumLength)]
            : string.Empty;

    private static int ReadNonNegativeInt(
        IReadOnlyDictionary<string, string> fields,
        string key) =>
        fields.TryGetValue(key, out string? value) &&
        int.TryParse(value, out int parsed) &&
        parsed >= 0
            ? parsed
            : -1;

    private static string Clean(string value, bool decodeSpaces)
    {
        StringBuilder cleaned = new(value.Length);
        foreach (char character in value)
        {
            if (decodeSpaces && character == '\x7f')
            {
                cleaned.Append(' ');
            }
            else if (!char.IsControl(character) && character != '\x7f')
            {
                cleaned.Append(character);
            }
        }
        return cleaned.ToString().Trim();
    }
}
