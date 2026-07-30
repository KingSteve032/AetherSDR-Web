using System.Globalization;
using AetherSDR.Web.Radio;

namespace AetherSDR.TxHil;

internal sealed record HilCwxSnapshot(
    int? SentIndex,
    int? Wpm,
    int? BreakInDelayMilliseconds,
    bool? QskEnabled,
    DateTimeOffset? SentObservedAt,
    DateTimeOffset? SentFreshUntil,
    DateTimeOffset? ConfigurationObservedAt,
    DateTimeOffset? ConfigurationFreshUntil)
{
    public bool HasFreshSentIndex(DateTimeOffset now) =>
        SentIndex is not null &&
        SentObservedAt is not null &&
        SentFreshUntil > now;

    public bool HasFreshConfiguration(DateTimeOffset now) =>
        Wpm is not null &&
        BreakInDelayMilliseconds is not null &&
        QskEnabled is not null &&
        ConfigurationObservedAt is not null &&
        ConfigurationFreshUntil > now;

    public bool IsCompleteAndFresh(DateTimeOffset now) =>
        HasFreshSentIndex(now) && HasFreshConfiguration(now);
}

internal sealed record HilCwxRadioSnapshot(
    RadioTxOccupancySnapshot TxOccupancy,
    IReadOnlyList<RadioGuiClientDiagnostics> GuiClients,
    HilCwxSnapshot Cwx)
{
    public IReadOnlyList<RadioGuiClientDiagnostics> ExternalGuiClients =>
        GuiClients.Where(client => !client.IsThisSession).ToArray();
}

internal sealed record HilCwxCommandResult(
    bool Success,
    uint Code,
    string Body)
{
    public static HilCwxCommandResult Ok(string body = "") =>
        new(true, 0, body);

    public static HilCwxCommandResult Rejected(
        uint code,
        string body = "") =>
        new(false, code, body);
}

internal interface IHilCwxRadio
{
    uint ClientHandle { get; }

    HilCwxRadioSnapshot Snapshot();

    Task<HilCwxCommandResult> SendCwxCommandAsync(
        string command,
        CancellationToken cancellationToken);
}

internal sealed record HilCwxConfigurationRoundTripResult(
    int OriginalWpm,
    int OriginalBreakInDelayMilliseconds,
    bool OriginalQskEnabled,
    int TestWpm,
    int TestBreakInDelayMilliseconds,
    bool TestQskEnabled);

internal sealed record HilCwxIdentificationResult(
    string Callsign,
    int Wpm,
    int StartIndex,
    int EndIndex,
    DateTimeOffset StartedAt,
    DateTimeOffset DrainedAt,
    DateTimeOffset IdleAt,
    bool SawExactOwnedTransmit);

internal sealed class HilCwxStatusTracker(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan ObservationLifetime = TimeSpan.FromSeconds(8);

    private readonly object m_gate = new();
    private readonly TimeProvider m_timeProvider =
        timeProvider ?? TimeProvider.System;
    private int? m_sentIndex;
    private int? m_wpm;
    private int? m_breakInDelayMilliseconds;
    private bool? m_qskEnabled;
    private DateTimeOffset? m_sentObservedAt;
    private DateTimeOffset? m_wpmObservedAt;
    private DateTimeOffset? m_delayObservedAt;
    private DateTimeOffset? m_qskObservedAt;

    public bool Observe(IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        bool changed = false;
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        lock (m_gate)
        {
            if (fields.TryGetValue("sent", out string? sentText) &&
                int.TryParse(
                    sentText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int sentIndex) &&
                sentIndex >= 0)
            {
                m_sentIndex = sentIndex;
                m_sentObservedAt = now;
                changed = true;
            }
            if (fields.TryGetValue("wpm", out string? wpmText) &&
                int.TryParse(
                    wpmText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int wpm) &&
                wpm is >= 5 and <= 100)
            {
                m_wpm = wpm;
                m_wpmObservedAt = now;
                changed = true;
            }
            if (fields.TryGetValue(
                    "break_in_delay",
                    out string? delayText) &&
                int.TryParse(
                    delayText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int delay) &&
                delay is >= 0 and <= 2_000)
            {
                m_breakInDelayMilliseconds = delay;
                m_delayObservedAt = now;
                changed = true;
            }
            if (fields.TryGetValue("qsk_enabled", out string? qskText) &&
                TryReadBoolean(qskText, out bool qskEnabled))
            {
                m_qskEnabled = qskEnabled;
                m_qskObservedAt = now;
                changed = true;
            }
        }
        return changed;
    }

    public HilCwxSnapshot Snapshot()
    {
        lock (m_gate)
        {
            DateTimeOffset? configurationOldest =
                m_wpmObservedAt is not null &&
                m_delayObservedAt is not null &&
                m_qskObservedAt is not null
                    ? new[]
                    {
                        m_wpmObservedAt.Value,
                        m_delayObservedAt.Value,
                        m_qskObservedAt.Value
                    }.Min()
                    : null;
            return new HilCwxSnapshot(
                m_sentIndex,
                m_wpm,
                m_breakInDelayMilliseconds,
                m_qskEnabled,
                m_sentObservedAt,
                m_sentObservedAt + ObservationLifetime,
                configurationOldest,
                configurationOldest + ObservationLifetime);
        }
    }

    internal static bool TryReadBoolean(string value, out bool parsed)
    {
        if (value == "1" ||
            bool.TryParse(value, out bool boolValue) && boolValue)
        {
            parsed = true;
            return true;
        }
        if (value == "0" ||
            bool.TryParse(value, out bool falseValue) && !falseValue)
        {
            parsed = false;
            return true;
        }
        parsed = false;
        return false;
    }
}

