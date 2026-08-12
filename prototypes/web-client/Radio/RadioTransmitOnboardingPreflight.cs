namespace AetherSDR.Web.Radio;

/// <summary>
/// Maps a discovered onboarding identity to the existing static production TX
/// preflight. Remote projections remain receive-only because the gateway does
/// not own their station-local radio or watchdog command boundary.
/// </summary>
internal static class RadioTransmitOnboardingPreflight
{
    internal static RadioTransmitPreflightSnapshot Evaluate(
        RadioOnboardingIdentity identity,
        string contentRootPath,
        StationTxProductionActivationSettings activation,
        RadioSettings radio,
        StationTxCommandTrustSettings commandTrust,
        StationTxCommandSigningSettings commandSigning,
        StationTxCommandEnvelopeCoordinatorSettings commandCoordinator,
        StationTxCommandTransportSettings commandTransport,
        StationTxEmergencyUnkeyTransportSettings emergencyUnkeyTransport,
        IndependentTxWatchdogSettings watchdog,
        TimeProvider timeProvider)
    {
        RadioOnboardingIdentity normalized =
            RadioOnboardingPolicyStore.NormalizeIdentity(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentNullException.ThrowIfNull(timeProvider);

        DateTimeOffset evaluatedAt = timeProvider.GetUtcNow();
        if (!string.Equals(
                normalized.Source,
                "local",
                StringComparison.Ordinal))
        {
            return new(
                ValidationOnly: true,
                normalized.SourceRadioId,
                Ready: false,
                Reason: "remote-radio-transmit-not-supported",
                MissingPrerequisites:
                [
                    "station-local-transmit-control-required"
                ],
                evaluatedAt);
        }

        StationTxProductionActivationPreflightReport report =
            StationTxProductionActivationPreflight.Evaluate(
                normalized.SourceRadioId,
                contentRootPath,
                activation,
                radio,
                commandTrust,
                commandSigning,
                commandCoordinator,
                commandTransport,
                emergencyUnkeyTransport,
                watchdog);
        return new(
            report.ValidationOnly,
            report.TargetRadioId,
            report.ReadyForOperatorActivation,
            report.Reason,
            report.MissingPrerequisites,
            evaluatedAt);
    }
}
