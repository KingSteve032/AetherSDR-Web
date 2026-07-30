using System.Collections.Concurrent;

namespace AetherSDR.Web.Radio;

internal sealed class FlexGuiClientRoster
{
    private const int MaximumFieldLength = 128;
    private readonly ConcurrentDictionary<uint, ClientState> m_clients = [];

    public void Clear() => m_clients.Clear();

    public bool Observe(string line)
    {
        if (!FlexStatusParser.TryParseClientStatus(
                line,
                out uint clientHandle,
                out string action,
                out IReadOnlyDictionary<string, string> fields))
        {
            return false;
        }

        if (string.Equals(
                action,
                "disconnected",
                StringComparison.OrdinalIgnoreCase))
        {
            m_clients.TryRemove(clientHandle, out _);
            return true;
        }

        bool connected =
            string.Equals(
                action,
                "connected",
                StringComparison.OrdinalIgnoreCase) ||
            fields.ContainsKey("connected");
        if (!connected && !m_clients.ContainsKey(clientHandle))
        {
            return true;
        }

        m_clients.AddOrUpdate(
            clientHandle,
            _ => ClientState.Create(fields),
            (_, current) => current.Apply(fields));
        return true;
    }

    public IReadOnlyList<RadioGuiClientDiagnostics> Snapshot(
        uint localClientHandle) =>
        m_clients
            .OrderBy(client => client.Key)
            .Select(client => new RadioGuiClientDiagnostics(
                client.Key,
                client.Value.ClientId,
                client.Value.Program,
                client.Value.Station,
                client.Value.Source,
                client.Value.LocalPtt,
                client.Key == localClientHandle))
            .ToArray();

    private sealed record ClientState(
        string ClientId,
        string Program,
        string Station,
        string Source,
        bool LocalPtt)
    {
        public static ClientState Create(
            IReadOnlyDictionary<string, string> fields)
        {
            string program = ReadText(fields, "program", "Unknown");
            return new ClientState(
                ReadText(fields, "client_id"),
                program,
                ReadText(fields, "station", program),
                ReadSource(fields),
                ReadBool(fields, "local_ptt"));
        }

        public ClientState Apply(
            IReadOnlyDictionary<string, string> fields)
        {
            string program = ReadText(fields, "program", Program);
            return this with
            {
                ClientId = ReadText(fields, "client_id", ClientId),
                Program = program,
                Station = ReadText(fields, "station", Station),
                Source = ReadSource(fields, Source),
                LocalPtt = fields.ContainsKey("local_ptt")
                    ? ReadBool(fields, "local_ptt")
                    : LocalPtt
            };
        }
    }

    private static string ReadSource(
        IReadOnlyDictionary<string, string> fields,
        string fallback = "")
    {
        foreach (string key in
                 new[] { "ip", "client_ip", "remote_ip", "name" })
        {
            string value = ReadText(fields, key);
            if (!string.IsNullOrEmpty(value) &&
                !Guid.TryParse(value, out _))
            {
                return value;
            }
        }
        return fallback;
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> fields,
        string key) =>
        fields.TryGetValue(key, out string? value) &&
        (value == "1" ||
         bool.TryParse(value, out bool parsed) && parsed);

    private static string ReadText(
        IReadOnlyDictionary<string, string> fields,
        string key,
        string fallback = "")
    {
        if (!fields.TryGetValue(key, out string? value))
        {
            return fallback;
        }

        string clean = string.Concat(
                value.Where(character => !char.IsControl(character)))
            .Trim();
        return clean[..Math.Min(clean.Length, MaximumFieldLength)];
    }
}
