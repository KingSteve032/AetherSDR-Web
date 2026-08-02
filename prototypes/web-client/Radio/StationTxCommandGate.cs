namespace AetherSDR.Web.Radio;

internal enum StationTxGateState
{
    Disabled,
    Idle,
    KeyPending,
    Keyed,
    UnkeyPending,
    Faulted
}

internal sealed record StationTxGateSnapshot(
    string RadioId,
    StationTxGateState State,
    string Reason,
    string? LeaseId,
    string? SessionId,
    string? BrowserClientId,
    uint ClientHandle,
    DateTimeOffset? IntentCreatedAt,
    DateTimeOffset? DeadlineAt,
    int UnkeyAttempts)
{
    public bool HasActiveIntent =>
        State is StationTxGateState.KeyPending or
            StationTxGateState.Keyed or
            StationTxGateState.UnkeyPending;
}

internal sealed record StationTxGateResult(
    bool Success,
    string Code,
    string Message,
    StationTxGateSnapshot Snapshot);

internal enum StationTxTransportOutcome
{
    Accepted,
    Rejected,
    Unknown
}

internal sealed record StationTxTransportResult(
    StationTxTransportOutcome Outcome,
    string Message)
{
    public bool Success => Outcome == StationTxTransportOutcome.Accepted;
    public bool OutcomeKnown => Outcome != StationTxTransportOutcome.Unknown;

    public static readonly StationTxTransportResult Ok =
        new(StationTxTransportOutcome.Accepted, string.Empty);

    public static StationTxTransportResult Rejected(string message) =>
        new(StationTxTransportOutcome.Rejected, message);

    public static StationTxTransportResult Unknown(string message) =>
        new(StationTxTransportOutcome.Unknown, message);
}

internal interface IStationTxCommandTransport
{
    bool IsConnected { get; }
    uint ClientHandle { get; }

    Task<StationTxTransportResult> SetTransmitAsync(
        bool enabled,
        uint expectedClientHandle,
        CancellationToken cancellationToken);
}

internal sealed record StationTxCommandGateCapabilities(
    bool Registered,
    bool TransmitEnabled,
    bool CommandTransportAvailable,
    bool SetTransmitAvailable,
    string Reason);

/// <summary>
/// Station-local, browser-inaccessible TX command gate. Production registers
/// this state machine only through the fail-closed lifecycle boundary with
/// transmit disabled. Reviewed command primitives may be present behind disabled
/// configuration, but no production caller can reach a keying command.
/// </summary>
internal sealed class StationTxCommandGate : IAsyncDisposable
{
    internal static readonly TimeSpan KeyConfirmationTimeout =
        TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan UnkeyConfirmationTimeout =
        TimeSpan.FromSeconds(2);
    internal const int MaximumUnkeyAttempts = 3;

    private readonly SemaphoreSlim m_gate = new(1, 1);
    private readonly bool m_allowTransmit;
    private readonly string m_radioId;
    private readonly TxLeaseManager m_leases;
    private readonly RadioTxOccupancyRegistry m_occupancy;
    private readonly IStationTxCommandTransport m_transport;
    private readonly TimeProvider m_timeProvider;

    private StationTxGateState m_state;
    private string m_reason;
    private ActiveIntent? m_intent;
    private int m_disposed;

