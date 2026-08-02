using AetherSDR.Web.Radio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class StationTxIndependentWatchdogSettingsTests
{
    [Fact]
    public void ArmingRequiresTheReviewedUnkeyTransport()
    {
        IndependentTxWatchdogSettings settings = new()
        {
            Enabled = true,
            ArmingEnabled = true,
            RadioCommandTransportEnabled = false
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Registry(settings));

        Assert.Contains(
            "ArmingEnabled requires",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExactEligibleLocalRadioReceivesBothDisabledByDefaultFlags()
    {
        StationTxIndependentWatchdogRegistry registry = Registry(new()
        {
            Enabled = true,
            RadioCommandTransportEnabled = true,
            ArmingEnabled = true,
            AllowedRadioIds = ["radio-a"],
            RadioCommandTimeoutMilliseconds = 2000
        });

        IndependentTxWatchdogLaunchCommand command = registry.ResolveCommand(
            Owner(localFlexEligible: true, radioId: "RADIO-A"));

        Assert.True(command.ExpectRadioCommandTransportAvailable);
        Assert.True(command.ExpectArmingAvailable);
        Assert.Equal(1, command.Arguments.Count(argument =>
            string.Equals(
                argument,
                "--unkey-enabled",
                StringComparison.Ordinal)));
        Assert.Equal(1, command.Arguments.Count(argument =>
            string.Equals(
                argument,
                "--arming-enabled",
                StringComparison.Ordinal)));
        Assert.DoesNotContain(command.Arguments, argument =>
            argument.Contains("key", StringComparison.OrdinalIgnoreCase) &&
            !argument.Contains("unkey", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false, "RADIO-A")]
    [InlineData(true, "RADIO-B")]
    public void IneligibleOrUnlistedRadioReceivesNoCommandOrArmingFlag(
        bool localFlexEligible,
        string radioId)
    {
        StationTxIndependentWatchdogRegistry registry = Registry(new()
        {
            Enabled = true,
            RadioCommandTransportEnabled = true,
            ArmingEnabled = true,
            AllowedRadioIds = ["RADIO-A"]
        });

        IndependentTxWatchdogLaunchCommand command = registry.ResolveCommand(
            Owner(localFlexEligible, radioId));

        Assert.False(command.ExpectRadioCommandTransportAvailable);
        Assert.False(command.ExpectArmingAvailable);
        Assert.Equal(["--stdio"], command.Arguments);
    }

    private static StationTxIndependentWatchdogRegistry Registry(
        IndependentTxWatchdogSettings settings) =>
        new(
            Options.Create(settings),
            new TestEnvironment(),
            NullLoggerFactory.Instance);

    private static StationTxIndependentWatchdogOwner Owner(
        bool localFlexEligible,
        string radioId) =>
        new(
            radioId,
            "session-a",
            "browser-a",
            "gateway-a",
            "engine-a",
            "192.0.2.15",
            4992,
            localFlexEligible);

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AetherSDR.Web.Tests";
        public IFileProvider WebRootFileProvider { get; set; } =
            new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
