using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AetherSDR.TxWatchdog;

internal enum WatchdogUnkeyTransportOutcome
{
    Accepted,
    Rejected,
    Unknown
}

internal sealed record WatchdogUnkeyTransportResult(
    WatchdogUnkeyTransportOutcome Outcome,
    string Message)
{
    public bool Success => Outcome == WatchdogUnkeyTransportOutcome.Accepted;
    public bool OutcomeKnown => Outcome != WatchdogUnkeyTransportOutcome.Unknown;

    public static readonly WatchdogUnkeyTransportResult Ok =
        new(WatchdogUnkeyTransportOutcome.Accepted, string.Empty);

    public static WatchdogUnkeyTransportResult Rejected(string message) =>
        new(WatchdogUnkeyTransportOutcome.Rejected, message);

    public static WatchdogUnkeyTransportResult Unknown(string message) =>
        new(WatchdogUnkeyTransportOutcome.Unknown, message);
}

internal sealed record WatchdogUnkeyTransportConfiguration(
    bool Enabled,
    string RadioId,
    IPAddress? Address,
    int Port,
    TimeSpan CommandTimeout)
{
    public static readonly WatchdogUnkeyTransportConfiguration Disabled =
        new(
            Enabled: false,
            RadioId: string.Empty,
            Address: null,
            Port: 0,
            CommandTimeout: TimeSpan.FromSeconds(2));
}

internal sealed record WatchdogUnkeyTransportDiagnostics(
    bool Registered,
    bool ConfiguredEnabled,
    bool Available,
    string RadioId,
    int Port,
    int CommandTimeoutMilliseconds,
    long AttemptCount,
    long ForwardedCount,
    long AcceptedCount,
    long RejectedCount,
    long UnknownCount,
    uint LastProtectedClientHandle,
    string LastOutcome,
    string LastReason,
    DateTimeOffset? LastObservedAt);

internal interface IWatchdogUnkeyTransport
{
    bool IsAvailable { get; }
    WatchdogUnkeyTransportDiagnostics Snapshot { get; }