    public StationTxCommandGate(
        bool allowTransmit,
        string radioId,
        TxLeaseManager leases,
        RadioTxOccupancyRegistry occupancy,
        IStationTxCommandTransport transport,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(occupancy);
        ArgumentNullException.ThrowIfNull(transport);
        m_radioId = NormalizeRadioId(radioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(m_radioId);
        m_allowTransmit = allowTransmit;
        m_leases = leases;
        m_occupancy = occupancy;
        m_transport = transport;
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_state = allowTransmit
            ? StationTxGateState.Idle
            : StationTxGateState.Disabled;
        m_reason = allowTransmit
            ? "idle"
            : "transmit-disabled";
    }

    public StationTxGateSnapshot Snapshot
    {
        get
        {
            ActiveIntent? intent = m_intent;
            return new StationTxGateSnapshot(
                m_radioId,
                m_state,
                m_reason,
                intent?.LeaseId,
                intent?.SessionId,
                intent?.BrowserClientId,
                intent?.ClientHandle ?? 0,
                intent?.CreatedAt,
                intent?.DeadlineAt,
                intent?.UnkeyAttempts ?? 0);
        }
    }

    internal StationTxCommandGateCapabilities Capabilities
    {
        get
        {
            bool commandTransportAvailable;
            string reason;
            try
            {
                commandTransportAvailable =
                    m_transport.IsConnected && m_transport.ClientHandle != 0;
                reason = !m_allowTransmit
                    ? "transmit-disabled"
                    : commandTransportAvailable
                        ? "ready"
                        : "command-transport-unavailable";
            }
            catch
            {
                commandTransportAvailable = false;
                reason = "command-transport-capabilities-faulted";
            }

            bool setTransmitAvailable =
                m_allowTransmit && commandTransportAvailable;
            return new StationTxCommandGateCapabilities(
                Registered: true,
                TransmitEnabled: m_allowTransmit,
                commandTransportAvailable,
                setTransmitAvailable,
                reason);
        }
    }

    public async Task<StationTxGateResult> RequestKeyAsync(
        string leaseId,
        string sessionId,
        string browserClientId,
        CancellationToken cancellationToken = default)
    {
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!m_allowTransmit)
            {
                return Denied(
                    "transmit_disabled",
                    "Station transmit is disabled.");
            }
            if (m_intent is not null)
            {
                return Denied(
                    "tx_busy",
                    "This physical radio already has an active TX intent.");
            }
            if (!m_transport.IsConnected || m_transport.ClientHandle == 0)
            {
                return Denied(
                    "radio_disconnected",
                    "The station-local FLEX control session is not connected.");
            }
            if (!m_leases.TryValidate(
                    m_radioId,
                    leaseId,
                    sessionId,
                    browserClientId,
                    out TxLease? lease,
                    out string? leaseError))
            {
                return Denied(
                    "lease_required",
                    leaseError ?? "A current TX lease is required.");
            }
            RadioTxOccupancySnapshot occupancy =
                m_occupancy.GetSnapshot(m_radioId);
            if (!occupancy.BrowserLeaseAllowed)
            {
                return DeniedForOccupancy(occupancy);
            }
            StationTxGateResult? pttAuthority =
                ValidateLocalPttAuthority(
                    occupancy,
                    m_transport.ClientHandle);
            if (pttAuthority is not null)
            {
                return pttAuthority;
            }

            DateTimeOffset now = m_timeProvider.GetUtcNow();
            uint clientHandle = m_transport.ClientHandle;
            ActiveIntent pending = new(
                lease!.LeaseId,
                sessionId,
                browserClientId,
                clientHandle,
                now,
                now + KeyConfirmationTimeout,
                0);
            m_intent = pending;
            m_state = StationTxGateState.KeyPending;
            m_reason = "key-command-pending";

            StationTxTransportResult command =
                await m_transport.SetTransmitAsync(
                    enabled: true,
                    expectedClientHandle: clientHandle,
                    cancellationToken);
            if (!command.Success)
            {
                if (!command.OutcomeKnown)
                {
                    m_reason = "key-command-outcome-unknown";
                    return Failed(
                        "key_command_outcome_unknown",
                        command.Message.Length > 0
                            ? command.Message
                            : "The key command outcome is unknown; the watchdog will reconcile radio state.");
                }
                ClearIntent(
                    StationTxGateState.Faulted,
                    "key-command-rejected");
                return Failed(
                    "key_command_rejected",
                    command.Message.Length > 0
                        ? command.Message
                        : "The radio rejected the key command.");
            }

            // A lease can expire while the radio command is in flight. Never
            // leave a pending intent behind without immediately reconciling it.
            return await EvaluateLockedAsync(
                "key-command-sent",
                cancellationToken);
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<StationTxGateResult> RequestUnkeyAsync(
        string leaseId,
        string sessionId,
        string browserClientId,
        CancellationToken cancellationToken = default)
    {
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (m_intent is null ||
                !MatchesOwner(
                    m_intent,
                    leaseId,
                    sessionId,
                    browserClientId))
            {
                return Denied(
                    "tx_owner_required",
                    "Only the exact active TX intent owner may request unkey.");
            }
            return await BeginOwnershipSafeUnkeyLockedAsync(
                "operator-unkey",
                cancellationToken);
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<StationTxGateResult> EvaluateAsync(
        string reason = "watchdog",
        CancellationToken cancellationToken = default)
    {
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await EvaluateLockedAsync(reason, cancellationToken);
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<StationTxGateResult> HandleLeaseChangeAsync(
        TxLeaseChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (m_intent is null ||
                !string.Equals(
                    change.Lease.RadioId,
                    m_radioId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    change.Lease.LeaseId,
                    m_intent.LeaseId,
                    StringComparison.Ordinal))
            {
                return Succeeded("ignored", "The lease change is unrelated.");
            }
            if (change.Active)
            {
                return await EvaluateLockedAsync(
                    $"lease-{change.Reason}",
                    cancellationToken);
            }
            return await BeginOwnershipSafeUnkeyLockedAsync(
                $"lease-{change.Reason}",
                cancellationToken);
        }
        finally
        {
            m_gate.Release();
        }
    }

    private async Task<StationTxGateResult> EvaluateLockedAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (m_intent is null)
        {
            return Succeeded("idle", "No TX intent is active.");
        }

        ActiveIntent intent = m_intent;
        if (!m_transport.IsConnected ||
            m_transport.ClientHandle == 0 ||
            m_transport.ClientHandle != intent.ClientHandle)
        {
            ClearIntent(
                StationTxGateState.Faulted,
                "flex-client-lost");
            return Failed(
                "flex_client_lost",
                "The exact FLEX GUI client that created the TX intent is no longer connected.");
        }

        bool leaseValid = m_leases.TryValidate(
            m_radioId,
            intent.LeaseId,
            intent.SessionId,
            intent.BrowserClientId,
            out _,
            out _);
        if (!leaseValid)
        {
            return await BeginOwnershipSafeUnkeyLockedAsync(
                "lease-lost",
                cancellationToken);
        }

        RadioTxOccupancySnapshot occupancy =
            m_occupancy.GetSnapshot(m_radioId);
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        switch (m_state)
        {
            case StationTxGateState.KeyPending:
                if (IsExactAetherOwner(occupancy, intent.ClientHandle))
                {
                    m_state = StationTxGateState.Keyed;
                    m_reason = "radio-confirmed-keyed";
                    m_intent = intent with { DeadlineAt = null };
                    return Succeeded(
                        "keyed",
                        "The radio confirmed AetherSDR-owned transmit.");
                }
                if (occupancy.State == RadioTxOccupancyState.External)
                {
                    ClearIntent(
                        StationTxGateState.Faulted,
                        "external-owner-after-key-request");
                    return Failed(
                        "external_tx_owner",
                        "An external SmartSDR, Maestro, or hardware source owns transmit; no unkey command was sent.");
                }
                if (occupancy.State is RadioTxOccupancyState.Ambiguous or
                    RadioTxOccupancyState.Unknown)
                {
                    ClearIntent(
                        StationTxGateState.Faulted,
                        "ownership-not-proven");
                    return Failed(
                        "tx_ownership_unknown",
                        "Transmit ownership could not be proven; no unkey command was sent.");
                }
                if (intent.DeadlineAt is not null && now >= intent.DeadlineAt)
                {
                    ClearIntent(
                        StationTxGateState.Faulted,
                        "key-confirmation-timeout");
                    return Failed(
                        "key_confirmation_timeout",
                        "The radio did not confirm transmit before the deadline.");
                }
                m_reason = reason;
                return Succeeded(
                    "key_pending",
                    "Waiting for radio interlock confirmation.");

            case StationTxGateState.Keyed:
                if (IsExactAetherOwner(occupancy, intent.ClientHandle))
                {
                    m_reason = reason;
                    return Succeeded(
                        "keyed",
                        "AetherSDR retains proven transmit ownership.");
                }
                if (occupancy.State == RadioTxOccupancyState.Idle)
                {
                    ClearIntent(
                        StationTxGateState.Idle,
                        "radio-returned-idle");
                    return Succeeded(
                        "unkeyed",
                        "The radio is no longer transmitting.");
                }
                ClearIntent(
                    StationTxGateState.Faulted,
                    occupancy.State == RadioTxOccupancyState.External
                        ? "external-owner-replaced-aether"
                        : "keyed-ownership-lost");
                return Failed(
                    occupancy.State == RadioTxOccupancyState.External
                        ? "external_tx_owner"
                        : "tx_ownership_unknown",
                    "AetherSDR no longer has proven TX ownership; no unkey command was sent.");

            case StationTxGateState.UnkeyPending:
                if (occupancy.State == RadioTxOccupancyState.Idle)
                {
                    ClearIntent(
                        StationTxGateState.Idle,
                        "radio-confirmed-unkeyed");
                    return Succeeded(
                        "unkeyed",
                        "The radio confirmed receive/idle state.");
                }
                if (!IsExactAetherOwner(occupancy, intent.ClientHandle))
                {
                    ClearIntent(
                        StationTxGateState.Faulted,
                        "unkey-ownership-lost");
                    return Failed(
                        occupancy.State == RadioTxOccupancyState.External
                            ? "external_tx_owner"
                            : "tx_ownership_unknown",
                        "TX ownership is no longer proven; no additional unkey command was sent.");
                }
                if (intent.DeadlineAt is not null && now >= intent.DeadlineAt)
                {
                    if (intent.UnkeyAttempts >= MaximumUnkeyAttempts)
                    {
                        ClearIntent(
                            StationTxGateState.Faulted,
                            "unkey-confirmation-timeout");
                        return Failed(
                            "unkey_confirmation_timeout",
                            "The radio remained keyed after the bounded unkey attempts.");
                    }
                    return await SendUnkeyLockedAsync(
                        intent,
                        "unkey-retry",
                        cancellationToken);
                }
                m_reason = reason;
                return Succeeded(
                    "unkey_pending",
                    "Waiting for the radio to confirm receive/idle state.");

            default:
                return Failed(
                    "invalid_gate_state",
                    "The TX gate entered an invalid state.");
        }
    }

    private async Task<StationTxGateResult> BeginOwnershipSafeUnkeyLockedAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (m_intent is null)
        {
            return Succeeded("idle", "No TX intent is active.");
        }

        ActiveIntent intent = m_intent;
        RadioTxOccupancySnapshot occupancy =
            m_occupancy.GetSnapshot(m_radioId);
        if (occupancy.State == RadioTxOccupancyState.Idle)
        {
            ClearIntent(StationTxGateState.Idle, reason);
            return Succeeded(
                "unkeyed",
                "The radio is already in receive/idle state.");
        }
        if (!IsExactAetherOwner(occupancy, intent.ClientHandle))
        {
            ClearIntent(
                StationTxGateState.Faulted,
                occupancy.State == RadioTxOccupancyState.External
                    ? "external-owner-protected"
                    : "ownership-not-proven-for-unkey");
            return Failed(
                occupancy.State == RadioTxOccupancyState.External
                    ? "external_tx_owner"
                    : "tx_ownership_unknown",
                "AetherSDR ownership is not proven; the gate refused to send a global unkey command.");
        }
        return await SendUnkeyLockedAsync(
            intent,
            reason,
            cancellationToken);
    }

