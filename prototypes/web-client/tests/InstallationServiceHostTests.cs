using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationServiceHostTests
{
    [Fact]
    public void DefaultsToGatewayAndAcceptsFixedStationEngineRole()
    {
        Assert.Equal(
            InstallationServiceHostRole.Gateway,
            InstallationServiceHost.Validate(
                new InstallationServiceHostSettings()));
        Assert.Equal(
            InstallationServiceHostRole.StationEngine,
            InstallationServiceHost.Validate(
                new InstallationServiceHostSettings
                {
                    Role = InstallationServiceHostRole.StationEngine
                }));
    }

    [Fact]
    public void RejectsUnknownRole()
    {
        Assert.Throws<InvalidOperationException>(
            () => InstallationServiceHost.Validate(
                new InstallationServiceHostSettings
                {
                    Role = (InstallationServiceHostRole)99
                }));
    }
}
