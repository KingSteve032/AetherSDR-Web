using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxProductionActivationConfigurationInterlockTests
{
    [Fact]
    public void SettingsOwnOneDisabledByDefaultActivationSwitch()
    {
        StationTxProductionActivationSettings settings = new();

        Assert.False(settings.Enabled);
        Assert.Equal(
            "StationTxProductionActivation",
            StationTxProductionActivationSettings.SectionName);
        Assert.True(typeof(StationTxProductionActivationSettings).IsPublic);
        Assert.False(
            typeof(StationTxProductionActivationConfigurationInterlock).IsPublic);
    }

    [Fact]
    public void DormantConfigurationIsValidButNotAnActivationRequest()
    {
        StationTxProductionActivationConfigurationDiagnostics diagnostics =
            StationTxProductionActivationConfigurationInterlock.Evaluate(
                Inputs(activationRequested: false, configured: false));

        Assert.True(diagnostics.Registered);
        Assert.False(diagnostics.ActivationRequested);
        Assert.True(diagnostics.ConfigurationValid);
        Assert.Equal("activation-not-requested", diagnostics.Reason);
        Assert.Contains("transmit-disabled", diagnostics.MissingPrerequisites);
        Assert.Contains(
            "browser-tx-lease-disabled",
            diagnostics.MissingPrerequisites);
    }

    [Fact]
    public void RequestedPartialConfigurationFailsInDeterministicOrder()
    {
        StationTxProductionActivationConfigurationDiagnostics diagnostics =
            StationTxProductionActivationConfigurationInterlock.Evaluate(
                Inputs(activationRequested: true, configured: false));

        Assert.True(diagnostics.ActivationRequested);
        Assert.False(diagnostics.ConfigurationValid);
        Assert.Equal("local-flex-mode-required", diagnostics.Reason);
        Assert.Equal(
            [
                "local-flex-mode-required",
                "transmit-disabled",
                "browser-tx-lease-disabled",
                "command-trust-verification-disabled",
                "command-trust-key-unconfigured",
                "command-signing-disabled",
                "command-signing-key-unconfigured",
                "command-submission-disabled",
                "command-transport-disabled",
                "command-transport-allowlist-empty",
                "emergency-unkey-transport-disabled",
                "emergency-unkey-transport-allowlist-empty",
                "watchdog-supervision-disabled",
                "watchdog-unkey-transport-disabled",
                "watchdog-unkey-transport-allowlist-empty",
                "watchdog-arming-disabled"
            ],
            diagnostics.MissingPrerequisites);
    }

    [Fact]
    public void CompleteRequestedConfigurationIsReadyForDynamicEvaluationOnly()
    {
        StationTxProductionActivationConfigurationDiagnostics diagnostics =
            StationTxProductionActivationConfigurationInterlock.Evaluate(
                Inputs(activationRequested: true, configured: true));

        Assert.True(diagnostics.ActivationRequested);
        Assert.True(diagnostics.ConfigurationValid);
        Assert.Equal("configuration-ready", diagnostics.Reason);
        Assert.Empty(diagnostics.MissingPrerequisites);
    }

    [Fact]
    public void StartupValidationRejectsOnlyAnInvalidActivationRequest()
    {
        StationTxProductionActivationConfigurationDiagnostics dormant =
            StationTxProductionActivationConfigurationInterlock.ValidateOrThrow(
                Inputs(activationRequested: false, configured: false));
        Assert.True(dormant.ConfigurationValid);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => StationTxProductionActivationConfigurationInterlock
                .ValidateOrThrow(
                    Inputs(activationRequested: true, configured: false)));

        Assert.Contains(
            StationTxProductionActivationSettings.SectionName,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "local-flex-mode-required",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsProjectionRequiresEveryStaticPrerequisite()
    {
        StationTxProductionActivationConfigurationDiagnostics diagnostics =
            StationTxProductionActivationConfigurationInterlock.ValidateOrThrow(
                new StationTxProductionActivationSettings { Enabled = true },
                new RadioSettings
                {
                    Mode = "FlexRx",
                    AllowTransmit = true,
                    BrowserTxLeaseEnabled = true
                },
                new StationTxCommandTrustSettings
                {
                    VerificationEnabled = true,
                    Keys =
                    [
                        new StationTxCommandTrustKeySettings
                        {
                            KeyId = "station-key",
                            PublicKeyPath = "/run/aethersdr/station-key.pub"
                        }
                    ]
                },
                new StationTxCommandSigningSettings
                {
                    SigningEnabled = true,
                    KeyId = "station-key",
                    PrivateKeyPath = "/run/aethersdr/station-key.key"
                },
                new StationTxCommandEnvelopeCoordinatorSettings
                {
                    SubmissionEnabled = true
                },
                new StationTxCommandTransportSettings
                {
                    Enabled = true,
                    AllowedRadioIds = ["radio-a"]
                },
                new StationTxEmergencyUnkeyTransportSettings
                {
                    Enabled = true,
                    AllowedRadioIds = ["radio-a"]
                },
                new IndependentTxWatchdogSettings
                {
                    Enabled = true,
                    RadioCommandTransportEnabled = true,
                    ArmingEnabled = true,
                    AllowedRadioIds = ["radio-a"]
                });

        Assert.True(diagnostics.ConfigurationValid);
        Assert.Equal("configuration-ready", diagnostics.Reason);
    }

    private static StationTxProductionActivationConfigurationInputs Inputs(
        bool activationRequested,
        bool configured) =>
        new(
            activationRequested,
            LocalFlexModeConfigured: configured,
            AllowTransmitConfigured: configured,
            BrowserTxLeaseConfigured: configured,
            CommandTrustVerificationEnabled: configured,
            CommandTrustKeyConfigured: configured,
            CommandSigningEnabled: configured,
            CommandSigningKeyConfigured: configured,
            CommandSubmissionEnabled: configured,
            CommandTransportEnabled: configured,
            CommandTransportAllowlistConfigured: configured,
            EmergencyUnkeyTransportEnabled: configured,
            EmergencyUnkeyTransportAllowlistConfigured: configured,
            WatchdogSupervisionEnabled: configured,
            WatchdogCommandTransportEnabled: configured,
            WatchdogRadioAllowlistConfigured: configured,
            WatchdogArmingEnabled: configured);
}
