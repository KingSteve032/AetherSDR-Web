namespace AetherSDR.Web.Radio;

public sealed record StationTxCommandTransactionCompositionDiagnostics(
    bool Registered,
    bool SafetyArmCompositionAttached,
    bool CommandSessionCompositionAttached,
    bool AuthoritySnapshotAvailable,
    bool KeyAvailable,
    bool HeartbeatAvailable,
    bool UnkeyAvailable,
    bool AbortAvailable,
    bool Active,
    bool ReconciliationRequired,
    string State,
    long AttemptCount,
    long ArmForwardedCount,
    long CommandForwardedCount,
    long HeartbeatForwardedCount,
    long CleanupForwardedCount,
    long AcceptedCount,
    long RejectedCount,
    long UnknownCount,
    string LastOperation,
    string LastOutcome,
    DateTimeOffset? LastObservedAt,
    string Reason);

internal enum StationTxCommandTransactionOutcome
{
    Accepted = 1,
    Rejected = 2,
    Unknown = 3
}

internal enum StationTxCommandTransactionState
{
    Idle = 1,
    Arming = 2,
    SubmittingKey = 3,
    Armed = 4,
    Heartbeating = 5,
    SubmittingUnkey = 6,
    Aborting = 7,
    Reconciling = 8
}

internal sealed record StationTxCommandTransactionRequest(
    string ConnectionClientId,
    long Sequence,
    BrowserTxIntent Intent,
    DateTimeOffset ObservedAt,
    TimeSpan HeartbeatTimeout);

internal sealed record StationTxCommandTransactionHeartbeatRequest(
    string ConnectionClientId,
    TimeSpan HeartbeatTimeout);

internal sealed record StationTxCommandTransactionAbortRequest(
    string ConnectionClientId,
    string Reason);

internal sealed record StationTxCommandTransactionResult(
    StationTxCommandTransactionOutcome Outcome,
    string Code,
    string Message,
    StationTxCommandTransactionCompositionDiagnostics Diagnostics,
    StationTxSafetyArmCompositionResult? ArmResult,
    StationTxCommandSessionCompositionResult? CommandResult,
    StationTxSafetyArmCompositionResult? CleanupResult)
{
    public bool Success =>
        Outcome == StationTxCommandTransactionOutcome.Accepted;

    public bool OutcomeKnown =>
        Outcome != StationTxCommandTransactionOutcome.Unknown;
}

/// <summary>
/// Per-session transaction composition that joins the existing safety-arm and
/// signed-command compositions without adding a browser, HTTP, WebSocket,
/// AetherRemote, watchdog, reconnect, timer, or retry caller.
///
/// A key transaction resolves current lifecycle authority, arms once, verifies
/// that the stable ownership tuple did not change, and submits one command. A
/// known key rejection clears the matching arm. An unknown key outcome retains
/// the arm and requires reconciliation. An unkey transaction first refreshes
/// the exact arm, submits once, and clears the arm only after confirmed command
/// acceptance. No command or safety operation is retried automatically.
/// </summary>
internal sealed class StationTxCommandTransactionComposition
{
    private const int MaximumConnectionIdLength = 128;
    private const int MaximumAbortReasonLength = 64;

    private readonly object m_gate = new();
    private readonly SemaphoreSlim m_operationGate = new(1, 1);
    private readonly IStationTxSafetyArmTransactionParticipant?
        m_safetyArmComposition;
    private readonly IStationTxCommandTransactionSubmissionParticipant?
        m_commandComposition;
    private readonly Func<string?, StationTxCommandAuthorityResolution>
        m_authorityResolver;
    private readonly TimeProvider m_timeProvider;

    private StationTxCommandTransactionState m_state =
        StationTxCommandTransactionState.Idle;
    private ActiveTransaction? m_active;
    private long m_attemptCount;
    private long m_armForwardedCount;
    private long m_commandForwardedCount;
    private long m_heartbeatForwardedCount;
    private long m_cleanupForwardedCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private long m_unknownCount;
    private string m_lastOperation = "none";
    private string m_lastOutcome = "none";
    private DateTimeOffset? m_lastObservedAt;