internal sealed class HilCwxIdentifier
{
    public const string RequiredCallsign = "KC4CAW";
    public const int IdentificationWpm = 20;
    private const int IdentificationBlock = 1;

    private readonly TimeProvider m_timeProvider;
    private readonly TimeSpan m_pollInterval;
    private readonly TimeSpan m_transmitTimeout;
    private readonly TimeSpan m_idleTimeout;

    public HilCwxIdentifier(
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        TimeSpan? transmitTimeout = null,
        TimeSpan? idleTimeout = null)
    {
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(25);
        m_transmitTimeout = transmitTimeout ?? TimeSpan.FromSeconds(15);
        m_idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(5);
        if (m_pollInterval <= TimeSpan.Zero ||
            m_transmitTimeout <= TimeSpan.Zero ||
            m_idleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                "CWX timing values must be positive.");
        }
    }

    public async Task<HilCwxConfigurationRoundTripResult>
        VerifyConfigurationRoundTripAsync(
            IHilCwxRadio radio,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(radio);
        if (radio.ClientHandle == 0)
        {
            throw new InvalidOperationException(
                "The CWX verifier has no FLEX client handle.");
        }
        HilCwxRadioSnapshot initial = radio.Snapshot();
        ValidatePreconditions(initial, radio.ClientHandle);
        int originalWpm = initial.Cwx.Wpm!.Value;
        int originalDelay = initial.Cwx.BreakInDelayMilliseconds!.Value;
        bool originalQsk = initial.Cwx.QskEnabled!.Value;
        try
        {
            await SendRequiredAsync(
                radio,
                "cwx clear",
                cancellationToken);
            await ConfigureIdentificationSettingsAsync(
                radio,
                originalDelay,
                originalQsk,
                cancellationToken);
            await SendRequiredAsync(
                radio,
                "cwx clear",
                cancellationToken);
            await RestoreCwxSettingsAsync(
                radio,
                originalWpm,
                originalDelay,
                originalQsk,
                cancellationToken);
            return new HilCwxConfigurationRoundTripResult(
                originalWpm,
                originalDelay,
                originalQsk,
                IdentificationWpm,
                originalDelay,
                originalQsk);
        }
        catch (Exception exception)
        {
            Exception? cleanupFailure = await AbortAsync(
                radio,
                originalWpm,
                originalDelay,
                originalQsk);
            if (cleanupFailure is not null)
            {
                throw new InvalidOperationException(
                    "CWX configuration verification failed and its bounded cleanup also failed.",
                    new AggregateException(exception, cleanupFailure));
            }
            throw;
        }
    }

    public async Task<HilCwxIdentificationResult> IdentifyAsync(
        IHilCwxRadio radio,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(radio);
        if (radio.ClientHandle == 0)
        {
            throw new InvalidOperationException(
                "The CWX identifier has no FLEX client handle.");
        }

        HilCwxRadioSnapshot initial = radio.Snapshot();
        ValidatePreconditions(initial, radio.ClientHandle);
        int originalWpm = initial.Cwx.Wpm!.Value;
        int originalDelay = initial.Cwx.BreakInDelayMilliseconds!.Value;
        bool originalQsk = initial.Cwx.QskEnabled!.Value;
        bool sawExactOwnedTransmit = false;
        bool drained = false;
        int startIndex = -1;
        int endIndex = -1;
        DateTimeOffset startedAt = m_timeProvider.GetUtcNow();
        DateTimeOffset drainedAt = default;
        DateTimeOffset idleAt = default;

        try
        {
            await SendRequiredAsync(
                radio,
                "cwx clear",
                cancellationToken);
            await ConfigureIdentificationSettingsAsync(
                radio,
                originalDelay,
                originalQsk,
                cancellationToken);

            HilCwxCommandResult send = await radio.SendCwxCommandAsync(
                $"cwx send \"{RequiredCallsign}\" {IdentificationBlock}",
                cancellationToken);
            if (!send.Success ||
                !TryParseSendReply(
                    send.Body,
                    RequiredCallsign.Length,
                    out startIndex,
                    out endIndex))
            {
                throw new InvalidOperationException(
                    $"FLEX rejected or returned an invalid CWX send reply: " +
                    $"0x{send.Code:x8} {send.Body}".Trim());
            }
            startedAt = m_timeProvider.GetUtcNow();

            DateTimeOffset transmitDeadline = startedAt + m_transmitTimeout;
            while (m_timeProvider.GetUtcNow() < transmitDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HilCwxRadioSnapshot snapshot = radio.Snapshot();
                ValidateNoExternalClient(snapshot);
                if (snapshot.TxOccupancy.State is
                    RadioTxOccupancyState.External or
                    RadioTxOccupancyState.Ambiguous or
                    RadioTxOccupancyState.Unknown)
                {
                    throw new InvalidOperationException(
                        "CWX identification lost unambiguous AetherSDR TX ownership.");
                }
                if (IsExactOwnedTransmit(
                        snapshot.TxOccupancy,
                        radio.ClientHandle))
                {
                    sawExactOwnedTransmit = true;
                }
                if (snapshot.Cwx.HasFreshSentIndex(
                        m_timeProvider.GetUtcNow()) &&
                    snapshot.Cwx.SentIndex >= endIndex)
                {
                    drained = true;
                    drainedAt = m_timeProvider.GetUtcNow();
                    break;
                }
                await DelayAsync(cancellationToken);
            }
            if (!drained)
            {
                throw new TimeoutException(
                    $"CWX sent index did not reach {endIndex} within " +
                    $"{m_transmitTimeout.TotalSeconds:F0} seconds.");
            }
            if (!sawExactOwnedTransmit)
            {
                throw new InvalidOperationException(
                    "The radio never confirmed CWX transmit ownership for the exact HIL client handle.");
            }

            await WaitForAsync(
                radio,
                snapshot =>
                    snapshot.TxOccupancy.State ==
                        RadioTxOccupancyState.Idle &&
                    snapshot.TxOccupancy.FreshUntil >
                        m_timeProvider.GetUtcNow(),
                "radio-confirmed idle after CWX identification",
                m_idleTimeout,
                cancellationToken);
            idleAt = m_timeProvider.GetUtcNow();

            await SendRequiredAsync(
                radio,
                "cwx clear",
                cancellationToken);
            await RestoreCwxSettingsAsync(
                radio,
                originalWpm,
                originalDelay,
                originalQsk,
                cancellationToken);

            return new HilCwxIdentificationResult(
                RequiredCallsign,
                IdentificationWpm,
                startIndex,
                endIndex,
                startedAt,
                drainedAt,
                idleAt,
                sawExactOwnedTransmit);
        }
        catch (Exception exception)
        {
            Exception? cleanupFailure = await AbortAsync(
                radio,
                originalWpm,
                originalDelay,
                originalQsk);
            if (cleanupFailure is not null)
            {
                throw new InvalidOperationException(
                    "CWX identification failed and its bounded cleanup also failed; use the remote power kill and verify PSOC2 locally.",
                    new AggregateException(exception, cleanupFailure));
            }
            throw;
        }
    }

    private void ValidatePreconditions(
        HilCwxRadioSnapshot snapshot,
        uint clientHandle)
    {
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        ValidateNoExternalClient(snapshot);
        if (snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            snapshot.TxOccupancy.FreshUntil <= now)
        {
            throw new InvalidOperationException(
                "CWX identification requires a fresh idle interlock.");
        }
        if (!snapshot.TxOccupancy.HasExclusiveLocalPttAuthority(clientHandle))
        {
            throw new InvalidOperationException(
                "CWX identification requires exclusive Local PTT authority for the exact HIL client.");
        }
        if (!snapshot.Cwx.HasFreshConfiguration(now))
        {
            throw new InvalidOperationException(
                "CWX identification requires fresh WPM, break-in delay, and QSK status so settings can be restored exactly.");
        }
    }

    private static void ValidateNoExternalClient(
        HilCwxRadioSnapshot snapshot)
    {
        if (snapshot.ExternalGuiClients.Count != 0)
        {
            throw new InvalidOperationException(
                "CWX identification is blocked while another FLEX GUI client is connected.");
        }
    }

    private async Task<Exception?> AbortAsync(
        IHilCwxRadio radio,
        int originalWpm,
        int originalDelay,
        bool originalQsk)
    {
        using CancellationTokenSource cleanup =
            new(TimeSpan.FromSeconds(5));
        List<Exception> failures = [];
        try
        {
            await SendRequiredAsync(
                radio,
                "cwx clear",
                cleanup.Token);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            HilCwxRadioSnapshot snapshot = radio.Snapshot();
            if (IsExactOwnedTransmit(
                    snapshot.TxOccupancy,
                    radio.ClientHandle))
            {
                await SendRequiredAsync(
                    radio,
                    "xmit 0",
                    cleanup.Token);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await WaitForAsync(
                radio,
                candidate =>
                    candidate.TxOccupancy.State ==
                        RadioTxOccupancyState.Idle &&
                    candidate.TxOccupancy.FreshUntil >
                        m_timeProvider.GetUtcNow(),
                "idle during CWX abort",
                m_idleTimeout,
                cleanup.Token);
            await RestoreCwxSettingsAsync(
                radio,
                originalWpm,
                originalDelay,
                originalQsk,
                cleanup.Token);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures)
        };
    }

    private async Task ConfigureIdentificationSettingsAsync(
        IHilCwxRadio radio,
        int expectedDelay,
        bool expectedQsk,
        CancellationToken cancellationToken)
    {
        await SendRequiredAsync(
            radio,
            $"cwx wpm {IdentificationWpm}",
            cancellationToken);
        await WaitForAsync(
            radio,
            snapshot =>
                snapshot.Cwx.HasFreshConfiguration(
                    m_timeProvider.GetUtcNow()) &&
                snapshot.Cwx.Wpm == IdentificationWpm &&
                snapshot.Cwx.QskEnabled == expectedQsk &&
                snapshot.Cwx.BreakInDelayMilliseconds == expectedDelay,
            "CWX speed confirmation with unchanged QSK and break-in delay",
            TimeSpan.FromSeconds(3),
            cancellationToken);
    }

    private async Task RestoreCwxSettingsAsync(
        IHilCwxRadio radio,
        int wpm,
        int delay,
        bool qsk,
        CancellationToken cancellationToken)
    {
        await SendRequiredAsync(
            radio,
            $"cwx wpm {wpm}",
            cancellationToken);
        await WaitForAsync(
            radio,
            snapshot =>
                snapshot.Cwx.HasFreshConfiguration(
                    m_timeProvider.GetUtcNow()) &&
                snapshot.Cwx.Wpm == wpm &&
                snapshot.Cwx.QskEnabled == qsk &&
                snapshot.Cwx.BreakInDelayMilliseconds == delay,
            "CWX settings restoration",
            TimeSpan.FromSeconds(3),
            cancellationToken);
    }

    private static async Task SendRequiredAsync(
        IHilCwxRadio radio,
        string command,
        CancellationToken cancellationToken)
    {
        HilCwxCommandResult result = await radio.SendCwxCommandAsync(
            command,
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"FLEX rejected '{command}' with 0x{result.Code:x8}: " +
                result.Body);
        }
    }

    private async Task WaitForAsync(
        IHilCwxRadio radio,
        Func<HilCwxRadioSnapshot, bool> predicate,
        string description,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = m_timeProvider.GetUtcNow() + timeout;
        while (m_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HilCwxRadioSnapshot snapshot = radio.Snapshot();
            ValidateNoExternalClient(snapshot);
            if (predicate(snapshot))
            {
                return;
            }
            await DelayAsync(cancellationToken);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        Task.Delay(m_pollInterval, m_timeProvider, cancellationToken);

    internal static bool TryParseSendReply(
        string body,
        int characterCount,
        out int startIndex,
        out int endIndex)
    {
        startIndex = -1;
        endIndex = -1;
        if (characterCount < 1)
        {
            return false;
        }
        string first = body.Split(',', 2)[0].Trim();
        if (!int.TryParse(
                first,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out startIndex) ||
            startIndex < 0 ||
            startIndex > int.MaxValue - characterCount + 1)
        {
            startIndex = -1;
            return false;
        }
        endIndex = startIndex + characterCount - 1;
        return true;
    }

    private static bool IsExactOwnedTransmit(
        RadioTxOccupancySnapshot occupancy,
        uint clientHandle) =>
        occupancy.State == RadioTxOccupancyState.AetherOwned &&
        occupancy.Occupants.Count == 1 &&
        occupancy.Occupants[0].AetherOwned &&
        occupancy.Occupants[0].ClientHandle == clientHandle;
}
