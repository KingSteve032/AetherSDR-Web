namespace AetherSDR.Web.Radio;

public sealed record StationTxProductionReadinessDiagnostics(
    bool Registered,
    bool Ready,
    string Reason,
    bool AllowTransmitConfigured,
    bool BrowserTxLeaseConfigured,
    bool CommandCoordinatorAttached,
    bool CommandSubmissionEnabled,
    bool SigningAvailable,
    bool SignatureVerificationAvailable,
    bool CommandBoundaryEnabled,
    bool CommandAdapterRegistered,
    bool GateTransmitEnabled,
    bool CommandTransportAvailable,
    bool SetTransmitAvailable,
    bool EmergencyUnkeyTransportAvailable,
    bool SafetyArmAuthorityRegistered,
    bool WatchdogSupervisionEnabled,
    bool WatchdogProcessRunning,
    bool WatchdogIpcConnected,
    bool WatchdogCommandTransportAvailable,
    bool WatchdogArmingAvailable,
    IReadOnlyList<string> MissingPrerequisites);

internal sealed record StationTxProductionReadinessConfiguration(
    bool AllowTransmitConfigured,
    bool BrowserTxLeaseConfigured)
{
    public static readonly StationTxProductionReadinessConfiguration Disabled =
        new(
            AllowTransmitConfigured: false,
            BrowserTxLeaseConfigured: false);
}

internal sealed record StationTxProductionReadinessInputs(
    bool AllowTransmitConfigured,
    bool BrowserTxLeaseConfigured,
    bool CommandCoordinatorAttached,
    bool CommandSubmissionEnabled,
    bool SigningAvailable,
    bool SignatureVerificationAvailable,
    bool CommandBoundaryEnabled,
    bool CommandAdapterRegistered,
    bool GateTransmitEnabled,
    bool CommandTransportAvailable,
    bool SetTransmitAvailable,
    bool EmergencyUnkeyTransportAvailable,
    bool SafetyArmAuthorityRegistered,
    bool WatchdogSupervisionEnabled,
    bool WatchdogProcessRunning,
    bool WatchdogIpcConnected,
    bool WatchdogCommandTransportAvailable,
    bool WatchdogArmingAvailable);

/// <summary>
/// One server-owned readiness decision for activating production browser TX.
/// It does not authorize an operator action, hold a lease, call the transaction
/// ingress, or own a radio transport. It reports every missing infrastructure
/// prerequisite in deterministic order so later reviewed integration cannot
/// infer readiness from a browser control or one partial capability.
/// </summary>
internal static class StationTxProductionReadinessPolicy
{
    public static StationTxProductionReadinessDiagnostics Evaluate(
        StationTxProductionReadinessInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        List<string> missing = [];
        AddMissing(
            missing,
            inputs.AllowTransmitConfigured,
            "transmit-disabled");
        AddMissing(
            missing,
            inputs.BrowserTxLeaseConfigured,
            "browser-tx-lease-disabled");
        AddMissing(
            missing,
            inputs.CommandCoordinatorAttached,
            "command-coordinator-unattached");
        AddMissing(
            missing,
            inputs.CommandSubmissionEnabled,
            "command-submission-disabled");
        AddMissing(
            missing,
            inputs.SigningAvailable,
            "command-signing-unavailable");
        AddMissing(
            missing,
            inputs.SignatureVerificationAvailable,
            "command-verification-unavailable");
        AddMissing(
            missing,
            inputs.CommandBoundaryEnabled,
            "command-boundary-disabled");
        AddMissing(
            missing,
            inputs.CommandAdapterRegistered,
            "command-adapter-unregistered");
        AddMissing(
            missing,
            inputs.GateTransmitEnabled,
            "command-gate-transmit-disabled");
        AddMissing(
            missing,
            inputs.CommandTransportAvailable,
            "command-transport-unavailable");
        AddMissing(
            missing,
            inputs.SetTransmitAvailable,
            "set-transmit-unavailable");
        AddMissing(
            missing,
            inputs.EmergencyUnkeyTransportAvailable,
            "emergency-unkey-transport-unavailable");
        AddMissing(
            missing,
            inputs.SafetyArmAuthorityRegistered,
            "safety-arm-authority-unregistered");
        AddMissing(
            missing,
            inputs.WatchdogSupervisionEnabled,
            "watchdog-supervision-disabled");
        AddMissing(
            missing,
            inputs.WatchdogProcessRunning,
            "watchdog-process-unavailable");
        AddMissing(
            missing,
            inputs.WatchdogIpcConnected,
            "watchdog-ipc-unavailable");
        AddMissing(
            missing,
            inputs.WatchdogCommandTransportAvailable,
            "watchdog-unkey-transport-unavailable");
        AddMissing(
            missing,
            inputs.WatchdogArmingAvailable,
            "watchdog-arming-unavailable");

        bool ready = missing.Count == 0;
        string[] missingSnapshot = [.. missing];
        return new StationTxProductionReadinessDiagnostics(
            Registered: true,
            ready,
            Reason: ready ? "ready" : missingSnapshot[0],
            inputs.AllowTransmitConfigured,
            inputs.BrowserTxLeaseConfigured,
            inputs.CommandCoordinatorAttached,
            inputs.CommandSubmissionEnabled,
            inputs.SigningAvailable,
            inputs.SignatureVerificationAvailable,
            inputs.CommandBoundaryEnabled,
            inputs.CommandAdapterRegistered,
            inputs.GateTransmitEnabled,
            inputs.CommandTransportAvailable,
            inputs.SetTransmitAvailable,
            inputs.EmergencyUnkeyTransportAvailable,
            inputs.SafetyArmAuthorityRegistered,
            inputs.WatchdogSupervisionEnabled,
            inputs.WatchdogProcessRunning,
            inputs.WatchdogIpcConnected,
            inputs.WatchdogCommandTransportAvailable,
            inputs.WatchdogArmingAvailable,
            missingSnapshot);
    }

    private static void AddMissing(
        ICollection<string> missing,
        bool available,
        string code)
    {
        if (!available)
        {
            missing.Add(code);
        }
    }
}
