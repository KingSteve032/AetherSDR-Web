namespace AetherSDR.Web.Radio;

/// <summary>
/// Per-session executor that maps one already validated SetTransmit command to
/// the existing station TX gate. It owns no FLEX transport, safety arm,
/// browser route, retry loop, or command-signing responsibility. The gate
/// remains authoritative for enablement, exact lease ownership, Local PTT,
/// radio occupancy, command outcome reconciliation, and ownership-safe unkey.
/// </summary>
internal sealed class StationTxCommandGateExecutor(
    StationTxCommandGate gate) : IStationTxCommandAdapterExecutor
{
    public StationTxCommandAdapterExecutorCapabilities Capabilities
    {
        get
        {
            StationTxCommandGateCapabilities capabilities = gate.Capabilities;
            return new StationTxCommandAdapterExecutorCapabilities(
                Registered: capabilities.Registered,
                ArmingAvailable: capabilities.SetTransmitAvailable,
                SetTransmitAvailable: capabilities.SetTransmitAvailable,
                capabilities.Reason);
        }
    }

    public async Task<StationTxTransportResult> ExecuteAsync(
        StationTxValidatedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        if (command.Action != StationTxCommandAction.SetTransmit)
        {
            return StationTxTransportResult.Rejected(
                "The station TX gate executor supports only SetTransmit.");
        }

        StationTxGateResult result = command.Enabled
            ? await gate.RequestKeyAsync(
                command.LeaseId,
                command.SessionId,
                command.BrowserClientId,
                cancellationToken)
            : await gate.RequestUnkeyAsync(
                command.LeaseId,
                command.SessionId,
                command.BrowserClientId,
                cancellationToken);

        if (result.Success)
        {
            return StationTxTransportResult.Ok;
        }

        string message = string.IsNullOrWhiteSpace(result.Message)
            ? result.Code
            : $"{result.Code}: {result.Message}";
        return IsUnknownCommandOutcome(result.Code)
            ? StationTxTransportResult.Unknown(message)
            : StationTxTransportResult.Rejected(message);
    }

    private static bool IsUnknownCommandOutcome(string code) =>
        string.Equals(
            code,
            "key_command_outcome_unknown",
            StringComparison.Ordinal) ||
        string.Equals(
            code,
            "unkey_command_outcome_unknown",
            StringComparison.Ordinal);
}