    private async Task<StationTxGateResult> SendUnkeyLockedAsync(
        ActiveIntent intent,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!m_transport.IsConnected ||
            m_transport.ClientHandle != intent.ClientHandle)
        {
            ClearIntent(
                StationTxGateState.Faulted,
                "flex-client-lost-before-unkey");
            return Failed(
                "flex_client_lost",
                "The exact FLEX client is unavailable, so no unkey command was sent.");
        }

        StationTxTransportResult command =
            await m_transport.SetTransmitAsync(
                enabled: false,
                expectedClientHandle: intent.ClientHandle,
                cancellationToken);
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        int attempts = checked(intent.UnkeyAttempts + 1);
        m_intent = intent with
        {
            DeadlineAt = now + UnkeyConfirmationTimeout,
            UnkeyAttempts = attempts
        };
        m_state = StationTxGateState.UnkeyPending;
        if (!command.Success)
        {
            m_reason = command.OutcomeKnown
                ? "unkey-command-rejected"
                : "unkey-command-outcome-unknown";
            return Failed(
                command.OutcomeKnown
                    ? "unkey_command_rejected"
                    : "unkey_command_outcome_unknown",
                command.Message.Length > 0
                    ? command.Message
                    : command.OutcomeKnown
                        ? "The radio rejected the unkey command; the watchdog will retry while ownership remains proven."
                        : "The unkey command outcome is unknown; the watchdog will reconcile and retry safely.");
        }

