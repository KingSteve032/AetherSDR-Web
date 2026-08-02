using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.TxWatchdog;

public sealed class WatchdogHostEngine : IAsyncDisposable
{
    private readonly object m_gate = new();
    private readonly TimeProvider m_timeProvider;
    private readonly IWatchdogUnkeyTransport m_unkeyTransport;
    private readonly WatchdogSafetyController m_safetyController;
    private readonly string m_hostInstanceId;
    private readonly DateTimeOffset m_startedAt;

    private WatchdogIdentity? m_identity;
    private bool m_connected;
    private long m_lastSequence;
    private string m_lastObservation = "process-started-disarmed";
    private DateTimeOffset? m_lastObservedAt;
    private int m_disposed;

    public WatchdogHostEngine(
        TimeProvider? timeProvider = null,
        string? hostInstanceId = null)
        : this(
            timeProvider,
            hostInstanceId,
            new UnavailableWatchdogUnkeyTransport(),
            armingEnabled: false)
    {
    }

    internal WatchdogHostEngine(
        TimeProvider? timeProvider,
        string? hostInstanceId,
        IWatchdogUnkeyTransport unkeyTransport,
        bool armingEnabled = false)
    {
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_unkeyTransport = unkeyTransport ??
            throw new ArgumentNullException(nameof(unkeyTransport));
        m_safetyController = new WatchdogSafetyController(
            armingEnabled,
            m_unkeyTransport,
            m_timeProvider);
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
            ThrowIfDisposed();
            return request.Kind switch
            {
                WatchdogRequestKind.Status => Success(request.RequestId),
                WatchdogRequestKind.Register => Register(request),
                WatchdogRequestKind.Arm => Arm(request),
                WatchdogRequestKind.Heartbeat => Heartbeat(request),
                WatchdogRequestKind.Disarm => Disarm(request),
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

    internal Task EvaluateDeadlineAsync(
        CancellationToken cancellationToken = default) =>
        m_safetyController.EvaluateDeadlineAsync(cancellationToken);

    private WatchdogResponse Register(WatchdogRequest request)
    {
        if (!TryGetAuthority(request, out WatchdogIdentity identity, out long sequence))
        {
            return Failure(request.RequestId, "missing-authority-envelope");
        }
        if (request.HeartbeatTimeoutMilliseconds is not null)
        {
            return Failure(request.RequestId, "unexpected-heartbeat-timeout");
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
        m_lastObservation = m_safetyController.Snapshot.Armed
            ? "registered-armed"
            : "registered-disarmed";
        m_lastObservedAt = m_timeProvider.GetUtcNow();
        return Success(request.RequestId);
    }

    private WatchdogResponse Arm(WatchdogRequest request)
    {
        if (!TryGetCurrentAuthority(
                request,
                requireConnected: true,
                out WatchdogIdentity identity,
                out long sequence,
                out WatchdogResponse? failure))
        {
            return failure!;
        }
        if (!request.HeartbeatTimeoutMilliseconds.HasValue)
        {
            return Failure(request.RequestId, "arm-requires-heartbeat-timeout");
        }

        WatchdogSafetyOperationResult result = m_safetyController.Arm(
            identity,
            TimeSpan.FromMilliseconds(
                request.HeartbeatTimeoutMilliseconds.Value));
        if (!result.Success)
        {
            return Failure(request.RequestId, result.Code);
        }

        Advance(sequence, "armed-exact-authority");
        return Success(request.RequestId);
    }

    private WatchdogResponse Heartbeat(WatchdogRequest request)
    {
        if (!TryGetCurrentAuthority(
                request,
                requireConnected: true,
                out WatchdogIdentity identity,
                out long sequence,
                out WatchdogResponse? failure))
        {
            return failure!;
        }

        WatchdogSafetyControllerSnapshot safety = m_safetyController.Snapshot;
        if (safety.Armed)
        {
            if (!request.HeartbeatTimeoutMilliseconds.HasValue)
            {
                return Failure(
                    request.RequestId,
                    "armed-heartbeat-requires-timeout");
            }
            WatchdogSafetyOperationResult result =
                m_safetyController.Heartbeat(
                    identity,
                    TimeSpan.FromMilliseconds(
                        request.HeartbeatTimeoutMilliseconds.Value));
            if (!result.Success)
            {
                return Failure(request.RequestId, result.Code);
            }
            Advance(sequence, "heartbeat-observed-armed");
            return Success(request.RequestId);
        }

        if (request.HeartbeatTimeoutMilliseconds is not null)
        {
            return Failure(request.RequestId, "unexpected-heartbeat-timeout");
        }
        Advance(sequence, "heartbeat-observed-disarmed");
        return Success(request.RequestId);
    }

    private WatchdogResponse Disarm(WatchdogRequest request)
    {
        if (!TryGetCurrentAuthority(
                request,
                requireConnected: false,
                out WatchdogIdentity identity,
                out long sequence,
                out WatchdogResponse? failure))
        {
            return failure!;
        }
        if (request.HeartbeatTimeoutMilliseconds is not null)
        {
            return Failure(request.RequestId, "unexpected-heartbeat-timeout");
        }

        WatchdogSafetyOperationResult result =
            m_safetyController.Disarm(identity);
        if (!result.Success)
        {
            return Failure(request.RequestId, result.Code);
        }

        Advance(sequence, "explicit-disarm-observed");
        return Success(request.RequestId);
    }

    private WatchdogResponse Disconnect(WatchdogRequest request)
    {
        if (!TryGetCurrentAuthority(
                request,
                requireConnected: false,
                out WatchdogIdentity identity,
                out long sequence,
                out WatchdogResponse? failure))
        {
            return failure!;
        }
        if (request.HeartbeatTimeoutMilliseconds is not null)
        {
            return Failure(request.RequestId, "unexpected-heartbeat-timeout");
        }

        m_connected = false;
        m_safetyController.ObserveDisconnect(identity);
        Advance(
            sequence,
            m_safetyController.Snapshot.Armed
                ? "disconnect-observed-armed"
                : "disconnect-observed-disarmed");
        return Success(request.RequestId);
    }

    private bool TryGetCurrentAuthority(
        WatchdogRequest request,
        bool requireConnected,
        out WatchdogIdentity identity,
        out long sequence,
        out WatchdogResponse? failure)
    {
        failure = null;
        if (!TryGetAuthority(request, out identity, out sequence))
        {
            failure = Failure(request.RequestId, "missing-authority-envelope");
            return false;
        }
        if (m_identity is null)
        {
            failure = Failure(request.RequestId, "not-registered");
            return false;
        }
        if (!Equals(m_identity, identity))
        {
            failure = Failure(request.RequestId, "identity-mismatch");
            return false;
        }
        if (requireConnected && !m_connected)
        {
            failure = Failure(
                request.RequestId,
                "disconnected-registration-required");
            return false;
        }
        if (sequence <= m_lastSequence)
        {
            failure = Failure(request.RequestId, "stale-sequence");
            return false;
        }
        return true;
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

    private void Advance(long sequence, string observation)
    {
        m_lastSequence = sequence;
        m_lastObservation = observation;
        m_lastObservedAt = m_timeProvider.GetUtcNow();
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

    private WatchdogSnapshot SnapshotLocked()
    {
        WatchdogSafetyControllerSnapshot safety = m_safetyController.Snapshot;
        return new WatchdogSnapshot(
            m_hostInstanceId,
            m_startedAt,
            safety.State,
            safety.Reason,
            RadioCommandTransportAvailable: m_unkeyTransport.IsAvailable,
            safety.ArmingAvailable,
            Registered: m_identity is not null,
            Connected: m_connected,
            Identity: m_identity,
            LeaseBound: m_identity is not null,
            m_lastSequence,
            m_lastObservation,
            m_lastObservedAt,
            safety.Armed,
            safety.ArmedAt,
            safety.LastHeartbeatAt,
            safety.HeartbeatDeadlineAt,
            safety.HeartbeatTimeoutMilliseconds,
            safety.UnkeyAttemptCount,
            safety.UnkeyAcceptedCount,
            safety.UnkeyRejectedCount,
            safety.UnkeyUnknownCount,
            safety.LastUnkeyOutcome,
            safety.LastUnkeyReason);
    }

    private static string? NormalizeHostInstanceId(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 128 &&
            !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }

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
        await m_safetyController.DisposeAsync();
    }
}
