using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class RadioOnboardingPolicyStoreTests
{
    private static readonly RadioOnboardingIdentity RemoteIdentity = new(
        "remote:station-a:flex-6600",
        "remote",
        "station-a",
        "flex-6600");

    [Fact]
    public void NewlyDiscoveredRadioDefaultsToUnmanagedReceiveOnly()
    {
        using TestDirectory directory = new();
        RadioOnboardingPolicyStore store = CreateStore(directory);

        RadioOnboardingPolicySnapshot policy =
            store.GetPolicy(RemoteIdentity);

        Assert.Equal(
            RadioTransmitPolicyStates.ReceiveOnly,
            policy.TransmitPolicyState);
        Assert.False(policy.Onboarded);
        Assert.Equal("remote", policy.Source);
        Assert.Equal("station-a", policy.StationId);
        Assert.Equal("flex-6600", policy.SourceRadioId);
        Assert.Null(policy.Label);
        Assert.Null(policy.UpdatedAt);
        Assert.False(File.Exists(directory.PolicyPath));
    }

    [Fact]
    public void StableLabelAndSourceOwnershipPersistAndReload()
    {
        using TestDirectory directory = new();
        RadioOnboardingPolicyStore first = CreateStore(directory);

        RadioOnboardingPolicySnapshot updated = first.UpdateLabel(
            RemoteIdentity,
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
            reloaded.GetPolicy(RemoteIdentity with
            {
                RadioId = "REMOTE:STATION-A:FLEX-6600"
            });

        Assert.Equal("Club Station", persisted.Label);
        Assert.True(persisted.Onboarded);
        Assert.Equal("station-a", persisted.StationId);
        Assert.Equal("flex-6600", persisted.SourceRadioId);
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

    [Fact]
    public void ChangedPhysicalIdentityDoesNotInheritOnboardingOrTxPolicy()
    {
        using TestDirectory directory = new();
        RadioOnboardingPolicyStore store = CreateStore(directory);
        _ = store.UpdateLabel(
            RemoteIdentity,
            "Club Station",
            "administrator");
        _ = store.UpdateTransmitPolicy(
            RemoteIdentity,
            RadioTransmitPolicyStates.TxEligible,
            "administrator",
            ReadyPreflight());

        RadioOnboardingPolicySnapshot replacement =
            store.GetPolicy(RemoteIdentity with
            {
                SourceRadioId = "different-flex"
            });

        Assert.False(replacement.Onboarded);
        Assert.Null(replacement.Label);
        Assert.Equal(
            RadioTransmitPolicyStates.ReceiveOnly,
            replacement.TransmitPolicyState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidLabelDoesNotCreateOrChangePolicy(string label)
    {
        using TestDirectory directory = new();
        RadioOnboardingPolicyStore store = CreateStore(directory);

        Assert.Throws<ArgumentException>(() => store.UpdateLabel(
            RemoteIdentity,
            label,
            "administrator"));

        RadioOnboardingPolicySnapshot policy =
            store.GetPolicy(RemoteIdentity);
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
            RemoteIdentity,
            "Club Station",
            "administrator"));

        RadioOnboardingPolicySnapshot policy =
            store.GetPolicy(RemoteIdentity);
        Assert.False(policy.Onboarded);
        Assert.Null(policy.Label);
        Assert.Equal(
            RadioTransmitPolicyStates.ReceiveOnly,
            policy.TransmitPolicyState);
    }

    [Fact]
    public void TxEligibilityRequiresOnboardingAndExactReadyPreflight()
    {
        using TestDirectory directory = new();
        RadioOnboardingPolicyStore store = CreateStore(directory);

        Assert.Throws<InvalidOperationException>(() =>
            store.UpdateTransmitPolicy(
                RemoteIdentity,
                RadioTransmitPolicyStates.TxEligible,
                "administrator",
                ReadyPreflight()));

        _ = store.UpdateLabel(
            RemoteIdentity,
            "Club Station",
            "administrator");
        Assert.Throws<ArgumentException>(() => store.UpdateTransmitPolicy(
            RemoteIdentity,
            RadioTransmitPolicyStates.TxEligible,
            "administrator",
            ReadyPreflight() with { TargetRadioId = "different-flex" }));

        RadioOnboardingPolicySnapshot updated =
            store.UpdateTransmitPolicy(
                RemoteIdentity,
                RadioTransmitPolicyStates.TxEligible,
                "administrator",
                ReadyPreflight());

        Assert.Equal(
            RadioTransmitPolicyStates.TxEligible,
            updated.TransmitPolicyState);
        Assert.True(updated.TransmitPreflight?.Ready);
        Assert.Empty(updated.TransmitPreflight!.MissingPrerequisites);
    }

    [Fact]
    public void FailedPrerequisitesAreDurableAndDisablingClearsEvidence()
    {
        using TestDirectory directory = new();
        RadioOnboardingPolicyStore store = CreateStore(directory);
        _ = store.UpdateLabel(
            RemoteIdentity,
            "Club Station",
            "administrator");
        RadioTransmitPreflightSnapshot failure = ReadyPreflight() with
        {
            Ready = false,
            Reason = "watchdog-radio-not-allowed",
            MissingPrerequisites = ["watchdog-radio-not-allowed"]
        };

        RadioOnboardingPolicySnapshot failed =
            store.UpdateTransmitPolicy(
                RemoteIdentity,
                RadioTransmitPolicyStates.PrerequisitesFailed,
                "administrator",
                failure);
        Assert.Equal(
            RadioTransmitPolicyStates.PrerequisitesFailed,
            failed.TransmitPolicyState);
        Assert.Equal(
            "watchdog-radio-not-allowed",
            failed.TransmitPreflight?.Reason);

        RadioOnboardingPolicySnapshot disabled =
            store.UpdateTransmitPolicy(
                RemoteIdentity,
                RadioTransmitPolicyStates.TemporarilyDisabled,
                "administrator");
        Assert.Equal(
            RadioTransmitPolicyStates.TemporarilyDisabled,
            disabled.TransmitPolicyState);
        Assert.Null(disabled.TransmitPreflight);

        RadioOnboardingPolicyStore reloaded = CreateStore(directory);
        Assert.Equal(
            RadioTransmitPolicyStates.TemporarilyDisabled,
            reloaded.GetPolicy(RemoteIdentity).TransmitPolicyState);
    }

    private static RadioTransmitPreflightSnapshot ReadyPreflight() =>
        new(
            ValidationOnly: true,
            TargetRadioId: RemoteIdentity.SourceRadioId,
            Ready: true,
            Reason: "preflight-ready",
            MissingPrerequisites: [],
            EvaluatedAt: DateTimeOffset.UtcNow);

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