        m_reason = reason;
        return Succeeded(
            "unkey_pending",
            "An ownership-safe unkey command was sent; waiting for radio confirmation.");
    }

    private StationTxGateResult? ValidateLocalPttAuthority(
        RadioTxOccupancySnapshot occupancy,
        uint clientHandle)
    {
        if (occupancy.LocalPttOwners.Count == 0)
        {
            return Denied(
                "local_ptt_unassigned",
                "No FLEX GUI client currently owns Local PTT authority.");
        }
        if (occupancy.LocalPttOwners.Count != 1)
        {
            return Denied(
                "local_ptt_ambiguous",
                "FLEX Local PTT authority is ambiguous.");
        }

        RadioTxOccupant owner = occupancy.LocalPttOwners[0];
        if (!occupancy.HasExclusiveLocalPttAuthority(clientHandle))
        {
            return Denied(
                "external_local_ptt_owner",
                $"Local PTT authority belongs to " +
                $"{(owner.Station.Length > 0 ? owner.Station : owner.Program)}.");
        }
        return null;
    }

    private StationTxGateResult DeniedForOccupancy(
        RadioTxOccupancySnapshot occupancy) =>
        occupancy.State switch
        {
            RadioTxOccupancyState.External => Denied(
                "external_tx_owner",
                "An external SmartSDR, Maestro, or hardware source is transmitting."),
            RadioTxOccupancyState.AetherOwned => Denied(
                "aether_tx_owner",
                "Another AetherSDR session is transmitting."),
            RadioTxOccupancyState.Ambiguous => Denied(
                "tx_ownership_unknown",
                "Radio TX ownership is ambiguous."),
            _ => Denied(
                "tx_occupancy_stale",
                "Radio TX occupancy is unknown or stale.")
        };

    private static bool IsExactAetherOwner(
        RadioTxOccupancySnapshot occupancy,
        uint clientHandle) =>
        occupancy.HasExclusiveAetherTransmitOwnership(clientHandle);

    private static bool MatchesOwner(
        ActiveIntent intent,
        string leaseId,
        string sessionId,
        string browserClientId) =>
        string.Equals(intent.LeaseId, leaseId, StringComparison.Ordinal) &&
        string.Equals(intent.SessionId, sessionId, StringComparison.Ordinal) &&
        string.Equals(
            intent.BrowserClientId,
            browserClientId,
            StringComparison.Ordinal);

    private void ClearIntent(StationTxGateState state, string reason)
    {
        m_intent = null;
        m_state = state;
        m_reason = reason;
    }

    private StationTxGateResult Succeeded(string code, string message) =>
        new(true, code, message, Snapshot);

    private StationTxGateResult Denied(string code, string message) =>
        new(false, code, message, Snapshot);

    private StationTxGateResult Failed(string code, string message) =>
        new(false, code, message, Snapshot);

    private static string NormalizeRadioId(string? radioId)
    {
        string normalized = radioId?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 128 &&
               normalized.All(character => !char.IsControl(character))
            ? normalized.ToUpperInvariant()
            : string.Empty;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref m_disposed) != 0,
            this);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) == 0)
        {
            m_gate.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private sealed record ActiveIntent(
        string LeaseId,
        string SessionId,
        string BrowserClientId,
        uint ClientHandle,
        DateTimeOffset CreatedAt,
        DateTimeOffset? DeadlineAt,
        int UnkeyAttempts);
}

