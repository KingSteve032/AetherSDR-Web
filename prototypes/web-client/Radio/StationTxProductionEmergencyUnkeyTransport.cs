namespace AetherSDR.Web.Radio;

public sealed class StationTxEmergencyUnkeyTransportSettings
{
    public const string SectionName = "StationTxEmergencyUnkeyTransport";

    public bool Enabled { get; set; }
    public string[] AllowedRadioIds { get; set; } = [];
    public int CommandTimeoutMilliseconds { get; set; } = 2000;
}

public sealed record StationTxEmergencyUnkeyTransportRegistrationDiagnostics(
    bool Registered,
    bool ConfiguredEnabled,
    int AllowedRadioCount,
    int CommandTimeoutMilliseconds,
    string Reason);

public sealed record StationTxProductionEmergencyUnkeyTransportDiagnostics(
    bool Registered,
    bool ConfiguredEnabled,
    bool LocalFlexEligible,
    bool RadioAllowed,
    bool CommandChannelAttached,
    bool ClientHandleAvailable,
    bool Available,
    bool UnkeyAvailable,
    int CommandTimeoutMilliseconds,
    long AttemptCount,
    long ForwardedCount,
    long AcceptedCount,
    long RejectedCount,
    long UnknownCount,
    string LastOutcome,
    string LastReason,
    DateTimeOffset? LastObservedAt);

internal sealed record StationTxEmergencyUnkeyTransportConfiguration(
    bool Enabled,
    IReadOnlySet<string> AllowedRadioIds,
    TimeSpan CommandTimeout)
{
    public bool IsRadioAllowed(string radioId) =>
        AllowedRadioIds.Contains(radioId.Trim().ToUpperInvariant());
}

internal static class StationTxEmergencyUnkeyTransportSettingsValidator
{
    internal const int MaximumAllowedRadioCount = 16;
    internal const int MaximumRadioIdLength = 128;
    internal const int MinimumCommandTimeoutMilliseconds = 250;
    internal const int MaximumCommandTimeoutMilliseconds = 5000;

    public static StationTxEmergencyUnkeyTransportConfiguration Validate(
        StationTxEmergencyUnkeyTransportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.CommandTimeoutMilliseconds is
            < MinimumCommandTimeoutMilliseconds or
            > MaximumCommandTimeoutMilliseconds)
        {
            throw new InvalidOperationException(
                $"{StationTxEmergencyUnkeyTransportSettings.SectionName}:" +
                $"CommandTimeoutMilliseconds must be between " +
                $"{MinimumCommandTimeoutMilliseconds} and " +
                $"{MaximumCommandTimeoutMilliseconds}.");
        }

        string[] configured = settings.AllowedRadioIds ?? [];
        if (configured.Length > MaximumAllowedRadioCount)
        {
            throw new InvalidOperationException(
                $"{StationTxEmergencyUnkeyTransportSettings.SectionName}:" +
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
                    $"{StationTxEmergencyUnkeyTransportSettings.SectionName}:" +
                    "AllowedRadioIds contains a duplicate radio ID.");
            }
        }

        if (settings.Enabled && normalized.Count == 0)
        {
            throw new InvalidOperationException(
                $"{StationTxEmergencyUnkeyTransportSettings.SectionName}:" +
                "AllowedRadioIds must contain at least one exact radio ID " +
                "when the emergency-unkey transport is enabled.");
        }

        return new StationTxEmergencyUnkeyTransportConfiguration(
            settings.Enabled,
            normalized,
            TimeSpan.FromMilliseconds(settings.CommandTimeoutMilliseconds));
    }

    public static StationTxEmergencyUnkeyTransportRegistrationDiagnostics
        CreateDiagnostics(StationTxEmergencyUnkeyTransportSettings settings)
    {
        StationTxEmergencyUnkeyTransportConfiguration configuration =
            Validate(settings);
        return new StationTxEmergencyUnkeyTransportRegistrationDiagnostics(
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
                $"{StationTxEmergencyUnkeyTransportSettings.SectionName}:" +
                "AllowedRadioIds contains an empty radio ID.");
        }

        string normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length > MaximumRadioIdLength ||
            normalized.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"{StationTxEmergencyUnkeyTransportSettings.SectionName}:" +
                "AllowedRadioIds contains an invalid radio ID.");
        }

        return normalized;
    }
}

internal interface IStationTxProductionEmergencyUnkeyTransport :
    IStationTxEmergencyUnkeyTransport
{
    StationTxProductionEmergencyUnkeyTransportDiagnostics Snapshot { get; }
}

