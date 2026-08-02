namespace AetherSDR.Web.Radio;

public sealed class StationTxProductionActivationSettings
{
    public const string SectionName = "StationTxProductionActivation";

    public bool Enabled { get; set; }
}

internal sealed record StationTxProductionActivationConfigurationInputs(
    bool ActivationRequested,
    bool LocalFlexModeConfigured,
    bool AllowTransmitConfigured,
    bool BrowserTxLeaseConfigured,
    bool CommandTrustVerificationEnabled,
    bool CommandTrustKeyConfigured,
    bool CommandSigningEnabled,
    bool CommandSigningKeyConfigured,
    bool CommandSubmissionEnabled,
    bool CommandTransportEnabled,
    bool CommandTransportAllowlistConfigured,
    bool EmergencyUnkeyTransportEnabled,
    bool EmergencyUnkeyTransportAllowlistConfigured,
    bool WatchdogSupervisionEnabled,
    bool WatchdogCommandTransportEnabled,
    bool WatchdogRadioAllowlistConfigured,
    bool WatchdogArmingEnabled);

public sealed record StationTxProductionActivationConfigurationDiagnostics(
    bool Registered,
    bool ActivationRequested,
    bool ConfigurationValid,
    string Reason,
    IReadOnlyList<string> MissingPrerequisites);

/// <summary>
/// Fail-closed startup interlock for the single production TX activation
/// request. It validates only configuration assembly. It has no browser,
/// command, lease, gate, transport, watchdog operation, or activation method.
/// Dynamic authority and radio readiness remain owned by the production
/// readiness policy and lifecycle.
/// </summary>
internal static class StationTxProductionActivationConfigurationInterlock
{
    public static StationTxProductionActivationConfigurationDiagnostics Dormant
        { get; } = Evaluate(new(
            ActivationRequested: false,
            LocalFlexModeConfigured: false,
            AllowTransmitConfigured: false,
            BrowserTxLeaseConfigured: false,
            CommandTrustVerificationEnabled: false,
            CommandTrustKeyConfigured: false,
            CommandSigningEnabled: false,
            CommandSigningKeyConfigured: false,
            CommandSubmissionEnabled: false,
            CommandTransportEnabled: false,
            CommandTransportAllowlistConfigured: false,
            EmergencyUnkeyTransportEnabled: false,
            EmergencyUnkeyTransportAllowlistConfigured: false,
            WatchdogSupervisionEnabled: false,
            WatchdogCommandTransportEnabled: false,
            WatchdogRadioAllowlistConfigured: false,
            WatchdogArmingEnabled: false));

    public static StationTxProductionActivationConfigurationDiagnostics
        ValidateOrThrow(
            StationTxProductionActivationSettings activation,
            RadioSettings radio,
            StationTxCommandTrustSettings commandTrust,
            StationTxCommandSigningSettings commandSigning,
            StationTxCommandEnvelopeCoordinatorSettings commandCoordinator,
            StationTxCommandTransportSettings commandTransport,
            StationTxEmergencyUnkeyTransportSettings emergencyUnkeyTransport,
            IndependentTxWatchdogSettings watchdog)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(commandTrust);
        ArgumentNullException.ThrowIfNull(commandSigning);
        ArgumentNullException.ThrowIfNull(commandCoordinator);
        ArgumentNullException.ThrowIfNull(commandTransport);
        ArgumentNullException.ThrowIfNull(emergencyUnkeyTransport);
        ArgumentNullException.ThrowIfNull(watchdog);

