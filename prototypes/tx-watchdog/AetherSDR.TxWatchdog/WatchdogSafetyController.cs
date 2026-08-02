using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.TxWatchdog;

internal sealed record WatchdogSafetyOperationResult(
    bool Success,
    string Code,
    string Message)
{
    public static WatchdogSafetyOperationResult Accepted(
        string code,
        string message) =>
        new(true, code, message);

    public static WatchdogSafetyOperationResult Rejected(
        string code,
        string message) =>
        new(false, code, message);
}

internal sealed record WatchdogSafetyControllerSnapshot(
    string State,
    string Reason,
    bool ArmingAvailable,
    bool Armed,
    DateTimeOffset? ArmedAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? HeartbeatDeadlineAt,
    int? HeartbeatTimeoutMilliseconds,
    long UnkeyAttemptCount,
    long UnkeyAcceptedCount,
    long UnkeyRejectedCount,
    long UnkeyUnknownCount,
    string LastUnkeyOutcome,
    string LastUnkeyReason);

/// <summary>
/// Independent-process, unkey-only watchdog state machine. One exact authority
/// tuple may be armed for one bounded heartbeat deadline. Expiry produces at
/// most one ownership-checked unkey attempt. The controller never retries and
/// never exposes a key or arbitrary-command operation.
/// </summary>
internal sealed class WatchdogSafetyController : IAsyncDisposable
{
    private readonly object m_gate = new();
    private readonly bool m_armingEnabled;
    private readonly IWatchdogUnkeyTransport m_unkeyTransport;
    private readonly TimeProvider m_timeProvider;

    private WatchdogIdentity? m_identity;
    private string m_state = "Disarmed";
    private string m_reason;
    private DateTimeOffset? m_armedAt;
    private DateTimeOffset? m_lastHeartbeatAt;
    private DateTimeOffset? m_heartbeatDeadlineAt;
    private TimeSpan? m_heartbeatTimeout;
    private long m_armEpoch;
    private long m_unkeyAttemptCount;
    private long m_unkeyAcceptedCount;
    private long m_unkeyRejectedCount;
    private long m_unkeyUnknownCount;
    private string m_lastUnkeyOutcome = "none";
    private string m_lastUnkeyReason = "none";
    private ITimer? m_deadlineTimer;
    private Task m_deadlineTask = Task.CompletedTask;
    private int m_disposed;

    public WatchdogSafetyController(
        bool armingEnabled,
        IWatchdogUnkeyTransport unkeyTransport,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(unkeyTransport);
        m_armingEnabled = armingEnabled;
        m_unkeyTransport = unkeyTransport;
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_reason = InitialDisarmedReason();
    }

    public WatchdogSafetyControllerSnapshot Snapshot
    {
        get
        {
            lock (m_gate)
            {
                return SnapshotLocked();
            }
        }
    }

