namespace AetherSDR.Web.Radio;

public sealed class StationTxCommandTransportSettings
{
    public const string SectionName = "StationTxCommandTransport";

    public bool Enabled { get; set; }
    public string[] AllowedRadioIds { get; set; } = [];
    public int CommandTimeoutMilliseconds { get; set; } = 2000;
}

public sealed record StationTxCommandTransportRegistrationDiagnostics(
    bool Registered,
    bool ConfiguredEnabled,
    int AllowedRadioCount,
    int CommandTimeoutMilliseconds,
    string Reason);

public sealed record StationTxProductionCommandTransportDiagnostics(
    bool Registered,
    bool ConfiguredEnabled,
    bool LocalFlexEligible,
    bool RadioAllowed,
    bool CommandChannelAttached,
    bool ClientHandleAvailable,
    bool Available,
    bool SetTransmitAvailable,
    int CommandTimeoutMilliseconds,
    long AttemptCount,
    long ForwardedCount,
    long KeyAttemptCount,
    long UnkeyAttemptCount,
    long AcceptedCount,
    long RejectedCount,
    long UnknownCount,
    string LastOperation,
    string LastOutcome,
    string LastReason,
    DateTimeOffset? LastObservedAt);

internal sealed record StationTxCommandTransportConfiguration(
    bool Enabled,
    IReadOnlySet<string> AllowedRadioIds,
    TimeSpan CommandTimeout)
{
    public bool IsRadioAllowed(string radioId) =>
        AllowedRadioIds.Contains(radioId.Trim().ToUpperInvariant());
}

internal static class StationTxCommandTransportSettingsValidator
{
    internal const int MaximumAllowedRadioCount = 16;
    internal const int MaximumRadioIdLength = 128;
    internal const int MinimumCommandTimeoutMilliseconds = 250;
    internal const int MaximumCommandTimeoutMilliseconds = 5000;

    public static StationTxCommandTransportConfiguration Validate(
        StationTxCommandTransportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.CommandTimeoutMilliseconds is
            < MinimumCommandTimeoutMilliseconds or
            > MaximumCommandTimeoutMilliseconds)
        {
            throw new InvalidOperationException(
                $"{StationTxCommandTransportSettings.SectionName}:" +
                $"CommandTimeoutMilliseconds must be between " +
                $"{MinimumCommandTimeoutMilliseconds} and " +
                $"{MaximumCommandTimeoutMilliseconds}.");
        }

        string[] configured = settings.AllowedRadioIds ?? [];
        if (configured.Length > MaximumAllowedRadioCount)
        {
            throw new InvalidOperationException(
                $"{StationTxCommandTransportSettings.SectionName}:" +
                $"AllowedRadioIds supports at most " +
                $"{MaximumAllowedRadioCount} entries.");
        }

        HashSet<string> normalized = new(StringComparer.Ordinal);
        foreach (string? value in configured)
        {
            string radioId = NormalizeRadioId(value);
            if (!normalized.Add(radioId))
            {
                throw new InvalidOperationException(
                    $"{StationTxCommandTransportSettings.SectionName}:" +
                    "AllowedRadioIds contains a duplicate radio ID.");
            }
        }

        if (settings.Enabled && normalized.Count == 0)
        {
            throw new InvalidOperationException(
                $"{StationTxCommandTransportSettings.SectionName}:" +
                "AllowedRadioIds must contain at least one exact radio ID " +
                "when the production command transport is enabled.");
        }

        return new StationTxCommandTransportConfiguration(
            settings.Enabled,
            normalized,
            TimeSpan.FromMilliseconds(settings.CommandTimeoutMilliseconds));
    }

    public static StationTxCommandTransportRegistrationDiagnostics
        CreateDiagnostics(StationTxCommandTransportSettings settings)
    {
        StationTxCommandTransportConfiguration configuration = Validate(settings);
        return new StationTxCommandTransportRegistrationDiagnostics(
            Registered: true,
            ConfiguredEnabled: configuration.Enabled,
            AllowedRadioCount: configuration.AllowedRadioIds.Count,
            CommandTimeoutMilliseconds:
                checked((int)configuration.CommandTimeout.TotalMilliseconds),
            Reason: configuration.Enabled
                ? "configured-awaiting-session"
                : "transport-disabled");
    }

    private static string NormalizeRadioId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{StationTxCommandTransportSettings.SectionName}:" +
                "AllowedRadioIds contains an empty radio ID.");
        }

        string normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length > MaximumRadioIdLength ||
            normalized.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"{StationTxCommandTransportSettings.SectionName}:" +
                "AllowedRadioIds contains an invalid radio ID.");
        }

        return normalized;
    }
}