    public StationTxCommandTransactionComposition(
        IStationTxSafetyArmTransactionParticipant? safetyArmComposition,
        IStationTxCommandTransactionSubmissionParticipant? commandComposition,
        Func<string?, StationTxCommandAuthorityResolution> authorityResolver,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(authorityResolver);
        m_safetyArmComposition = safetyArmComposition;
        m_commandComposition = commandComposition;
        m_authorityResolver = authorityResolver;
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public StationTxCommandTransactionCompositionDiagnostics Snapshot
    {
        get
        {
            StationTxSafetyArmCompositionDiagnostics? safety =
                GetSafetySnapshot();
            StationTxCommandSessionCompositionDiagnostics? command =
                GetCommandSnapshot();
            StationTxCommandAuthorityResolution authority =
                ResolveAuthority(connectionClientId: null);

            ActiveTransaction? active;
            StationTxCommandTransactionState state;
            long attemptCount;
            long armForwardedCount;
            long commandForwardedCount;
            long heartbeatForwardedCount;
            long cleanupForwardedCount;
            long acceptedCount;
            long rejectedCount;
            long unknownCount;
            string lastOperation;
            string lastOutcome;
            DateTimeOffset? lastObservedAt;
            lock (m_gate)
            {
                active = m_active;
                state = m_state;
                attemptCount = m_attemptCount;
                armForwardedCount = m_armForwardedCount;
                commandForwardedCount = m_commandForwardedCount;
                heartbeatForwardedCount = m_heartbeatForwardedCount;
                cleanupForwardedCount = m_cleanupForwardedCount;
                acceptedCount = m_acceptedCount;
                rejectedCount = m_rejectedCount;
                unknownCount = m_unknownCount;
                lastOperation = m_lastOperation;
                lastOutcome = m_lastOutcome;
                lastObservedAt = m_lastObservedAt;
            }

            bool commandPrepared = CommandPreparedForArm(command);
            bool keyAvailable =
                active is null &&
                safety?.ArmAvailable == true &&
                commandPrepared &&
                authority.Success;
            bool heartbeatAvailable =
                active is not null &&
                safety?.HeartbeatAvailable == true &&
                authority.Success;
            bool unkeyAvailable =
                active is not null &&
                command?.SubmissionAvailable == true &&
                authority.Success;
            bool abortAvailable =
                active is not null &&
                safety?.AbortAvailable == true &&
                authority.Success;
            string reason = GetReason(
                safety,
                command,
                authority,
                active,
                keyAvailable,
                heartbeatAvailable,
                unkeyAvailable,
                abortAvailable);

            return new StationTxCommandTransactionCompositionDiagnostics(
                Registered: true,
                SafetyArmCompositionAttached: safety is not null,
                CommandSessionCompositionAttached: command is not null,
                AuthoritySnapshotAvailable: authority.Success,
                KeyAvailable: keyAvailable,
                HeartbeatAvailable: heartbeatAvailable,
                UnkeyAvailable: unkeyAvailable,
                AbortAvailable: abortAvailable,
                Active: active is not null,
                ReconciliationRequired:
                    active?.ReconciliationRequired == true,
                State: StateName(state),
                attemptCount,
                armForwardedCount,
                commandForwardedCount,
                heartbeatForwardedCount,
                cleanupForwardedCount,
                acceptedCount,
                rejectedCount,
                unknownCount,
                lastOperation,
                lastOutcome,
                lastObservedAt,
                reason);
        }
    }

    internal async Task<StationTxCommandTransactionResult> SubmitAsync(
        StationTxCommandTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Intent);
        cancellationToken.ThrowIfCancellationRequested();

        await m_operationGate.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            string operation = request.Intent.Enabled == false
                ? "unkey"
                : "key";
            BeginAttempt(operation, now);

            TransactionFailure? failure = ValidateSubmitRequest(request);
            if (failure is not null)
            {
                return Finish(
                    StationTxCommandTransactionOutcome.Rejected,
                    failure.Code,
                    failure.Message,
                    now);
            }

            return request.Intent.Enabled!.Value
                ? await ExecuteKeyAsync(request, now, cancellationToken)
                : await ExecuteUnkeyAsync(request, now, cancellationToken);
        }
        finally
        {
            m_operationGate.Release();
        }
    }

    internal async Task<StationTxCommandTransactionResult> HeartbeatAsync(
        StationTxCommandTransactionHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        await m_operationGate.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            BeginAttempt("heartbeat", now);
            TransactionFailure? failure = ValidateConnectionAndTimeout(
                request.ConnectionClientId,
                request.HeartbeatTimeout);
            if (failure is not null)
            {
                return Finish(
                    StationTxCommandTransactionOutcome.Rejected,
                    failure.Code,
                    failure.Message,
                    now);
            }

            ActiveTransaction? active = GetActive();
            failure = ValidateActiveConnection(active, request.ConnectionClientId);
            if (failure is not null)
            {
                return Finish(
                    StationTxCommandTransactionOutcome.Rejected,
                    failure.Code,
                    failure.Message,
                    now);
            }
            if (m_safetyArmComposition is null)
            {
                return Finish(
                    StationTxCommandTransactionOutcome.Rejected,
                    "safety_arm_composition_unattached",
                    "No station TX safety-arm composition is attached.",
                    now);
            }

            SetState(StationTxCommandTransactionState.Heartbeating, now);
            RecordHeartbeatForwarded(now);
            StationTxSafetyArmCompositionResult result;
            try
            {
                result = await m_safetyArmComposition.HeartbeatAsync(
                    new StationTxSafetyArmCompositionHeartbeatRequest(
                        request.ConnectionClientId,
                        request.HeartbeatTimeout),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                MarkReconciliationRequired("heartbeat-cancelled", now);
                RecordExceptionUnknown("heartbeat-cancelled", now);
                throw;
            }
            catch
            {
                MarkReconciliationRequired("heartbeat-exception", now);
                RecordExceptionUnknown("heartbeat-exception", now);
                throw;
            }

            if (!result.Success)
            {
                MarkReconciliationRequired(result.Code, now);
                return Finish(
                    StationTxCommandTransactionOutcome.Rejected,
                    result.Code,
                    result.Message,
                    now,
                    armResult: result);
            }

            RefreshActiveAuthority(request.ConnectionClientId);
            RestoreActiveState(now);
            return Finish(
                StationTxCommandTransactionOutcome.Accepted,
                "heartbeat_accepted",
                "The exact station TX safety heartbeat was accepted.",
                now,
                armResult: result);
        }
        finally
        {
            m_operationGate.Release();
        }
    }

    internal async Task<StationTxCommandTransactionResult> AbortAsync(
        StationTxCommandTransactionAbortRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        await m_operationGate.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            BeginAttempt("abort", now);
            TransactionFailure? failure = ValidateAbortRequest(request);
            if (failure is not null)
            {
                return Finish(
                    StationTxCommandTransactionOutcome.Rejected,
                    failure.Code,
                    failure.Message,
                    now);
            }

            ActiveTransaction? active = GetActive();
            failure = ValidateActiveConnection(active, request.ConnectionClientId);
            if (failure is not null)
            {
                return Finish(
                    StationTxCommandTransactionOutcome.Rejected,
                    failure.Code,
                    failure.Message,
                    now);
            }
            if (m_safetyArmComposition is null)
            {
                return Finish(
                    StationTxCommandTransactionOutcome.Rejected,
                    "safety_arm_composition_unattached",
                    "No station TX safety-arm composition is attached.",
                    now);
            }

            SetState(StationTxCommandTransactionState.Aborting, now);
            RecordCleanupForwarded(now);
            StationTxSafetyArmCompositionResult result;
            try
            {
                result = await m_safetyArmComposition.AbortAsync(
                    new StationTxSafetyArmCompositionAbortRequest(
                        request.ConnectionClientId,
                        request.Reason),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                MarkReconciliationRequired("abort-cancelled", now);
                RecordExceptionUnknown("abort-cancelled", now);
                throw;
            }
            catch
            {
                MarkReconciliationRequired("abort-exception", now);
                RecordExceptionUnknown("abort-exception", now);
                throw;
            }

            if (!result.Success)
            {
                MarkReconciliationRequired(result.Code, now);
                return Finish(
                    StationTxCommandTransactionOutcome.Rejected,
                    result.Code,
                    result.Message,
                    now,
                    cleanupResult: result);
            }

            ClearActive(now);
            return Finish(
                StationTxCommandTransactionOutcome.Accepted,
                "abort_accepted",
                "The exact station TX safety arm was cleared.",
                now,
                cleanupResult: result);
        }
        finally
        {
            m_operationGate.Release();
        }
    }

    private async Task<StationTxCommandTransactionResult> ExecuteKeyAsync(
        StationTxCommandTransactionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (GetActive() is not null)
        {
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                "transaction_active",
                "An exact station TX command transaction is already active.",
                now);
        }
        if (m_safetyArmComposition is null)
        {
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                "safety_arm_composition_unattached",
                "No station TX safety-arm composition is attached.",
                now);
        }
        if (m_commandComposition is null)
        {
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                "command_composition_unattached",
                "No station TX command session composition is attached.",
                now);
        }

        StationTxSafetyArmCompositionDiagnostics safetySnapshot =
            m_safetyArmComposition.Snapshot;
        StationTxCommandSessionCompositionDiagnostics commandSnapshot =
            m_commandComposition.Snapshot;
        TransactionFailure? preparationFailure =
            ValidateKeyPreparation(safetySnapshot, commandSnapshot);
        if (preparationFailure is not null)
        {
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                preparationFailure.Code,
                preparationFailure.Message,
                now);
        }

        StationTxCommandAuthorityResolution initial =
            ResolveAuthority(request.ConnectionClientId);
        if (!initial.Success)
        {
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                initial.Code,
                initial.Message,
                now);
        }

        SetProvisionalActive(
            request,
            initial.Authority!,
            StationTxCommandTransactionState.Arming,
            now);
        RecordArmForwarded(now);
        StationTxSafetyArmCompositionResult armResult;
        try
        {
            armResult = await m_safetyArmComposition.ArmAsync(
                new StationTxSafetyArmCompositionArmRequest(
                    request.ConnectionClientId,
                    request.HeartbeatTimeout),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            MarkReconciliationRequired("arm-cancelled", now);
            RecordExceptionUnknown("arm-cancelled", now);
            throw;
        }
        catch
        {
            MarkReconciliationRequired("arm-exception", now);
            RecordExceptionUnknown("arm-exception", now);
            throw;
        }

        if (!armResult.Success)
        {
            ClearActive(now);
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                armResult.Code,
                armResult.Message,
                now,
                armResult: armResult);
        }

        StationTxCommandAuthorityResolution armed =
            ResolveAuthority(request.ConnectionClientId);
        TransactionFailure? armedFailure =
            ValidateArmedAuthority(initial.Authority!, armed);
        if (armedFailure is not null)
        {
            CleanupAttempt cleanup = await TryCleanupAsync(
                request.ConnectionClientId,
                "transaction-authority-changed",
                now);
            return FinishKnownFailureAfterCleanup(
                armedFailure,
                now,
                armResult,
                cleanup);
        }

        UpdateActiveAuthority(armed.Authority!, now);
        commandSnapshot = m_commandComposition.Snapshot;
        if (!commandSnapshot.SubmissionAvailable)
        {
            CleanupAttempt cleanup = await TryCleanupAsync(
                request.ConnectionClientId,
                "transaction-command-unavailable",
                now);
            return FinishKnownFailureAfterCleanup(
                new TransactionFailure(
                    NormalizeReasonCode(
                        commandSnapshot.Reason,
                        "command_path_unavailable_after_arm"),
                    "The signed command path is unavailable after safety arming."),
                now,
                armResult,
                cleanup);
        }

        SetState(StationTxCommandTransactionState.SubmittingKey, now);
        RecordCommandForwarded(now);
        StationTxCommandSessionCompositionResult commandResult;
        try
        {
            commandResult = await m_commandComposition.SubmitAsync(
                ToCommandRequest(request),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            MarkReconciliationRequired("key-submission-cancelled", now);
            RecordExceptionUnknown("key-submission-cancelled", now);
            throw;
        }
        catch
        {
            MarkReconciliationRequired("key-submission-exception", now);
            RecordExceptionUnknown("key-submission-exception", now);
            throw;
        }

        if (commandResult.Success)
        {
            RestoreActiveState(now);
            return Finish(
                StationTxCommandTransactionOutcome.Accepted,
                "key_accepted",
                "The signed station TX key command was accepted once.",
                now,
                armResult,
                commandResult);
        }
        if (CommandOutcomeUnknown(commandResult))
        {
            MarkReconciliationRequired(commandResult.Code, now);
            return Finish(
                StationTxCommandTransactionOutcome.Unknown,
                commandResult.Code,
                commandResult.Message,
                now,
                armResult,
                commandResult);
        }

        CleanupAttempt rejectionCleanup = await TryCleanupAsync(
            request.ConnectionClientId,
            "transaction-key-rejected",
            now);
        return FinishKnownCommandRejection(
            commandResult,
            now,
            armResult,
            rejectionCleanup);
    }

    private async Task<StationTxCommandTransactionResult> ExecuteUnkeyAsync(
        StationTxCommandTransactionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ActiveTransaction? active = GetActive();
        TransactionFailure? failure =
            ValidateActiveConnection(active, request.ConnectionClientId);
        if (failure is not null)
        {
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                failure.Code,
                failure.Message,
                now);
        }
        if (m_safetyArmComposition is null)
        {
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                "safety_arm_composition_unattached",
                "No station TX safety-arm composition is attached.",
                now);
        }
        if (m_commandComposition is null)
        {
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                "command_composition_unattached",
                "No station TX command session composition is attached.",
                now);
        }

        StationTxCommandAuthorityResolution current =
            ResolveAuthority(request.ConnectionClientId);
        failure = ValidateActiveAuthority(active!, current);
        if (failure is not null)
        {
            MarkReconciliationRequired(failure.Code, now);
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                failure.Code,
                failure.Message,
                now);
        }

        SetState(StationTxCommandTransactionState.Heartbeating, now);
        RecordHeartbeatForwarded(now);
        StationTxSafetyArmCompositionResult heartbeatResult;
        try
        {
            heartbeatResult = await m_safetyArmComposition.HeartbeatAsync(
                new StationTxSafetyArmCompositionHeartbeatRequest(
                    request.ConnectionClientId,
                    request.HeartbeatTimeout),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            MarkReconciliationRequired("unkey-heartbeat-cancelled", now);
            RecordExceptionUnknown("unkey-heartbeat-cancelled", now);
            throw;
        }
        catch
        {
            MarkReconciliationRequired("unkey-heartbeat-exception", now);
            RecordExceptionUnknown("unkey-heartbeat-exception", now);
            throw;
        }
        if (!heartbeatResult.Success)
        {
            MarkReconciliationRequired(heartbeatResult.Code, now);
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                heartbeatResult.Code,
                heartbeatResult.Message,
                now,
                armResult: heartbeatResult);
        }

        StationTxCommandSessionCompositionDiagnostics commandSnapshot =
            m_commandComposition.Snapshot;
        if (!commandSnapshot.SubmissionAvailable)
        {
            RestoreActiveState(now);
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                NormalizeReasonCode(
                    commandSnapshot.Reason,
                    "command_path_unavailable"),
                "The signed unkey command path is unavailable.",
                now,
                armResult: heartbeatResult);
        }

        SetState(StationTxCommandTransactionState.SubmittingUnkey, now);
        RecordCommandForwarded(now);
        StationTxCommandSessionCompositionResult commandResult;
        try
        {
            commandResult = await m_commandComposition.SubmitAsync(
                ToCommandRequest(request),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            MarkReconciliationRequired("unkey-submission-cancelled", now);
            RecordExceptionUnknown("unkey-submission-cancelled", now);
            throw;
        }
        catch
        {
            MarkReconciliationRequired("unkey-submission-exception", now);
            RecordExceptionUnknown("unkey-submission-exception", now);
            throw;
        }

        if (!commandResult.Success)
        {
            if (CommandOutcomeUnknown(commandResult))
            {
                MarkReconciliationRequired(commandResult.Code, now);
                return Finish(
                    StationTxCommandTransactionOutcome.Unknown,
                    commandResult.Code,
                    commandResult.Message,
                    now,
                    heartbeatResult,
                    commandResult);
            }

            RestoreActiveState(now);
            return Finish(
                StationTxCommandTransactionOutcome.Rejected,
                commandResult.Code,
                commandResult.Message,
                now,
                heartbeatResult,
                commandResult);
        }

        CleanupAttempt cleanup = await TryCleanupAsync(
            request.ConnectionClientId,
            "transaction-unkey-confirmed",
            now);
        if (!cleanup.Success)
        {
            MarkReconciliationRequired(cleanup.Code, now);
            return Finish(
                StationTxCommandTransactionOutcome.Unknown,
                cleanup.Code,
                cleanup.Message,
                now,
                heartbeatResult,
                commandResult,
                cleanup.Result);
        }

        return Finish(
            StationTxCommandTransactionOutcome.Accepted,
            "unkey_accepted",
            "The signed station TX unkey command was accepted and the matching safety arm was cleared.",
            now,
            heartbeatResult,
            commandResult,
            cleanup.Result);
    }

    private async Task<CleanupAttempt> TryCleanupAsync(
        string connectionClientId,
        string reason,
        DateTimeOffset now)
    {
        if (m_safetyArmComposition is null)
        {
            MarkReconciliationRequired(
                "safety_arm_composition_unattached",
                now);
            return new CleanupAttempt(
                Success: false,
                Code: "safety_arm_composition_unattached",
                Message: "No station TX safety-arm composition is attached for cleanup.",
                Result: null);
        }

        SetState(StationTxCommandTransactionState.Aborting, now);
        RecordCleanupForwarded(now);
        try
        {
            StationTxSafetyArmCompositionResult result =
                await m_safetyArmComposition.AbortAsync(
                    new StationTxSafetyArmCompositionAbortRequest(
                        connectionClientId,
                        reason),
                    CancellationToken.None);
            if (result.Success)
            {
                ClearActive(now);
                return new CleanupAttempt(
                    Success: true,
                    result.Code,
                    result.Message,
                    result);
            }

            MarkReconciliationRequired(result.Code, now);
            return new CleanupAttempt(
                Success: false,
                result.Code,
                result.Message,
                result);
        }
        catch
        {
            MarkReconciliationRequired("cleanup-outcome-unknown", now);
            return new CleanupAttempt(
                Success: false,
                Code: "cleanup_outcome_unknown",
                Message: "The matching safety-arm cleanup outcome is unknown.",
                Result: null);
        }
    }

    private StationTxCommandTransactionResult FinishKnownFailureAfterCleanup(
        TransactionFailure failure,
        DateTimeOffset now,
        StationTxSafetyArmCompositionResult armResult,
        CleanupAttempt cleanup)
    {
        string message = cleanup.Success
            ? failure.Message
            : $"{failure.Message} Safety-arm cleanup requires reconciliation: {cleanup.Message}";
        return Finish(
            StationTxCommandTransactionOutcome.Rejected,
            failure.Code,
            message,
            now,
            armResult,
            cleanupResult: cleanup.Result);
    }

    private StationTxCommandTransactionResult FinishKnownCommandRejection(
        StationTxCommandSessionCompositionResult commandResult,
        DateTimeOffset now,
        StationTxSafetyArmCompositionResult armResult,
        CleanupAttempt cleanup)
    {
        string message = cleanup.Success
            ? commandResult.Message
            : $"{commandResult.Message} Safety-arm cleanup requires reconciliation: {cleanup.Message}";
        return Finish(
            StationTxCommandTransactionOutcome.Rejected,
            commandResult.Code,
            message,
            now,
            armResult,
            commandResult,
            cleanup.Result);
    }

    private StationTxCommandTransactionResult Finish(
        StationTxCommandTransactionOutcome outcome,
        string code,
        string message,
        DateTimeOffset now,
        StationTxSafetyArmCompositionResult? armResult = null,
        StationTxCommandSessionCompositionResult? commandResult = null,
        StationTxSafetyArmCompositionResult? cleanupResult = null)
    {
        lock (m_gate)
        {
            switch (outcome)
            {
                case StationTxCommandTransactionOutcome.Accepted:
                    m_acceptedCount = checked(m_acceptedCount + 1);
                    break;
                case StationTxCommandTransactionOutcome.Rejected:
                    m_rejectedCount = checked(m_rejectedCount + 1);
                    break;
                case StationTxCommandTransactionOutcome.Unknown:
                    m_unknownCount = checked(m_unknownCount + 1);
                    break;
            }
            m_lastOutcome = NormalizeOutcome(code);
            m_lastObservedAt = now;
        }

        return new StationTxCommandTransactionResult(
            outcome,
            code,
            message,
            Snapshot,
            armResult,
            commandResult,
            cleanupResult);
    }

    private static TransactionFailure? ValidateSubmitRequest(
        StationTxCommandTransactionRequest request)
    {
        TransactionFailure? connection = ValidateConnectionAndTimeout(
            request.ConnectionClientId,
            request.HeartbeatTimeout);
        if (connection is not null)
        {
            return connection;
        }
        if (request.Sequence <= 0 ||
            request.Sequence > RadioBrowserTxProtocol.MaximumSafeInteger)
        {
            return new(
                "invalid_intent_sequence",
                "The validated browser intent sequence is invalid.");
        }
        if (request.Intent.Kind is not BrowserTxIntentKind.Mox and
            not BrowserTxIntentKind.Ptt)
        {
            return new(
                "unsupported_intent",
                "Only deliberate MOX or PTT intent can enter a station TX transaction.");
        }
        if (!request.Intent.Enabled.HasValue)
        {
            return new(
                "missing_intent_value",
                "The deliberate MOX or PTT intent requires a Boolean value.");
        }
        return null;
    }

    private static TransactionFailure? ValidateConnectionAndTimeout(
        string? connectionClientId,
        TimeSpan heartbeatTimeout)
    {
        string connection = connectionClientId?.Trim() ?? string.Empty;
        if (connection.Length is 0 or > MaximumConnectionIdLength ||
            connection.Any(char.IsControl))
        {
            return new(
                "invalid_connection_client_id",
                "The live browser connection identity is invalid.");
        }
        if (heartbeatTimeout <
                StationTxSafetySupervisor.MinimumHeartbeatTimeout ||
            heartbeatTimeout >
                StationTxSafetySupervisor.MaximumHeartbeatTimeout)
        {
            return new(
                "invalid_heartbeat_timeout",
                "The heartbeat timeout is outside the bounded safety range.");
        }
        return null;
    }

    private static TransactionFailure? ValidateAbortRequest(
        StationTxCommandTransactionAbortRequest request)
    {
        string connection = request.ConnectionClientId?.Trim() ?? string.Empty;
        if (connection.Length is 0 or > MaximumConnectionIdLength ||
            connection.Any(char.IsControl))
        {
            return new(
                "invalid_connection_client_id",
                "The live browser connection identity is invalid.");
        }
        string reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > MaximumAbortReasonLength ||
            reason.Any(char.IsControl))
        {
            return new(
                "invalid_abort_reason",
                "A bounded transaction abort reason is required.");
        }
        return null;
    }

    private static TransactionFailure? ValidateActiveConnection(
        ActiveTransaction? active,
        string connectionClientId)
    {
        if (active is null)
        {
            return new(
                "transaction_inactive",
                "No exact station TX command transaction is active.");
        }
        if (!string.Equals(
                active.ConnectionClientId,
                connectionClientId.Trim(),
                StringComparison.Ordinal))
        {
            return new(
                "connection_mismatch",
                "The active station TX command transaction belongs to a different connection.");
        }
        return null;
    }

    private static TransactionFailure? ValidateKeyPreparation(
        StationTxSafetyArmCompositionDiagnostics safety,
        StationTxCommandSessionCompositionDiagnostics command)
    {
        if (!CommandPreparedForArm(command))
        {
            return new(
                NormalizeReasonCode(
                    CommandPreparationReason(command),
                    "command_path_unavailable"),
                "The signed command path is not prepared for a key transaction.");
        }
        if (!safety.ArmAvailable)
        {
            return new(
                NormalizeReasonCode(safety.Reason, "safety_arm_unavailable"),
                "The exact station TX safety arm is unavailable.");
        }
        return null;
    }

    private static TransactionFailure? ValidateArmedAuthority(
        StationTxCommandAuthority initial,
        StationTxCommandAuthorityResolution armed)
    {
        if (!armed.Success)
        {
            return new(armed.Code, armed.Message);
        }
        StationTxCommandAuthority current = armed.Authority!;
        if (!StableAuthorityTupleMatches(initial, current))
        {
            return new(
                "authority_changed_before_submit",
                "The lifecycle-owned station TX authority changed after arming and before command submission.");
        }
        if (!SafetyIdentityMatchesAuthority(current))
        {
            return new(
                "safety_arm_mismatch_before_submit",
                "The active safety arm does not match the exact lifecycle-owned command authority.");
        }
        return null;
    }

    private static TransactionFailure? ValidateActiveAuthority(
        ActiveTransaction active,
        StationTxCommandAuthorityResolution current)
    {
        if (!current.Success)
        {
            return new(current.Code, current.Message);
        }
        if (!StableAuthorityTupleMatches(active.Authority, current.Authority!))
        {
            return new(
                "active_authority_changed",
                "The lifecycle-owned authority no longer matches the active station TX transaction.");
        }
        if (!SafetyIdentityMatchesAuthority(current.Authority!))
        {
            return new(
                "active_safety_arm_mismatch",
                "The current safety arm no longer matches the active station TX transaction.");
        }
        return null;
    }

    private static bool StableAuthorityTupleMatches(
        StationTxCommandAuthority expected,
        StationTxCommandAuthority current) =>
        string.Equals(
            expected.StationId,
            current.StationId,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.RadioId,
            current.RadioId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            expected.SessionId,
            current.SessionId,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.BrowserClientId,
            current.BrowserClientId,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.LeaseId,
            current.LeaseId,
            StringComparison.Ordinal) &&
        expected.LeaseExpiresAt == current.LeaseExpiresAt &&
        string.Equals(
            expected.GatewayInstanceId,
            current.GatewayInstanceId,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.EngineInstanceId,
            current.EngineInstanceId,
            StringComparison.Ordinal) &&
        expected.ClientHandle == current.ClientHandle &&
        expected.Authenticated == current.Authenticated &&
        expected.BrowserFresh == current.BrowserFresh &&
        expected.EngineFresh == current.EngineFresh &&
        expected.GatewayFresh == current.GatewayFresh &&
        expected.AuthorityFresh == current.AuthorityFresh &&
        string.Equals(
            expected.Occupancy.RadioId,
            current.Occupancy.RadioId,
            StringComparison.OrdinalIgnoreCase);

    private static bool SafetyIdentityMatchesAuthority(
        StationTxCommandAuthority authority)
    {
        StationTxSafetySnapshot safety = authority.Safety;
        return safety.State == StationTxSafetyState.Armed &&
            safety.Active &&
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
            safety.ProtectedClientHandle == authority.ClientHandle;
    }

    private static bool CommandPreparedForArm(
        StationTxCommandSessionCompositionDiagnostics? command) =>
        command is not null &&
        command.CoordinatorAttached &&
        command.BoundaryAttached &&
        command.SubmissionEnabled &&
        command.SigningAvailable &&
        command.SignatureVerificationAvailable &&
        command.BoundaryEnabled &&
        command.BoundarySignatureVerificationAvailable &&
        command.CommandAdapterRegistered;

    private static string CommandPreparationReason(
        StationTxCommandSessionCompositionDiagnostics command)
    {
        if (!command.CoordinatorAttached)
        {
            return "coordinator-unattached";
        }
        if (!command.BoundaryAttached)
        {
            return "boundary-unattached";
        }
        if (!command.SubmissionEnabled)
        {
            return "submission-disabled";
        }
        if (!command.SigningAvailable)
        {
            return "signer-unavailable";
        }
        if (!command.SignatureVerificationAvailable)
        {
            return "signature-verifier-unavailable";
        }
        if (!command.BoundaryEnabled)
        {
            return "boundary-disabled";
        }
        if (!command.BoundarySignatureVerificationAvailable)
        {
            return "boundary-signature-verifier-unavailable";
        }
        return command.CommandAdapterRegistered
            ? "ready-for-arm"
            : "adapter-unavailable";
    }

    private static bool CommandOutcomeUnknown(
        StationTxCommandSessionCompositionResult result) =>
        string.Equals(
            result.Code,
            "adapter_outcome_unknown",
            StringComparison.Ordinal) ||
        string.Equals(
            result.CoordinatorResult?.BoundaryResult?.Code,
            "adapter_outcome_unknown",
            StringComparison.Ordinal);

    private static StationTxCommandSessionCompositionRequest ToCommandRequest(
        StationTxCommandTransactionRequest request) =>
        new(
            request.ConnectionClientId,
            request.Sequence,
            request.Intent,
            request.ObservedAt);

    private StationTxSafetyArmCompositionDiagnostics? GetSafetySnapshot()
    {
        try
        {
            return m_safetyArmComposition?.Snapshot;
        }
        catch
        {
            return null;
        }
    }

    private StationTxCommandSessionCompositionDiagnostics? GetCommandSnapshot()
    {
        try
        {
            return m_commandComposition?.Snapshot;
        }
        catch
        {
            return null;
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

    private static string GetReason(
        StationTxSafetyArmCompositionDiagnostics? safety,
        StationTxCommandSessionCompositionDiagnostics? command,
        StationTxCommandAuthorityResolution authority,
        ActiveTransaction? active,
        bool keyAvailable,
        bool heartbeatAvailable,
        bool unkeyAvailable,
        bool abortAvailable)
    {
        if (safety is null)
        {
            return "safety-arm-composition-unattached";
        }
        if (command is null)
        {
            return "command-composition-unattached";
        }
        if (active?.ReconciliationRequired == true)
        {
            return "reconciliation-required";
        }
        if (active is not null)
        {
            if (heartbeatAvailable || unkeyAvailable || abortAvailable)
            {
                return "active";
            }
            if (!authority.Success)
            {
                return authority.Code;
            }
            if (!safety.HeartbeatAvailable && !safety.AbortAvailable)
            {
                return safety.Reason;
            }
            return command.Reason;
        }
        if (keyAvailable)
        {
            return "ready";
        }
        if (!CommandPreparedForArm(command))
        {
            return CommandPreparationReason(command);
        }
        if (!safety.ArmAvailable)
        {
            return safety.Reason;
        }
        return authority.Success ? "transaction-unavailable" : authority.Code;
    }

    private ActiveTransaction? GetActive()
    {
        lock (m_gate)
        {
            return m_active;
        }
    }

    private void SetProvisionalActive(
        StationTxCommandTransactionRequest request,
        StationTxCommandAuthority authority,
        StationTxCommandTransactionState state,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_active = new ActiveTransaction(
                request.ConnectionClientId.Trim(),
                authority,
                request.Intent.IntentId,
                request.Sequence,
                request.HeartbeatTimeout,
                ReconciliationRequired: false);
            m_state = state;
            m_lastObservedAt = now;
        }
    }

    private void UpdateActiveAuthority(
        StationTxCommandAuthority authority,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            if (m_active is not null)
            {
                m_active = m_active with { Authority = authority };
            }
            m_state = StationTxCommandTransactionState.Armed;
            m_lastObservedAt = now;
        }
    }

    private void RefreshActiveAuthority(string connectionClientId)
    {
        StationTxCommandAuthorityResolution authority =
            ResolveAuthority(connectionClientId);
        if (!authority.Success)
        {
            return;
        }
        lock (m_gate)
        {
            if (m_active is not null &&
                StableAuthorityTupleMatches(
                    m_active.Authority,
                    authority.Authority!))
            {
                m_active = m_active with { Authority = authority.Authority! };
            }
        }
    }

    private void MarkReconciliationRequired(string outcome, DateTimeOffset now)
    {
        lock (m_gate)
        {
            if (m_active is not null)
            {
                m_active = m_active with { ReconciliationRequired = true };
            }
            m_state = StationTxCommandTransactionState.Reconciling;
            m_lastOutcome = NormalizeOutcome(outcome);
            m_lastObservedAt = now;
        }
    }

    private void RestoreActiveState(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_state = m_active?.ReconciliationRequired == true
                ? StationTxCommandTransactionState.Reconciling
                : m_active is null
                    ? StationTxCommandTransactionState.Idle
                    : StationTxCommandTransactionState.Armed;
            m_lastObservedAt = now;
        }
    }

    private void ClearActive(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_active = null;
            m_state = StationTxCommandTransactionState.Idle;
            m_lastObservedAt = now;
        }
    }

    private void SetState(
        StationTxCommandTransactionState state,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_state = state;
            m_lastObservedAt = now;
        }
    }

    private void BeginAttempt(string operation, DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_attemptCount = checked(m_attemptCount + 1);
            m_lastOperation = operation;
            m_lastOutcome = "attempting";
            m_lastObservedAt = now;
        }
    }

    private void RecordArmForwarded(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_armForwardedCount = checked(m_armForwardedCount + 1);
            m_lastOutcome = "arm-forwarded";
            m_lastObservedAt = now;
        }
    }

    private void RecordCommandForwarded(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_commandForwardedCount = checked(m_commandForwardedCount + 1);
            m_lastOutcome = "command-forwarded";
            m_lastObservedAt = now;
        }
    }

    private void RecordHeartbeatForwarded(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_heartbeatForwardedCount =
                checked(m_heartbeatForwardedCount + 1);
            m_lastOutcome = "heartbeat-forwarded";
            m_lastObservedAt = now;
        }
    }

    private void RecordCleanupForwarded(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_cleanupForwardedCount = checked(m_cleanupForwardedCount + 1);
            m_lastOutcome = "cleanup-forwarded";
            m_lastObservedAt = now;
        }
    }

    private void RecordExceptionUnknown(string outcome, DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_unknownCount = checked(m_unknownCount + 1);
            m_lastOutcome = NormalizeOutcome(outcome);
            m_lastObservedAt = now;
        }
    }

    private static string NormalizeReasonCode(string? value, string fallback)
    {
        string normalized = (value ?? string.Empty)
            .Trim()
            .Replace('-', '_');
        return normalized.Length is > 0 and <= 64 &&
            normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '_')
            ? normalized
            : fallback;
    }

    private static string NormalizeOutcome(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 96 &&
            !normalized.Any(char.IsControl)
            ? normalized
            : "unknown";
    }

    private static string StateName(
        StationTxCommandTransactionState state) =>
        state switch
        {
            StationTxCommandTransactionState.Idle => "idle",
            StationTxCommandTransactionState.Arming => "arming",
            StationTxCommandTransactionState.SubmittingKey => "submitting-key",
            StationTxCommandTransactionState.Armed => "armed",
            StationTxCommandTransactionState.Heartbeating => "heartbeating",
            StationTxCommandTransactionState.SubmittingUnkey =>
                "submitting-unkey",
            StationTxCommandTransactionState.Aborting => "aborting",
            StationTxCommandTransactionState.Reconciling => "reconciling",
            _ => "unknown"
        };

    private sealed record ActiveTransaction(
        string ConnectionClientId,
        StationTxCommandAuthority Authority,
        string KeyIntentId,
        long KeySequence,
        TimeSpan HeartbeatTimeout,
        bool ReconciliationRequired);

    private sealed record TransactionFailure(string Code, string Message);

    private sealed record CleanupAttempt(
        bool Success,
        string Code,
        string Message,
        StationTxSafetyArmCompositionResult? Result);
}
