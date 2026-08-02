namespace AetherSDR.Web.Radio;

internal sealed record StationTxCommandRadioConfirmationResult(
    bool Success,
    bool OutcomeKnown,
    string Code,
    string Message);

internal interface IStationTxCommandRadioConfirmationParticipant
{
    Task<StationTxCommandRadioConfirmationResult> ConfirmAsync(
        bool enabled,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded radio-authoritative confirmation barrier for the production command
/// gate. It sends no command. It only asks the existing gate to reconcile its
/// pending intent against fresh occupancy until keyed/idle is confirmed or the
/// gate's existing two-second confirmation window expires.
/// </summary>
internal sealed class StationTxCommandGateRadioConfirmation :
    IStationTxCommandRadioConfirmationParticipant
{
    internal static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(25);

    private readonly StationTxCommandGate m_gate;
    private readonly TimeProvider m_timeProvider;

    public StationTxCommandGateRadioConfirmation(
        StationTxCommandGate gate,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        m_gate = gate;
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<StationTxCommandRadioConfirmationResult> ConfirmAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        TimeSpan confirmationWindow = enabled
            ? StationTxCommandGate.KeyConfirmationTimeout
            : StationTxCommandGate.UnkeyConfirmationTimeout;
        DateTimeOffset deadline =
            m_timeProvider.GetUtcNow() + confirmationWindow;
        string reason = enabled
            ? "transaction-key-confirmation"
            : "transaction-unkey-confirmation";

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StationTxGateResult evaluation =
                await m_gate.EvaluateAsync(reason, cancellationToken);
            StationTxGateSnapshot snapshot = evaluation.Snapshot;

            if (enabled && snapshot.State == StationTxGateState.Keyed)
            {
                return new(
                    Success: true,
                    OutcomeKnown: true,
                    Code: "key_confirmed",
                    Message: "Fresh radio state confirmed the exact AetherSDR TX owner.");
            }
            if (!enabled &&
                snapshot.State == StationTxGateState.Idle &&
                !snapshot.HasActiveIntent)
            {
                return new(
                    Success: true,
                    OutcomeKnown: true,
                    Code: "unkey_confirmed",
                    Message: "Fresh radio state confirmed receive/idle and cleared the gate intent.");
            }
            if (snapshot.State is StationTxGateState.Faulted or
                StationTxGateState.Disabled)
            {
                return new(
                    Success: false,
                    OutcomeKnown: true,
                    evaluation.Code,
                    evaluation.Message);
            }
            if (m_timeProvider.GetUtcNow() >= deadline)
            {
                return new(
                    Success: false,
                    OutcomeKnown: false,
                    Code: enabled
                        ? "key_confirmation_timeout"
                        : "unkey_confirmation_timeout",
                    Message: enabled
                        ? "The key command outcome was not radio-confirmed before the bounded deadline."
                        : "The unkey command outcome was not radio-confirmed before the bounded deadline.");
            }

            await Task.Delay(
                PollInterval,
                m_timeProvider,
                cancellationToken);
        }
    }
}