    public WatchdogSafetyOperationResult Arm(
        WatchdogIdentity identity,
        TimeSpan heartbeatTimeout)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (m_gate)
        {
            ThrowIfDisposed();
            if (!ArmingAvailableLocked())
            {
                return WatchdogSafetyOperationResult.Rejected(
                    "arming-unavailable",
                    "Independent watchdog arming is disabled or its unkey transport is unavailable.");
            }
            if (!ValidHeartbeatTimeout(heartbeatTimeout))
            {
                return WatchdogSafetyOperationResult.Rejected(
                    "invalid-heartbeat-timeout",
                    "The watchdog heartbeat timeout is outside the bounded safety range.");
            }
            if (m_state != "Disarmed" || m_identity is not null)
            {
                return WatchdogSafetyOperationResult.Rejected(
                    "already-armed",
                    "The independent watchdog already has an active or unresolved arm epoch.");
            }

            DateTimeOffset now = m_timeProvider.GetUtcNow();
            m_identity = identity;
            m_state = "Armed";
            m_reason = "armed-heartbeat-current";
            m_armedAt = now;
            m_lastHeartbeatAt = now;
            m_heartbeatTimeout = heartbeatTimeout;
            m_heartbeatDeadlineAt = now + heartbeatTimeout;
            m_armEpoch = checked(m_armEpoch + 1);
            ScheduleDeadlineLocked(now, m_armEpoch);
            return WatchdogSafetyOperationResult.Accepted(
                "armed",
                "The independent watchdog is armed for the exact authority tuple and protected FLEX handle.");
        }
    }

    public WatchdogSafetyOperationResult Heartbeat(
        WatchdogIdentity identity,
        TimeSpan heartbeatTimeout)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (m_gate)
        {
            ThrowIfDisposed();
            if (!ValidHeartbeatTimeout(heartbeatTimeout))
            {
                return WatchdogSafetyOperationResult.Rejected(
                    "invalid-heartbeat-timeout",
                    "The watchdog heartbeat timeout is outside the bounded safety range.");
            }
            if (m_state != "Armed" || m_identity is null)
            {
                return WatchdogSafetyOperationResult.Rejected(
                    "not-armed",
                    "No active independent watchdog arm accepts a safety heartbeat.");
            }
            if (!Equals(m_identity, identity))
            {
                return WatchdogSafetyOperationResult.Rejected(
                    "identity-mismatch",
                    "The safety heartbeat does not match the exact armed authority tuple.");
            }

            DateTimeOffset now = m_timeProvider.GetUtcNow();
            if (m_heartbeatDeadlineAt is null || now >= m_heartbeatDeadlineAt)
            {
                return WatchdogSafetyOperationResult.Rejected(
                    "heartbeat-expired",
                    "The independent watchdog heartbeat deadline has already expired.");
            }

            m_reason = "armed-heartbeat-current";
            m_lastHeartbeatAt = now;
            m_heartbeatTimeout = heartbeatTimeout;
            m_heartbeatDeadlineAt = now + heartbeatTimeout;
            ScheduleDeadlineLocked(now, m_armEpoch);
            return WatchdogSafetyOperationResult.Accepted(
                "heartbeat",
                "The independent watchdog safety heartbeat was renewed.");
        }
    }

    public WatchdogSafetyOperationResult Disarm(WatchdogIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (m_gate)
        {
            ThrowIfDisposed();
            if (m_state == "Disarmed" && m_identity is null)
            {
                return WatchdogSafetyOperationResult.Accepted(
                    "disarmed",
                    "The independent watchdog is already Disarmed.");
            }
            if (m_identity is null || !Equals(m_identity, identity))
            {
                return WatchdogSafetyOperationResult.Rejected(
                    "identity-mismatch",
                    "The disarm request does not match the exact armed authority tuple.");
            }
            if (m_state is "Unkeying" or "ReconciliationRequired")
            {
                return WatchdogSafetyOperationResult.Rejected(
                    "reconciliation-required",
                    "An emergency unkey outcome must be reconciled before the watchdog can be disarmed.");
            }

            ClearArmLocked("explicitly-disarmed");
            return WatchdogSafetyOperationResult.Accepted(
                "disarmed",
                "The independent watchdog returned to Disarmed state.");
        }
    }

    public void ObserveDisconnect(WatchdogIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (m_gate)
        {
            if (Volatile.Read(ref m_disposed) != 0 ||
                m_state != "Armed" ||
                m_identity is null ||
                !Equals(m_identity, identity))
            {
                return;
            }
            m_reason = "armed-disconnected-awaiting-deadline";
        }
    }

    internal async Task EvaluateDeadlineAsync(
        CancellationToken cancellationToken = default)
    {
        WatchdogIdentity? identity;
        long epoch;
        lock (m_gate)
        {
            if (Volatile.Read(ref m_disposed) != 0 ||
                m_state != "Armed" ||
                m_identity is null ||
                m_heartbeatDeadlineAt is null ||
                m_timeProvider.GetUtcNow() < m_heartbeatDeadlineAt)
            {
                return;
            }

            identity = m_identity;
            epoch = m_armEpoch;
            m_state = "Unkeying";
            m_reason = "heartbeat-expired-unkeying";
            m_unkeyAttemptCount++;
            DisposeDeadlineTimerLocked();
        }

        WatchdogUnkeyTransportResult result;
        try
        {
            result = await m_unkeyTransport.RequestUnkeyAsync(
                identity.StationClientHandle,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = WatchdogUnkeyTransportResult.Unknown(
                "The watchdog unkey operation timed out after dispatch.");
        }
        catch (Exception exception)
        {
            result = WatchdogUnkeyTransportResult.Unknown(
                string.IsNullOrWhiteSpace(exception.Message)
                    ? "The watchdog unkey transport failed unexpectedly."
                    : exception.Message);
        }

        lock (m_gate)
        {
            if (Volatile.Read(ref m_disposed) != 0 ||
                epoch != m_armEpoch ||
                m_state != "Unkeying")
            {
                return;
            }

            m_lastUnkeyOutcome = result.Outcome.ToString().ToLowerInvariant();
            if (result.Success)
            {
                m_unkeyAcceptedCount++;
                m_lastUnkeyReason = "deadline-unkey-accepted";
                ClearArmLocked("deadline-unkey-accepted");
                return;
            }

            m_state = "ReconciliationRequired";
            if (result.OutcomeKnown)
            {
                m_unkeyRejectedCount++;
                m_reason = "deadline-unkey-rejected";
                m_lastUnkeyReason = "deadline-unkey-rejected";
            }
            else
            {
                m_unkeyUnknownCount++;
                m_reason = "deadline-unkey-outcome-unknown";
                m_lastUnkeyReason = "deadline-unkey-outcome-unknown";
            }
        }
    }

    private void ScheduleDeadlineLocked(DateTimeOffset now, long epoch)
    {
        DisposeDeadlineTimerLocked();
        TimeSpan due = (m_heartbeatDeadlineAt ?? now) - now;
        if (due < TimeSpan.Zero)
        {
            due = TimeSpan.Zero;
        }
        m_deadlineTimer = m_timeProvider.CreateTimer(
            static state =>
            {
                DeadlineTimerState timer = (DeadlineTimerState)state!;
                timer.Owner.BeginDeadlineEvaluation(timer.Epoch);
            },
            new DeadlineTimerState(this, epoch),
            due,
            Timeout.InfiniteTimeSpan);
    }

    private void BeginDeadlineEvaluation(long epoch)
    {
        lock (m_gate)
        {
            if (Volatile.Read(ref m_disposed) != 0 ||
                epoch != m_armEpoch ||
                m_state != "Armed")
            {
                return;
            }
            m_deadlineTask = EvaluateDeadlineAsync();
        }
    }

    private WatchdogSafetyControllerSnapshot SnapshotLocked() =>
        new(
            m_state,
            m_reason,
            ArmingAvailableLocked(),
            Armed: m_identity is not null &&
                m_state is "Armed" or "Unkeying" or "ReconciliationRequired",
            m_armedAt,
            m_lastHeartbeatAt,
            m_heartbeatDeadlineAt,
            m_heartbeatTimeout.HasValue
                ? checked((int)m_heartbeatTimeout.Value.TotalMilliseconds)
                : null,
            m_unkeyAttemptCount,
            m_unkeyAcceptedCount,
            m_unkeyRejectedCount,
            m_unkeyUnknownCount,
            m_lastUnkeyOutcome,
            m_lastUnkeyReason);

    private bool ArmingAvailableLocked() =>
        m_armingEnabled && m_unkeyTransport.IsAvailable;

    private string InitialDisarmedReason() =>
        !m_unkeyTransport.IsAvailable
            ? "unkey-transport-disabled-disarmed"
            : !m_armingEnabled
                ? "watchdog-arming-disabled-disarmed"
                : "watchdog-arming-ready-disarmed";

    private void ClearArmLocked(string reason)
    {
        DisposeDeadlineTimerLocked();
        m_identity = null;
        m_state = "Disarmed";
        m_reason = reason;
        m_armedAt = null;
        m_lastHeartbeatAt = null;
        m_heartbeatDeadlineAt = null;
        m_heartbeatTimeout = null;
    }

    private void DisposeDeadlineTimerLocked()
    {
        m_deadlineTimer?.Dispose();
        m_deadlineTimer = null;
    }

    private static bool ValidHeartbeatTimeout(TimeSpan timeout) =>
        timeout >= TimeSpan.FromMilliseconds(
            WatchdogProtocol.MinimumHeartbeatTimeoutMilliseconds) &&
        timeout <= TimeSpan.FromMilliseconds(
            WatchdogProtocol.MaximumHeartbeatTimeoutMilliseconds);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref m_disposed) != 0,
            this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
        {
            return;
        }

        Task deadlineTask;
        lock (m_gate)
        {
            DisposeDeadlineTimerLocked();
            deadlineTask = m_deadlineTask;
        }
        try
        {
            await deadlineTask;
        }
        catch
        {
            // Deadline execution converts bounded transport failures to an
            // explicit reconciliation state. Disposal must not retry or hide a
            // caller cancellation.
        }
    }

    private sealed record DeadlineTimerState(
        WatchdogSafetyController Owner,
        long Epoch);
}
