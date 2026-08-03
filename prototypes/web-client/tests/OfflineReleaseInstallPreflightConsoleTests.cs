using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class OfflineReleaseInstallPreflightConsoleTests
{
    private const string TestKeyId = "m8b-preflight-test-key";
    private const string TestPrivateKeyPkcs8Base64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg" +
        "EjRWeJq83vEjRWeJq83vEjRWeJq83vEjRWeJq83vEjShRAN" +
        "CAARawLjuCeZXZ7tsfTRAu+FcuRLUr+ELbhoX/6Hs0fLlSZe" +
        "0NNZYPUqZa65oYGMMs9Ud19Qc/RZMzn4vZv5+EakU";
    private const string TestPublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWsC47gnmV2e" +
        "7bH00QLvhXLkS1K/hC24aF/+h7NHy5UmXtDTWWD1KmWuuaG" +
        "BjDLPVHdfUHP0WTM5+L2b+fhGpFA==";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public void ParserAcceptsExactPreflightAndPreservesApplicationArguments()
    {
        string bundle = Path.GetFullPath("bundle");

        OfflineReleaseInstallPreflightCommandLine commandLine =
            OfflineReleaseInstallPreflightCommandParser.Parse(
                [
                    "--preflight-offline-release-install", bundle,
                    "--release-preflight-installed-identity", "aethersdr-8.1.0",
                    "--release-preflight-installed-version", "8.1.0",
                    "--release-preflight-configuration-schema-version", "1",
                    "--release-preflight-protocol-version", "2",
                    "--environment=Development"
                ]);

        Assert.Equal(
            OfflineReleaseInstallPreflightCommandKind.Preflight,
            commandLine.Command);
        Assert.Equal(bundle, commandLine.BundleDirectory);
        Assert.Equal("aethersdr-8.1.0", commandLine.InstalledReleaseIdentity);
        Assert.Equal("8.1.0", commandLine.InstalledVersion);
        Assert.Equal(1, commandLine.ConfigurationSchemaVersion);
        Assert.Equal(2, commandLine.ProtocolVersion);
        Assert.Equal(["--environment=Development"], commandLine.ApplicationArguments);
    }

    [Fact]
    public void ParserLeavesOtherReleaseCommandsForTheirOwnedParser()
    {
        OfflineReleaseInstallPreflightCommandLine preflight =
            OfflineReleaseInstallPreflightCommandParser.Parse(
                ["--release-status"]);
        ReleaseUpdateConsoleCommandLine release =
            ReleaseUpdateConsoleCommandParser.Parse(preflight.ApplicationArguments);

        Assert.Equal(
            OfflineReleaseInstallPreflightCommandKind.None,
            preflight.Command);
        Assert.Equal(ReleaseUpdateConsoleCommandKind.Status, release.Command);
    }

    [Fact]
    public void ParserRejectsOptionsWithoutPreflightCommand()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OfflineReleaseInstallPreflightCommandParser.Parse(
                ["--release-preflight-installed-version", "8.1.0"]));
    }

    [Fact]
    public void ParserRejectsDuplicatePreflightCommand()
    {
        string bundle = Path.GetFullPath("bundle");

        Assert.Throws<InvalidOperationException>(() =>
            OfflineReleaseInstallPreflightCommandParser.Parse(
                [
                    "--preflight-offline-release-install", bundle,
                    "--preflight-offline-release-install", bundle
                ]));
    }

    [Theory]
    [InlineData("--release-preflight-installed-identity")]
    [InlineData("--release-preflight-installed-version")]
    [InlineData("--release-preflight-configuration-schema-version")]
    [InlineData("--release-preflight-protocol-version")]
    public void ParserRequiresEveryCompatibilityInput(string omitted)
    {
        string bundle = Path.GetFullPath("bundle");
        List<string> arguments =
        [
            "--preflight-offline-release-install", bundle,
            "--release-preflight-installed-identity", "aethersdr-8.1.0",
            "--release-preflight-installed-version", "8.1.0",
            "--release-preflight-configuration-schema-version", "1",
            "--release-preflight-protocol-version", "2"
        ];
        int index = arguments.IndexOf(omitted);
        arguments.RemoveRange(index, 2);

        Assert.Throws<InvalidOperationException>(() =>
            OfflineReleaseInstallPreflightCommandParser.Parse(arguments));
    }

    [Theory]
    [InlineData(" aethersdr-8.1.0")]
    [InlineData("aethersdr-8.1.0 ")]
    [InlineData("aethersdr 8.1.0")]
    public void ParserRejectsNonCanonicalInstalledIdentity(string identity)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ParseCommand(Path.GetFullPath("bundle"), identity: identity));
    }

    [Theory]
    [InlineData("08.1.0")]
    [InlineData("8.01.0")]
    [InlineData("8.1")]
    [InlineData(" 8.1.0")]
    public void ParserRejectsNonCanonicalInstalledVersion(string version)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ParseCommand(Path.GetFullPath("bundle"), version: version));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("1000001")]
    public void ParserRejectsNonCanonicalCompatibilityVersion(string version)
    {
        string bundle = Path.GetFullPath("bundle");
        Assert.Throws<InvalidOperationException>(() =>
            OfflineReleaseInstallPreflightCommandParser.Parse(
                [
                    "--preflight-offline-release-install", bundle,
                    "--release-preflight-installed-identity", "aethersdr-8.1.0",
                    "--release-preflight-installed-version", "8.1.0",
                    "--release-preflight-configuration-schema-version", version,
                    "--release-preflight-protocol-version", "2"
                ]));
    }

    [Theory]
    [InlineData("relative/bundle")]
    [InlineData(" bundle")]
    [InlineData("bundle ")]
    public void ParserRejectsNonCanonicalBundlePath(string path)
    {
        Assert.Throws<InvalidOperationException>(() => ParseCommand(path));
    }

    [Fact]
    public void PublicSurfaceCannotMutateReleaseOrReachTx()
    {
        string[] plannerMethods = typeof(OfflineReleaseInstallPreflightPlanner)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] consoleMethods = typeof(OfflineReleaseInstallPreflightConsole)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["CreateAsync"], plannerMethods);
        Assert.Equal(["ExecuteAsync", "get_Snapshot"], consoleMethods);
        Assert.DoesNotContain(
            plannerMethods.Concat(consoleMethods),
            name =>
                name.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Extract", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Stage", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Activate", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Rollback", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Transmit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnosticsRegisterOnlyReadAndVerificationBoundaries()
    {
        await using PreflightFixture fixture = new();
        OfflineReleaseInstallPreflightConsoleDiagnostics snapshot =
            fixture.Console.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.SetupStateReadRegistered);
        Assert.True(snapshot.ReleaseInventoryReadRegistered);
        Assert.True(snapshot.CurrentPointerReadRegistered);
        Assert.True(snapshot.SignedBundleVerificationRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.ArchiveExtractionRegistered);
        Assert.False(snapshot.StagingRegistered);
        Assert.False(snapshot.InstallationRegistered);
        Assert.False(snapshot.ActivationRegistered);
        Assert.False(snapshot.RollbackRegistered);
        Assert.False(snapshot.MigrationExecutionRegistered);
        Assert.False(snapshot.ServiceControlRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public async Task MissingSetupFailsBeforeBundleAccess()
    {
        await using PreflightFixture fixture = new();
        string missingBundle = Path.Combine(fixture.Root, "missing-bundle");

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(Command(missingBundle));

        Assert.False(result.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.StatusUnavailable,
            result.FailureCode);
        Assert.Equal(
            ReleaseStatusFailureCode.SetupStateMissing,
            result.StatusFailureCode);
        Assert.Null(result.BundleFailureCode);
    }

    [Fact]
    public async Task IncompleteSetupFailsClosed()
    {
        await using PreflightFixture fixture = new();
        await fixture.ConfigureAsync(complete: false);

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(Command(Path.Combine(fixture.Root, "missing")));

        Assert.False(result.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.SetupIncomplete,
            result.FailureCode);
    }

    [Fact]
    public async Task MissingCurrentReleaseFailsClosed()
    {
        await using PreflightFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(Command(Path.Combine(fixture.Root, "missing")));

        Assert.False(result.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.CurrentReleaseMissing,
            result.FailureCode);
    }

    [Fact]
    public async Task SuppliedInstalledIdentityMustMatchCurrentPointer()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using PreflightFixture fixture = new();
        await fixture.ConfigureAndActivateAsync();

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(
                Command(
                    Path.Combine(fixture.Root, "missing"),
                    installedIdentity: "aethersdr-8.0.0"));

        Assert.False(result.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.InstalledReleaseMismatch,
            result.FailureCode);
        Assert.True(result.CurrentPointerVerified);
    }

    [Fact]
    public async Task DisabledTrustRejectsBeforeMissingBundleAccess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using PreflightFixture fixture = new(trustEnabled: false);
        await fixture.ConfigureAndActivateAsync();
        string missingBundle = Path.Combine(fixture.Root, "missing-bundle");

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(Command(missingBundle));

        Assert.False(result.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.BundleVerificationFailed,
            result.FailureCode);
        Assert.Equal(
            ReleaseManifestFailureCode.VerificationTrustDisabled,
            result.ManifestFailureCode);
        Assert.Equal(
            LocalOfflineReleaseBundleFailureCode.VerificationFailed,
            result.BundleFailureCode);
    }

    [Fact]
    public async Task VerifiedBundleProducesStableRedactedPreflight()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using PreflightFixture fixture = new();
        await fixture.ConfigureAndActivateAsync();
        using TestBundle bundle = TestBundle.CreateValid(
            fixture.Root,
            ReleaseManifestChannel.Stable,
            txSupportCapable: false);
        bundle.Freeze();
        StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(bundle.Path),
            output);
        OfflineReleaseInstallPreflightConsoleReport report = Deserialize(output);

        Assert.Equal(OfflineReleaseInstallPreflightConsole.SuccessExitCode, exitCode);
        Assert.True(report.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.None,
            report.FailureCode);
        Assert.Equal("aethersdr-8.1.0", report.InstalledReleaseIdentity);
        Assert.Equal("aethersdr-8.2.0", report.TargetReleaseIdentity);
        Assert.Equal("8.2.0", report.TargetVersion);
        Assert.Equal(4, report.PackageCount);
        Assert.True(report.TotalPackageBytes > 0);
        Assert.True(report.CurrentPointerVerified);
        Assert.True(report.TargetAbsentFromInventory);
        Assert.True(report.StatusStable);
        Assert.DoesNotContain(fixture.Root, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(bundle.Path, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TestKeyId, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("packages/", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingTargetReleaseFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using PreflightFixture fixture = new();
        await fixture.ConfigureAndActivateAsync();
        fixture.AddRelease("aethersdr-8.2.0");
        using TestBundle bundle = TestBundle.CreateValid(
            fixture.Root,
            ReleaseManifestChannel.Stable,
            txSupportCapable: false);
        bundle.Freeze();

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(Command(bundle.Path));

        Assert.False(result.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.TargetReleaseAlreadyPresent,
            result.FailureCode);
    }

    [Fact]
    public async Task TargetReleaseMustDifferFromCurrentIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using PreflightFixture fixture = new();
        await fixture.ConfigureAndActivateAsync();
        using TestBundle bundle = TestBundle.CreateValid(
            fixture.Root,
            ReleaseManifestChannel.Stable,
            txSupportCapable: false,
            releaseIdentity: "aethersdr-8.1.0");
        bundle.Freeze();

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(Command(bundle.Path));

        Assert.False(result.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.InvalidTargetRelease,
            result.FailureCode);
    }

    [Fact]
    public async Task TxSupportCapabilityMustMatchCompletedSetupPolicy()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using PreflightFixture fixture = new();
        await fixture.ConfigureAndActivateAsync(installTransmitSupport: false);
        using TestBundle bundle = TestBundle.CreateValid(
            fixture.Root,
            ReleaseManifestChannel.Stable,
            txSupportCapable: true);
        bundle.Freeze();

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(Command(bundle.Path));

        Assert.False(result.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.TxSupportMismatch,
            result.FailureCode);
        Assert.True(result.TargetAbsentFromInventory);
    }

    [Fact]
    public async Task PinnedSetupAcceptsOnlyExactPinnedRelease()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using PreflightFixture fixture = new();
        await fixture.ConfigureAndActivateAsync(
            channel: InstallationUpdateChannel.Pinned,
            pinnedRelease: "aethersdr-8.2.0");
        using TestBundle bundle = TestBundle.CreateValid(
            fixture.Root,
            ReleaseManifestChannel.Pinned,
            txSupportCapable: false);
        bundle.Freeze();

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(Command(bundle.Path));

        Assert.True(result.Succeeded);
        Assert.Equal(InstallationUpdateChannel.Pinned, result.UpdateChannel);
        Assert.Equal("aethersdr-8.2.0", result.TargetReleaseIdentity);
    }

    [Fact]
    public async Task PinnedSetupRejectsDifferentSignedRelease()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using PreflightFixture fixture = new();
        await fixture.ConfigureAndActivateAsync(
            channel: InstallationUpdateChannel.Pinned,
            pinnedRelease: "aethersdr-8.3.0");
        using TestBundle bundle = TestBundle.CreateValid(
            fixture.Root,
            ReleaseManifestChannel.Pinned,
            txSupportCapable: false);
        bundle.Freeze();

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(Command(bundle.Path));

        Assert.False(result.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.BundleVerificationFailed,
            result.FailureCode);
        Assert.Equal(
            ReleaseManifestFailureCode.InvalidChannelRelationship,
            result.ManifestFailureCode);
    }

    [Fact]
    public async Task StatusChangeDuringVerificationFailsClosedDeterministically()
    {
        ReleaseStatusReadResult first = StatusResult(revision: 4);
        ReleaseStatusReadResult second = StatusResult(revision: 5);
        Queue<ReleaseStatusReadResult> statuses = new([first, second]);
        LocalOfflineReleaseBundleVerificationResult bundle = BundleSuccess();
        OfflineReleaseInstallPreflightPlanner planner = new(
            _ => Task.FromResult(statuses.Dequeue()),
            (_, _) => bundle,
            () => ReleaseManifestArchitecture.LinuxX64);

        OfflineReleaseInstallPreflightResult result =
            await planner.CreateAsync(Command(Path.GetFullPath("bundle")));

        Assert.False(result.Succeeded);
        Assert.Equal(
            OfflineReleaseInstallPreflightFailureCode.StatusChangedDuringPreflight,
            result.FailureCode);
        Assert.False(result.StatusStable);
    }

    [Fact]
    public async Task SuccessfulPreflightDoesNotMutateSetupInventoryOrCurrentPointer()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using PreflightFixture fixture = new();
        await fixture.ConfigureAndActivateAsync();
        using TestBundle bundle = TestBundle.CreateValid(
            fixture.Root,
            ReleaseManifestChannel.Stable,
            txSupportCapable: false);
        bundle.Freeze();
        byte[] setupBefore = await File.ReadAllBytesAsync(fixture.Paths.SetupStatePath);
        string[] inventoryBefore = Directory.GetDirectories(fixture.Paths.ReleaseDirectory)
            .Select(Path.GetFileName)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray()!;
        string? currentBefore = new DirectoryInfo(fixture.CurrentPath).LinkTarget;

        OfflineReleaseInstallPreflightResult result =
            await fixture.Planner.CreateAsync(Command(bundle.Path));

        byte[] setupAfter = await File.ReadAllBytesAsync(fixture.Paths.SetupStatePath);
        string[] inventoryAfter = Directory.GetDirectories(fixture.Paths.ReleaseDirectory)
            .Select(Path.GetFileName)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray()!;
        string? currentAfter = new DirectoryInfo(fixture.CurrentPath).LinkTarget;
        Assert.True(result.Succeeded);
        Assert.Equal(setupBefore, setupAfter);
        Assert.Equal(inventoryBefore, inventoryAfter);
        Assert.Equal(currentBefore, currentAfter);
        Assert.False(Directory.Exists(
            Path.Combine(fixture.Paths.ReleaseDirectory, "aethersdr-8.2.0")));
    }

    [Fact]
    public async Task ConsoleRequiresItsExactCommand()
    {
        await using PreflightFixture fixture = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Console.ExecuteAsync(
                OfflineReleaseInstallPreflightCommandLine.None([]),
                new StringWriter()));
    }

    private static OfflineReleaseInstallPreflightCommandLine ParseCommand(
        string bundle,
        string identity = "aethersdr-8.1.0",
        string version = "8.1.0") =>
        OfflineReleaseInstallPreflightCommandParser.Parse(
            [
                "--preflight-offline-release-install", bundle,
                "--release-preflight-installed-identity", identity,
                "--release-preflight-installed-version", version,
                "--release-preflight-configuration-schema-version", "1",
                "--release-preflight-protocol-version", "2"
            ]);

    private static OfflineReleaseInstallPreflightCommandLine Command(
        string bundle,
        string installedIdentity = "aethersdr-8.1.0") =>
        ParseCommand(bundle, installedIdentity);

    private static OfflineReleaseInstallPreflightConsoleReport Deserialize(
        StringWriter output) =>
        JsonSerializer.Deserialize<OfflineReleaseInstallPreflightConsoleReport>(
            output.ToString(),
            JsonOptions) ??
        throw new InvalidOperationException("Release preflight output was empty.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    private static ReleaseStatusReadResult StatusResult(long revision)
    {
        InstallationSetupState state = CompleteState(revision);
        return ReleaseStatusReadResult.Success(
            state,
            releaseDirectoryPresent: true,
            ["aethersdr-8.1.0"],
            currentPointerPresent: true,
            "aethersdr-8.1.0");
    }

    private static LocalOfflineReleaseBundleVerificationResult BundleSuccess()
    {
        ReleaseManifestVerificationReport verification = new(
            true,
            ReleaseManifestFailureCode.None,
            "verified",
            "aethersdr-8.2.0",
            "8.2.0",
            ReleaseManifestArchitecture.LinuxX64,
            ReleaseManifestChannel.Stable,
            4,
            false);
        LocalOfflineReleaseBundleVerificationReport report = new(
            true,
            LocalOfflineReleaseBundleFailureCode.None,
            "verified",
            4,
            100,
            verification);
        SignedReleaseManifestPayload payload = new()
        {
            SchemaVersion = SignedReleaseManifestPayload.CurrentSchemaVersion,
            ReleaseIdentity = "aethersdr-8.2.0",
            Version = "8.2.0",
            Channel = ReleaseManifestChannel.Stable,
            Architecture = ReleaseManifestArchitecture.LinuxX64,
            Packages =
            [
                Package("gateway", ReleasePackageRole.GatewayWeb, "gateway.tar", '1'),
                Package("broker", ReleasePackageRole.Broker, "broker.tar", '2'),
                Package("agent", ReleasePackageRole.AetherRemoteAgent, "agent.tar", '3'),
                Package("engine", ReleasePackageRole.StationEngine, "engine.tar", '4')
            ],
            Configuration = new ReleaseConfigurationCompatibility
            {
                TargetSchemaVersion = 1,
                MinimumCompatibleSchemaVersion = 1,
                MaximumCompatibleSchemaVersion = 1
            },
            Protocol = new ReleaseProtocolCompatibility
            {
                MinimumVersion = 1,
                MaximumVersion = 2
            },
            MinimumPreviousVersion = "8.1.0",
            Restart = new ReleaseRestartDeclaration
            {
                GatewayWeb = true,
                Broker = true,
                AetherRemoteAgent = true,
                StationEngine = true,
                Host = false
            },
            Migration = new ReleaseMigrationDeclaration
            {
                Kind = ReleaseMigrationKind.None,
                MigrationIdentity = string.Empty
            },
            TxSupport = new ReleaseTxSupportDeclaration
            {
                DeclarationVersion =
                    ReleaseTxSupportDeclaration.CurrentDeclarationVersion,
                Capability = ReleaseTxSupportCapability.None,
                EnablesTransmit = false,
                GrantsTransmitEligibility = false,
                CreatesBrowserTransmitAuthority = false,
                ArmsWatchdog = false
            },
            ReleaseNotes = new ReleaseNotesMetadata
            {
                Title = "Release",
                Summary = "Verified test release."
            }
        };
        return LocalOfflineReleaseBundleVerificationResult.Success(
            report,
            VerifiedReleaseManifestSnapshot.Create(payload),
            new VerifiedOfflineReleaseBundleSnapshot(
                Path.GetFullPath("bundle"),
                [1]));
    }

    private static SignedReleasePackage Package(
        string identity,
        ReleasePackageRole role,
        string fileName,
        char digestCharacter) =>
        new()
        {
            PackageIdentity = identity,
            Role = role,
            FileName = fileName,
            Length = 25,
            Sha256 = new string(digestCharacter, 64)
        };

    private static InstallationSetupState CompleteState(long revision) =>
        new()
        {
            Revision = revision,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            LastCompletedStep = InstallationSetupStep.Administrator,
            Lock = new InstallationSetupLock
            {
                Mode = InstallationSetupLockMode.Complete,
                ClaimedAt = DateTimeOffset.UnixEpoch,
                CompletedAt = DateTimeOffset.UnixEpoch
            },
            Topology = InstallationTopologyKind.PersonalSingleStation,
            CanonicalPublicUrl = "https://radio.example.org",
            Paths = new InstallationPaths(
                "/tmp/config",
                "/tmp/state",
                "/tmp/secrets",
                "/tmp/releases",
                "/tmp/backups",
                "/tmp/logs"),
            UpdateChannel = InstallationUpdateChannel.Stable,
            InstallTransmitSupport = false
        };

    private sealed class PreflightFixture : IAsyncDisposable
    {
        private readonly string m_keyDirectory;

        public PreflightFixture(bool trustEnabled = true)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-release-preflight-{Guid.NewGuid():N}");
            Paths = new InstallationPaths(
                Path.Combine(Root, "config"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(Root, "deployment", "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            Store = new InstallationSetupStore(Paths.SetupStatePath);
            Reader = new ReleaseInstallationStatusReader(Store, Paths);

            m_keyDirectory = Path.Combine(Root, "trust");
            Directory.CreateDirectory(m_keyDirectory);
            string keyPath = Path.Combine(m_keyDirectory, "release-public.pem");
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(TestPublicKeySpkiBase64),
                out _);
            File.WriteAllText(
                keyPath,
                key.ExportSubjectPublicKeyInfoPem(),
                new UTF8Encoding(false));
            SetTrustModes(m_keyDirectory, keyPath);

            ReleaseManifestTrustSettings settings = new()
            {
                VerificationEnabled = trustEnabled,
                Keys = trustEnabled
                    ?
                    [
                        new ReleaseManifestTrustKeySettings
                        {
                            KeyId = TestKeyId,
                            Algorithm =
                                ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                            PublicKeyPath = keyPath
                        }
                    ]
                    : []
            };
            ReleaseManifestTrustRegistry registry = new(
                Options.Create(settings),
                NullLogger<ReleaseManifestTrustRegistry>.Instance);
            SignedReleaseManifestVerificationService manifestService = new(
                registry,
                new SignedReleaseManifestVerifier());
            BundleService = new LocalOfflineReleaseBundleVerificationService(
                manifestService);
            Planner = new OfflineReleaseInstallPreflightPlanner(
                Reader,
                BundleService);
            Console = new OfflineReleaseInstallPreflightConsole(Planner);
        }

        public string Root { get; }
        public InstallationPaths Paths { get; }
        public InstallationSetupStore Store { get; }
        public ReleaseInstallationStatusReader Reader { get; }
        public LocalOfflineReleaseBundleVerificationService BundleService { get; }
        public OfflineReleaseInstallPreflightPlanner Planner { get; }
        public OfflineReleaseInstallPreflightConsole Console { get; }
        public string CurrentPath =>
            Path.Combine(Path.GetDirectoryName(Paths.ReleaseDirectory)!, "current");

        public async Task ConfigureAsync(
            bool complete = true,
            InstallationUpdateChannel channel = InstallationUpdateChannel.Stable,
            string pinnedRelease = "",
            bool installTransmitSupport = false)
        {
            InstallationSetupState initial = await Store.LoadOrCreateAsync();
            _ = await Store.UpdateAsync(
                initial.Revision,
                current => current with
                {
                    LastCompletedStep = complete
                        ? InstallationSetupStep.Administrator
                        : InstallationSetupStep.Paths,
                    Lock = new InstallationSetupLock
                    {
                        Mode = complete
                            ? InstallationSetupLockMode.Complete
                            : InstallationSetupLockMode.Claimed,
                        ClaimedAt = current.CreatedAt,
                        CompletedAt = complete ? current.CreatedAt : null
                    },
                    Topology = InstallationTopologyKind.PersonalSingleStation,
                    CanonicalPublicUrl = "https://radio.example.org",
                    Paths = Paths,
                    UpdateChannel = channel,
                    PinnedRelease = pinnedRelease,
                    InstallTransmitSupport = installTransmitSupport
                });
        }

        public async Task ConfigureAndActivateAsync(
            InstallationUpdateChannel channel = InstallationUpdateChannel.Stable,
            string pinnedRelease = "",
            bool installTransmitSupport = false)
        {
            await ConfigureAsync(
                complete: true,
                channel,
                pinnedRelease,
                installTransmitSupport);
            CreateReleaseRoot();
            string active = AddRelease("aethersdr-8.1.0");
            Directory.CreateSymbolicLink(CurrentPath, active);
        }

        public void CreateReleaseRoot()
        {
            Directory.CreateDirectory(Paths.ReleaseDirectory);
            SetSafeDirectoryMode(Paths.ReleaseDirectory);
        }

        public string AddRelease(string identity)
        {
            string path = Path.Combine(Paths.ReleaseDirectory, identity);
            Directory.CreateDirectory(path);
            SetSafeDirectoryMode(path);
            return path;
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
            return ValueTask.CompletedTask;
        }

        private static void SetTrustModes(string directory, string file)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    file,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        private static void SetSafeDirectoryMode(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);
            }
        }
    }

    private sealed class TestBundle : IDisposable
    {
        private TestBundle(string root)
        {
            Path = System.IO.Path.Combine(
                root,
                $"offline-bundle-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "packages"));
        }

        public string Path { get; }

        public static TestBundle CreateValid(
            string root,
            ReleaseManifestChannel channel,
            bool txSupportCapable,
            string releaseIdentity = "aethersdr-8.2.0")
        {
            TestBundle bundle = new(root);
            string[] relativePaths =
            [
                "packages/aethersdr-gateway-linux-x64.tar.gz",
                "packages/aethersdr-broker-linux-x64.tar.gz",
                "packages/aetherremote-agent-linux-x64.tar.gz",
                "packages/aethersdr-station-engine-linux-x64.tar.gz"
            ];
            byte[][] content =
            [
                Encoding.UTF8.GetBytes("gateway-preflight-package"),
                Encoding.UTF8.GetBytes("broker-preflight-package"),
                Encoding.UTF8.GetBytes("agent-preflight-package"),
                Encoding.UTF8.GetBytes("station-preflight-package")
            ];
            for (int index = 0; index < content.Length; index++)
            {
                bundle.Write(relativePaths[index], content[index]);
            }

            SignedReleaseManifestPayload payload = new()
            {
                SchemaVersion = SignedReleaseManifestPayload.CurrentSchemaVersion,
                ReleaseIdentity = releaseIdentity,
                Version = "8.2.0",
                Channel = channel,
                Architecture = ReleaseManifestArchitecture.LinuxX64,
                Packages =
                [
                    Declaration("gateway-web", ReleasePackageRole.GatewayWeb, relativePaths[0], content[0]),
                    Declaration("broker", ReleasePackageRole.Broker, relativePaths[1], content[1]),
                    Declaration("agent", ReleasePackageRole.AetherRemoteAgent, relativePaths[2], content[2]),
                    Declaration("station", ReleasePackageRole.StationEngine, relativePaths[3], content[3])
                ],
                Configuration = new ReleaseConfigurationCompatibility
                {
                    TargetSchemaVersion = 1,
                    MinimumCompatibleSchemaVersion = 1,
                    MaximumCompatibleSchemaVersion = 2
                },
                Protocol = new ReleaseProtocolCompatibility
                {
                    MinimumVersion = 1,
                    MaximumVersion = 3
                },
                MinimumPreviousVersion = "8.1.0",
                Restart = new ReleaseRestartDeclaration
                {
                    GatewayWeb = true,
                    Broker = true,
                    AetherRemoteAgent = true,
                    StationEngine = true,
                    Host = false
                },
                Migration = new ReleaseMigrationDeclaration
                {
                    Kind = ReleaseMigrationKind.None,
                    FromConfigurationSchemaVersion = null,
                    ToConfigurationSchemaVersion = null,
                    MigrationIdentity = string.Empty
                },
                TxSupport = new ReleaseTxSupportDeclaration
                {
                    DeclarationVersion =
                        ReleaseTxSupportDeclaration.CurrentDeclarationVersion,
                    Capability = txSupportCapable
                        ? ReleaseTxSupportCapability.Available
                        : ReleaseTxSupportCapability.None,
                    EnablesTransmit = false,
                    GrantsTransmitEligibility = false,
                    CreatesBrowserTransmitAuthority = false,
                    ArmsWatchdog = false
                },
                ReleaseNotes = new ReleaseNotesMetadata
                {
                    Title = "AetherSDR 8.2.0",
                    Summary = "Offline installation preflight test vector."
                }
            };
            bundle.WriteSignedManifest(payload);
            return bundle;
        }

        public void Freeze()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }
            foreach (string file in Directory.EnumerateFiles(
                Path,
                "*",
                SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(
                    file,
                    UnixFileMode.UserRead |
                    UnixFileMode.GroupRead |
                    UnixFileMode.OtherRead);
            }
            foreach (string directory in Directory.EnumerateDirectories(
                Path,
                "*",
                SearchOption.AllDirectories)
                .OrderByDescending(value => value.Length))
            {
                SetFrozenDirectoryMode(directory);
            }
            SetFrozenDirectoryMode(Path);
        }

        public void Dispose()
        {
            try
            {
                if (!OperatingSystem.IsWindows() && Directory.Exists(Path))
                {
                    File.SetUnixFileMode(
                        Path,
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
                    foreach (string directory in Directory.EnumerateDirectories(
                        Path,
                        "*",
                        SearchOption.AllDirectories))
                    {
                        File.SetUnixFileMode(
                            directory,
                            UnixFileMode.UserRead |
                            UnixFileMode.UserWrite |
                            UnixFileMode.UserExecute);
                    }
                    foreach (string file in Directory.EnumerateFiles(
                        Path,
                        "*",
                        SearchOption.AllDirectories))
                    {
                        File.SetUnixFileMode(
                            file,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        private void WriteSignedManifest(SignedReleaseManifestPayload payload)
        {
            byte[] privateKey = Convert.FromBase64String(
                TestPrivateKeyPkcs8Base64);
            using ECDsa key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(privateKey, out int bytesRead);
                if (bytesRead != privateKey.Length)
                {
                    throw new InvalidOperationException("Test private key was not fully read.");
                }
                byte[] signingBytes = SignedReleaseManifestJson.CreateSigningBytes(
                    payload,
                    ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                    TestKeyId);
                byte[] signature = key.SignData(
                    signingBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                SignedReleaseManifestDocument document = new()
                {
                    Payload = payload,
                    Signature = new ReleaseManifestSignature
                    {
                        Algorithm =
                            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                        KeyId = TestKeyId,
                        Value = ToBase64Url(signature)
                    }
                };
                Write(
                    LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                    SignedReleaseManifestJson.Serialize(document));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }

        private void Write(string relativePath, byte[] content)
        {
            string path = System.IO.Path.Combine(
                Path,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        private static SignedReleasePackage Declaration(
            string identity,
            ReleasePackageRole role,
            string path,
            byte[] content) =>
            new()
            {
                PackageIdentity = identity,
                Role = role,
                FileName = path,
                Length = content.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(content))
                    .ToLowerInvariant()
            };

        private static void SetFrozenDirectoryMode(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
        }

        private static string ToBase64Url(ReadOnlySpan<byte> value) =>
            Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
    }
}
