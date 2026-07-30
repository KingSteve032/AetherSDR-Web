using System.Net;
using System.Net.Sockets;
using System.Text;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Agent;

public sealed record LocalRadioAdvertisement(
    StationRadioAdvertisement Advertisement,
    string Host,
    int Port,
    DateTimeOffset LastSeen);

public sealed record LocalRadioEndpoint(
    StationRadioAdvertisement Advertisement,
    string Host,
    int Port);

public interface IStationRadioInventoryProvider
{
    IReadOnlyList<StationRadioAdvertisement> GetSnapshot();
    bool TryResolve(
        string radioId,
        out LocalRadioEndpoint? endpoint);
}

public sealed class FlexDiscoveryService(
    IOptions<AgentSettings> settings,
    ILogger<FlexDiscoveryService> logger)
    : BackgroundService, IStationRadioInventoryProvider
{
    public const int DiscoveryPort = 4992;
    private readonly object m_gate = new();
    private readonly Dictionary<string, LocalRadioAdvertisement> m_discovered =
        new(StringComparer.Ordinal);
    private readonly AgentSettings m_settings = settings.Value;

    public IReadOnlyList<StationRadioAdvertisement> GetSnapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<StationRadioAdvertisement> radios = [];
        lock (m_gate)
        {
            radios.AddRange(
                m_discovered.Values
                    .Where(radio =>
                        now - radio.LastSeen <=
                        TimeSpan.FromSeconds(
                            m_settings.RadioOfflineSeconds))
                    .Select(radio => radio.Advertisement));
        }
        radios.AddRange(
            m_settings.ConfiguredRadios.Select(ToAdvertisement));
        return radios
            .GroupBy(radio => radio.RadioId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(radio => radio.RadioId, StringComparer.Ordinal)
            .ToArray();
    }

    public bool TryResolve(
        string radioId,
        out LocalRadioEndpoint? endpoint)
    {
        endpoint = null;
        string normalized = radioId?.Trim() ?? string.Empty;
        if (!StationProtocolValidator.IsIdentifier(
                normalized,
                StationProtocol.MaximumRadioIdLength))
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (m_gate)
        {
            if (m_discovered.TryGetValue(
                    normalized,
                    out LocalRadioAdvertisement? discovered) &&
                now - discovered.LastSeen <=
                TimeSpan.FromSeconds(m_settings.RadioOfflineSeconds))
            {
                endpoint = new LocalRadioEndpoint(
                    discovered.Advertisement,
                    discovered.Host,
                    discovered.Port);
                return true;
            }
        }

        ConfiguredRadioSettings? configured =
            m_settings.ConfiguredRadios.FirstOrDefault(
                radio => string.Equals(
                    radio.RadioId,
                    normalized,
                    StringComparison.Ordinal));
        if (configured is null ||
            string.IsNullOrWhiteSpace(configured.Host) ||
            configured.Host.Length > 253 ||
            configured.Port is < 1 or > 65_535 ||
            configured.Host.Any(char.IsControl))
        {
            return false;
        }
        endpoint = new LocalRadioEndpoint(
            ToAdvertisement(configured),
            configured.Host,
            configured.Port);
        return true;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (!m_settings.DiscoveryEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException exception)
            {
                logger.LogWarning(
                    exception,
                    "FLEX discovery could not listen on UDP {Port}; retrying",
                    DiscoveryPort);
                await Task.Delay(
                    TimeSpan.FromSeconds(5),
                    stoppingToken);
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
            "Listening for station-local FLEX discovery on UDP {Port}",
            DiscoveryPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram =
                await udp.ReceiveAsync(cancellationToken);
            LocalRadioAdvertisement? radio =
                FlexDiscoveryParser.TryParse(
                    datagram.Buffer,
                    datagram.RemoteEndPoint.Address,
                    DateTimeOffset.UtcNow);
            if (radio is null)
            {
                continue;
            }
            lock (m_gate)
            {
                m_discovered[radio.Advertisement.RadioId] = radio;
            }
        }
    }

    private static StationRadioAdvertisement ToAdvertisement(
        ConfiguredRadioSettings radio) =>
        new(
            radio.RadioId,
            radio.Family,
            radio.Model,
            radio.Serial,
            radio.Nickname,
            radio.Status,
            radio.AvailableClients,
            radio.LicensedClients,
            radio.CapabilityHash);
}

public static class FlexDiscoveryParser
{
    private const int MaximumPacketBytes = 8 * 1024;

    public static LocalRadioAdvertisement? TryParse(
        ReadOnlySpan<byte> packet,
        IPAddress senderAddress,
        DateTimeOffset receivedAt)
    {
        if (packet.IsEmpty ||
            packet.Length > MaximumPacketBytes ||
            senderAddress.AddressFamily != AddressFamily.InterNetwork)
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
            string value = Clean(
                token[(separator + 1)..],
                decodeSpaces: true);
            if (key.Length is > 0 and <= 64 && value.Length <= 256)
            {
                fields[key] = value;
            }
        }

        string serial = Read(fields, "serial", 64);
        string model = Read(fields, "model", 64);
        if (!StationProtocolValidator.IsText(serial, 64) ||
            !StationProtocolValidator.IsText(model, 64))
        {
            return null;
        }

        string hostText = Read(fields, "ip", 64);
        IPAddress address =
            IPAddress.TryParse(hostText, out IPAddress? parsedAddress) &&
            parsedAddress.AddressFamily == AddressFamily.InterNetwork
                ? parsedAddress
                : senderAddress;

        int port = FlexDiscoveryService.DiscoveryPort;
        if (fields.TryGetValue("port", out string? portText) &&
            (!int.TryParse(portText, out port) ||
             port is < 1 or > 65_535))
        {
            return null;
        }

        int availableClients =
            ReadNonNegativeInt(fields, "available_clients");
        int licensedClients =
            ReadNonNegativeInt(fields, "licensed_clients");
        string radioStatus = NormalizeStatus(
            Read(fields, "status", 32),
            fields.TryGetValue("inuse", out string? inUse) &&
            inUse == "1");
        string radioId = $"flex:{serial}";
        StationRadioAdvertisement advertisement = new(
            radioId,
            "flex",
            model,
            serial,
            ReadPreferredName(fields),
            radioStatus,
            availableClients,
            licensedClients,
            string.Empty);
        return StationProtocolValidator.ValidateInventory(
            new StationInventoryMessage(
                StationMessageTypes.Inventory,
                1,
                [advertisement])) is null
            ? new LocalRadioAdvertisement(
                advertisement,
                address.ToString(),
                port,
                receivedAt)
            : null;
    }

    private static string ReadPreferredName(
        IReadOnlyDictionary<string, string> fields)
    {
        string nickname = Read(fields, "nickname", 64);
        if (!string.IsNullOrWhiteSpace(nickname))
        {
            return nickname;
        }
        return Read(fields, "callsign", 32);
    }

    private static string NormalizeStatus(string status, bool inUse)
    {
        if (inUse)
        {
            return "in-use";
        }
        return status.Trim().ToLowerInvariant() switch
        {
            "available" => "available",
            "in_use" or "in-use" => "in-use",
            "updating" => "updating",
            _ => "unknown"
        };
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
            else if (!char.IsControl(character) &&
                     character != '\x7f')
            {
                cleaned.Append(character);
            }
        }
        return cleaned.ToString().Trim();
    }
}
