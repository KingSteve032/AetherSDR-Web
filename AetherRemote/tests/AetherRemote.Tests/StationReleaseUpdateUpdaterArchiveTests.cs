extern alias updater;

using System.Runtime.Versioning;

namespace AetherRemote.Tests;

[SupportedOSPlatform("linux")]
public sealed class StationReleaseUpdateUpdaterArchiveTests
{
    [Theory]
    [InlineData(".", true, "")]
    [InlineData("./", true, "")]
    [InlineData("./AetherRemote.Agent", false, "AetherRemote.Agent")]
    [InlineData("./updater/", true, "updater")]
    [InlineData("updater/AetherRemote.Updater", false, "updater/AetherRemote.Updater")]
    public void DeterministicPackageEntriesNormalizeWithinExtractionRoot(
        string entryName,
        bool isDirectory,
        string expected)
    {
        string normalized = updater::StationReleaseUpdateUpdater.NormalizeArchiveEntryName(
            entryName,
            isDirectory);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("../escape", false)]
    [InlineData("./../escape", false)]
    [InlineData("././escape", false)]
    [InlineData("a//b", false)]
    [InlineData("/absolute", false)]
    [InlineData("dir/../escape", false)]
    [InlineData("dir/./file", false)]
    public void UnsafePackageEntriesRemainRejected(
        string entryName,
        bool isDirectory)
    {
        Assert.Throws<InvalidDataException>(() =>
            updater::StationReleaseUpdateUpdater.NormalizeArchiveEntryName(
                entryName,
                isDirectory));
    }
}
