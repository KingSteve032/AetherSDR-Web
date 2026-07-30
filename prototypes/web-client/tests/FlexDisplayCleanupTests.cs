using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class FlexDisplayCleanupTests
{
    [Fact]
    public void PanafallCleanupQueuesNativeCommandPairInOrder()
    {
        string[] commands =
            FlexRadioRxService.BuildDisplayRemovalCommands(
                "0x40000003",
                "0x42000003");

        Assert.Equal(
            [
                "display pan remove 0x40000003",
                "display panafall remove 0x42000003"
            ],
            commands);
    }

    [Theory]
    [InlineData("0x40000003", null, "display pan remove 0x40000003")]
    [InlineData(null, "0x42000003", "display panafall remove 0x42000003")]
    public void PartialDisplayOwnershipRemovesOnlyKnownResource(
        string? panId,
        string? waterfallId,
        string expected)
    {
        string command = Assert.Single(
            FlexRadioRxService.BuildDisplayRemovalCommands(
                panId,
                waterfallId));

        Assert.Equal(expected, command);
    }

    [Fact]
    public void MissingDisplayOwnershipQueuesNoRemoval()
    {
        Assert.Empty(
            FlexRadioRxService.BuildDisplayRemovalCommands(" ", null));
    }
}
