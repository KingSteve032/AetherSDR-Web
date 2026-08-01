namespace AetherSDR.Web.Radio;

public sealed record StationTxSafetyArmCompositionDiagnostics(
    bool Registered,
    bool ArmAuthorityAttached,
    bool ArmAuthorityRegistered,
    bool ArmAuthorityArmAvailable,
    bool ArmAuthorityHeartbeatAvailable,
    bool ArmAuthorityAbortAvailable,
    bool SessionAuthoritySnapshotAvailable,
    bool ArmAvailable,
    bool HeartbeatAvailable,
    bool AbortAvailable,
    long AttemptCount,
    long ForwardedCount,
    long AcceptedCount,
    long RejectedCount,
    string LastOperation,
    string LastOutcome,
    DateTimeOffset? LastObservedAt,
    string Reason);

internal enum StationTxSafetyArmOperation
{
    Arm = 1,
    Heartbeat = 2,
    Abort = 3
}

internal sealed record StationTxSafetyArmAuthorityCapabilities(
    bool Registered,
    bool ArmAvailable,
    bool HeartbeatAvailable,
    bool AbortAvailable,
    string Reason);

internal sealed record StationTxSafetyArmAuthorizationRequest(
    StationTxSafetyArmOperation Operation,
    StationTxCommandAuthority Authority,
    TimeSpan? HeartbeatTimeout,
    string? AbortReason);

internal sealed record StationTxSafetyArmAuthorizationResult(
    bool Success,
    string Code,
    string Message)
{
    public static StationTxSafetyArmAuthorizationResult Accepted() =>
        new(
            Success: true,
            Code: "authorized",
            Message: "The exact station TX safety operation is authorized.");

    public static StationTxSafetyArmAuthorizationResult Rejected(
        string code,
        string message) =>
        new(
            Success: false,
            code,
            message);
}

/// <summary>
/// Optional station-local authority for supervisor arm operations. An
/// implementation may authorize only the exact lifecycle-owned authority
/// supplied by the composition. It must not infer identity from browser data or
/// expose a browser, HTTP, WebSocket, AetherRemote, timer, reconnect, or retry
/// route.
/// </summary>
internal interface IStationTxSafetyArmAuthority
{
    StationTxSafetyArmAuthorityCapabilities Capabilities { get; }

    Task<StationTxSafetyArmAuthorizationResult> AuthorizeAsync(
        StationTxSafetyArmAuthorizationRequest request,
        CancellationToken cancellationToken);
}

internal sealed record StationTxSafetyArmCompositionArmRequest(
    string ConnectionClientId,
    TimeSpan HeartbeatTimeout);

internal sealed record StationTxSafetyArmCompositionHeartbeatRequest(
    string ConnectionClientId,
    TimeSpan HeartbeatTimeout);

internal sealed record StationTxSafetyArmCompositionAbortRequest(
    string ConnectionClientId,
    string Reason);

internal sealed record StationTxSafetyArmCompositionResult(
    bool Success,
    string Code,
    string Message,
    StationTxSafetyArmCompositionDiagnostics Diagnostics,
    StationTxSafetyResult? SafetyResult);

internal interface IStationTxSafetyArmTransactionParticipant
{
    StationTxSafetyArmCompositionDiagnostics Snapshot { get; }

    Task<StationTxSafetyArmCompositionResult> ArmAsync(
        StationTxSafetyArmCompositionArmRequest request,
        CancellationToken cancellationToken = default);

    Task<StationTxSafetyArmCompositionResult> HeartbeatAsync(
        StationTxSafetyArmCompositionHeartbeatRequest request,
        CancellationToken cancellationToken = default);

