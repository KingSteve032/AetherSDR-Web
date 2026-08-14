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

    [Fact]
    public void DirectorySymlinkReplacementAtomicallyReplacesExistingLinkEntry()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"aetherremote-updater-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string previous = Path.Combine(root, "previous");
            string target = Path.Combine(root, "target");
            string link = Path.Combine(root, "active");
            Directory.CreateDirectory(previous);
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, previous);

            updater::StationReleaseUpdateUpdater.ReplaceDirectorySymlink(link, target);

            DirectoryInfo replaced = new(link);
            replaced.Refresh();
            Assert.Equal(target, replaced.LinkTarget);
            Assert.False(Directory.Exists(link + ".aetherremote-new"));
            Assert.False(File.Exists(link + ".aetherremote-new"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
