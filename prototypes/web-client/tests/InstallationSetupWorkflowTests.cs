using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupWorkflowTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WorkflowRequiresClaimedLockAndOrderedSteps()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationSetupWorkflow workflow = new(store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.ConfigureTopologyAsync(
                initial.Revision,
                InstallationTopologyKind.PersonalSingleStation));
        InstallationSetupState unchanged = await store.LoadAsync();
        Assert.Equal(initial, unchanged);

        InstallationSetupState claimed =
            await ClaimAsync(store, time, unchanged);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.ConfigurePublicUrlAsync(
                claimed.Revision,
                "https://radio.example.org"));
        InstallationSetupState stillClaimed = await store.LoadAsync();

        Assert.Equal(claimed, stillClaimed);
        Assert.Equal(
            InstallationSetupStep.BootstrapClaim,
            stillClaimed.LastCompletedStep);
    }

    [Fact]
    public async Task WorkflowCollectsValidatedSettingsInOrder()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationSetupState claimed =
            await ClaimAsync(store, time, initial);
        InstallationSetupWorkflow workflow = new(store);
        InstallationPaths paths = CreatePaths(temporary);

        InstallationSetupState topology =
            await workflow.ConfigureTopologyAsync(
                claimed.Revision,
                InstallationTopologyKind.PersonalSingleStation);
        InstallationSetupState publicUrl =
            await workflow.ConfigurePublicUrlAsync(
                topology.Revision,
                "https://Radio.Example.org:443/");
        InstallationSetupState configuredPaths =
            await workflow.ConfigurePathsAsync(
                publicUrl.Revision,
                paths);
        InstallationSetupState channel =
            await workflow.ConfigureUpdateChannelAsync(
                configuredPaths.Revision,
                InstallationUpdateChannel.Stable);
        InstallationSetupState backup =
            await workflow.ConfirmBackupLocationAsync(channel.Revision);
        InstallationSetupState transmit =
            await workflow.ConfigureTransmitSupportAsync(
                backup.Revision,
                installTransmitSupport: false);

        Assert.Equal(
            InstallationSetupStep.TransmitSupport,
            transmit.LastCompletedStep);
        Assert.Equal(
            InstallationTopologyKind.PersonalSingleStation,
            transmit.Topology);
        Assert.Equal(
            "https://radio.example.org",
            transmit.CanonicalPublicUrl);
        Assert.Equal(paths, transmit.Paths);
        Assert.Equal(InstallationUpdateChannel.Stable, transmit.UpdateChannel);
        Assert.Equal(string.Empty, transmit.PinnedRelease);
        Assert.False(transmit.InstallTransmitSupport);
        Assert.Equal(claimed.Revision + 6, transmit.Revision);
    }

    [Fact]
    public async Task RevisitingEarlierStepPreservesLaterProgress()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState completed =
            await ConfigureThroughTransmitSupportAsync(
                store,
                time,
                InstallationTopologyKind.PersonalSingleStation,
                installTransmitSupport: false);
        InstallationSetupWorkflow workflow = new(store);

        InstallationSetupState revised =
            await workflow.ConfigureTopologyAsync(
                completed.Revision,
                InstallationTopologyKind.HybridGateway);

        Assert.Equal(
            InstallationTopologyKind.HybridGateway,
            revised.Topology);
        Assert.Equal(
            InstallationSetupStep.TransmitSupport,
            revised.LastCompletedStep);
        Assert.Equal(completed.CanonicalPublicUrl, revised.CanonicalPublicUrl);
        Assert.Equal(completed.Paths, revised.Paths);
    }

    [Fact]
    public async Task PinnedChannelRequiresBoundedExactReleaseIdentity()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState paths =
            await ConfigureThroughPathsAsync(store, time);
        InstallationSetupWorkflow workflow = new(store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.ConfigureUpdateChannelAsync(
                paths.Revision,
                InstallationUpdateChannel.Pinned));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.ConfigureUpdateChannelAsync(
                paths.Revision,
                InstallationUpdateChannel.Stable,
                "v8.0.0"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.ConfigureUpdateChannelAsync(
                paths.Revision,
                InstallationUpdateChannel.Pinned,
                "release/latest"));
        string oversizedRelease = new(
            'a',
            InstallationReleaseIdentity.MaximumLength + 1);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.ConfigureUpdateChannelAsync(
                paths.Revision,
                InstallationUpdateChannel.Pinned,
                oversizedRelease));

        InstallationSetupState pinned =
            await workflow.ConfigureUpdateChannelAsync(
                paths.Revision,
                InstallationUpdateChannel.Pinned,
                "20260803-m8a.1");

        Assert.Equal(InstallationUpdateChannel.Pinned, pinned.UpdateChannel);
        Assert.Equal("20260803-m8a.1", pinned.PinnedRelease);
        Assert.Throws<InvalidOperationException>(
            () => InstallationSetupStateValidator.Validate(
                pinned with { PinnedRelease = "release/latest" }));
    }

    [Fact]
    public async Task PreflightIsNonMutatingAndReportsHybridPlan()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState configured =
            await ConfigureThroughTransmitSupportAsync(
                store,
                time,
                InstallationTopologyKind.HybridGateway,
                installTransmitSupport: true);
        string before = await File.ReadAllTextAsync(store.StatePath);
        InstallationSetupPreflight preflight = new(store, time);

        InstallationSetupPreflightReport report =
            await preflight.CreateAsync();
        string after = await File.ReadAllTextAsync(store.StatePath);
        InstallationSetupState reloaded = await store.LoadAsync();

        Assert.Equal(before, after);
        Assert.Equal(configured, reloaded);
        Assert.Equal(configured.Revision, report.StateRevision);
        Assert.True(report.ReadyForInstallerReview);
        Assert.Equal(
            InstallationTopologyKind.HybridGateway,
            report.Topology);
        Assert.Contains(
            report.PlannedUsers,
            value => value.StartsWith("aethersdr ", StringComparison.Ordinal));
        Assert.Contains(
            report.PlannedUsers,
            value => value.StartsWith("aetherremote ", StringComparison.Ordinal));
        Assert.Contains("AetherSDR.Web gateway package", report.PlannedPackages);
        Assert.Contains("AetherRemote.Broker package", report.PlannedPackages);
        Assert.DoesNotContain(
            "AetherSDR.Web station-engine package",
            report.PlannedPackages);
        Assert.Contains(
            report.PlannedPackages,
            value => value.Contains("TxWatchdog", StringComparison.Ordinal));
        Assert.Contains("aethersdr-web.service", report.PlannedServices);
        Assert.Contains("aetherremote-broker.service", report.PlannedServices);
        Assert.DoesNotContain(
            "aetherremote-station-engine.service",
            report.PlannedServices);
        Assert.DoesNotContain("aetherremote-agent.service", report.PlannedServices);
        Assert.Contains(
            report.PlannedPorts,
            value => value.Contains("5080", StringComparison.Ordinal));
        Assert.Contains(
            report.PlannedPorts,
            value => value.Contains("5090", StringComparison.Ordinal));
        Assert.DoesNotContain(
            report.PlannedPorts,
            value => value.Contains("5081", StringComparison.Ordinal));
        Assert.Contains(configured.Paths!.SetupStatePath, report.PlannedFiles);
        Assert.Contains(
            report.Warnings,
            value => value.Contains(
                "does not enable TX",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoteNodePreflightHasOutboundOnlyGatewayExpectation()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        _ = await ConfigureThroughTransmitSupportAsync(
            store,
            time,
            InstallationTopologyKind.RemoteStationNode,
            installTransmitSupport: false);

        InstallationSetupPreflightReport report =
            await new InstallationSetupPreflight(store, time).CreateAsync();

        Assert.DoesNotContain("aethersdr-web.service", report.PlannedServices);
        Assert.DoesNotContain("aetherremote-broker.service", report.PlannedServices);
        Assert.Contains(
            "aetherremote-station-engine.service",
            report.PlannedServices);
        Assert.Contains("aetherremote-agent.service", report.PlannedServices);
        Assert.Contains(
            report.PlannedPorts,
            value => value.Contains(
                "Outbound TCP 443",
                StringComparison.Ordinal));
        Assert.Contains(
            report.PlannedProxyChanges,
            value => value.Contains(
                "No reverse-proxy change",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            report.PlannedPackages,
            value => value.Contains("TxWatchdog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreflightDoesNotCreateMissingStateOrAcceptIncompleteState()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore missingStore = CreateStore(temporary, time);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => new InstallationSetupPreflight(missingStore, time)
                .CreateAsync());
        Assert.False(File.Exists(missingStore.StatePath));

        InstallationSetupState initial =
            await missingStore.LoadOrCreateAsync();
        InstallationSetupState claimed =
            await ClaimAsync(missingStore, time, initial);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new InstallationSetupPreflight(missingStore, time)
                .CreateAsync());
        InstallationSetupState unchanged = await missingStore.LoadAsync();
        Assert.Equal(claimed, unchanged);
    }

    private static async Task<InstallationSetupState>
        ConfigureThroughTransmitSupportAsync(
            InstallationSetupStore store,
            ManualTimeProvider time,
            InstallationTopologyKind topology,
            bool installTransmitSupport)
    {
        InstallationSetupState paths =
            await ConfigureThroughPathsAsync(store, time, topology);
        InstallationSetupWorkflow workflow = new(store);
        InstallationSetupState channel =
            await workflow.ConfigureUpdateChannelAsync(
                paths.Revision,
                InstallationUpdateChannel.Stable);
        InstallationSetupState backup =
            await workflow.ConfirmBackupLocationAsync(channel.Revision);
        return await workflow.ConfigureTransmitSupportAsync(
            backup.Revision,
            installTransmitSupport);
    }

    private static async Task<InstallationSetupState> ConfigureThroughPathsAsync(
        InstallationSetupStore store,
        ManualTimeProvider time,
        InstallationTopologyKind topology =
            InstallationTopologyKind.PersonalSingleStation)
    {
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationSetupState claimed =
            await ClaimAsync(store, time, initial);
        InstallationSetupWorkflow workflow = new(store);
        InstallationSetupState configuredTopology =
            await workflow.ConfigureTopologyAsync(claimed.Revision, topology);
        InstallationSetupState publicUrl =
            await workflow.ConfigurePublicUrlAsync(
                configuredTopology.Revision,
                "https://radio.example.org");
        string root =
            Path.GetDirectoryName(
                Path.GetDirectoryName(store.StatePath)!)!;
        return await workflow.ConfigurePathsAsync(
            publicUrl.Revision,
            CreatePaths(root));
    }

    private static async Task<InstallationSetupState> ClaimAsync(
        InstallationSetupStore store,
        ManualTimeProvider time,
        InstallationSetupState state)
    {
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationBootstrapTokenIssue issue =
            await tokenService.IssueAsync(state.Revision);
        return await tokenService.ClaimAsync(
            issue.State.Revision,
            issue.Token);
    }

    private static InstallationSetupStore CreateStore(
        TemporaryDirectory temporary,
        ManualTimeProvider time) =>
        new(
            Path.Combine(
                temporary.Path,
                "state",
                "setup",
                "installation.json"),
            time);

    private static InstallationPaths CreatePaths(TemporaryDirectory temporary) =>
        CreatePaths(temporary.Path);

    private static InstallationPaths CreatePaths(string root) =>
        new(
            Path.Combine(root, "config"),
            Path.Combine(root, "state-data"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "releases"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "logs"));

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-setup-workflow-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    Path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
