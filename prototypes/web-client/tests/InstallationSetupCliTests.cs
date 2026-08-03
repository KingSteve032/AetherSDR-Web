using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupCliTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 14, 5, 0, TimeSpan.Zero);

    [Fact]
    public void ParserBuildsTypedSetupCommandsAndPreservesHostArguments()
    {
        InstallationSetupConsoleCommandLine topology =
            InstallationSetupConsoleCommandParser.Parse(
                [
                    "--environment",
                    "Development",
                    InstallationSetupConsoleCommandParser.ConfigureTopologySwitch,
                    "remote-station-node"
                ]);
        InstallationSetupConsoleCommandLine channel =
            InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser
                        .ConfigureUpdateChannelSwitch,
                    "pinned",
                    InstallationSetupConsoleCommandParser.PinnedReleaseSwitch,
                    "20260803-m8a.2"
                ]);
        InstallationSetupConsoleCommandLine transmit =
            InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser
                        .ConfigureTransmitSupportSwitch,
                    "false"
                ]);

        Assert.Equal(
            InstallationSetupConsoleCommandKind.ConfigureTopology,
            topology.Command);
        Assert.Equal(
            InstallationTopologyKind.RemoteStationNode,
            topology.Topology);
        Assert.Equal(
            ["--environment", "Development"],
            topology.ApplicationArguments);
        Assert.Equal(
            InstallationUpdateChannel.Pinned,
            channel.UpdateChannel);
        Assert.Equal("20260803-m8a.2", channel.PinnedRelease);
        Assert.False(transmit.InstallTransmitSupport);
    }

    [Fact]
    public void ParserRejectsInvalidOrAmbiguousSetupArguments()
    {
        Assert.Throws<InvalidOperationException>(
            () => InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser.ConfigureTopologySwitch,
                    "unsupported"
                ]));
        Assert.Throws<InvalidOperationException>(
            () => InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser
                        .ConfigureUpdateChannelSwitch,
                    "stable",
                    InstallationSetupConsoleCommandParser.PinnedReleaseSwitch,
                    "v1"
                ]));
        Assert.Throws<InvalidOperationException>(
            () => InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser
                        .ConfigureUpdateChannelSwitch,
                    "pinned"
                ]));
        Assert.Throws<InvalidOperationException>(
            () => InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser
                        .ConfigureTransmitSupportSwitch,
                    "yes"
                ]));
        Assert.Throws<InvalidOperationException>(
            () => InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser.ClaimBootstrapTokenSwitch,
                    InstallationSetupConsoleCommandParser.StatusSwitch
                ]));
    }

    [Fact]
    public async Task InteractiveClaimConsumesSecretWithoutEchoOrPersistence()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        string statePath = Path.Combine(
            temporary.Path,
            "setup",
            "installation.json");
        InstallationSetupStore store = new(statePath, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationBootstrapTokenIssue issue =
            await tokenService.IssueAsync(initial.Revision);
        InstallationSetupConsole console = new(
            store,
            tokenService,
            preflight: new InstallationSetupPreflight(store, time));
        InstallationSetupConsoleCommandLine command =
            InstallationSetupConsoleCommandParser.Parse(
                [InstallationSetupConsoleCommandParser.ClaimBootstrapTokenSwitch]);
        using StringWriter output = new();
        int reads = 0;

        await console.ExecuteAsync(
            command,
            installationPaths: null,
            output,
            interactiveSecretInput: true,
            secretReader: _ =>
            {
                reads++;
                return ValueTask.FromResult(issue.Token);
            });

        string persisted = await File.ReadAllTextAsync(statePath);
        InstallationSetupState claimed = await store.LoadAsync();
        string text = output.ToString();
        Assert.Equal(1, reads);
        Assert.Equal(InstallationSetupLockMode.Claimed, claimed.Lock.Mode);
        Assert.Equal(InstallationSetupStep.BootstrapClaim, claimed.LastCompletedStep);
        Assert.DoesNotContain(issue.Token, text);
        Assert.DoesNotContain(issue.Token, persisted);
        Assert.DoesNotContain("bootstrapTokenHash", text);
        Assert.Contains("\"lockMode\": \"claimed\"", text);
    }

    [Fact]
    public async Task ClaimRefusesNonInteractiveInputBeforeReadingSecret()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationBootstrapTokenIssue issue =
            await tokenService.IssueAsync(initial.Revision);
        InstallationSetupConsole console = new(store, tokenService);
        InstallationSetupConsoleCommandLine command =
            InstallationSetupConsoleCommandLine.For(
                InstallationSetupConsoleCommandKind.ClaimBootstrapToken);
        bool readerCalled = false;
        using StringWriter output = new();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => console.ExecuteAsync(
                command,
                installationPaths: null,
                output,
                interactiveSecretInput: false,
                secretReader: _ =>
                {
                    readerCalled = true;
                    return ValueTask.FromResult(issue.Token);
                }));

        InstallationSetupState unchanged = await store.LoadAsync();
        Assert.False(readerCalled);
        Assert.Equal(issue.State, unchanged);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task LocalCommandsCompleteOrderedConfigurationAndPrintPreflight()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenIssue issue =
            await tokenService.IssueAsync(initial.Revision);
        InstallationPaths paths = InstallationPaths.Resolve(
            temporary.Path,
            InstallationPathLayout.Development);
        InstallationSetupConsole console = new(
            store,
            tokenService,
            preflight: new InstallationSetupPreflight(store, time));

        await ExecuteAsync(
            console,
            paths,
            InstallationSetupConsoleCommandParser.Parse(
                [InstallationSetupConsoleCommandParser.ClaimBootstrapTokenSwitch]),
            secretReader: _ => ValueTask.FromResult(issue.Token));
        await ExecuteAsync(
            console,
            paths,
            InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser.ConfigureTopologySwitch,
                    "hybrid-gateway"
                ]));
        await ExecuteAsync(
            console,
            paths,
            InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser.ConfigurePublicUrlSwitch,
                    "https://radio.example.org"
                ]));
        await ExecuteAsync(
            console,
            paths,
            InstallationSetupConsoleCommandParser.Parse(
                [InstallationSetupConsoleCommandParser.ConfigurePathsSwitch]));
        await ExecuteAsync(
            console,
            paths,
            InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser
                        .ConfigureUpdateChannelSwitch,
                    "beta"
                ]));
        await ExecuteAsync(
            console,
            paths,
            InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser
                        .ConfirmBackupLocationSwitch
                ]));
        string transmitStatus = await ExecuteAsync(
            console,
            paths,
            InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser
                        .ConfigureTransmitSupportSwitch,
                    "false"
                ]));
        InstallationSetupState configured = await store.LoadAsync();
        string before = await File.ReadAllTextAsync(store.StatePath);
        string preflight = await ExecuteAsync(
            console,
            paths,
            InstallationSetupConsoleCommandParser.Parse(
                [InstallationSetupConsoleCommandParser.PreflightSwitch]));
        string after = await File.ReadAllTextAsync(store.StatePath);

        Assert.Equal(
            InstallationSetupStep.TransmitSupport,
            configured.LastCompletedStep);
        Assert.Equal(InstallationTopologyKind.HybridGateway, configured.Topology);
        Assert.Equal(InstallationUpdateChannel.Beta, configured.UpdateChannel);
        Assert.False(configured.InstallTransmitSupport);
        Assert.Contains("\"lastCompletedStep\": \"transmitSupport\"", transmitStatus);
        Assert.Contains("\"readyForInstallerReview\": true", preflight);
        Assert.Contains("aethersdr-web.service", preflight);
        Assert.Contains("aetherremote-broker.service", preflight);
        Assert.DoesNotContain("AetherSDR.TxWatchdog package", preflight);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ConfigurationCommandsFailClosedBeforeClaimOrOutOfOrder()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = CreateStore(temporary, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationSetupConsole console = new(store, tokenService);
        InstallationPaths paths = InstallationPaths.Resolve(
            temporary.Path,
            InstallationPathLayout.Development);
        InstallationSetupConsoleCommandLine topology =
            InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser.ConfigureTopologySwitch,
                    "personal-single-station"
                ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteAsync(console, paths, topology));

        InstallationBootstrapTokenIssue issue =
            await tokenService.IssueAsync(initial.Revision);
        await ExecuteAsync(
            console,
            paths,
            InstallationSetupConsoleCommandLine.For(
                InstallationSetupConsoleCommandKind.ClaimBootstrapToken),
            _ => ValueTask.FromResult(issue.Token));
        InstallationSetupConsoleCommandLine publicUrl =
            InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser.ConfigurePublicUrlSwitch,
                    "https://radio.example.org"
                ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteAsync(console, paths, publicUrl));
        InstallationSetupState unchanged = await store.LoadAsync();
        Assert.Equal(
            InstallationSetupStep.BootstrapClaim,
            unchanged.LastCompletedStep);
    }

    private static InstallationSetupStore CreateStore(
        TemporaryDirectory temporary,
        TimeProvider time) =>
        new(
            Path.Combine(temporary.Path, "state", "setup", "installation.json"),
            time);

    private static async Task<string> ExecuteAsync(
        InstallationSetupConsole console,
        InstallationPaths paths,
        InstallationSetupConsoleCommandLine command,
        Func<CancellationToken, ValueTask<string>>? secretReader = null)
    {
        using StringWriter output = new();
        await console.ExecuteAsync(
            command,
            paths,
            output,
            interactiveSecretInput: secretReader is not null,
            secretReader: secretReader);
        return output.ToString();
    }

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
                $"aethersdr-setup-cli-tests-{Guid.NewGuid():N}");
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