/// <summary>
/// Reviewed production emergency-unkey transport. It has no key method, accepts
/// an exact expected FLEX handle on every operation, sends at most one `xmit 0`,
/// and defaults disabled behind an exact radio allowlist. Phase 2U registers it
/// with the still-Disarmed supervisor but adds no arm or browser caller.
/// </summary>
internal sealed class StationTxProductionEmergencyUnkeyTransport :
    IStationTxProductionEmergencyUnkeyTransport
{
    internal static readonly string UnkeyCommand = "xmit 0";
    private const int MaximumResultMessageLength = 256;

    private readonly object m_gate = new();
    private readonly StationTxEmergencyUnkeyTransportConfiguration
        m_configuration;
    private readonly bool m_localFlexEligible;
    private readonly bool m_radioAllowed;
    private readonly IStationTxFlexCommandChannel m_channel;
    private readonly TimeProvider m_timeProvider;
    private long m_attemptCount;
    private long m_forwardedCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private long m_unknownCount;
    private string m_lastOutcome = "none";
    private string m_lastReason = "transport-disabled";
    private DateTimeOffset? m_lastObservedAt;

    public StationTxProductionEmergencyUnkeyTransport(
        StationTxEmergencyUnkeyTransportSettings settings,
        string radioId,
        bool localFlexEligible,
        IStationTxFlexCommandChannel channel,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(radioId);
        ArgumentNullException.ThrowIfNull(channel);

        m_configuration =
            StationTxEmergencyUnkeyTransportSettingsValidator.Validate(settings);
        m_localFlexEligible = localFlexEligible;
        m_radioAllowed = m_configuration.IsRadioAllowed(radioId);
        m_channel = channel;
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_lastReason = GetAvailability().Reason;
    }

    public bool IsConnected => GetAvailability().Available;

    public StationTxProductionEmergencyUnkeyTransportDiagnostics Snapshot
    {
        get
        {
            TransportAvailability availability = GetAvailability();
            lock (m_gate)
            {
                return new StationTxProductionEmergencyUnkeyTransportDiagnostics(
                    Registered: true,
                    m_configuration.Enabled,
                    m_localFlexEligible,
                    m_radioAllowed,
                    availability.ChannelAttached,
                    ClientHandleAvailable: availability.ClientHandle != 0,
                    availability.Available,
                    UnkeyAvailable: availability.Available,
                    CommandTimeoutMilliseconds: checked(
                        (int)m_configuration.CommandTimeout.TotalMilliseconds),
                    m_attemptCount,
                    m_forwardedCount,
                    m_acceptedCount,
                    m_rejectedCount,
                    m_unknownCount,
                    m_lastOutcome,
                    m_lastReason,
                    m_lastObservedAt);
            }
        }
    }

    public async Task<StationTxTransportResult> RequestUnkeyAsync(
        uint expectedProtectedClientHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginAttempt();

        if (expectedProtectedClientHandle == 0)
        {
            return Record(
                StationTxTransportOutcome.Rejected,
                "expected-client-handle-required",
                "An exact non-zero protected FLEX client handle is required.");
        }

        TransportAvailability availability = GetAvailability();
        if (!availability.Available)
        {
            return Record(
                StationTxTransportOutcome.Rejected,
                availability.Reason,
                "The production emergency-unkey transport is unavailable.");
        }
        if (availability.ClientHandle != expectedProtectedClientHandle)
        {
            return Record(
                StationTxTransportOutcome.Rejected,
                "client-handle-mismatch",
                "The exact protected FLEX client handle is no longer connected.");
        }

        lock (m_gate)
        {
            m_forwardedCount++;
        }

        try
        {
            FlexCommandResponse response = await m_channel.SendForClientAsync(
                expectedProtectedClientHandle,
                UnkeyCommand,
                m_configuration.CommandTimeout,
                cancellationToken);
            if (response.IsSuccess)
            {
                return Record(
                    StationTxTransportOutcome.Accepted,
                    "accepted",
                    string.Empty);
            }

            return Record(
                StationTxTransportOutcome.Rejected,
                "radio-rejected",
                BoundMessage(
                    $"FLEX returned 0x{response.Code:x8}: {response.Body}"));
        }
        catch (InvalidOperationException exception)
        {
            return Record(
                StationTxTransportOutcome.Rejected,
                "command-channel-rejected",
                BoundMessage(exception.Message));
        }
        catch (IOException exception)
        {
            return Record(
                StationTxTransportOutcome.Unknown,
                "command-outcome-unknown",
                BoundMessage(exception.Message));
        }
        catch (TimeoutException exception)
        {
            return Record(
                StationTxTransportOutcome.Unknown,
                "command-outcome-unknown",
                BoundMessage(exception.Message));
        }
    }

    private void BeginAttempt()
    {
        lock (m_gate)
        {
            m_attemptCount++;
            m_lastObservedAt = m_timeProvider.GetUtcNow();
        }
    }

    private StationTxTransportResult Record(
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
                        "An unsupported emergency-unkey transport outcome was returned.");
            }
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
