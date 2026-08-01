namespace AetherSDR.Web.Radio;

public sealed record StationTxCommandAdapterCompositionDiagnostics(
    bool Registered,
    bool ExecutorAttached,
    bool ExecutorRegistered,
    bool ExecutorArmingAvailable,
    bool ExecutorSetTransmitAvailable,
    bool AuthoritySnapshotAvailable,
    bool CommandAdapterRegistered,
    bool ArmingAvailable,
    bool SetTransmitAvailable,
    long AttemptCount,
    long ForwardedCount,
    long AcceptedCount,
    long RejectedCount,
    string LastOutcome,
    DateTimeOffset? LastObservedAt,
    string Reason);

internal sealed record StationTxCommandAdapterExecutorCapabilities(
    bool Registered,
    bool ArmingAvailable,
    bool SetTransmitAvailable,
    string Reason);

/// <summary>
/// Future station-local execution boundary for one already validated command.
/// An implementation may not infer authority from the command and may not
/// expose a browser, HTTP, WebSocket, AetherRemote, watchdog, or timer route.
/// Phase 2L registers no production implementation.
/// </summary>
internal interface IStationTxCommandAdapterExecutor
{
    StationTxCommandAdapterExecutorCapabilities Capabilities { get; }

    Task<StationTxTransportResult> ExecuteAsync(
        StationTxValidatedCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-session adapter composition beneath the signed command boundary. It
/// independently re-resolves exact server-owned authority before delegating to
/// an optional internal executor. Production supplies no executor, so the
/// existing command-adapter, arming, and SetTransmit capability bits remain
/// false and no radio command can be reached.
/// </summary>
internal sealed class StationTxCommandAdapterComposition :
    IStationTxCommandAdapter
{
    private readonly object m_gate = new();
    private readonly IStationTxCommandAdapterExecutor? m_executor;
    private readonly Func<string?, StationTxCommandAuthorityResolution>
        m_authorityResolver;
    private readonly TimeProvider m_timeProvider;
    private long m_attemptCount;
    private long m_forwardedCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private string m_lastOutcome = "none";
    private DateTimeOffset? m_lastObservedAt;

    public StationTxCommandAdapterComposition(
        IStationTxCommandAdapterExecutor? executor,
        Func<string?, StationTxCommandAuthorityResolution> authorityResolver,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(authorityResolver);
        m_executor = executor;
        m_authorityResolver = authorityResolver;
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public StationTxCommandAdapterCompositionDiagnostics Snapshot
    {
        get
        {
            StationTxCommandAdapterExecutorCapabilities? executor =
                GetExecutorCapabilities();
            StationTxCommandAuthorityResolution authority =
                ResolveAuthority();
            bool adapterRegistered = executor?.Registered == true;
            bool armingAvailable =
                adapterRegistered &&
                executor!.ArmingAvailable &&
                authority.Success &&
                AuthorityIsFreshlyArmed(
                    authority.Authority!,
                    m_timeProvider.GetUtcNow());
            bool setTransmitAvailable =
                armingAvailable && executor!.SetTransmitAvailable;
            string reason = GetReason(executor, authority, armingAvailable);

            lock (m_gate)
            {
                return new StationTxCommandAdapterCompositionDiagnostics(
                    Registered: true,
                    ExecutorAttached: executor is not null,
                    ExecutorRegistered: executor?.Registered == true,
                    ExecutorArmingAvailable:
                        executor?.ArmingAvailable == true,
                    ExecutorSetTransmitAvailable:
                        executor?.SetTransmitAvailable == true,
                    AuthoritySnapshotAvailable: authority.Success,
                    CommandAdapterRegistered: adapterRegistered,
                    ArmingAvailable: armingAvailable,
                    SetTransmitAvailable: setTransmitAvailable,
                    m_attemptCount,
                    m_forwardedCount,
                    m_acceptedCount,
                    m_rejectedCount,
                    m_lastOutcome,
                    m_lastObservedAt,
                    reason);
            }
        }
    }

    bool IStationTxCommandAdapter.IsRegistered =>
        Snapshot.CommandAdapterRegistered;

    bool IStationTxCommandAdapter.ArmingAvailable =>
        Snapshot.ArmingAvailable;

    bool IStationTxCommandAdapter.SupportsSetTransmit =>
        Snapshot.SetTransmitAvailable;

    async Task<StationTxTransportResult> IStationTxCommandAdapter.ExecuteAsync(
        StationTxValidatedCommand command,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(command, cancellationToken);

    internal async Task<StationTxTransportResult> ExecuteAsync(
        StationTxValidatedCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = m_timeProvider.GetUtcNow();
        BeginAttempt(now);

        StationTxCommandAdapterExecutorCapabilities? executor =
            GetExecutorCapabilities();
        AdapterFailure? failure = executor is null
            ? new(
                "executor_unattached",
                "No station-local command executor is attached.")
            : null;
        if (failure is null && !executor!.Registered)
        {
            failure = new(
                "executor_unregistered",
                "The station-local command executor is not registered.");
        }
        if (failure is null && !executor!.ArmingAvailable)
        {
            failure = new(
                "executor_arming_unavailable",
                "The station-local command executor has no arming authority.");
        }
        if (failure is null && !executor!.SetTransmitAvailable)
        {
            failure = new(
                "executor_command_unavailable",
                "The station-local command executor cannot set transmit.");
        }

        StationTxCommandAuthorityResolution? authority = null;
        if (failure is null)
        {
            authority = ResolveAuthority();
            if (!authority.Success)
            {
                failure = new(authority.Code, authority.Message);
            }
        }
        if (failure is null)
        {
            failure = ValidateCommand(command, authority!.Authority!, now);
        }

        if (failure is not null)
        {
            return Reject(failure, now);
        }

        RecordForwarded(now);
        StationTxTransportResult result;
        try
        {
            result = await m_executor!.ExecuteAsync(
                command,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RecordException("cancelled", now);
            throw;
        }
        catch
        {
            RecordException("executor-exception", now);
            throw;
        }

        Complete(result, now);
        return result;
    }

    private StationTxCommandAdapterExecutorCapabilities?
        GetExecutorCapabilities()
    {
        try
        {
            return m_executor?.Capabilities;
        }
        catch
        {
            return new StationTxCommandAdapterExecutorCapabilities(
                Registered: false,
                ArmingAvailable: false,
                SetTransmitAvailable: false,
                Reason: "executor-capabilities-faulted");
        }
    }

    private StationTxCommandAuthorityResolution ResolveAuthority()
    {
        try
        {
            return m_authorityResolver(null);
        }
        catch
        {
            return StationTxCommandAuthorityResolution.Rejected(
                "authority_resolution_failed",
                "The session-owned station command authority could not be resolved.");
        }
    }

    private static AdapterFailure? ValidateCommand(
        StationTxValidatedCommand command,
        StationTxCommandAuthority authority,
        DateTimeOffset now)
    {
        if (command.Action != StationTxCommandAction.SetTransmit)
        {
            return new(
                "unsupported_command",
                "Only the station-local SetTransmit command is supported.");
        }
        if (command.IssuedAt > now + StationTxCommandBoundary.MaximumClockSkew ||
            command.ExpiresAt <= command.IssuedAt ||
            command.ExpiresAt <= now ||
            command.ExpiresAt - command.IssuedAt >
                StationTxCommandBoundary.MaximumEnvelopeLifetime ||
            now - command.IssuedAt >
                StationTxCommandBoundary.MaximumEnvelopeAge)
        {
            return new(
                "command_stale",
                "The validated station command is expired or outside its bounded lifetime.");
        }
        if (!string.Equals(
                command.StationId,
                authority.StationId,
                StringComparison.Ordinal))
        {
            return new("station_mismatch", "The station identity does not match.");
        }
        if (!string.Equals(
                command.RadioId,
                authority.RadioId,
                StringComparison.Ordinal))
        {
            return new("radio_mismatch", "The radio identity does not match.");
        }
        if (!string.Equals(
                command.SessionId,
                authority.SessionId,
                StringComparison.Ordinal))
        {
            return new("session_mismatch", "The web session identity does not match.");
        }
        if (!string.Equals(
                command.BrowserClientId,
                authority.BrowserClientId,
                StringComparison.Ordinal))
        {
            return new(
                "browser_client_mismatch",
                "The browser client identity does not match.");
        }
        if (!string.Equals(
                command.LeaseId,
                authority.LeaseId,
                StringComparison.Ordinal) ||
            authority.LeaseExpiresAt <= now ||
            authority.LeaseExpiresAt < command.ExpiresAt)
        {
            return new(
                "lease_mismatch",
                "The TX lease is absent, expired, or mismatched.");
        }
        if (!string.Equals(
                command.GatewayInstanceId,
                authority.GatewayInstanceId,
                StringComparison.Ordinal))
        {
            return new(
                "gateway_instance_mismatch",
                "The gateway instance identity does not match.");
        }
        if (!string.Equals(
                command.EngineInstanceId,
                authority.EngineInstanceId,
                StringComparison.Ordinal))
        {
            return new(
                "engine_instance_mismatch",
                "The station engine identity does not match.");
        }
        if (command.ClientHandle == 0 ||
            command.ClientHandle != authority.ClientHandle)
        {
            return new(
                "client_handle_mismatch",
                "The protected FLEX client handle does not match.");
        }
        if (!authority.Authenticated)
        {
            return new(
                "authentication_stale",
                "Authentication is not current.");
        }
        if (!authority.BrowserFresh ||
            !authority.EngineFresh ||
            !authority.GatewayFresh ||
            !authority.AuthorityFresh)
        {
            return new(
                "authority_stale",
                "The station command authority observations are stale.");
        }

        RadioTxOccupancySnapshot occupancy = authority.Occupancy;
        if (!string.Equals(
                occupancy.RadioId,
                command.RadioId,
                StringComparison.OrdinalIgnoreCase) ||
            occupancy.ObservedAt is null ||
            occupancy.FreshUntil is null ||
            occupancy.FreshUntil <= now)
        {
            return new(
                "occupancy_stale",
                "Radio-authoritative TX occupancy is stale or mismatched.");
        }
        if (!occupancy.BrowserLeaseAllowed)
        {
            return new(
                "radio_not_idle",
                "Radio-authoritative TX occupancy is not idle.");
        }
        if (!occupancy.HasExclusiveLocalPttAuthority(command.ClientHandle))
        {
            return new(
                "local_ptt_authority_mismatch",
                "Exclusive Local PTT authority does not match the protected FLEX handle.");
        }
        if (!AuthorityIsFreshlyArmed(authority, now))
        {
            return new(
                "safety_not_armed",
                "The independent TX safety identity is not freshly armed for this command.");
        }

        return null;
    }

    private static bool AuthorityIsFreshlyArmed(
        StationTxCommandAuthority authority,
        DateTimeOffset now)
    {
        StationTxSafetySnapshot safety = authority.Safety;
        return safety.State == StationTxSafetyState.Armed &&
            string.Equals(
                safety.RadioId,
                authority.RadioId,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                safety.EngineInstanceId,
                authority.EngineInstanceId,
                StringComparison.Ordinal) &&
            string.Equals(
                safety.LeaseId,
                authority.LeaseId,
                StringComparison.Ordinal) &&
            string.Equals(
                safety.SessionId,
                authority.SessionId,
                StringComparison.Ordinal) &&
            string.Equals(
                safety.BrowserClientId,
                authority.BrowserClientId,
                StringComparison.Ordinal) &&
            safety.ProtectedClientHandle == authority.ClientHandle &&
            safety.HeartbeatDeadlineAt is not null &&
            safety.HeartbeatDeadlineAt > now;
    }

    private static string GetReason(
        StationTxCommandAdapterExecutorCapabilities? executor,
        StationTxCommandAuthorityResolution authority,
        bool armingAvailable)
    {
        if (executor is null)
        {
            return "executor-unattached";
        }
        if (!executor.Registered)
        {
            return string.IsNullOrWhiteSpace(executor.Reason)
                ? "executor-unregistered"
                : executor.Reason;
        }
        if (!executor.ArmingAvailable)
        {
            return "executor-arming-unavailable";
        }
        if (!executor.SetTransmitAvailable)
        {
            return "executor-command-unavailable";
        }
        if (!authority.Success)
        {
            return authority.Code;
        }
        return armingAvailable ? "ready" : "safety-not-armed";
    }

    private void BeginAttempt(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_attemptCount++;
            m_lastObservedAt = now;
            m_lastOutcome = "attempting";
        }
    }

    private void RecordForwarded(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_forwardedCount++;
            m_lastObservedAt = now;
            m_lastOutcome = "forwarded";
        }
    }

    private void RecordException(string outcome, DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_rejectedCount++;
            m_lastObservedAt = now;
            m_lastOutcome = outcome;
        }
    }

    private StationTxTransportResult Reject(
        AdapterFailure failure,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_rejectedCount++;
            m_lastObservedAt = now;
            m_lastOutcome = failure.Code;
        }
        return StationTxTransportResult.Rejected(failure.Message);
    }

    private void Complete(
        StationTxTransportResult result,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            if (result.Success)
            {
                m_acceptedCount++;
                m_lastOutcome = "accepted";
            }
            else
            {
                m_rejectedCount++;
                m_lastOutcome = result.OutcomeKnown
                    ? "executor-rejected"
                    : "executor-outcome-unknown";
            }
            m_lastObservedAt = now;
        }
    }

    private sealed record AdapterFailure(string Code, string Message);
}
