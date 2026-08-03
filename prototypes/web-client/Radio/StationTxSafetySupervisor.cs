namespace AetherSDR.Web.Radio;

internal enum StationTxSafetyState
{
    Disarmed,
    Armed,
    UnkeyPending,
    Faulted
}

internal sealed record StationTxSafetyArm(
    string EngineInstanceId,
    string LeaseId,
    string SessionId,
    string BrowserClientId,
    uint ProtectedClientHandle,
    TimeSpan HeartbeatTimeout);

internal sealed record StationTxSafetySnapshot(
    string RadioId,
    StationTxSafetyState State,
    string Reason,
    string? EngineInstanceId,
    string? LeaseId,
    string? SessionId,
    string? BrowserClientId,
    uint ProtectedClientHandle,
    DateTimeOffset? ArmedAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? HeartbeatDeadlineAt,
    DateTimeOffset? UnkeyDeadlineAt,
    int UnkeyAttempts,
    bool SawProtectedTransmit)
{
    public bool Active =>
        State is StationTxSafetyState.Armed or
            StationTxSafetyState.UnkeyPending;
}

internal sealed record StationTxSafetyResult(
    bool Success,
    string Code,
    string Message,
    StationTxSafetySnapshot Snapshot);

/// <summary>
/// Unkey-only station transport intended for a process that is independent of
/// the engine which can request transmit. The implementation must not expose a
/// key command.
/// </summary>
internal interface IStationTxEmergencyUnkeyTransport
{
    bool IsConnected { get; }

    Task<StationTxTransportResult> RequestUnkeyAsync(
        uint expectedProtectedClientHandle,
        CancellationToken cancellationToken);
}

/// <summary>
/// Private station-local safety supervisor for an independently monitored TX
/// heartbeat. It can never key a radio. It may issue a global unkey only while
/// fresh radio state proves that the single TX occupant is the exact FLEX
/// client handle named by the active arm record.
///
/// Production registers this state machine only inside the fail-closed
/// per-session lifecycle. Phase 2U may attach a disabled-by-default, exact-handle
/// emergency-unkey transport, but the supervisor remains Disarmed and has no
/// browser endpoint or arm caller. The independent watchdog also remains a
/// separate Disarmed process boundary.
/// </summary>
internal sealed class StationTxSafetySupervisor : IAsyncDisposable
{
    internal static readonly TimeSpan MinimumHeartbeatTimeout =
        TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaximumHeartbeatTimeout =
        TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan UnkeyConfirmationTimeout =
        TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan TransportRetryInterval =
        TimeSpan.FromMilliseconds(250);
    internal const int MaximumUnkeyAttempts = 3;

    private readonly SemaphoreSlim m_gate = new(1, 1);
    private readonly string m_radioId;
    private readonly RadioTxOccupancyRegistry m_occupancy;
    private readonly IStationTxEmergencyUnkeyTransport m_transport;
    private readonly TimeProvider m_timeProvider;

    private StationTxSafetyState m_state = StationTxSafetyState.Disarmed;
    private string m_reason = "disarmed";
    private ActiveArm? m_arm;
    private int m_disposed;

