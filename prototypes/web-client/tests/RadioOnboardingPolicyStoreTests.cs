using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class RadioOnboardingPolicyStoreTests
{
    [Fact]
    public void NewlyDiscoveredRadioDefaultsToUnmanagedReceiveOnly()
    {
        using TestDirectory directory = new();
        RadioOnboardingPolicyStore store = CreateStore(directory);

        RadioOnboardingPolicySnapshot policy =
            store.GetPolicy("remote:station-a:flex-6600");

        Assert.Equal(
            RadioTransmitPolicyStates.ReceiveOnly,
            policy.TransmitPolicyState);
        Assert.False(policy.Onboarded);
        Assert.Null(policy.Label);
        Assert.Null(policy.UpdatedAt);
        Assert.False(File.Exists(directory.PolicyPath));
    }

    [Fact]
    public void StableLabelIsTrimmedPersistedAndReloaded()
    {
        using TestDirectory directory = new();
        RadioOnboardingPolicyStore first = CreateStore(directory);

        RadioOnboardingPolicySnapshot updated = first.UpdateLabel(
            "remote:station-a:flex-6600",
            "  Club Station  ",
            "administrator-1");

        Assert.Equal("Club Station", updated.Label);
        Assert.True(updated.Onboarded);
        Assert.Equal(
            RadioTransmitPolicyStates.ReceiveOnly,
            updated.TransmitPolicyState);
        Assert.Equal("administrator-1", updated.UpdatedBy);
        Assert.NotNull(updated.UpdatedAt);

        RadioOnboardingPolicyStore reloaded = CreateStore(directory);
        RadioOnboardingPolicySnapshot persisted =
            reloaded.GetPolicy("REMOTE:STATION-A:FLEX-6600");

        Assert.Equal("Club Station", persisted.Label);
        Assert.True(persisted.Onboarded);
        Assert.Equal(
            RadioTransmitPolicyStates.ReceiveOnly,
            persisted.TransmitPolicyState);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(directory.PolicyPath));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidLabelDoesNotCreateOrChangePolicy(string label)
    {
        using TestDirectory directory = new();
        RadioOnboardingPolicyStore store = CreateStore(directory);

        Assert.Throws<ArgumentException>(() => store.UpdateLabel(
            "flex:1234",
            label,
            "administrator"));

        RadioOnboardingPolicySnapshot policy =
            store.GetPolicy("flex:1234");
        Assert.False(policy.Onboarded);
        Assert.Equal(
            RadioTransmitPolicyStates.ReceiveOnly,
            policy.TransmitPolicyState);
        Assert.False(File.Exists(directory.PolicyPath));
    }

    [Fact]
    public void PersistenceFailureRollsBackInMemoryOnboarding()
    {
        using TestDirectory directory = new();
        Directory.CreateDirectory(directory.RootPath);
        string blockedDirectory = Path.Combine(
            directory.RootPath,
            "not-a-directory");
        File.WriteAllText(blockedDirectory, "occupied");
        RadioOnboardingPolicyStore store = new(
            Path.Combine(blockedDirectory, "onboarding.json"),
            NullLogger<RadioOnboardingPolicyStore>.Instance);

        Assert.ThrowsAny<IOException>(() => store.UpdateLabel(
            "flex:1234",
            "Club Station",
            "administrator"));

        RadioOnboardingPolicySnapshot policy =
            store.GetPolicy("flex:1234");
        Assert.False(policy.Onboarded);
        Assert.Null(policy.Label);
        Assert.Equal(
            RadioTransmitPolicyStates.ReceiveOnly,
            policy.TransmitPolicyState);
    }

    private static RadioOnboardingPolicyStore CreateStore(
        TestDirectory directory) =>
        new(
            directory.PolicyPath,
            NullLogger<RadioOnboardingPolicyStore>.Instance);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "aethersdr-web-tests",
                Guid.NewGuid().ToString("N"));
            PolicyPath = Path.Combine(RootPath, "onboarding.json");
        }

        public string RootPath { get; }
        public string PolicyPath { get; }

        public void Dispose()
        {
            string resolvedRoot = Path.GetFullPath(RootPath);
            string resolvedTestRoot = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "aethersdr-web-tests"));
            if (resolvedRoot.StartsWith(
                    resolvedTestRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }
}
