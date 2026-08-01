namespace AetherSDR.Web.Radio;

public sealed record StationTxSafetyArmAuthorityDiagnostics(
    bool Registered,
    bool BoundaryRegistered,
    bool BoundaryEnabled,
    bool SignatureVerificationAvailable,
    bool CommandAdapterRegistered,
    bool AdapterExecutorAttached,
    bool AdapterExecutorRegistered,
    bool GateExecutorRegistered,
    bool GateTransmitEnabled,
    bool CommandTransportAvailable,
    bool GateSetTransmitAvailable,
    bool SessionAuthoritySnapshotAvailable,
    string GateState,
    string SafetyState,
    bool ArmAvailable,
    bool HeartbeatAvailable,
    bool AbortAvailable,
    long AttemptCount,
    long AcceptedCount,
    long RejectedCount,
    string LastOperation,
    string LastOutcome,
    DateTimeOffset? LastObservedAt,
    string Reason);

/// <summary>
/// Lifecycle-owned authorization boundary for the existing safety-arm
/// composition. It does not arm the supervisor directly and owns no command
/// transport. Every authorization attempt independently re-reads the signed
/// command boundary, adapter, gate executor, command gate, supervisor, and the
/// current lifecycle-owned authority tuple.
///
/// Arm and heartbeat require the complete command path to be ready. Abort does
/// not depend on that path remaining available; it remains limited to an exact
/// current safety identity and ownership-safe radio state so capability loss
/// cannot remove the fail-safe abort decision.
/// </summary>
internal sealed class StationTxSafetyArmAuthority :
    IStationTxSafetyArmAuthority
{
    private const int MaximumAbortReasonLength = 64;

    private readonly object m_gate = new();
    private readonly Func<StationTxCommandCapabilities>
        m_boundaryCapabilitiesProvider;
    private readonly Func<StationTxCommandAdapterCompositionDiagnostics>
        m_adapterDiagnosticsProvider;
    private readonly Func<StationTxCommandAdapterExecutorCapabilities>
        m_executorCapabilitiesProvider;
    private readonly Func<StationTxCommandGateCapabilities>
        m_gateCapabilitiesProvider;
    private readonly Func<StationTxGateSnapshot> m_gateSnapshotProvider;
    private readonly Func<StationTxSafetySnapshot> m_safetySnapshotProvider;
    private readonly Func<string?, StationTxCommandAuthorityResolution>
        m_authorityResolver;
    private readonly TimeProvider m_timeProvider;

    private long m_attemptCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private string m_lastOperation = "none";
    private string m_lastOutcome = "none";
    private DateTimeOffset? m_lastObservedAt;

    public StationTxSafetyArmAuthority(
        Func<StationTxCommandCapabilities> boundaryCapabilitiesProvider,
        Func<StationTxCommandAdapterCompositionDiagnostics>
            adapterDiagnosticsProvider,
        Func<StationTxCommandAdapterExecutorCapabilities>
            executorCapabilitiesProvider,
        Func<StationTxCommandGateCapabilities> gateCapabilitiesProvider,
        Func<StationTxGateSnapshot> gateSnapshotProvider,
        Func<StationTxSafetySnapshot> safetySnapshotProvider,
        Func<string?, StationTxCommandAuthorityResolution> authorityResolver,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(boundaryCapabilitiesProvider);
        ArgumentNullException.ThrowIfNull(adapterDiagnosticsProvider);
        ArgumentNullException.ThrowIfNull(executorCapabilitiesProvider);
        ArgumentNullException.ThrowIfNull(gateCapabilitiesProvider);
        ArgumentNullException.ThrowIfNull(gateSnapshotProvider);
        ArgumentNullException.ThrowIfNull(safetySnapshotProvider);
        ArgumentNullException.ThrowIfNull(authorityResolver);

        m_boundaryCapabilitiesProvider = boundaryCapabilitiesProvider;
        m_adapterDiagnosticsProvider = adapterDiagnosticsProvider;
        m_executorCapabilitiesProvider = executorCapabilitiesProvider;
        m_gateCapabilitiesProvider = gateCapabilitiesProvider;
        m_gateSnapshotProvider = gateSnapshotProvider;
        m_safetySnapshotProvider = safetySnapshotProvider;
        m_authorityResolver = authorityResolver;
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public StationTxSafetyArmAuthorityDiagnostics Snapshot
    {
        get
        {
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            AuthorityStateRead read = ReadState();
            if (!read.Success)
            {
                lock (m_gate)
                {
                    return new StationTxSafetyArmAuthorityDiagnostics(
                        Registered: true,
                        BoundaryRegistered: false,
                        BoundaryEnabled: false,
                        SignatureVerificationAvailable: false,
                        CommandAdapterRegistered: false,
                        AdapterExecutorAttached: false,
                        AdapterExecutorRegistered: false,
                        GateExecutorRegistered: false,
                        GateTransmitEnabled: false,
                        CommandTransportAvailable: false,
                        GateSetTransmitAvailable: false,
                        SessionAuthoritySnapshotAvailable: false,
                        GateState: "Unavailable",
                        SafetyState: "Unavailable",
                        ArmAvailable: false,
                        HeartbeatAvailable: false,
                        AbortAvailable: false,
                        m_attemptCount,
                        m_acceptedCount,
                        m_rejectedCount,
                        m_lastOperation,
                        m_lastOutcome,
                        m_lastObservedAt,
                        read.Failure!.Code.Replace('_', '-'));
                }
            }

            AuthorityState state = read.State!;
            StationTxCommandAuthorityResolution resolution = state.Authority;
            AuthorityFailure? armFailure = resolution.Success
                ? ValidateOperation(
                    StationTxSafetyArmOperation.Arm,
                    resolution.Authority!,
                    state,
                    now,
                    heartbeatTimeout:
                        StationTxSafetySupervisor.MinimumHeartbeatTimeout,
                    abortReason: null)
                : new(resolution.Code, resolution.Message);
            AuthorityFailure? heartbeatFailure = resolution.Success
                ? ValidateOperation(
                    StationTxSafetyArmOperation.Heartbeat,
                    resolution.Authority!,
                    state,
                    now,
                    heartbeatTimeout:
                        StationTxSafetySupervisor.MinimumHeartbeatTimeout,
                    abortReason: null)
                : new(resolution.Code, resolution.Message);
            AuthorityFailure? abortFailure = resolution.Success
                ? ValidateOperation(
                    StationTxSafetyArmOperation.Abort,
                    resolution.Authority!,
                    state,
                    now,
                    heartbeatTimeout: null,
                    abortReason: "diagnostic-abort")
                : new(resolution.Code, resolution.Message);

            bool armAvailable = armFailure is null;
            bool heartbeatAvailable = heartbeatFailure is null;
            bool abortAvailable = abortFailure is null;
            string reason = armAvailable || heartbeatAvailable || abortAvailable
                ? "ready"
                : SelectReason(
                    state.Safety,
                    armFailure,
                    heartbeatFailure,
                    abortFailure);

            lock (m_gate)
            {
                return new StationTxSafetyArmAuthorityDiagnostics(
                    Registered: true,
                    state.Boundary.BoundaryRegistered,
                    state.Boundary.BoundaryEnabled,
                    state.Boundary.SignatureVerificationAvailable,
                    state.Boundary.CommandAdapterRegistered,
                    state.Adapter.ExecutorAttached,
                    state.Adapter.ExecutorRegistered,
                    state.Executor.Registered,
                    state.GateCapabilities.TransmitEnabled,
                    state.GateCapabilities.CommandTransportAvailable,
                    state.GateCapabilities.SetTransmitAvailable,
                    resolution.Success,
                    state.GateSnapshot.State.ToString(),
                    state.Safety.State.ToString(),
                    armAvailable,
                    heartbeatAvailable,
                    abortAvailable,
                    m_attemptCount,
                    m_acceptedCount,
                    m_rejectedCount,
                    m_lastOperation,
                    m_lastOutcome,
                    m_lastObservedAt,
                    reason);
            }
        }
    }

    StationTxSafetyArmAuthorityCapabilities
        IStationTxSafetyArmAuthority.Capabilities
    {
        get
        {
            StationTxSafetyArmAuthorityDiagnostics snapshot = Snapshot;
            return new StationTxSafetyArmAuthorityCapabilities(
                snapshot.Registered,
                snapshot.ArmAvailable,
                snapshot.HeartbeatAvailable,
                snapshot.AbortAvailable,
                snapshot.Reason);
        }
    }

    async Task<StationTxSafetyArmAuthorizationResult>
        IStationTxSafetyArmAuthority.AuthorizeAsync(
            StationTxSafetyArmAuthorizationRequest request,
            CancellationToken cancellationToken) =>
        await AuthorizeAsync(request, cancellationToken);

    internal Task<StationTxSafetyArmAuthorizationResult> AuthorizeAsync(
        StationTxSafetyArmAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authority);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = m_timeProvider.GetUtcNow();
        BeginAttempt(request.Operation, now);

        AuthorityFailure? requestFailure = ValidateRequest(request);
        if (requestFailure is not null)
        {
            return Task.FromResult(Reject(requestFailure, now));
        }

        AuthorityStateRead read = ReadState();
        if (!read.Success)
        {
            return Task.FromResult(Reject(read.Failure!, now));
        }

        AuthorityState state = read.State!;
        if (!state.Authority.Success)
        {
            return Task.FromResult(Reject(
                new AuthorityFailure(
                    NormalizeCode(
                        state.Authority.Code,
                        "authority_unavailable"),
                    NormalizeMessage(
                        state.Authority.Message,
                        "The current lifecycle-owned station TX authority is unavailable.")),
                now));
        }

        StationTxCommandAuthority current = state.Authority.Authority!;
        if (!AuthorityTupleMatches(request.Authority, current))
        {
            return Task.FromResult(Reject(
                new AuthorityFailure(
                    "authority_tuple_mismatch",
                    "The supplied station TX authority no longer matches the exact current lifecycle tuple."),
                now));
        }

        AuthorityFailure? failure = ValidateOperation(
            request.Operation,
            current,
            state,
            now,
            request.HeartbeatTimeout,
            request.AbortReason);
        if (failure is not null)
        {
            return Task.FromResult(Reject(failure, now));
        }

        return Task.FromResult(Accept(request.Operation, now));
    }

    private AuthorityStateRead ReadState()
    {
        try
        {
            return AuthorityStateRead.Accepted(
                new AuthorityState(
                    m_boundaryCapabilitiesProvider(),
                    m_adapterDiagnosticsProvider(),
                    m_executorCapabilitiesProvider(),
                    m_gateCapabilitiesProvider(),
                    m_gateSnapshotProvider(),
                    m_safetySnapshotProvider(),
                    m_authorityResolver(null)));
        }
        catch
        {
            return AuthorityStateRead.Rejected(
                "authority_state_unavailable",
                "The station TX safety-arm authority could not read its lifecycle-owned dependencies.");
        }
    }

    private static AuthorityFailure? ValidateRequest(
        StationTxSafetyArmAuthorizationRequest request)
    {
        if (!Enum.IsDefined(request.Operation))
        {
            return new(
                "unsupported_operation",
                "The station TX safety-arm operation is unsupported.");
        }

        if (request.Operation is StationTxSafetyArmOperation.Arm or
            StationTxSafetyArmOperation.Heartbeat)
        {
            if (!request.HeartbeatTimeout.HasValue ||
                request.HeartbeatTimeout.Value <
                    StationTxSafetySupervisor.MinimumHeartbeatTimeout ||
                request.HeartbeatTimeout.Value >
                    StationTxSafetySupervisor.MaximumHeartbeatTimeout)
            {
                return new(
                    "invalid_heartbeat_timeout",
                    "The heartbeat timeout is outside the bounded safety range.");
            }
        }
        else if (request.HeartbeatTimeout.HasValue)
        {
            return new(
                "unexpected_heartbeat_timeout",
                "Abort authorization cannot include a heartbeat timeout.");
        }

        if (request.Operation == StationTxSafetyArmOperation.Abort)
        {
            string reason = request.AbortReason?.Trim() ?? string.Empty;
            if (reason.Length is 0 or > MaximumAbortReasonLength ||
                reason.Any(char.IsControl))
            {
                return new(
                    "invalid_abort_reason",
                    "A bounded abort reason is required.");
            }
        }
        else if (request.AbortReason is not null)
        {
            return new(
                "unexpected_abort_reason",
                "Arm and heartbeat authorization cannot include an abort reason.");
        }

        return null;
    }

    private static AuthorityFailure? ValidateOperation(
        StationTxSafetyArmOperation operation,
        StationTxCommandAuthority authority,
        AuthorityState state,
        DateTimeOffset now,
        TimeSpan? heartbeatTimeout,
        string? abortReason)
    {
        AuthorityFailure? common = ValidateCommonAuthority(
            authority,
            state,
            now);
        if (common is not null)
        {
            return common;
        }

        return operation switch
        {
            StationTxSafetyArmOperation.Arm =>
                ValidateArm(authority, state),
            StationTxSafetyArmOperation.Heartbeat =>
                ValidateHeartbeat(authority, state, now),
            StationTxSafetyArmOperation.Abort =>
                ValidateAbort(authority, state),
            _ => new AuthorityFailure(
                "unsupported_operation",
                "The station TX safety-arm operation is unsupported.")
        };
    }

    private static AuthorityFailure? ValidateCommonAuthority(
        StationTxCommandAuthority authority,
        AuthorityState state,
        DateTimeOffset now)
    {
        if (authority.ClientHandle == 0)
        {
            return new(
                "client_handle_unavailable",
                "No protected FLEX client handle is available.");
        }
        if (authority.LeaseExpiresAt <= now)
        {
            return new(
                "lease_expired",
                "The lifecycle-owned TX lease has expired.");
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
                "The lifecycle-owned station TX authority is stale.");
        }
        if (!string.Equals(
                authority.RadioId,
                state.GateSnapshot.RadioId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                authority.RadioId,
                state.Safety.RadioId,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "radio_mismatch",
                "The command gate, safety supervisor, and lifecycle authority are not bound to the same radio.");
        }

        RadioTxOccupancySnapshot occupancy = authority.Occupancy;
        if (!string.Equals(
                occupancy.RadioId,
                authority.RadioId,
                StringComparison.OrdinalIgnoreCase) ||
            occupancy.ObservedAt is null ||
            occupancy.FreshUntil is null ||
            occupancy.FreshUntil <= now)
        {
            return new(
                "occupancy_stale",
                "Radio-authoritative TX occupancy is stale or mismatched.");
        }

        return null;
    }

    private static AuthorityFailure? ValidateArm(
        StationTxCommandAuthority authority,
        AuthorityState state)
    {
        AuthorityFailure? path = ValidateCommandPath(
            state,
            requireFreshArm: false);
        if (path is not null)
        {
            return path;
        }
        if (state.GateSnapshot.State != StationTxGateState.Idle ||
            state.GateSnapshot.HasActiveIntent)
        {
            return new(
                "gate_not_idle",
                "The station TX command gate must be idle before safety arming.");
        }
        if (state.Safety.State != StationTxSafetyState.Disarmed ||
            state.Safety.Active)
        {
            return new(
                "safety_not_disarmed",
                "The station TX safety supervisor must be Disarmed before arming.");
        }
        if (!authority.Occupancy.BrowserLeaseAllowed)
        {
            return new(
                "radio_not_idle",
                "Fresh idle radio-authoritative occupancy is required before arming.");
        }
        if (!authority.Occupancy.HasExclusiveLocalPttAuthority(
                authority.ClientHandle))
        {
            return new(
                "local_ptt_authority_mismatch",
                "Exclusive Local PTT authority does not match the protected FLEX handle.");
        }
        return null;
    }

    private static AuthorityFailure? ValidateHeartbeat(
        StationTxCommandAuthority authority,
        AuthorityState state,
        DateTimeOffset now)
    {
        AuthorityFailure? path = ValidateCommandPath(
            state,
            requireFreshArm: true);
        if (path is not null)
        {
            return path;
        }
        AuthorityFailure? arm = ValidateExactArm(
            authority,
            state.Safety,
            now,
            requireFreshDeadline: true);
        if (arm is not null)
        {
            return arm;
        }
        AuthorityFailure? ownership = ValidateOwnership(
            authority,
            requireIdleLocalPtt: true);
        if (ownership is not null)
        {
            return ownership;
        }
        return ValidateGateForActiveArm(
            authority,
            state.GateSnapshot,
            allowNoIntent: false);
    }

    private static AuthorityFailure? ValidateAbort(
        StationTxCommandAuthority authority,
        AuthorityState state)
    {
        AuthorityFailure? arm = ValidateExactArm(
            authority,
            state.Safety,
            DateTimeOffset.MinValue,
            requireFreshDeadline: false);
        if (arm is not null)
        {
            return arm;
        }
        AuthorityFailure? ownership = ValidateOwnership(
            authority,
            requireIdleLocalPtt: false);
        if (ownership is not null)
        {
            return ownership;
        }
        return ValidateGateForActiveArm(
            authority,
            state.GateSnapshot,
            allowNoIntent: true);
    }

    private static AuthorityFailure? ValidateCommandPath(
        AuthorityState state,
        bool requireFreshArm)
    {
        StationTxCommandCapabilities boundary = state.Boundary;
        if (!boundary.BoundaryRegistered)
        {
            return new(
                "boundary_unregistered",
                "The station-local signed command boundary is not registered.");
        }
        if (!boundary.BoundaryEnabled)
        {
            return new(
                "boundary_disabled",
                "The station-local signed command boundary is disabled.");
        }
        if (!boundary.SignatureVerificationAvailable)
        {
            return new(
                "signature_verifier_unavailable",
                "The station-local command signature verifier is unavailable.");
        }
        if (!boundary.CommandAdapterRegistered ||
            !state.Adapter.CommandAdapterRegistered)
        {
            return new(
                "adapter_unavailable",
                "The station-local command adapter is unavailable.");
        }
        if (!state.Adapter.ExecutorAttached ||
            !state.Adapter.ExecutorRegistered ||
            !state.Executor.Registered)
        {
            return new(
                "gate_executor_unavailable",
                "The station-local command gate executor is unavailable.");
        }
        if (!state.Executor.ArmingAvailable ||
            !state.Executor.SetTransmitAvailable ||
            !state.Adapter.ExecutorArmingAvailable ||
            !state.Adapter.ExecutorSetTransmitAvailable)
        {
            return new(
                "gate_executor_command_unavailable",
                "The station-local command gate executor cannot set transmit.");
        }
        if (!state.GateCapabilities.Registered)
        {
            return new(
                "gate_unregistered",
                "The station TX command gate is not registered.");
        }
        if (!state.GateCapabilities.TransmitEnabled)
        {
            return new(
                "transmit_disabled",
                "Station transmit remains disabled at the command gate.");
        }
        if (!state.GateCapabilities.CommandTransportAvailable)
        {
            return new(
                "command_transport_unavailable",
                "The station-local FLEX command transport is unavailable.");
        }
        if (!state.GateCapabilities.SetTransmitAvailable)
        {
            return new(
                "set_transmit_unavailable",
                "The command gate cannot set transmit.");
        }
        if (requireFreshArm &&
            (!boundary.ArmingAvailable ||
             !boundary.SetTransmitAvailable ||
             !state.Adapter.ArmingAvailable ||
             !state.Adapter.SetTransmitAvailable))
        {
            return new(
                "armed_command_path_unavailable",
                "The signed command path is not ready for the current safety arm.");
        }
        return null;
    }

    private static AuthorityFailure? ValidateExactArm(
        StationTxCommandAuthority authority,
        StationTxSafetySnapshot safety,
        DateTimeOffset now,
        bool requireFreshDeadline)
    {
        if (safety.State != StationTxSafetyState.Armed ||
            !safety.Active ||
            !string.Equals(
                safety.RadioId,
                authority.RadioId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                safety.EngineInstanceId,
                authority.EngineInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                safety.LeaseId,
                authority.LeaseId,
                StringComparison.Ordinal) ||
            !string.Equals(
                safety.SessionId,
                authority.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                safety.BrowserClientId,
                authority.BrowserClientId,
                StringComparison.Ordinal) ||
            safety.ProtectedClientHandle != authority.ClientHandle)
        {
            return new(
                "safety_arm_mismatch",
                "The active safety arm does not match the exact lifecycle-owned authority.");
        }
        if (requireFreshDeadline &&
            (safety.HeartbeatDeadlineAt is null ||
             safety.HeartbeatDeadlineAt <= now))
        {
            return new(
                "safety_heartbeat_expired",
                "The active safety heartbeat has expired.");
        }
        return null;
    }

    private static AuthorityFailure? ValidateOwnership(
        StationTxCommandAuthority authority,
        bool requireIdleLocalPtt)
    {
        RadioTxOccupancySnapshot occupancy = authority.Occupancy;
        if (occupancy.State == RadioTxOccupancyState.Idle)
        {
            if (!requireIdleLocalPtt ||
                occupancy.HasExclusiveLocalPttAuthority(
                    authority.ClientHandle))
            {
                return null;
            }
            return new(
                "local_ptt_authority_mismatch",
                "Exclusive Local PTT authority no longer matches the protected FLEX handle.");
        }
        if (occupancy.HasExclusiveAetherTransmitOwnership(
                authority.ClientHandle))
        {
            return null;
        }
        return new(
            "tx_ownership_mismatch",
            "Only idle state or the exact single AetherSDR TX owner may be authorized.");
    }

    private static AuthorityFailure? ValidateGateForActiveArm(
        StationTxCommandAuthority authority,
        StationTxGateSnapshot gate,
        bool allowNoIntent)
    {
        if (!gate.HasActiveIntent)
        {
            return allowNoIntent || gate.State == StationTxGateState.Idle
                ? null
                : new AuthorityFailure(
                    "gate_state_mismatch",
                    "The command gate state is incompatible with the active safety arm.");
        }
        if (gate.State == StationTxGateState.UnkeyPending)
        {
            return new(
                "gate_unkey_pending",
                "The command gate is already reconciling an unkey operation.");
        }
        if (!string.Equals(
                gate.RadioId,
                authority.RadioId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                gate.LeaseId,
                authority.LeaseId,
                StringComparison.Ordinal) ||
            !string.Equals(
                gate.SessionId,
                authority.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                gate.BrowserClientId,
                authority.BrowserClientId,
                StringComparison.Ordinal) ||
            gate.ClientHandle != authority.ClientHandle)
        {
            return new(
                "gate_identity_mismatch",
                "The active command gate intent does not match the exact safety identity.");
        }
        return gate.State is StationTxGateState.KeyPending or
            StationTxGateState.Keyed
            ? null
            : new AuthorityFailure(
                "gate_state_mismatch",
                "The command gate state is incompatible with the active safety arm.");
    }

    private static bool AuthorityTupleMatches(
        StationTxCommandAuthority supplied,
        StationTxCommandAuthority current) =>
        string.Equals(
            supplied.StationId,
            current.StationId,
            StringComparison.Ordinal) &&
        string.Equals(
            supplied.RadioId,
            current.RadioId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            supplied.SessionId,
            current.SessionId,
            StringComparison.Ordinal) &&
        string.Equals(
            supplied.BrowserClientId,
            current.BrowserClientId,
            StringComparison.Ordinal) &&
        string.Equals(
            supplied.LeaseId,
            current.LeaseId,
            StringComparison.Ordinal) &&
        supplied.LeaseExpiresAt == current.LeaseExpiresAt &&
        string.Equals(
            supplied.GatewayInstanceId,
            current.GatewayInstanceId,
            StringComparison.Ordinal) &&
        string.Equals(
            supplied.EngineInstanceId,
            current.EngineInstanceId,
            StringComparison.Ordinal) &&
        supplied.ClientHandle == current.ClientHandle &&
        supplied.Authenticated == current.Authenticated &&
        supplied.BrowserFresh == current.BrowserFresh &&
        supplied.EngineFresh == current.EngineFresh &&
        supplied.GatewayFresh == current.GatewayFresh &&
        supplied.AuthorityFresh == current.AuthorityFresh &&
        supplied.Occupancy.State == current.Occupancy.State &&
        SafetyIdentityMatches(supplied.Safety, current.Safety);

    private static bool SafetyIdentityMatches(
        StationTxSafetySnapshot supplied,
        StationTxSafetySnapshot current) =>
        supplied.State == current.State &&
        string.Equals(
            supplied.RadioId,
            current.RadioId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            supplied.EngineInstanceId,
            current.EngineInstanceId,
            StringComparison.Ordinal) &&
        string.Equals(
            supplied.LeaseId,
            current.LeaseId,
            StringComparison.Ordinal) &&
        string.Equals(
            supplied.SessionId,
            current.SessionId,
            StringComparison.Ordinal) &&
        string.Equals(
            supplied.BrowserClientId,
            current.BrowserClientId,
            StringComparison.Ordinal) &&
        supplied.ProtectedClientHandle == current.ProtectedClientHandle &&
        supplied.HeartbeatDeadlineAt == current.HeartbeatDeadlineAt;

    private static string SelectReason(
        StationTxSafetySnapshot safety,
        AuthorityFailure? arm,
        AuthorityFailure? heartbeat,
        AuthorityFailure? abort)
    {
        AuthorityFailure? selected = safety.Active
            ? abort ?? heartbeat ?? arm
            : arm ?? heartbeat ?? abort;
        return (selected?.Code ?? "operations_unavailable").Replace('_', '-');
    }

    private void BeginAttempt(
        StationTxSafetyArmOperation operation,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_attemptCount++;
            m_lastOperation = OperationName(operation);
            m_lastOutcome = "attempting";
            m_lastObservedAt = now;
        }
    }

    private StationTxSafetyArmAuthorizationResult Accept(
        StationTxSafetyArmOperation operation,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_acceptedCount++;
            m_lastOperation = OperationName(operation);
            m_lastOutcome = "authorized";
            m_lastObservedAt = now;
        }
        return StationTxSafetyArmAuthorizationResult.Accepted();
    }

    private StationTxSafetyArmAuthorizationResult Reject(
        AuthorityFailure failure,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_rejectedCount++;
            m_lastOutcome = failure.Code;
            m_lastObservedAt = now;
        }
        return StationTxSafetyArmAuthorizationResult.Rejected(
            failure.Code,
            failure.Message);
    }

    private static string OperationName(
        StationTxSafetyArmOperation operation) =>
        operation switch
        {
            StationTxSafetyArmOperation.Arm => "arm",
            StationTxSafetyArmOperation.Heartbeat => "heartbeat",
            StationTxSafetyArmOperation.Abort => "abort",
            _ => "unknown"
        };

    private static string NormalizeCode(string? value, string fallback)
    {
        string normalized = (value?.Trim() ?? string.Empty)
            .Replace('-', '_');
        return normalized.Length is > 0 and <= 64 &&
            normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '_')
            ? normalized
            : fallback;
    }

    private static string NormalizeMessage(string? value, string fallback)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 512 &&
            !normalized.Any(char.IsControl)
            ? normalized
            : fallback;
    }

    private sealed record AuthorityState(
        StationTxCommandCapabilities Boundary,
        StationTxCommandAdapterCompositionDiagnostics Adapter,
        StationTxCommandAdapterExecutorCapabilities Executor,
        StationTxCommandGateCapabilities GateCapabilities,
        StationTxGateSnapshot GateSnapshot,
        StationTxSafetySnapshot Safety,
        StationTxCommandAuthorityResolution Authority);

    private sealed record AuthorityStateRead(
        bool Success,
        AuthorityState? State,
        AuthorityFailure? Failure)
    {
        public static AuthorityStateRead Accepted(AuthorityState state) =>
            new(true, state, null);

        public static AuthorityStateRead Rejected(
            string code,
            string message) =>
            new(false, null, new AuthorityFailure(code, message));
    }

    private sealed record AuthorityFailure(string Code, string Message);
}
