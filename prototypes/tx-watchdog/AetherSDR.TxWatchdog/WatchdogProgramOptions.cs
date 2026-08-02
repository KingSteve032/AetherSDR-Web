using System.Globalization;
using System.Net;

namespace AetherSDR.TxWatchdog;

internal sealed record WatchdogProgramOptions(
    WatchdogUnkeyTransportConfiguration UnkeyTransport,
    bool ArmingEnabled)
{
    private const int EnabledArgumentCount = 10;
    private const int ArmedArgumentCount = 11;

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
                WatchdogUnkeyTransportConfiguration.Disabled,
                ArmingEnabled: false);
            return true;
        }

        bool armingEnabled =
            args.Count == ArmedArgumentCount &&
            string.Equals(args[2], "--arming-enabled", StringComparison.Ordinal);
        int offset = armingEnabled ? 1 : 0;
        if (args.Count is not (EnabledArgumentCount or ArmedArgumentCount) ||
            !string.Equals(args[0], "--stdio", StringComparison.Ordinal) ||
            !string.Equals(args[1], "--unkey-enabled", StringComparison.Ordinal) ||
            (args.Count == ArmedArgumentCount && !armingEnabled) ||
            !string.Equals(
                args[2 + offset],
                "--radio-id",
                StringComparison.Ordinal) ||
            !string.Equals(
                args[4 + offset],
                "--radio-host",
                StringComparison.Ordinal) ||
            !string.Equals(
                args[6 + offset],
                "--radio-port",
                StringComparison.Ordinal) ||
            !string.Equals(
                args[8 + offset],
                "--command-timeout-ms",
                StringComparison.Ordinal))
        {
            error = "invalid-arguments";
            return false;
        }

        string radioId = args[3 + offset].Trim().ToUpperInvariant();
        if (radioId.Length is 0 or > 128 || radioId.Any(char.IsControl))
        {
            error = "invalid-radio-id";
            return false;
        }
        if (!IPAddress.TryParse(args[5 + offset], out IPAddress? address) ||
            address.AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetwork)
        {
            error = "invalid-radio-host";
            return false;
        }
        if (!int.TryParse(
                args[7 + offset],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int port) ||
            port is < 1 or > 65535)
        {
            error = "invalid-radio-port";
            return false;
        }
        if (!int.TryParse(
                args[9 + offset],
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
            options = new WatchdogProgramOptions(
                configuration,
                armingEnabled);
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
        "[--arming-enabled] --radio-id <id> --radio-host <IPv4> " +
        "--radio-port <port> --command-timeout-ms <250-5000>]";
}
