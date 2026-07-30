using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class RadioAccessPolicyStoreTests
{
    [Fact]
    public void NewRadiosDefaultToSharedAccess()
    {
        using TestDirectory directory = new();
        RadioAccessPolicyStore store = CreateStore(directory);

        RadioAccessPolicySnapshot policy = store.GetPolicy("flex:1234");
        RadioAccessDecision decision = store.Evaluate(
            "flex:1234",
            "operator-b",
            ["operator-a"],
            administratorBypass: false);

        Assert.Equal(RadioAccessModes.Shared, policy.Mode);
        Assert.Null(policy.ReservedUserId);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void ExclusiveAccessRejectsAnotherActiveAccount()
    {
        using TestDirectory directory = new();
        RadioAccessPolicyStore store = CreateStore(directory);
        store.Update(
            "flex:1234",
            RadioAccessModes.Exclusive,
            null,
            "administrator");

        RadioAccessDecision sameUser = store.Evaluate(
            "flex:1234",
            "operator-a",
            ["operator-a"],
            administratorBypass: false);
        RadioAccessDecision otherUser = store.Evaluate(
            "flex:1234",
            "operator-b",
            ["operator-a"],
            administratorBypass: false);

        Assert.True(sameUser.Allowed);
        Assert.False(otherUser.Allowed);
        Assert.Contains("exclusively", otherUser.Reason);
    }

    [Fact]
    public void ReservationRejectsEveryOtherAccount()
    {
        using TestDirectory directory = new();
        RadioAccessPolicyStore store = CreateStore(directory);
        store.Update(
            "flex:1234",
            RadioAccessModes.Shared,
            "operator-a",
            "administrator");

        Assert.True(
            store.Evaluate(
                "flex:1234",
                "operator-a",
                [],
                administratorBypass: false).Allowed);
        Assert.False(
            store.Evaluate(
                "flex:1234",
                "operator-b",
                [],
                administratorBypass: false).Allowed);
        Assert.True(
            store.Evaluate(
                "flex:1234",
                "administrator",
                [],
                administratorBypass: true).Allowed);
    }

    [Fact]
    public void PolicySurvivesAStoreRestart()
    {
        using TestDirectory directory = new();
        RadioAccessPolicyStore first = CreateStore(directory);
        first.Update(
            "flex:1234",
            RadioAccessModes.Exclusive,
            "operator-a",
            "administrator");

        RadioAccessPolicyStore reloaded = CreateStore(directory);
        RadioAccessPolicySnapshot policy =
            reloaded.GetPolicy("flex:1234");

        Assert.Equal(RadioAccessModes.Exclusive, policy.Mode);
        Assert.Equal("operator-a", policy.ReservedUserId);
        Assert.Equal("administrator", policy.UpdatedBy);
        Assert.NotNull(policy.UpdatedAt);
    }

    [Fact]
    public void InvalidModeIsRejectedWithoutChangingTheSavedPolicy()
    {
        using TestDirectory directory = new();
        RadioAccessPolicyStore store = CreateStore(directory);

        Assert.Throws<ArgumentException>(
            () => store.Update(
                "flex:1234",
                "unrestricted",
                null,
                "administrator"));

        Assert.Equal(
            RadioAccessModes.Shared,
            store.GetPolicy("flex:1234").Mode);
        Assert.False(File.Exists(directory.PolicyPath));
    }

    [Fact]
    public void PersistenceFailureRollsBackTheInMemoryPolicy()
    {
        using TestDirectory directory = new();
        Directory.CreateDirectory(directory.RootPath);
        string blockedDirectory = Path.Combine(
            directory.RootPath,
            "not-a-directory");
        File.WriteAllText(blockedDirectory, "occupied");
        RadioAccessPolicyStore store = new(
            Path.Combine(blockedDirectory, "policies.json"),
            NullLogger<RadioAccessPolicyStore>.Instance);

        Assert.ThrowsAny<IOException>(() => store.Update(
            "flex:1234",
            RadioAccessModes.Exclusive,
            "operator-a",
            "administrator"));

        RadioAccessPolicySnapshot policy = store.GetPolicy("flex:1234");
        Assert.Equal(RadioAccessModes.Shared, policy.Mode);
        Assert.Null(policy.ReservedUserId);
    }

    private static RadioAccessPolicyStore CreateStore(
        TestDirectory directory) =>
        new(
            directory.PolicyPath,
            NullLogger<RadioAccessPolicyStore>.Instance);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "aethersdr-web-tests",
                Guid.NewGuid().ToString("N"));
            PolicyPath = Path.Combine(RootPath, "policies.json");
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
