using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.TxWatchdog;

public sealed class WatchdogHostEngine
{
    private readonly object m_gate = new();
    private readonly TimeProvider m_timeProvider;
    private readonly string m_hostInstanceId;
    private readonly DateTimeOffset m_startedAt;

    private WatchdogIdentity? m_identity;
    private bool m_connected;
    private long m_lastSequence;
    private string m_lastObservation = "process-started-disarmed";
    private DateTimeOffset? m_lastObservedAt;

    public WatchdogHostEngine(
        TimeProvider? timeProvider = null,
        string? hostInstanceId = null)
    {
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_hostInstanceId = NormalizeHostInstanceId(hostInstanceId) ??
            $"watchdog-{Guid.NewGuid():N}";
        m_startedAt = m_timeProvider.GetUtcNow();
    }

    public WatchdogSnapshot Snapshot
    {
        get
        {
            lock (m_gate)
            {
                return SnapshotLocked();
            }
        }
    }

    public WatchdogResponse Process(WatchdogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (m_gate)
        {
            return request.Kind switch
            {
                WatchdogRequestKind.Status => Success(request.RequestId),
                WatchdogRequestKind.Register => Register(request),
                WatchdogRequestKind.Heartbeat => Heartbeat(request),
                WatchdogRequestKind.Disconnect => Disconnect(request),
                _ => Failure(request.RequestId, "unknown-request-type")
            };
        }
    }

    public WatchdogResponse Reject(string requestId, string error)
    {
        string normalizedRequestId = string.IsNullOrWhiteSpace(requestId)
            ? "invalid"
            : requestId.Trim();
        lock (m_gate)
        {
            return Failure(normalizedRequestId, error);
        }
    }

    private WatchdogResponse Register(WatchdogRequest request)
    {
        if (!TryGetAuthority(request, out WatchdogIdentity identity, out long sequence))
        {
            return Failure(request.RequestId, "missing-authority-envelope");
        }

        if (m_identity is not null && !Equals(m_identity, identity))
        {
            return Failure(request.RequestId, "identity-mismatch");
        }
        if (sequence <= m_lastSequence)
        {
            return Failure(request.RequestId, "stale-sequence");
        }

        m_identity = identity;
        m_connected = true;
        m_lastSequence = sequence;
        m_lastObservation = "registered-disarmed";
        m_lastObservedAt = m_timeProvider.GetUtcNow();
        return Success(request.RequestId);
    }

    private WatchdogResponse Heartbeat(WatchdogRequest request)
    {
        if (!TryGetAuthority(request, out WatchdogIdentity identity, out long sequence))
        {
            return Failure(request.RequestId, "missing-authority-envelope");
        }
        if (m_identity is null)
        {
            return Failure(request.RequestId, "not-registered");
        }
        if (!Equals(m_identity, identity))
        {
            return Failure(request.RequestId, "identity-mismatch");
        }
        if (!m_connected)
        {
            return Failure(request.RequestId, "disconnected-registration-required");
        }
        if (sequence <= m_lastSequence)
        {
            return Failure(request.RequestId, "stale-sequence");
        }

        m_lastSequence = sequence;
        m_lastObservation = "heartbeat-observed-disarmed";
        m_lastObservedAt = m_timeProvider.GetUtcNow();
        return Success(request.RequestId);
    }

    private WatchdogResponse Disconnect(WatchdogRequest request)
    {
        if (!TryGetAuthority(request, out WatchdogIdentity identity, out long sequence))
        {
            return Failure(request.RequestId, "missing-authority-envelope");
        }
        if (m_identity is null)
        {
            return Failure(request.RequestId, "not-registered");
        }
        if (!Equals(m_identity, identity))
        {
            return Failure(request.RequestId, "identity-mismatch");
        }
        if (sequence <= m_lastSequence)
        {
            return Failure(request.RequestId, "stale-sequence");
        }

        m_connected = false;
        m_lastSequence = sequence;
        m_lastObservation = "disconnect-observed-disarmed";
        m_lastObservedAt = m_timeProvider.GetUtcNow();
        return Success(request.RequestId);
    }

    private static bool TryGetAuthority(
        WatchdogRequest request,
        out WatchdogIdentity identity,
        out long sequence)
    {
        identity = request.Identity!;
        sequence = request.Sequence ?? 0;
        return request.Identity is not null && sequence > 0;
    }

    private WatchdogResponse Success(string requestId) =>
        new(
            WatchdogProtocol.Version,
            requestId,
            Ok: true,
            Error: null,
            SnapshotLocked());

    private WatchdogResponse Failure(string requestId, string error) =>
        new(
            WatchdogProtocol.Version,
            requestId,
            Ok: false,
            error,
            SnapshotLocked());

    private WatchdogSnapshot SnapshotLocked() =>
        new(
            m_hostInstanceId,
            m_startedAt,
            State: "Disarmed",
            Reason: "command-incapable-skeleton",
            RadioCommandTransportAvailable: false,
            ArmingAvailable: false,
            Registered: m_identity is not null,
            Connected: m_connected,
            Identity: m_identity,
            LeaseBound: m_identity is not null,
            m_lastSequence,
            m_lastObservation,
            m_lastObservedAt);

    private static string? NormalizeHostInstanceId(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 128 &&
            !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }
}
