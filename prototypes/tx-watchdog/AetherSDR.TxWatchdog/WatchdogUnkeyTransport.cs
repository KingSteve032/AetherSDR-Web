using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

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

internal sealed record WatchdogFlexUnkeyResponse(
    uint Code,
    string Body,
    bool CommandSent = true,
    bool AlreadyIdle = false)
{
    public bool IsSuccess => Code == 0;
}

internal interface IWatchdogFlexUnkeyChannel
{
    Task<WatchdogFlexUnkeyResponse> SendUnkeyAsync(
        WatchdogUnkeyTransportConfiguration configuration,
        uint expectedProtectedClientHandle,
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
                    expectedProtectedClientHandle,
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
/// Minimal FLEX TCP observer with one purpose-bound operation. It uses only two
/// fixed status subscriptions and one fixed unkey command. Before sending the
/// command it requires fresh radio interlock state naming the exact protected
/// handle as TX owner and a fresh client-roster observation showing that handle
/// still connected. Idle or mismatched ownership sends no command.
/// </summary>
internal sealed class TcpWatchdogFlexUnkeyChannel : IWatchdogFlexUnkeyChannel
{
    private const int MaximumLineCharacters = 4096;
    private const int MaximumResponseLines = 256;
    private const string ClientSubscription = "sub client all";
    private const string TxSubscription = "sub tx all";

    private static readonly Regex ClientStatus = new(
        @"^client\s+(?<id>(?:0x)?[0-9a-fA-F]+)" +
        @"(?:\s+(?<action>connected|disconnected))?" +
        @"(?:\s+.*)?$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex InterlockField = new(
        @"(?<key>[A-Za-z0-9_]+)=(?<value>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<WatchdogFlexUnkeyResponse> SendUnkeyAsync(
        WatchdogUnkeyTransportConfiguration configuration,
        uint expectedProtectedClientHandle,
        CancellationToken cancellationToken)
    {
        if (expectedProtectedClientHandle == 0)
        {
            throw new InvalidOperationException(
                "An exact non-zero protected FLEX handle is required.");
        }

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
            $"C1|{ClientSubscription}".AsMemory(),
            timeout.Token);
        await writer.WriteLineAsync(
            $"C2|{TxSubscription}".AsMemory(),
            timeout.Token);
        await writer.FlushAsync(timeout.Token);

        OwnershipObservation ownership = await WaitForOwnershipAsync(
            reader,
            expectedProtectedClientHandle,
            timeout.Token);
        if (ownership.AlreadyIdle)
        {
            return new WatchdogFlexUnkeyResponse(
                Code: 0,
                Body: "radio-already-idle",
                CommandSent: false,
                AlreadyIdle: true);
        }

        await writer.WriteLineAsync(
            $"C3|{FlexWatchdogUnkeyTransport.UnkeyCommand}".AsMemory(),
            timeout.Token);
        await writer.FlushAsync(timeout.Token);
        try
        {
            WatchdogFlexUnkeyResponse response =
                await WaitForUnkeyConfirmationAsync(
                    reader,
                    expectedSequence: 3,
                    expectedProtectedClientHandle,
                    timeout.Token);
            return response with { CommandSent = true };
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException(
                "The FLEX unkey outcome could not be confirmed after dispatch.",
                exception);
        }
    }

    private static async Task WaitForSessionHandleAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < MaximumResponseLines; index++)
        {
            string line = await ReadBoundedLineAsync(reader, cancellationToken);
            if (line.StartsWith('H') &&
                TryParseFlexUInt(line.AsSpan(1), out uint handle) &&
                handle != 0)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "The FLEX radio did not provide a valid control-session handle.");
    }

    private static async Task<OwnershipObservation> WaitForOwnershipAsync(
        StreamReader reader,
        uint expectedProtectedClientHandle,
        CancellationToken cancellationToken)
    {
        bool clientSubscriptionAccepted = false;
        bool txSubscriptionAccepted = false;
        bool protectedClientObserved = false;
        string interlockState = string.Empty;
        uint? txClientHandle = null;

        for (int index = 0; index < MaximumResponseLines; index++)
        {
            string line = await ReadBoundedLineAsync(reader, cancellationToken);
            if (TryParseResponseLine(
                    line,
                    out uint responseSequence,
                    out uint responseCode,
                    out string responseBody))
            {
                if (responseSequence is not (1 or 2))
                {
                    throw new InvalidOperationException(
                        "The FLEX radio returned an unexpected ownership-observer response.");
                }
                if (responseCode != 0)
                {
                    throw new InvalidOperationException(
                        $"The FLEX ownership subscription was rejected with 0x{responseCode:x8}: {responseBody}");
                }
                clientSubscriptionAccepted |= responseSequence == 1;
                txSubscriptionAccepted |= responseSequence == 2;
            }
            else if (TryParseClientStatus(
                         line,
                         out uint clientHandle,
                         out string action))
            {
                if (clientHandle == expectedProtectedClientHandle)
                {
                    protectedClientObserved = !string.Equals(
                        action,
                        "disconnected",
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            else if (TryParseInterlockStatus(
                         line,
                         ref interlockState,
                         ref txClientHandle))
            {
            }

            if (!clientSubscriptionAccepted ||
                !txSubscriptionAccepted ||
                interlockState.Length == 0)
            {
                continue;
            }
            if (IsIdleState(interlockState))
            {
                return new OwnershipObservation(AlreadyIdle: true);
            }
            if (txClientHandle.HasValue &&
                txClientHandle.Value != expectedProtectedClientHandle)
            {
                throw new InvalidOperationException(
                    "Fresh FLEX interlock state names a different TX owner; no unkey command was sent.");
            }
            if (protectedClientObserved &&
                txClientHandle == expectedProtectedClientHandle)
            {
                return new OwnershipObservation(AlreadyIdle: false);
            }
        }

        throw new InvalidOperationException(
            "Fresh exact FLEX TX ownership could not be proven; no unkey command was sent.");
    }

    private static async Task<WatchdogFlexUnkeyResponse>
        WaitForUnkeyConfirmationAsync(
            StreamReader reader,
            uint expectedSequence,
            uint expectedProtectedClientHandle,
            CancellationToken cancellationToken)
    {
        bool responseReceived = false;
        bool idleObserved = false;
        uint responseCode = 0;
        string responseBody = string.Empty;
        string interlockState = string.Empty;
        uint? txClientHandle = null;

        for (int index = 0; index < MaximumResponseLines; index++)
        {
            string line = await ReadBoundedLineAsync(reader, cancellationToken);
            if (TryParseResponseLine(
                    line,
                    out uint sequence,
                    out uint code,
                    out string body))
            {
                if (sequence != expectedSequence || responseReceived)
                {
                    throw new InvalidOperationException(
                        "The FLEX radio returned an unexpected unkey response sequence.");
                }
                responseReceived = true;
                responseCode = code;
                responseBody = body;
                if (responseCode != 0)
                {
                    return new WatchdogFlexUnkeyResponse(
                        responseCode,
                        responseBody);
                }
            }
            else if (TryParseInterlockStatus(
                         line,
                         ref interlockState,
                         ref txClientHandle))
            {
                if (IsIdleState(interlockState))
                {
                    idleObserved = true;
                }
                else if (txClientHandle.HasValue &&
                    txClientHandle.Value != expectedProtectedClientHandle)
                {
                    throw new InvalidOperationException(
                        "Fresh FLEX interlock state named a different TX owner after the unkey command was sent.");
                }
            }

            if (responseReceived && responseCode == 0 && idleObserved)
            {
                return new WatchdogFlexUnkeyResponse(
                    responseCode,
                    responseBody);
            }
        }

        throw new IOException(
            "The FLEX radio did not confirm idle after accepting the unkey command.");
    }

    private static bool TryParseResponseLine(
        string line,
        out uint sequence,
        out uint code,
        out string body)
    {
        sequence = 0;
        code = 0;
        body = string.Empty;
        if (!line.StartsWith('R'))
        {
            return false;
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
                out sequence) ||
            !uint.TryParse(
                line.AsSpan(
                    firstSeparator + 1,
                    secondSeparator - firstSeparator - 1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out code))
        {
            throw new InvalidOperationException(
                "The FLEX radio returned an invalid command response.");
        }
        body = line[(secondSeparator + 1)..];
        return true;
    }

    private static bool TryParseClientStatus(
        string line,
        out uint clientHandle,
        out string action)
    {
        clientHandle = 0;
        action = string.Empty;
        int separator = line.IndexOf('|');
        if (!line.StartsWith('S') ||
            separator < 0 || separator == line.Length - 1)
        {
            return false;
        }

        Match match = ClientStatus.Match(line[(separator + 1)..].Trim());
        if (!match.Success ||
            !TryParseFlexUInt(match.Groups["id"].Value.AsSpan(), out clientHandle) ||
            clientHandle == 0)
        {
            return false;
        }
        action = match.Groups["action"].Value;
        return true;
    }

    private static bool TryParseInterlockStatus(
        string line,
        ref string state,
        ref uint? txClientHandle)
    {
        int separator = line.IndexOf('|');
        if (!line.StartsWith('S') ||
            separator < 0 || separator == line.Length - 1)
        {
            return false;
        }
        string body = line[(separator + 1)..].Trim();
        if (!body.StartsWith("interlock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (Match field in InterlockField.Matches(body))
        {
            string key = field.Groups["key"].Value;
            string value = field.Groups["value"].Value.Trim('"');
            if (string.Equals(key, "state", StringComparison.OrdinalIgnoreCase))
            {
                string normalized = value.Trim().ToUpperInvariant();
                if (normalized.Length is 0 or > 64 ||
                    normalized.Any(character =>
                        char.IsControl(character) || char.IsWhiteSpace(character)))
                {
                    return false;
                }
                state = normalized;
            }
            else if (string.Equals(
                         key,
                         "tx_client_handle",
                         StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseFlexUInt(value.AsSpan(), out uint parsed))
                {
                    return false;
                }
                txClientHandle = parsed == 0 ? null : parsed;
            }
        }

        if (IsIdleState(state))
        {
            txClientHandle = null;
        }
        return true;
    }

    private static bool TryParseFlexUInt(
        ReadOnlySpan<char> value,
        out uint parsed)
    {
        ReadOnlySpan<char> text = value.Trim().Trim('"');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        return uint.TryParse(
            text,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out parsed);
    }

    private static bool IsIdleState(string state) =>
        state is "READY" or "RECEIVE";

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

    private sealed record OwnershipObservation(bool AlreadyIdle);
}
