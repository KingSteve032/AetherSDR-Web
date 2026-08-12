using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class RadioTransmitOnboardingPreflightTests
{
    [Fact]
    public void LocalRadioUsesItsExactPhysicalSourceId()
    {
        RadioOnboardingIdentity identity = new(
            "selector:local-flex",
            "local",
            "",
            "flex:1234");

        RadioTransmitPreflightSnapshot result =
            RadioTransmitOnboardingPreflight.Evaluate(
                identity,
                AppContext.BaseDirectory,
                new StationTxProductionActivationSettings(),
                new RadioSettings(),
                new StationTxCommandTrustSettings(),
                new StationTxCommandSigningSettings(),
                new StationTxCommandEnvelopeCoordinatorSettings(),
                new StationTxCommandTransportSettings(),
                new StationTxEmergencyUnkeyTransportSettings(),
                new IndependentTxWatchdogSettings(),
                TimeProvider.System);

        Assert.True(result.ValidationOnly);
        Assert.False(result.Ready);
        Assert.Equal("FLEX:1234", result.TargetRadioId);
        Assert.NotEmpty(result.MissingPrerequisites);
    }

    [Fact]
    public void RemoteRadioIsRejectedWithoutEvaluatingLocalTxConfiguration()
    {
        RadioOnboardingIdentity identity = new(
            "remote:station-a:flex-6600",
            "remote",
            "station-a",
            "flex-6600");

        RadioTransmitPreflightSnapshot result =
            RadioTransmitOnboardingPreflight.Evaluate(
                identity,
                AppContext.BaseDirectory,
                new StationTxProductionActivationSettings(),
                new RadioSettings(),
                new StationTxCommandTrustSettings(),
                new StationTxCommandSigningSettings(),
                new StationTxCommandEnvelopeCoordinatorSettings(),
                new StationTxCommandTransportSettings(),
                new StationTxEmergencyUnkeyTransportSettings(),
                new IndependentTxWatchdogSettings(),
                TimeProvider.System);

        Assert.True(result.ValidationOnly);
        Assert.False(result.Ready);
        Assert.Equal(identity.SourceRadioId, result.TargetRadioId);
        Assert.Equal(
            "remote-radio-transmit-not-supported",
            result.Reason);
        Assert.Equal(
            ["station-local-transmit-control-required"],
            result.MissingPrerequisites);
    }
}