/// <summary>
/// Private station-local watchdog for the hidden gate. It is intentionally not
/// registered as a hosted service until hardware-in-the-loop unkey testing is
/// complete.
/// </summary>
internal sealed class StationTxCommandWatchdog(
    StationTxCommandGate gate,
    ILogger<StationTxCommandWatchdog> logger,
    TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(100);
    private readonly TimeProvider m_timeProvider =
        timeProvider ?? TimeProvider.System;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(PollInterval, m_timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            StationTxGateResult result = await gate.EvaluateAsync(
                "station-watchdog",
                cancellationToken);
            if (!result.Success)
            {
                logger.LogError(
                    "Station TX watchdog failure {Code}: {Message}; state={State} radio={RadioId} handle=0x{ClientHandle:x8}",
                    result.Code,
                    result.Message,
                    result.Snapshot.State,
                    result.Snapshot.RadioId,
                    result.Snapshot.ClientHandle);
            }
        }
    }
}

#if AETHERSDR_TX_HIL
/// <summary>
/// Real FLEX command adapter for an explicit hardware-in-the-loop build. Normal
/// production publishes do not define AETHERSDR_TX_HIL and therefore exclude
/// this HIL-specific adapter; Phase 2T's separate production-primary adapter is
/// independently disabled and allowlisted.
/// </summary>
internal sealed class FlexStationTxCommandTransport(
    FlexRadioCommandRouter router,
    Func<uint> clientHandleProvider)
    : IStationTxCommandTransport
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    public bool IsConnected => router.IsAttached;
    public uint ClientHandle => clientHandleProvider();

    public async Task<StationTxTransportResult> SetTransmitAsync(
        bool enabled,
        uint expectedClientHandle,
        CancellationToken cancellationToken)
    {
        try
        {
            FlexCommandResponse response = await router.SendForClientAsync(
                expectedClientHandle,
                enabled ? "xmit 1" : "xmit 0",
                CommandTimeout,
                cancellationToken);
            return response.IsSuccess
                ? StationTxTransportResult.Ok
                : StationTxTransportResult.Rejected(
                    $"FLEX returned 0x{response.Code:x8}: {response.Body}".Trim());
        }
        catch (InvalidOperationException exception)
        {
            return StationTxTransportResult.Rejected(exception.Message);
        }
        catch (IOException exception)
        {
            return StationTxTransportResult.Unknown(exception.Message);
        }
        catch (TimeoutException exception)
        {
            return StationTxTransportResult.Unknown(exception.Message);
        }
    }
}
#endif