        return ValidateOrThrow(CreateInputs(
            activation,
            radio,
            commandTrust,
            commandSigning,
            commandCoordinator,
            commandTransport,
            emergencyUnkeyTransport,
            watchdog));
    }

    public static StationTxProductionActivationConfigurationDiagnostics
        ValidateOrThrow(
            StationTxProductionActivationConfigurationInputs inputs)
    {
        StationTxProductionActivationConfigurationDiagnostics diagnostics =
            Evaluate(inputs);
        if (diagnostics.ActivationRequested &&
            !diagnostics.ConfigurationValid)
        {
            throw new InvalidOperationException(
                $"{StationTxProductionActivationSettings.SectionName} " +
                "requested production TX activation with incomplete " +
                $"configuration: {string.Join(", ", diagnostics.MissingPrerequisites)}.");
        }

        return diagnostics;
    }

    public static StationTxProductionActivationConfigurationDiagnostics Evaluate(
        StationTxProductionActivationConfigurationInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        List<string> missing = [];
        AddMissing(
            missing,
            inputs.LocalFlexModeConfigured,
            "local-flex-mode-required");
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
            inputs.CommandTrustVerificationEnabled,
            "command-trust-verification-disabled");
        AddMissing(
            missing,
            inputs.CommandTrustKeyConfigured,
            "command-trust-key-unconfigured");
        AddMissing(
            missing,
            inputs.CommandSigningEnabled,
            "command-signing-disabled");
        AddMissing(
            missing,
            inputs.CommandSigningKeyConfigured,
            "command-signing-key-unconfigured");
        AddMissing(
            missing,
            inputs.CommandSubmissionEnabled,
            "command-submission-disabled");
        AddMissing(
            missing,
            inputs.CommandTransportEnabled,
            "command-transport-disabled");
        AddMissing(
            missing,
            inputs.CommandTransportAllowlistConfigured,
            "command-transport-allowlist-empty");
        AddMissing(
            missing,
            inputs.EmergencyUnkeyTransportEnabled,
            "emergency-unkey-transport-disabled");
        AddMissing(
            missing,
            inputs.EmergencyUnkeyTransportAllowlistConfigured,
            "emergency-unkey-transport-allowlist-empty");
        AddMissing(
            missing,
            inputs.WatchdogSupervisionEnabled,
            "watchdog-supervision-disabled");
        AddMissing(
            missing,
            inputs.WatchdogCommandTransportEnabled,
            "watchdog-unkey-transport-disabled");
        AddMissing(
            missing,
            inputs.WatchdogRadioAllowlistConfigured,
            "watchdog-unkey-transport-allowlist-empty");
        AddMissing(
            missing,
            inputs.WatchdogArmingEnabled,
            "watchdog-arming-disabled");

        bool configurationValid =
            !inputs.ActivationRequested || missing.Count == 0;
        string reason = !inputs.ActivationRequested
            ? "activation-not-requested"
            : missing.Count == 0
                ? "configuration-ready"
                : missing[0];
        return new StationTxProductionActivationConfigurationDiagnostics(
            Registered: true,
            inputs.ActivationRequested,
            configurationValid,
            reason,
            missing.ToArray());
    }

    public static StationTxProductionActivationConfigurationInputs CreateInputs(
        StationTxProductionActivationSettings activation,
        RadioSettings radio,
        StationTxCommandTrustSettings commandTrust,
        StationTxCommandSigningSettings commandSigning,
        StationTxCommandEnvelopeCoordinatorSettings commandCoordinator,
        StationTxCommandTransportSettings commandTransport,
        StationTxEmergencyUnkeyTransportSettings emergencyUnkeyTransport,
        IndependentTxWatchdogSettings watchdog)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(commandTrust);
        ArgumentNullException.ThrowIfNull(commandSigning);
        ArgumentNullException.ThrowIfNull(commandCoordinator);
        ArgumentNullException.ThrowIfNull(commandTransport);
        ArgumentNullException.ThrowIfNull(emergencyUnkeyTransport);
        ArgumentNullException.ThrowIfNull(watchdog);

        return new StationTxProductionActivationConfigurationInputs(
            activation.Enabled,
            LocalFlexModeConfigured: string.Equals(
                radio.Mode,
                "FlexRx",
                StringComparison.OrdinalIgnoreCase),
            radio.AllowTransmit,
            radio.BrowserTxLeaseEnabled,
            commandTrust.VerificationEnabled,
            CommandTrustKeyConfigured: commandTrust.Keys is { Length: > 0 },
            commandSigning.SigningEnabled,
            CommandSigningKeyConfigured:
                !string.IsNullOrWhiteSpace(commandSigning.KeyId) &&
                !string.IsNullOrWhiteSpace(commandSigning.PrivateKeyPath),
            commandCoordinator.SubmissionEnabled,
            commandTransport.Enabled,
            CommandTransportAllowlistConfigured:
                HasConfiguredRadio(commandTransport.AllowedRadioIds),
            emergencyUnkeyTransport.Enabled,
            EmergencyUnkeyTransportAllowlistConfigured:
                HasConfiguredRadio(emergencyUnkeyTransport.AllowedRadioIds),
            watchdog.Enabled,
            watchdog.RadioCommandTransportEnabled,
            WatchdogRadioAllowlistConfigured:
                HasConfiguredRadio(watchdog.AllowedRadioIds),
            watchdog.ArmingEnabled);
    }

    private static void AddMissing(
        ICollection<string> missing,
        bool present,
        string reason)
    {
        if (!present)
        {
            missing.Add(reason);
        }
    }

    private static bool HasConfiguredRadio(IEnumerable<string>? radioIds) =>
        radioIds is not null &&
        radioIds.Any(value => !string.IsNullOrWhiteSpace(value));
}