    Task<StationTxSafetyArmCompositionResult> AbortAsync(
        StationTxSafetyArmCompositionAbortRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-session composition around the existing unkey-only safety supervisor.
/// A caller can provide only the current connection identity plus a bounded
/// heartbeat timeout or abort reason. Every radio, session, browser, lease,
/// engine, and FLEX-handle field is re-resolved from lifecycle-owned state.
///
/// Production attaches the lifecycle-owned Phase 2O authority, but the signed
/// boundary, command gate, and transports remain disabled and no operation
/// caller exists. The composition therefore remains observable but cannot arm,
/// heartbeat, or abort the supervisor. It owns no command transport and
/// performs no automatic call or retry.
/// </summary>
internal sealed class StationTxSafetyArmComposition :
    IStationTxSafetyArmTransactionParticipant
{
    private const int MaximumConnectionIdLength = 128;
    private const int MaximumAbortReasonLength = 64;

    private readonly object m_gate = new();
    private readonly IStationTxSafetyArmAuthority? m_armAuthority;
    private readonly StationTxSafetySupervisor m_supervisor;
    private readonly Func<string?, StationTxCommandAuthorityResolution>
        m_authorityResolver;
    private readonly TimeProvider m_timeProvider;
    private long m_attemptCount;
    private long m_forwardedCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private string m_lastOperation = "none";
    private string m_lastOutcome = "none";
    private DateTimeOffset? m_lastObservedAt;

    public StationTxSafetyArmComposition(
        IStationTxSafetyArmAuthority? armAuthority,
        StationTxSafetySupervisor supervisor,
        Func<string?, StationTxCommandAuthorityResolution> authorityResolver,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(authorityResolver);
        m_armAuthority = armAuthority;
        m_supervisor = supervisor;
        m_authorityResolver = authorityResolver;
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public StationTxSafetyArmCompositionDiagnostics Snapshot
    {
        get
        {
            StationTxSafetyArmAuthorityCapabilities? armAuthority =
                GetAuthorityCapabilities();
            StationTxCommandAuthorityResolution authority =
                ResolveAuthority(connectionClientId: null);
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            bool registered = armAuthority?.Registered == true;
            bool armAvailable = registered &&
                armAuthority!.ArmAvailable &&
                authority.Success &&
                ValidateArmAuthority(authority.Authority!, now) is null;
            bool heartbeatAvailable = registered &&
                armAuthority!.HeartbeatAvailable &&
                authority.Success &&
                ValidateHeartbeatAuthority(authority.Authority!, now) is null;
            bool abortAvailable = registered &&
                armAuthority!.AbortAvailable &&
                authority.Success &&
                ValidateAbortAuthority(authority.Authority!, now) is null;
            string reason = GetReason(
                armAuthority,
                authority,
                armAvailable,
                heartbeatAvailable,
                abortAvailable,
                now);

            lock (m_gate)
            {
                return new StationTxSafetyArmCompositionDiagnostics(
                    Registered: true,
                    ArmAuthorityAttached: armAuthority is not null,
                    ArmAuthorityRegistered: registered,
                    ArmAuthorityArmAvailable:
                        armAuthority?.ArmAvailable == true,
                    ArmAuthorityHeartbeatAvailable:
                        armAuthority?.HeartbeatAvailable == true,
                    ArmAuthorityAbortAvailable:
                        armAuthority?.AbortAvailable == true,
                    SessionAuthoritySnapshotAvailable: authority.Success,
                    ArmAvailable: armAvailable,
                    HeartbeatAvailable: heartbeatAvailable,
                    AbortAvailable: abortAvailable,
                    m_attemptCount,
                    m_forwardedCount,
                    m_acceptedCount,
                    m_rejectedCount,
                    m_lastOperation,
                    m_lastOutcome,
                    m_lastObservedAt,
                    reason);
            }
        }
    }

    Task<StationTxSafetyArmCompositionResult>
        IStationTxSafetyArmTransactionParticipant.ArmAsync(
            StationTxSafetyArmCompositionArmRequest request,
            CancellationToken cancellationToken) =>
        ArmAsync(request, cancellationToken);

    Task<StationTxSafetyArmCompositionResult>
        IStationTxSafetyArmTransactionParticipant.HeartbeatAsync(
            StationTxSafetyArmCompositionHeartbeatRequest request,
            CancellationToken cancellationToken) =>
        HeartbeatAsync(request, cancellationToken);

    Task<StationTxSafetyArmCompositionResult>
        IStationTxSafetyArmTransactionParticipant.AbortAsync(
            StationTxSafetyArmCompositionAbortRequest request,
            CancellationToken cancellationToken) =>
        AbortAsync(request, cancellationToken);

    internal Task<StationTxSafetyArmCompositionResult> ArmAsync(
        StationTxSafetyArmCompositionArmRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            StationTxSafetyArmOperation.Arm,
            request?.ConnectionClientId,
            request?.HeartbeatTimeout,
            abortReason: null,
            cancellationToken);

    internal Task<StationTxSafetyArmCompositionResult> HeartbeatAsync(
        StationTxSafetyArmCompositionHeartbeatRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            StationTxSafetyArmOperation.Heartbeat,
            request?.ConnectionClientId,
            request?.HeartbeatTimeout,
            abortReason: null,
            cancellationToken);

    internal Task<StationTxSafetyArmCompositionResult> AbortAsync(
        StationTxSafetyArmCompositionAbortRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            StationTxSafetyArmOperation.Abort,
            request?.ConnectionClientId,
            heartbeatTimeout: null,
            request?.Reason,
            cancellationToken);

    private async Task<StationTxSafetyArmCompositionResult> ExecuteAsync(
        StationTxSafetyArmOperation operation,
        string? connectionClientId,
        TimeSpan? heartbeatTimeout,
        string? abortReason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        BeginAttempt(operation, now);

        CompositionFailure? failure = ValidateRequest(
            operation,
            connectionClientId,
            heartbeatTimeout,
            abortReason);
        StationTxSafetyArmAuthorityCapabilities? capabilities =
            GetAuthorityCapabilities();
        if (failure is null && capabilities is null)
        {
            failure = new(
                "arm_authority_unattached",
                "No station-local safety arm authority is attached.");
        }
        if (failure is null && !capabilities!.Registered)
        {
            failure = new(
                "arm_authority_unregistered",
                "The station-local safety arm authority is not registered.");
        }
        if (failure is null && !OperationAvailable(capabilities!, operation))
        {
            failure = new(
                $"arm_authority_{OperationName(operation)}_unavailable",
                "The station-local safety arm authority does not permit this operation.");
        }

        StationTxCommandAuthorityResolution? authority = null;
        if (failure is null)
        {
            authority = ResolveAuthority(connectionClientId);
            if (!authority.Success)
            {
                failure = new(authority.Code, authority.Message);
            }
        }
        if (failure is null)
        {
            failure = operation switch
            {
                StationTxSafetyArmOperation.Arm =>
                    ValidateArmAuthority(authority!.Authority!, now),
                StationTxSafetyArmOperation.Heartbeat =>
                    ValidateHeartbeatAuthority(authority!.Authority!, now),
                StationTxSafetyArmOperation.Abort =>
                    ValidateAbortAuthority(authority!.Authority!, now),
                _ => new(
                    "unsupported_operation",
                    "The station TX safety operation is unsupported.")
            };
        }
        if (failure is not null)
        {
            return Reject(failure, now);
        }

        StationTxSafetyArmAuthorizationResult authorization;
        try
        {
            authorization = await m_armAuthority!.AuthorizeAsync(
                new StationTxSafetyArmAuthorizationRequest(
                    operation,
                    authority!.Authority!,
                    heartbeatTimeout,
                    abortReason),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RecordException("cancelled", now);
            throw;
        }
        catch
        {
            RecordException("arm-authority-exception", now);
            throw;
        }

        if (!authorization.Success)
        {
            return Reject(
                new(
                    NormalizeCode(authorization.Code, "authorization_rejected"),
                    NormalizeMessage(
                        authorization.Message,
                        "The station TX safety operation was not authorized.")),
                now);
        }

        RecordForwarded(now);
        StationTxSafetyResult safetyResult;
        try
        {
            StationTxCommandAuthority exact = authority!.Authority!;
            safetyResult = operation switch
            {
                StationTxSafetyArmOperation.Arm =>
                    await m_supervisor.ArmAsync(
                        new StationTxSafetyArm(
                            exact.EngineInstanceId,
                            exact.LeaseId,
                            exact.SessionId,
                            exact.BrowserClientId,
                            exact.ClientHandle,
                            heartbeatTimeout!.Value),
                        cancellationToken),
                StationTxSafetyArmOperation.Heartbeat =>
                    await m_supervisor.HeartbeatAsync(
                        exact.EngineInstanceId,
                        exact.LeaseId,
                        exact.ClientHandle,
                        heartbeatTimeout!.Value,
                        cancellationToken),
                StationTxSafetyArmOperation.Abort =>
                    await m_supervisor.AbortAsync(
                        abortReason!,
                        cancellationToken),
                _ => throw new InvalidOperationException(
                    "Unsupported station TX safety operation.")
            };
        }
        catch (OperationCanceledException)
        {
            RecordException("cancelled", now);
            throw;
        }
        catch
        {
            RecordException("supervisor-exception", now);
            throw;
        }

        return Complete(safetyResult, now);
    }

    private StationTxSafetyArmAuthorityCapabilities?
        GetAuthorityCapabilities()
    {
        try
        {
            return m_armAuthority?.Capabilities;
        }
        catch
        {
            return new StationTxSafetyArmAuthorityCapabilities(
                Registered: false,
                ArmAvailable: false,
                HeartbeatAvailable: false,
                AbortAvailable: false,
                Reason: "arm-authority-capabilities-faulted");
        }
    }

    private StationTxCommandAuthorityResolution ResolveAuthority(
        string? connectionClientId)
    {
        try
        {
            return m_authorityResolver(connectionClientId);
        }
        catch
        {
            return StationTxCommandAuthorityResolution.Rejected(
                "authority_resolution_failed",
                "The session-owned station command authority could not be resolved.");
        }
    }

    private CompositionFailure? ValidateArmAuthority(
        StationTxCommandAuthority authority,
        DateTimeOffset now)
    {
        CompositionFailure? common = ValidateCommonAuthority(authority, now);
        if (common is not null)
        {
            return common;
        }

        RadioTxOccupancySnapshot occupancy = authority.Occupancy;
        if (!occupancy.BrowserLeaseAllowed)
        {
            return new(
                "radio_not_idle",
                "Fresh idle radio-authoritative TX occupancy is required before arming.");
        }
        if (!occupancy.HasExclusiveLocalPttAuthority(authority.ClientHandle))
        {
            return new(
                "local_ptt_authority_mismatch",
                "Exclusive Local PTT authority does not match the protected FLEX handle.");
        }

        StationTxSafetySnapshot safety = m_supervisor.Snapshot;
        if (safety.State != StationTxSafetyState.Disarmed || safety.Active)
        {
            return new(
                "safety_not_disarmed",
                "The station TX safety supervisor must be Disarmed before arming.");
        }
        if (!string.Equals(
                safety.RadioId,
                authority.RadioId,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "safety_radio_mismatch",
                "The safety supervisor is bound to a different radio.");
        }
        return null;
    }

    private CompositionFailure? ValidateHeartbeatAuthority(
        StationTxCommandAuthority authority,
        DateTimeOffset now)
    {
        CompositionFailure? common = ValidateCommonAuthority(authority, now);
        if (common is not null)
        {
            return common;
        }
        CompositionFailure? ownership = ValidateActiveOwnership(
            authority,
            allowIdle: true,
            requireIdleLocalPtt: true);
        if (ownership is not null)
        {
            return ownership;
        }
        return ValidateExactArm(authority, now, requireFreshDeadline: true);
    }

    private CompositionFailure? ValidateAbortAuthority(
        StationTxCommandAuthority authority,
        DateTimeOffset now)
    {
        CompositionFailure? common = ValidateCommonAuthority(authority, now);
        if (common is not null)
        {
            return common;
        }
        CompositionFailure? ownership = ValidateActiveOwnership(
            authority,
            allowIdle: true,
            requireIdleLocalPtt: false);
        if (ownership is not null)
        {
            return ownership;
        }
        return ValidateExactArm(authority, now, requireFreshDeadline: false);
    }

    private CompositionFailure? ValidateCommonAuthority(
        StationTxCommandAuthority authority,
        DateTimeOffset now)
    {
        StationTxSafetySnapshot safety = m_supervisor.Snapshot;
        if (!string.Equals(
                authority.RadioId,
                safety.RadioId,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "radio_mismatch",
                "The lifecycle authority does not match the safety supervisor radio.");
        }
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
                "The current TX lease has expired.");
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

    private static CompositionFailure? ValidateActiveOwnership(
        StationTxCommandAuthority authority,
        bool allowIdle,
        bool requireIdleLocalPtt)
    {
        RadioTxOccupancySnapshot occupancy = authority.Occupancy;
        if (allowIdle && occupancy.State == RadioTxOccupancyState.Idle)
        {
            if (!requireIdleLocalPtt ||
                occupancy.HasExclusiveLocalPttAuthority(authority.ClientHandle))
            {
                return null;
            }
            return new(
                "local_ptt_authority_mismatch",
                "Exclusive Local PTT authority no longer matches the protected FLEX handle.");
        }
        if (occupancy.HasExclusiveAetherTransmitOwnership(authority.ClientHandle))
        {
            return null;
        }
        return new(
            "tx_ownership_mismatch",
            "Only idle state or the exact single AetherSDR TX owner may use the safety arm composition.");
    }

    private CompositionFailure? ValidateExactArm(
        StationTxCommandAuthority authority,
        DateTimeOffset now,
        bool requireFreshDeadline)
    {
        StationTxSafetySnapshot safety = m_supervisor.Snapshot;
        if (safety.State != StationTxSafetyState.Armed ||
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

    private static CompositionFailure? ValidateRequest(
        StationTxSafetyArmOperation operation,
        string? connectionClientId,
        TimeSpan? heartbeatTimeout,
        string? abortReason)
    {
        string connection = connectionClientId?.Trim() ?? string.Empty;
        if (connection.Length is 0 or > MaximumConnectionIdLength ||
            connection.Any(char.IsControl))
        {
            return new(
                "invalid_connection_client_id",
                "The live browser connection identity is invalid.");
        }

        if (operation is StationTxSafetyArmOperation.Arm or
            StationTxSafetyArmOperation.Heartbeat)
        {
            if (!heartbeatTimeout.HasValue ||
                heartbeatTimeout.Value <
                    StationTxSafetySupervisor.MinimumHeartbeatTimeout ||
                heartbeatTimeout.Value >
                    StationTxSafetySupervisor.MaximumHeartbeatTimeout)
            {
                return new(
                    "invalid_heartbeat_timeout",
                    "The heartbeat timeout is outside the bounded safety range.");
            }
        }
        if (operation == StationTxSafetyArmOperation.Abort)
        {
            string reason = abortReason?.Trim() ?? string.Empty;
            if (reason.Length is 0 or > MaximumAbortReasonLength ||
                reason.Any(char.IsControl))
            {
                return new(
                    "invalid_abort_reason",
                    "A bounded abort reason is required.");
            }
        }
        return null;
    }

    private static bool OperationAvailable(
        StationTxSafetyArmAuthorityCapabilities capabilities,
        StationTxSafetyArmOperation operation) =>
        operation switch
        {
            StationTxSafetyArmOperation.Arm => capabilities.ArmAvailable,
            StationTxSafetyArmOperation.Heartbeat =>
                capabilities.HeartbeatAvailable,
            StationTxSafetyArmOperation.Abort => capabilities.AbortAvailable,
            _ => false
        };

    private static string GetReason(
        StationTxSafetyArmAuthorityCapabilities? armAuthority,
        StationTxCommandAuthorityResolution authority,
        bool armAvailable,
        bool heartbeatAvailable,
        bool abortAvailable,
        DateTimeOffset now)
    {
        if (armAuthority is null)
        {
            return "arm-authority-unattached";
        }
        if (!armAuthority.Registered)
        {
            return string.IsNullOrWhiteSpace(armAuthority.Reason)
                ? "arm-authority-unregistered"
                : armAuthority.Reason;
        }
        if (!authority.Success)
        {
            return authority.Code;
        }
        if (armAvailable || heartbeatAvailable || abortAvailable)
        {
            return "ready";
        }
        if (armAuthority.ArmAvailable)
        {
            CompositionFailure? failure =
                ValidateStaticArmAuthority(authority.Authority!, now);
            if (failure is not null)
            {
                return failure.Code.Replace('_', '-');
            }
        }
        if (!armAuthority.ArmAvailable &&
            !armAuthority.HeartbeatAvailable &&
            !armAuthority.AbortAvailable)
        {
            return string.IsNullOrWhiteSpace(armAuthority.Reason)
                ? "arm-authority-operations-unavailable"
                : armAuthority.Reason;
        }
        return "safety-operation-unavailable";
    }

    private static CompositionFailure? ValidateStaticArmAuthority(
        StationTxCommandAuthority authority,
        DateTimeOffset now)
    {
        if (authority.LeaseExpiresAt <= now)
        {
            return new("lease_expired", string.Empty);
        }
        if (!authority.Authenticated ||
            !authority.BrowserFresh ||
            !authority.EngineFresh ||
            !authority.GatewayFresh ||
            !authority.AuthorityFresh)
        {
            return new("authority_stale", string.Empty);
        }
        RadioTxOccupancySnapshot occupancy = authority.Occupancy;
        if (occupancy.FreshUntil is null || occupancy.FreshUntil <= now)
        {
            return new("occupancy_stale", string.Empty);
        }
        if (!occupancy.BrowserLeaseAllowed)
        {
            return new("radio_not_idle", string.Empty);
        }
        if (!occupancy.HasExclusiveLocalPttAuthority(authority.ClientHandle))
        {
            return new("local_ptt_authority_mismatch", string.Empty);
        }
        return null;
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

    private void RecordForwarded(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_forwardedCount++;
            m_lastOutcome = "forwarded";
            m_lastObservedAt = now;
        }
    }

    private void RecordException(string outcome, DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_rejectedCount++;
            m_lastOutcome = outcome;
            m_lastObservedAt = now;
        }
    }

    private StationTxSafetyArmCompositionResult Reject(
        CompositionFailure failure,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_rejectedCount++;
            m_lastOutcome = failure.Code;
            m_lastObservedAt = now;
        }
        return new StationTxSafetyArmCompositionResult(
            Success: false,
            failure.Code,
            failure.Message,
            Snapshot,
            SafetyResult: null);
    }

    private StationTxSafetyArmCompositionResult Complete(
        StationTxSafetyResult safetyResult,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            if (safetyResult.Success)
            {
                m_acceptedCount++;
            }
            else
            {
                m_rejectedCount++;
            }
            m_lastOutcome = safetyResult.Code;
            m_lastObservedAt = now;
        }
        return new StationTxSafetyArmCompositionResult(
            safetyResult.Success,
            safetyResult.Code,
            safetyResult.Message,
            Snapshot,
            safetyResult);
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
        string normalized = value?.Trim() ?? string.Empty;
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

    private sealed record CompositionFailure(string Code, string Message);
}
