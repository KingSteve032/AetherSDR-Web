using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AetherRemote.Agent;

public sealed record FlexDiscoveryConsoleRadio(
    string RadioId,
    string Model,
    string Serial,
    string Nickname,
    string Status,
    string Host,
    int Port,
    int AvailableClients,
    int LicensedClients);

public static class FlexDiscoveryConsole
{
    private const int MinimumSeconds = 1;
    private const int MaximumSeconds = 10;

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        string.Equals(args[0], "--discover-once", StringComparison.Ordinal);

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        int seconds = 3;
        if (args.Count == 3 &&
            string.Equals(args[1], "--seconds", StringComparison.Ordinal) &&
            int.TryParse(args[2], out int parsed) &&
            parsed is >= MinimumSeconds and <= MaximumSeconds)
        {
            seconds = parsed;
        }
        else if (args.Count != 1)
        {
            await error.WriteLineAsync(
                "Usage: AetherRemote.Agent --discover-once [--seconds 1-10]");
            return 2;
        }

        try
        {
            IReadOnlyList<LocalRadioAdvertisement> radios =
                await ObserveAsync(
                    TimeSpan.FromSeconds(seconds),
                    cancellationToken);
            FlexDiscoveryConsoleRadio[] report = radios
                .Select(radio => new FlexDiscoveryConsoleRadio(
                    radio.Advertisement.RadioId,
                    radio.Advertisement.Model,
                    radio.Advertisement.Serial,
                    radio.Advertisement.Nickname,
                    radio.Advertisement.Status,
                    radio.Host,
                    radio.Port,
                    radio.Advertisement.AvailableClients,
                    radio.Advertisement.LicensedClients))
                .OrderBy(radio => radio.RadioId, StringComparer.Ordinal)
                .ToArray();
            await output.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        command = "discoverOnce",
                        discoverySeconds = seconds,
                        radioCount = report.Length,
                        radios = report,
                        radioCommandSent = false,
                        transmitActionPerformed = false
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return 0;
        }
        catch (SocketException exception)
        {
            await error.WriteLineAsync(
                $"FLEX discovery listener failed: {exception.SocketErrorCode}");
            return 2;
        }
    }

    internal static async Task<IReadOnlyList<LocalRadioAdvertisement>> ObserveAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration < TimeSpan.FromSeconds(MinimumSeconds) ||
            duration > TimeSpan.FromSeconds(MaximumSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        using UdpClient udp = new(AddressFamily.InterNetwork);
        udp.Client.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress,
            true);
        udp.Client.Bind(
            new IPEndPoint(IPAddress.Any, FlexDiscoveryService.DiscoveryPort));

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        Dictionary<string, LocalRadioAdvertisement> radios =
            new(StringComparer.Ordinal);
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                UdpReceiveResult datagram = await udp.ReceiveAsync(deadline.Token);
                LocalRadioAdvertisement? radio =
                    FlexDiscoveryParser.TryParse(
                        datagram.Buffer,
                        datagram.RemoteEndPoint.Address,
                        DateTimeOffset.UtcNow);
                if (radio is not null)
                {
                    radios[radio.Advertisement.RadioId] = radio;
                }
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                  !cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        return radios.Values
            .OrderBy(radio => radio.Advertisement.RadioId, StringComparer.Ordinal)
            .ToArray();
    }
}