    public StationTxSafetySupervisor(
        string radioId,
        RadioTxOccupancyRegistry occupancy,
        IStationTxEmergencyUnkeyTransport transport,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(occupancy);
        ArgumentNullException.ThrowIfNull(transport);
        m_radioId = NormalizeIdentifier(radioId, 128).ToUpperInvariant();
        ArgumentException.ThrowIfNullOrWhiteSpace(m_radioId);
        m_occupancy = occupancy;
        m_transport = transport;
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public StationTxSafetySnapshot Snapshot
    {
        get
        {
            ActiveArm? arm = m_arm;
            return new StationTxSafetySnapshot(
                m_radioId,
                m_state,
                m_reason,
                arm?.EngineInstanceId,
                arm?.LeaseId,
                arm?.SessionId,
                arm?.BrowserClientId,
                arm?.ProtectedClientHandle ?? 0,
                arm?.ArmedAt,
                arm?.LastHeartbeatAt,
                arm?.HeartbeatDeadlineAt,
                arm?.UnkeyDeadlineAt,
                arm?.UnkeyAttempts ?? 0,
                arm?.SawProtectedTransmit ?? false);
        }
    }

    public async Task<StationTxSafetyResult> ArmAsync(
        StationTxSafetyArm request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            string? validationError = ValidateArm(request);
            if (validationError is not null)
            {
                return Denied("invalid_arm", validationError);
            }
            if (m_state != StationTxSafetyState.Disarmed || m_arm is not null)
            {
                return Denied(
                    "already_armed",
                    "The station safety supervisor already has an active or faulted arm record.");
            }

            DateTimeOffset now = m_timeProvider.GetUtcNow();
            RadioTxOccupancySnapshot occupancy =
                m_occupancy.GetSnapshot(m_radioId);
            if (!IsFreshIdle(occupancy, now))
            {
                return Denied(
                    "idle_interlock_required",
                    "A fresh idle radio interlock is required before arming the station safety supervisor.");
            }
            if (!HasExactLocalPttOwner(
                    occupancy,
                    request.ProtectedClientHandle))
            {
                return Denied(
                    "local_ptt_owner_mismatch",
                    "Local PTT authority does not belong exclusively to the protected FLEX client handle.");
            }

            m_arm = new ActiveArm(
                NormalizeIdentifier(request.EngineInstanceId, 128),
                NormalizeIdentifier(request.LeaseId, 64),
                NormalizeIdentifier(request.SessionId, 128),
                NormalizeIdentifier(request.BrowserClientId, 128),
                request.ProtectedClientHandle,
                request.HeartbeatTimeout,
                now,
                now,
                now + request.HeartbeatTimeout,
                null,
                0,
                false);
            m_state = StationTxSafetyState.Armed;
            m_reason = "armed";
            return Succeeded(
                "armed",
                "The station safety supervisor is armed for the exact engine and FLEX client handle.");
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<StationTxSafetyResult> HeartbeatAsync(
        string engineInstanceId,
        string leaseId,
        uint protectedClientHandle,
        TimeSpan heartbeatTimeout,
        CancellationToken cancellationToken = default)
    {
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (m_state != StationTxSafetyState.Armed || m_arm is null)
            {
                return Denied(
                    "not_armed",
                    "No armed station TX safety record accepts heartbeats.");
            }
            if (!ValidHeartbeatTimeout(heartbeatTimeout))
            {
                return Denied(
                    "invalid_heartbeat_timeout",
                    "The heartbeat timeout is outside the bounded safety range.");
            }

            ActiveArm arm = m_arm;
            if (!string.Equals(
                    arm.EngineInstanceId,
                    NormalizeIdentifier(engineInstanceId, 128),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    arm.LeaseId,
                    NormalizeIdentifier(leaseId, 64),
                    StringComparison.Ordinal) ||
                protectedClientHandle == 0 ||
                protectedClientHandle != arm.ProtectedClientHandle)
            {
                return Denied(
                    "heartbeat_owner_mismatch",
                    "The heartbeat does not match the exact armed engine, lease, and FLEX client handle.");
            }

            DateTimeOffset now = m_timeProvider.GetUtcNow();
            if (now >= arm.HeartbeatDeadlineAt)
            {
                return await EvaluateLockedAsync(
                    "late-heartbeat",
                    cancellationToken);
            }

            m_arm = arm with
            {
                HeartbeatTimeout = heartbeatTimeout,
                LastHeartbeatAt = now,
                HeartbeatDeadlineAt = now + heartbeatTimeout
            };
            m_reason = "heartbeat";
            return Succeeded(
                "heartbeat",
                "The station TX safety heartbeat was renewed.");
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<StationTxSafetyResult> AbortAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            string normalizedReason = NormalizeIdentifier(reason, 64);
            if (normalizedReason.Length == 0)
            {
                return Denied(
                    "invalid_abort_reason",
                    "A bounded abort reason is required.");
            }
            return await BeginOwnershipSafeUnkeyLockedAsync(
                $"abort-{normalizedReason}",
                cancellationToken);
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<StationTxSafetyResult> EvaluateAsync(
        string reason = "watchdog",
        CancellationToken cancellationToken = default)
    {
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await EvaluateLockedAsync(
                NormalizeIdentifier(reason, 64) is { Length: > 0 } normalized
                    ? normalized
                    : "watchdog",
                cancellationToken);
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<StationTxSafetyResult> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            RadioTxOccupancySnapshot occupancy =
                m_occupancy.GetSnapshot(m_radioId);
            if (!IsFreshIdle(occupancy, now))
            {
                return Denied(
                    "idle_required_for_reset",
                    "The safety supervisor can reset only after a fresh idle interlock is confirmed.");
            }
            ClearArm("reset-idle");
            return Succeeded(
                "reset",
                "The station TX safety supervisor returned to disarmed state.");
        }
        finally
        {
            m_gate.Release();
        }
    }

    private async Task<StationTxSafetyResult> EvaluateLockedAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (m_arm is null || m_state == StationTxSafetyState.Disarmed)
        {
            return Succeeded("disarmed", "No station TX safety arm is active.");
        }

        ActiveArm arm = m_arm;
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        RadioTxOccupancySnapshot occupancy =
            m_occupancy.GetSnapshot(m_radioId);

        if (m_state == StationTxSafetyState.Faulted)
        {
            if (IsFreshIdle(occupancy, now))
            {
                ClearArm("radio-idle-after-fault");
                return Succeeded(
                    "disarmed",
                    "The radio returned to idle after the safety fault.");
            }
            return Failed(
                "safety_fault",
                "The station TX safety supervisor remains faulted until fresh idle is confirmed.");
        }

        if (IsFreshIdle(occupancy, now))
        {
            if (m_state == StationTxSafetyState.UnkeyPending ||
                arm.SawProtectedTransmit)
            {
                ClearArm("radio-confirmed-idle");
                return Succeeded(
                    "unkeyed",
                    "The radio confirmed idle and the safety arm was cleared.");
            }
            if (now >= arm.HeartbeatDeadlineAt)
            {
                ClearArm("heartbeat-expired-before-tx");
                return Succeeded(
                    "disarmed",
                    "The heartbeat expired while the radio remained idle; no unkey command was needed.");
            }
            m_reason = reason;
            return Succeeded(
                "armed_idle",
                "The supervisor remains armed while the radio is idle.");
        }

        if (!IsFresh(occupancy, now))
        {
            return Fault(
                "tx_occupancy_stale",
                "Radio TX occupancy is stale or unavailable; no global unkey command was sent.");
        }

        bool exactProtectedOwner =
            IsExactProtectedTxOwner(
                occupancy,
                arm.ProtectedClientHandle);
        if (!exactProtectedOwner)
        {
            return Fault(
                occupancy.State == RadioTxOccupancyState.External
                    ? "external_tx_owner"
                    : "tx_ownership_unknown",
                "The active TX owner is not the exact protected FLEX client handle; no global unkey command was sent.");
        }

        if (!arm.SawProtectedTransmit)
        {
            arm = arm with { SawProtectedTransmit = true };
            m_arm = arm;
        }

        if (m_state == StationTxSafetyState.Armed)
        {
            if (now >= arm.HeartbeatDeadlineAt)
            {
                return await SendUnkeyLockedAsync(
                    arm,
                    "heartbeat-expired",
                    cancellationToken);
            }
            m_reason = reason;
            return Succeeded(
                "protected_tx",
                "The protected FLEX client retains TX ownership and its heartbeat is current.");
        }

        if (m_state == StationTxSafetyState.UnkeyPending)
        {
            if (arm.UnkeyDeadlineAt is null ||
                now < arm.UnkeyDeadlineAt)
            {
                m_reason = reason;
                return Succeeded(
                    "unkey_pending",
                    "Waiting for radio-confirmed idle after the emergency unkey request.");
            }
            if (arm.UnkeyAttempts >= MaximumUnkeyAttempts)
            {
                return Fault(
                    "unkey_confirmation_timeout",
                    "The protected FLEX client remained keyed after the bounded emergency unkey attempts.");
            }
            return await SendUnkeyLockedAsync(
                arm,
                "unkey-retry",
                cancellationToken);
        }

        return Fault(
            "invalid_safety_state",
            "The station TX safety supervisor entered an invalid state.");
    }

    private async Task<StationTxSafetyResult>
        BeginOwnershipSafeUnkeyLockedAsync(
            string reason,
            CancellationToken cancellationToken)
    {
        if (m_arm is null || m_state == StationTxSafetyState.Disarmed)
        {
            return Succeeded(
                "disarmed",
                "No station TX safety arm is active.");
        }

        ActiveArm arm = m_arm;
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        RadioTxOccupancySnapshot occupancy =
            m_occupancy.GetSnapshot(m_radioId);
        if (IsFreshIdle(occupancy, now))
        {
            ClearArm(reason);
            return Succeeded(
                "unkeyed",
                "The radio was already idle; no unkey command was needed.");
        }
        if (!IsFresh(occupancy, now))
        {
            return Fault(
                "tx_occupancy_stale",
                "Radio TX occupancy is stale or unavailable; no global unkey command was sent.");
        }
        if (!IsExactProtectedTxOwner(
                occupancy,
                arm.ProtectedClientHandle))
        {
            return Fault(
                occupancy.State == RadioTxOccupancyState.External
                    ? "external_tx_owner"
                    : "tx_ownership_unknown",
                "The active TX owner is not the exact protected FLEX client handle; no global unkey command was sent.");
        }
        if (!arm.SawProtectedTransmit)
        {
            arm = arm with { SawProtectedTransmit = true };
            m_arm = arm;
        }
        return await SendUnkeyLockedAsync(
            arm,
            reason,
            cancellationToken);
    }

    private async Task<StationTxSafetyResult> SendUnkeyLockedAsync(
        ActiveArm arm,
        string reason,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        m_state = StationTxSafetyState.UnkeyPending;
        if (!m_transport.IsConnected)
        {
            m_arm = arm with
            {
                UnkeyDeadlineAt = now + TransportRetryInterval
            };
            m_reason = "emergency-transport-unavailable";
            return Failed(
                "emergency_transport_unavailable",
                "The emergency unkey transport is unavailable; the supervisor will retry while exact ownership remains proven.");
        }

        StationTxTransportResult command =
            await m_transport.RequestUnkeyAsync(
                arm.ProtectedClientHandle,
                cancellationToken);
        DateTimeOffset completedAt = m_timeProvider.GetUtcNow();
        int attempts = checked(arm.UnkeyAttempts + 1);
        m_arm = arm with
        {
            UnkeyAttempts = attempts,
            UnkeyDeadlineAt = completedAt + UnkeyConfirmationTimeout
        };
        m_reason = command.Success
            ? reason
            : command.OutcomeKnown
                ? "emergency-unkey-rejected"
                : "emergency-unkey-outcome-unknown";
        if (!command.Success)
        {
            return Failed(
                command.OutcomeKnown
                    ? "emergency_unkey_rejected"
                    : "emergency_unkey_outcome_unknown",
                command.Message.Length > 0
                    ? command.Message
                    : "The emergency unkey command was not confirmed; the supervisor will reconcile and retry while exact ownership remains proven.");
        }
        return Succeeded(
            "unkey_pending",
            "An ownership-safe emergency unkey command was sent; waiting for radio-confirmed idle.");
    }

    private StationTxSafetyResult Fault(string code, string message)
    {
        m_state = StationTxSafetyState.Faulted;
        m_reason = code.Replace('_', '-');
        return Failed(code, message);
    }

    private void ClearArm(string reason)
    {
        m_arm = null;
        m_state = StationTxSafetyState.Disarmed;
        m_reason = reason;
    }

    private static bool IsFresh(
        RadioTxOccupancySnapshot occupancy,
        DateTimeOffset now) =>
        occupancy.ObservedAt is not null &&
        occupancy.FreshUntil is not null &&
        occupancy.FreshUntil > now &&
        occupancy.State != RadioTxOccupancyState.Unknown;

    private static bool IsFreshIdle(
        RadioTxOccupancySnapshot occupancy,
        DateTimeOffset now) =>
        IsFresh(occupancy, now) &&
        occupancy.State == RadioTxOccupancyState.Idle;

    private static bool HasExactLocalPttOwner(
        RadioTxOccupancySnapshot occupancy,
        uint protectedClientHandle) =>
        protectedClientHandle != 0 &&
        occupancy.LocalPttOwners.Count == 1 &&
        occupancy.LocalPttOwners[0].ClientHandle == protectedClientHandle;

    private static bool IsExactProtectedTxOwner(
        RadioTxOccupancySnapshot occupancy,
        uint protectedClientHandle) =>
        protectedClientHandle != 0 &&
        occupancy.State != RadioTxOccupancyState.Idle &&
        occupancy.Occupants.Count == 1 &&
        occupancy.Occupants[0].ClientHandle == protectedClientHandle;

    private static string? ValidateArm(StationTxSafetyArm request)
    {
        if (NormalizeIdentifier(request.EngineInstanceId, 128).Length == 0 ||
            NormalizeIdentifier(request.LeaseId, 64).Length == 0 ||
            NormalizeIdentifier(request.SessionId, 128).Length == 0 ||
            NormalizeIdentifier(request.BrowserClientId, 128).Length == 0 ||
            request.ProtectedClientHandle == 0 ||
            !ValidHeartbeatTimeout(request.HeartbeatTimeout))
        {
            return "The safety arm requires bounded engine, lease, session, browser, handle, and heartbeat values.";
        }
        return null;
    }

    private static bool ValidHeartbeatTimeout(TimeSpan value) =>
        value >= MinimumHeartbeatTimeout &&
        value <= MaximumHeartbeatTimeout;

    private static string NormalizeIdentifier(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 &&
               normalized.Length <= maximumLength &&
               normalized.All(character => !char.IsControl(character))
            ? normalized
            : string.Empty;
    }

    private StationTxSafetyResult Succeeded(string code, string message) =>
        new(true, code, message, Snapshot);

    private StationTxSafetyResult Denied(string code, string message) =>
        new(false, code, message, Snapshot);

    private StationTxSafetyResult Failed(string code, string message) =>
        new(false, code, message, Snapshot);

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

    private sealed record ActiveArm(
        string EngineInstanceId,
        string LeaseId,
        string SessionId,
        string BrowserClientId,
        uint ProtectedClientHandle,
        TimeSpan HeartbeatTimeout,
        DateTimeOffset ArmedAt,
        DateTimeOffset LastHeartbeatAt,
        DateTimeOffset HeartbeatDeadlineAt,
        DateTimeOffset? UnkeyDeadlineAt,
        int UnkeyAttempts,
        bool SawProtectedTransmit);
}

/// <summary>
/// Private polling loop for the unkey-only safety supervisor. It is not a
/// hosted service and is not registered in production.
/// </summary>
internal sealed class StationTxSafetyWatchdog(
    StationTxSafetySupervisor supervisor,
    ILogger<StationTxSafetyWatchdog> logger,
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
            StationTxSafetyResult result = await supervisor.EvaluateAsync(
                "safety-watchdog",
                cancellationToken);
            if (!result.Success)
            {
                logger.LogCritical(
                    "Station TX safety failure {Code}: {Message}; state={State} radio={RadioId} protectedHandle=0x{ProtectedClientHandle:x8} attempts={Attempts}",
                    result.Code,
                    result.Message,
                    result.Snapshot.State,
                    result.Snapshot.RadioId,
                    result.Snapshot.ProtectedClientHandle,
                    result.Snapshot.UnkeyAttempts);
            }
        }
    }
}