    Task<WatchdogUnkeyTransportResult> RequestUnkeyAsync(
        uint expectedProtectedClientHandle,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableWatchdogUnkeyTransport :
    IWatchdogUnkeyTransport
{
    public bool IsAvailable => false;

    public WatchdogUnkeyTransportDiagnostics Snapshot { get; } = new(
        Registered: true,
        ConfiguredEnabled: false,
        Available: false,
        RadioId: string.Empty,
        Port: 0,
        CommandTimeoutMilliseconds: 2000,
        AttemptCount: 0,
        ForwardedCount: 0,
        AcceptedCount: 0,
        RejectedCount: 0,
        UnknownCount: 0,
        LastProtectedClientHandle: 0,
        LastOutcome: "none",
        LastReason: "transport-disabled",
        LastObservedAt: null);

    public Task<WatchdogUnkeyTransportResult> RequestUnkeyAsync(
        uint expectedProtectedClientHandle,
        CancellationToken cancellationToken) =>
        Task.FromResult(WatchdogUnkeyTransportResult.Rejected(
            "The independent watchdog unkey transport is disabled."));
}

internal sealed record WatchdogFlexUnkeyResponse(uint Code, string Body)
{
    public bool IsSuccess => Code == 0;
}

internal interface IWatchdogFlexUnkeyChannel
{
    Task<WatchdogFlexUnkeyResponse> SendUnkeyAsync(
        WatchdogUnkeyTransportConfiguration configuration,
        CancellationToken cancellationToken);
}

/// <summary>
/// Purpose-built independent-process unkey adapter. It has no key method and no
/// arbitrary command method. It accepts the protected FLEX handle only as a
/// purpose-bound input for the future ownership observer; Phase 2U adds no host
/// caller, so the transport remains unreachable even when configured.
/// </summary>
internal sealed class FlexWatchdogUnkeyTransport : IWatchdogUnkeyTransport
{
    internal static readonly string UnkeyCommand = "xmit 0";
    private const int MaximumResultMessageLength = 256;

    private readonly object m_gate = new();
    private readonly WatchdogUnkeyTransportConfiguration m_configuration;
    private readonly IWatchdogFlexUnkeyChannel m_channel;
    private readonly TimeProvider m_timeProvider;
    private long m_attemptCount;
    private long m_forwardedCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private long m_unknownCount;
    private uint m_lastProtectedClientHandle;
    private string m_lastOutcome = "none";
    private string m_lastReason;
    private DateTimeOffset? m_lastObservedAt;

    public FlexWatchdogUnkeyTransport(
        WatchdogUnkeyTransportConfiguration configuration,
        IWatchdogFlexUnkeyChannel? channel = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        m_configuration = Validate(configuration);
        m_channel = channel ?? new TcpWatchdogFlexUnkeyChannel();
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_lastReason = m_configuration.Enabled
            ? "ready"
            : "transport-disabled";
    }

    public bool IsAvailable => m_configuration.Enabled;

    public WatchdogUnkeyTransportDiagnostics Snapshot
    {
        get
        {
            lock (m_gate)
            {
                return new WatchdogUnkeyTransportDiagnostics(
                    Registered: true,
                    ConfiguredEnabled: m_configuration.Enabled,
                    Available: IsAvailable,
                    m_configuration.RadioId,
                    m_configuration.Port,
                    CommandTimeoutMilliseconds: checked(
                        (int)m_configuration.CommandTimeout.TotalMilliseconds),
                    m_attemptCount,
                    m_forwardedCount,
                    m_acceptedCount,
                    m_rejectedCount,
                    m_unknownCount,
                    m_lastProtectedClientHandle,
                    m_lastOutcome,
                    m_lastReason,
                    m_lastObservedAt);
            }
        }
    }

    public async Task<WatchdogUnkeyTransportResult> RequestUnkeyAsync(
        uint expectedProtectedClientHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginAttempt(expectedProtectedClientHandle);

        if (!m_configuration.Enabled)
        {
            return Record(
                WatchdogUnkeyTransportOutcome.Rejected,
                "transport-disabled",
                "The independent watchdog unkey transport is disabled.");
        }
        if (expectedProtectedClientHandle == 0)
        {
            return Record(
                WatchdogUnkeyTransportOutcome.Rejected,
                "expected-client-handle-required",
                "An exact non-zero protected FLEX client handle is required.");
        }

        lock (m_gate)
        {
            m_forwardedCount++;
        }

        try
        {
            WatchdogFlexUnkeyResponse response =
                await m_channel.SendUnkeyAsync(
                    m_configuration,
                    cancellationToken);
            return response.IsSuccess
                ? Record(
                    WatchdogUnkeyTransportOutcome.Accepted,
                    "accepted",
                    string.Empty)
                : Record(
                    WatchdogUnkeyTransportOutcome.Rejected,
                    "radio-rejected",
                    BoundMessage(
                        $"FLEX returned 0x{response.Code:x8}: {response.Body}"));
        }
        catch (InvalidOperationException exception)
        {
            return Record(
                WatchdogUnkeyTransportOutcome.Rejected,
                "command-channel-rejected",
                BoundMessage(exception.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Record(
                WatchdogUnkeyTransportOutcome.Unknown,
                "command-outcome-unknown",
                "The independent watchdog unkey command timed out.");
        }
        catch (IOException exception)
        {
            return Record(
                WatchdogUnkeyTransportOutcome.Unknown,
                "command-outcome-unknown",
                BoundMessage(exception.Message));
        }
        catch (SocketException exception)
        {
            return Record(
                WatchdogUnkeyTransportOutcome.Unknown,
                "command-outcome-unknown",
                BoundMessage(exception.Message));
        }
    }

    private void BeginAttempt(uint expectedProtectedClientHandle)
    {
        lock (m_gate)
        {
            m_attemptCount++;
            m_lastProtectedClientHandle = expectedProtectedClientHandle;
            m_lastObservedAt = m_timeProvider.GetUtcNow();
        }
    }

    private WatchdogUnkeyTransportResult Record(
        WatchdogUnkeyTransportOutcome outcome,
        string reason,
        string message)
    {
        lock (m_gate)
        {
            switch (outcome)
            {
                case WatchdogUnkeyTransportOutcome.Accepted:
                    m_acceptedCount++;
                    break;
                case WatchdogUnkeyTransportOutcome.Rejected:
                    m_rejectedCount++;
                    break;
                case WatchdogUnkeyTransportOutcome.Unknown:
                    m_unknownCount++;
                    break;
                default:
                    throw new InvalidOperationException(
                        "An unsupported watchdog unkey outcome was returned.");
            }
            m_lastOutcome = outcome.ToString().ToLowerInvariant();
            m_lastReason = reason;
            m_lastObservedAt = m_timeProvider.GetUtcNow();
        }

        return new WatchdogUnkeyTransportResult(outcome, message);
    }

    private static WatchdogUnkeyTransportConfiguration Validate(
        WatchdogUnkeyTransportConfiguration configuration)
    {
        if (!configuration.Enabled)
        {
            return WatchdogUnkeyTransportConfiguration.Disabled;
        }
        if (string.IsNullOrWhiteSpace(configuration.RadioId) ||
            configuration.RadioId.Length > 128 ||
            configuration.RadioId.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "The watchdog unkey radio ID is invalid.");
        }
        if (configuration.Address is null ||
            configuration.Address.AddressFamily != AddressFamily.InterNetwork ||
            !IsUnicastIpv4(configuration.Address))
        {
            throw new InvalidOperationException(
                "The watchdog unkey radio host must be a unicast IPv4 address.");
        }
        if (configuration.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "The watchdog unkey radio port is invalid.");
        }
        if (configuration.CommandTimeout < TimeSpan.FromMilliseconds(250) ||
            configuration.CommandTimeout > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException(
                "The watchdog unkey command timeout is outside the bounded range.");
        }

        return configuration with
        {
            RadioId = configuration.RadioId.Trim().ToUpperInvariant()
        };
    }

    private static bool IsUnicastIpv4(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return !address.Equals(IPAddress.Any) &&
            !address.Equals(IPAddress.None) &&
            !address.Equals(IPAddress.Broadcast) &&
            bytes[0] is > 0 and < 224;
    }

    private static string BoundMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = new(
            value.Trim()
                .Select(character => char.IsControl(character) ? ' ' : character)
                .ToArray());
        return normalized.Length <= MaximumResultMessageLength
            ? normalized
            : normalized[..MaximumResultMessageLength];
    }
}

/// <summary>
/// Minimal FLEX TCP client with one operation and one encoded command. It does
/// not expose status subscriptions, keying, arbitrary command text, retries, or
/// reconnect behavior.
/// </summary>
internal sealed class TcpWatchdogFlexUnkeyChannel : IWatchdogFlexUnkeyChannel
{
    private const int MaximumLineCharacters = 4096;
    private const int MaximumResponseLines = 128;

