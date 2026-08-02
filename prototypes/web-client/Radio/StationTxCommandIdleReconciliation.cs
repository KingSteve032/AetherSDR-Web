namespace AetherSDR.Web.Radio;

/// <summary>
/// Lifecycle-only cleanup participant for an independent-watchdog unkey that
/// has already been accepted and followed by fresh radio-authoritative idle.
/// It has no command transport and cannot key or unkey. It only reconciles the
/// existing command gate and local safety supervisor to their clean idle state
/// while the transaction composition holds its single-operation lock.
/// </summary>
internal sealed class StationTxCommandIdleReconciliationParticipant :
    IStationTxCommandIdleReconciliationParticipant
{
    private readonly StationTxCommandGate m_gate;
    private readonly StationTxSafetySupervisor m_supervisor;
    private readonly RadioTxOccupancyRegistry m_occupancy;
    private readonly TimeProvider m_timeProvider;

    public StationTxCommandIdleReconciliationParticipant(
        StationTxCommandGate gate,
        StationTxSafetySupervisor supervisor,
        RadioTxOccupancyRegistry occupancy,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(occupancy);
        m_gate = gate;
        m_supervisor = supervisor;
        m_occupancy = occupancy;
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<StationTxCommandIdleReconciliationResult> ReconcileAsync(
        StationTxCommandAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = m_timeProvider.GetUtcNow();
        RadioTxOccupancySnapshot occupancy =
            m_occupancy.GetSnapshot(authority.RadioId);
        StationTxGateSnapshot gateBefore = m_gate.Snapshot;
        StationTxSafetySnapshot safetyBefore = m_supervisor.Snapshot;

        if (!IsFreshIdle(occupancy, authority.RadioId, now))
        {
            return Rejected(
                "idle_reconciliation_radio_not_fresh_idle",
                "Fresh radio-authoritative idle is required before cleanup.",
                occupancy,
                gateBefore,
                safetyBefore);
        }
        if (!GateBelongsToAuthorityOrIsClean(gateBefore, authority))
        {
            return Rejected(
                "idle_reconciliation_gate_identity_mismatch",
                "The command gate does not belong to the active transaction.",
                occupancy,
                gateBefore,
                safetyBefore);
        }
        if (!SafetyBelongsToAuthorityOrIsClean(safetyBefore, authority))
        {
            return Rejected(
                "idle_reconciliation_safety_identity_mismatch",
                "The local safety arm does not belong to the active transaction.",
                occupancy,
                gateBefore,
                safetyBefore);
        }

        StationTxGateResult gateResult = await m_gate.EvaluateAsync(
            "independent-watchdog-unkey-accepted",
            cancellationToken);
        if (!GateIsClean(gateResult.Snapshot, authority.RadioId))
        {
            return Rejected(
                "idle_reconciliation_gate_not_clean",
                gateResult.Message,
                occupancy,
                gateResult.Snapshot,
                safetyBefore);
        }

        StationTxSafetyResult safetyResult =
            await m_supervisor.ResetAsync(cancellationToken);
        if (!safetyResult.Success ||
            !SafetyIsClean(safetyResult.Snapshot, authority.RadioId))
        {
            return Rejected(
                "idle_reconciliation_safety_not_clean",
                safetyResult.Message,
                occupancy,
                gateResult.Snapshot,
                safetyResult.Snapshot);
        }

        RadioTxOccupancySnapshot confirmed =
            m_occupancy.GetSnapshot(authority.RadioId);
        if (!IsFreshIdle(
                confirmed,
                authority.RadioId,
                m_timeProvider.GetUtcNow()))
        {
            return Rejected(
                "idle_reconciliation_radio_idle_lost",
                "Radio-authoritative idle was lost during cleanup.",
                confirmed,
                gateResult.Snapshot,
                safetyResult.Snapshot);
        }

        return new StationTxCommandIdleReconciliationResult(
            Success: true,
            Code: "idle_reconciliation_complete",
            Message: "Fresh radio idle cleared the exact command intent and local safety arm without another radio command.",
            confirmed,
            gateResult.Snapshot,
            safetyResult.Snapshot);
    }

    private static bool GateBelongsToAuthorityOrIsClean(
        StationTxGateSnapshot gate,
        StationTxCommandAuthority authority)
    {
        if (!string.Equals(
                gate.RadioId,
                authority.RadioId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!gate.HasActiveIntent)
        {
            return GateIsClean(gate, authority.RadioId);
        }
        return string.Equals(
                   gate.LeaseId,
                   authority.LeaseId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   gate.SessionId,
                   authority.SessionId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   gate.BrowserClientId,
                   authority.BrowserClientId,
                   StringComparison.Ordinal) &&
               gate.ClientHandle == authority.ClientHandle;
    }

    private static bool SafetyBelongsToAuthorityOrIsClean(
        StationTxSafetySnapshot safety,
        StationTxCommandAuthority authority)
    {
        if (!string.Equals(
                safety.RadioId,
                authority.RadioId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!safety.Active)
        {
            return SafetyIsClean(safety, authority.RadioId);
        }
        return string.Equals(
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

    private static bool GateIsClean(
        StationTxGateSnapshot gate,
        string radioId) =>
        string.Equals(
            gate.RadioId,
            radioId,
            StringComparison.OrdinalIgnoreCase) &&
        gate.State == StationTxGateState.Idle &&
        !gate.HasActiveIntent &&
        gate.LeaseId is null &&
        gate.SessionId is null &&
        gate.BrowserClientId is null &&
        gate.ClientHandle == 0;

    private static bool SafetyIsClean(
        StationTxSafetySnapshot safety,
        string radioId) =>
        string.Equals(
            safety.RadioId,
            radioId,
            StringComparison.OrdinalIgnoreCase) &&
        safety.State == StationTxSafetyState.Disarmed &&
        !safety.Active &&
        safety.EngineInstanceId is null &&
        safety.LeaseId is null &&
        safety.SessionId is null &&
        safety.BrowserClientId is null &&
        safety.ProtectedClientHandle == 0;

    private static bool IsFreshIdle(
        RadioTxOccupancySnapshot occupancy,
        string radioId,
        DateTimeOffset now) =>
        string.Equals(
            occupancy.RadioId,
            radioId,
            StringComparison.OrdinalIgnoreCase) &&
        occupancy.State == RadioTxOccupancyState.Idle &&
        occupancy.ObservedAt is not null &&
        occupancy.FreshUntil is not null &&
        occupancy.FreshUntil > now;

    private static StationTxCommandIdleReconciliationResult Rejected(
        string code,
        string message,
        RadioTxOccupancySnapshot occupancy,
        StationTxGateSnapshot gate,
        StationTxSafetySnapshot safety) =>
        new(
            Success: false,
            code,
            message,
            occupancy,
            gate,
            safety);
}