internal interface IStationTxProductionCommandTransport :
    IStationTxCommandTransport
{
    StationTxProductionCommandTransportDiagnostics Snapshot { get; }
}

/// <summary>
/// Reviewed production FLEX key/unkey transport. The transport is compiled into
/// the normal server artifact but defaults disabled, requires an exact radio
/// allowlist, accepts an exact expected FLEX client handle on every operation,
/// and has no retry or browser-facing entry point. The station command gate is
/// still constructed transmit-disabled in Phase 2T, so production cannot invoke
/// this transport even when its configuration is partially prepared.
/// </summary>
internal sealed class StationTxProductionCommandTransport :
    IStationTxProductionCommandTransport
{
    internal static readonly string KeyCommand = "xmit 1";
    internal static readonly string UnkeyCommand = "xmit 0";
    private const int MaximumResultMessageLength = 256;

    private readonly object m_gate = new();
    private readonly StationTxCommandTransportConfiguration m_configuration;
    private readonly bool m_localFlexEligible;
    private readonly bool m_radioAllowed;
    private readonly IStationTxFlexCommandChannel m_channel;
    private readonly TimeProvider m_timeProvider;
    private long m_attemptCount;
    private long m_forwardedCount;
    private long m_keyAttemptCount;
    private long m_unkeyAttemptCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private long m_unknownCount;
    private string m_lastOperation = "none";
    private string m_lastOutcome = "none";
    private string m_lastReason = "transport-disabled";
    private DateTimeOffset? m_lastObservedAt;

    public StationTxProductionCommandTransport(
        StationTxCommandTransportSettings settings,
        string radioId,
        bool localFlexEligible,
        IStationTxFlexCommandChannel channel,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(radioId);
        ArgumentNullException.ThrowIfNull(channel);

        m_configuration =
            StationTxCommandTransportSettingsValidator.Validate(settings);
        m_localFlexEligible = localFlexEligible;
        m_radioAllowed = m_configuration.IsRadioAllowed(radioId);
        m_channel = channel;
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_lastReason = GetAvailability().Reason;
    }

    public bool IsConnected => GetAvailability().Available;

    public uint ClientHandle
    {
        get
        {
            TransportAvailability availability = GetAvailability();
            return availability.Available ? availability.ClientHandle : 0;
        }
    }

    public StationTxProductionCommandTransportDiagnostics Snapshot
    {
        get
        {
            TransportAvailability availability = GetAvailability();
            lock (m_gate)
            {
                return new StationTxProductionCommandTransportDiagnostics(
                    Registered: true,
                    m_configuration.Enabled,
                    m_localFlexEligible,
                    m_radioAllowed,
                    availability.ChannelAttached,
                    ClientHandleAvailable: availability.ClientHandle != 0,
                    availability.Available,
                    SetTransmitAvailable: availability.Available,
                    CommandTimeoutMilliseconds: checked(
                        (int)m_configuration.CommandTimeout.TotalMilliseconds),
                    m_attemptCount,
                    m_forwardedCount,
                    m_keyAttemptCount,
                    m_unkeyAttemptCount,
                    m_acceptedCount,
                    m_rejectedCount,
                    m_unknownCount,
                    m_lastOperation,
                    m_lastOutcome,
                    m_lastReason,
                    m_lastObservedAt);
            }
        }
    }

    public async Task<StationTxTransportResult> SetTransmitAsync(
        bool enabled,
        uint expectedClientHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string operation = enabled ? "key" : "unkey";
        BeginAttempt(operation, enabled);

        if (expectedClientHandle == 0)
        {
            return Record(
                operation,
                StationTxTransportOutcome.Rejected,
                "expected-client-handle-required",
                "An exact non-zero FLEX client handle is required.");
        }

        TransportAvailability availability = GetAvailability();
        if (!availability.Available)
        {
            return Record(
                operation,
                StationTxTransportOutcome.Rejected,
                availability.Reason,
                "The production station TX command transport is unavailable.");
        }
        if (availability.ClientHandle != expectedClientHandle)
        {
            return Record(
                operation,
                StationTxTransportOutcome.Rejected,
                "client-handle-mismatch",
                "The exact authorized FLEX client handle is no longer connected.");
        }

        lock (m_gate)
        {
            m_forwardedCount++;
        }

        try
        {
            FlexCommandResponse response = await m_channel.SendForClientAsync(
                expectedClientHandle,
                enabled ? KeyCommand : UnkeyCommand,
                m_configuration.CommandTimeout,
                cancellationToken);
            if (response.IsSuccess)
            {
                return Record(
                    operation,
                    StationTxTransportOutcome.Accepted,
                    "accepted",
                    string.Empty);
            }

            return Record(
                operation,
                StationTxTransportOutcome.Rejected,
                "radio-rejected",
                BoundMessage(
                    $"FLEX returned 0x{response.Code:x8}: {response.Body}"));
        }
        catch (InvalidOperationException exception)
        {
            return Record(
                operation,
                StationTxTransportOutcome.Rejected,
                "command-channel-rejected",
                BoundMessage(exception.Message));
        }
        catch (IOException exception)
        {
            return Record(
                operation,
                StationTxTransportOutcome.Unknown,
                "command-outcome-unknown",
                BoundMessage(exception.Message));
        }
        catch (TimeoutException exception)
        {
            return Record(
                operation,
                StationTxTransportOutcome.Unknown,
                "command-outcome-unknown",
                BoundMessage(exception.Message));
        }
    }

    private void BeginAttempt(string operation, bool enabled)
    {
        lock (m_gate)
        {
            m_attemptCount++;
            if (enabled)
            {
                m_keyAttemptCount++;
            }
            else
            {
                m_unkeyAttemptCount++;
            }
            m_lastOperation = operation;
            m_lastObservedAt = m_timeProvider.GetUtcNow();
        }
    }

    private StationTxTransportResult Record(
        string operation,
        StationTxTransportOutcome outcome,
        string reason,
        string message)
    {
        lock (m_gate)
        {
            switch (outcome)
            {
                case StationTxTransportOutcome.Accepted:
                    m_acceptedCount++;
                    break;
                case StationTxTransportOutcome.Rejected:
                    m_rejectedCount++;
                    break;
                case StationTxTransportOutcome.Unknown:
                    m_unknownCount++;
                    break;
                default:
                    throw new InvalidOperationException(
                        "An unsupported production command transport outcome was returned.");
            }
            m_lastOperation = operation;
            m_lastOutcome = outcome.ToString().ToLowerInvariant();
            m_lastReason = reason;
            m_lastObservedAt = m_timeProvider.GetUtcNow();
        }

        return new StationTxTransportResult(outcome, message);
    }

    private TransportAvailability GetAvailability()
    {
        bool attached;
        uint clientHandle;
        try
        {
            attached = m_channel.IsAttached;
            clientHandle = m_channel.ClientHandle;
        }
        catch
        {
            return new(false, false, 0, "command-channel-faulted");
        }

        if (!m_configuration.Enabled)
        {
            return new(false, attached, clientHandle, "transport-disabled");
        }
        if (!m_localFlexEligible)
        {
            return new(false, attached, clientHandle, "local-flex-ineligible");
        }
        if (!m_radioAllowed)
        {
            return new(false, attached, clientHandle, "radio-not-allowed");
        }
        if (!attached)
        {
            return new(false, false, clientHandle, "command-channel-unattached");
        }
        if (clientHandle == 0)
        {
            return new(false, true, 0, "client-handle-unavailable");
        }

        return new(true, true, clientHandle, "ready");
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

    private sealed record TransportAvailability(
        bool Available,
        bool ChannelAttached,
        uint ClientHandle,
        string Reason);
}