    public async Task<WatchdogFlexUnkeyResponse> SendUnkeyAsync(
        WatchdogUnkeyTransportConfiguration configuration,
        CancellationToken cancellationToken)
    {
        IPAddress address = configuration.Address ??
            throw new InvalidOperationException(
                "The watchdog unkey radio address is unavailable.");
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(configuration.CommandTimeout);

        using TcpClient client = new(AddressFamily.InterNetwork)
        {
            NoDelay = true
        };
        await client.ConnectAsync(address, configuration.Port, timeout.Token);
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        await using StreamWriter writer = new(
            stream,
            Encoding.ASCII,
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await WaitForSessionHandleAsync(reader, timeout.Token);
        await writer.WriteLineAsync(
            $"C1|{FlexWatchdogUnkeyTransport.UnkeyCommand}".AsMemory(),
            timeout.Token);
        await writer.FlushAsync(timeout.Token);
        return await WaitForResponseAsync(reader, timeout.Token);
    }

    private static async Task WaitForSessionHandleAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < MaximumResponseLines; index++)
        {
            string line = await ReadBoundedLineAsync(reader, cancellationToken);
            if (line.StartsWith('H') &&
                uint.TryParse(
                    line.AsSpan(1),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out uint handle) &&
                handle != 0)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "The FLEX radio did not provide a valid control-session handle.");
    }

    private static async Task<WatchdogFlexUnkeyResponse> WaitForResponseAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < MaximumResponseLines; index++)
        {
            string line = await ReadBoundedLineAsync(reader, cancellationToken);
            if (!line.StartsWith('R'))
            {
                continue;
            }

            int firstSeparator = line.IndexOf('|');
            int secondSeparator = firstSeparator < 0
                ? -1
                : line.IndexOf('|', firstSeparator + 1);
            if (firstSeparator <= 1 || secondSeparator < 0 ||
                !uint.TryParse(
                    line.AsSpan(1, firstSeparator - 1),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out uint sequence) ||
                sequence != 1 ||
                !uint.TryParse(
                    line.AsSpan(
                        firstSeparator + 1,
                        secondSeparator - firstSeparator - 1),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out uint code))
            {
                throw new InvalidOperationException(
                    "The FLEX radio returned an invalid unkey response.");
            }

            return new WatchdogFlexUnkeyResponse(
                code,
                line[(secondSeparator + 1)..]);
        }

        throw new IOException(
            "The FLEX radio returned no matching unkey response.");
    }

    private static async Task<string> ReadBoundedLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        string? line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            throw new IOException("The FLEX radio closed the TCP connection.");
        }
        if (line.Length > MaximumLineCharacters)
        {
            throw new InvalidOperationException(
                "The FLEX radio returned an oversized control line.");
        }
        return line;
    }
}
