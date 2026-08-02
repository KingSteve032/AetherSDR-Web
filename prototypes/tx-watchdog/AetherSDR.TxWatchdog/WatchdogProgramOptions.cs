using System.Globalization;
using System.Net;

namespace AetherSDR.TxWatchdog;

internal sealed record WatchdogProgramOptions(
    WatchdogUnkeyTransportConfiguration UnkeyTransport)
{
    private const int EnabledArgumentCount = 10;

    public static bool TryParse(
        IReadOnlyList<string> args,
        out WatchdogProgramOptions? options,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        error = string.Empty;

        if (args.Count == 1 &&
            string.Equals(args[0], "--stdio", StringComparison.Ordinal))
        {
            options = new WatchdogProgramOptions(
                WatchdogUnkeyTransportConfiguration.Disabled);
            return true;
        }

        if (args.Count != EnabledArgumentCount ||
            !string.Equals(args[0], "--stdio", StringComparison.Ordinal) ||
            !string.Equals(args[1], "--unkey-enabled", StringComparison.Ordinal) ||
            !string.Equals(args[2], "--radio-id", StringComparison.Ordinal) ||
            !string.Equals(args[4], "--radio-host", StringComparison.Ordinal) ||
            !string.Equals(args[6], "--radio-port", StringComparison.Ordinal) ||
            !string.Equals(
                args[8],
                "--command-timeout-ms",
                StringComparison.Ordinal))
        {
            error = "invalid-arguments";
            return false;
        }

        string radioId = args[3].Trim().ToUpperInvariant();
        if (radioId.Length is 0 or > 128 || radioId.Any(char.IsControl))
        {
            error = "invalid-radio-id";
            return false;
        }
        if (!IPAddress.TryParse(args[5], out IPAddress? address) ||
            address.AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetwork)
        {
            error = "invalid-radio-host";
            return false;
        }
        if (!int.TryParse(
                args[7],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int port) ||
            port is < 1 or > 65535)
        {
            error = "invalid-radio-port";
            return false;
        }
        if (!int.TryParse(
                args[9],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int timeoutMilliseconds) ||
            timeoutMilliseconds is < 250 or > 5000)
        {
            error = "invalid-command-timeout";
            return false;
        }

        try
        {
            WatchdogUnkeyTransportConfiguration configuration = new(
                Enabled: true,
                radioId,
                address,
                port,
                TimeSpan.FromMilliseconds(timeoutMilliseconds));
            _ = new FlexWatchdogUnkeyTransport(configuration);
            options = new WatchdogProgramOptions(configuration);
            return true;
        }
        catch (InvalidOperationException)
        {
            error = "invalid-unkey-transport-configuration";
            return false;
        }
    }

    public static string Usage =>
        "Usage: AetherSDR.TxWatchdog --stdio [--unkey-enabled " +
        "--radio-id <id> --radio-host <IPv4> --radio-port <port> " +
        "--command-timeout-ms <250-5000>]";
}
