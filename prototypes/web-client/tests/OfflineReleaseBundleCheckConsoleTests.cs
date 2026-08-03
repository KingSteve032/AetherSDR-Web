using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class OfflineReleaseBundleCheckConsoleTests
{
    private const string TestKeyId = "m8b-cli-test-key";
    private const string TestPrivateKeyPkcs8Base64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg" +
        "EjRWeJq83vEjRWeJq83vEjRWeJq83vEjRWeJq83vEjShRAN" +
        "CAARawLjuCeZXZ7tsfTRAu+FcuRLUr+ELbhoX/6Hs0fLlSZe" +
        "0NNZYPUqZa65oYGMMs9Ud19Qc/RZMzn4vZv5+EakU";
    private const string TestPublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWsC47gnmV2e" +
        "7bH00QLvhXLkS1K/hC24aF/+h7NHy5UmXtDTWWD1KmWuuaG" +
        "BjDLPVHdfUHP0WTM5+L2b+fhGpFA==";

    [Fact]
    public void ParserWithoutReleaseCommandPreservesApplicationArguments()
    {
        ReleaseUpdateConsoleCommandLine parsed =
            ReleaseUpdateConsoleCommandParser.Parse(
                ["--urls", "http://127.0.0.1:5080"]);

        Assert.Equal(ReleaseUpdateConsoleCommandKind.None, parsed.Command);
        Assert.Equal(
            ["--urls", "http://127.0.0.1:5080"],
            parsed.ApplicationArguments);
    }

    [Fact]
    public void CompleteStableCommandParsesAndPreservesUnrelatedArguments()
    {
        string path = CanonicalUnusedPath();

        ReleaseUpdateConsoleCommandLine parsed =
            ReleaseUpdateConsoleCommandParser.Parse(
            [
                ReleaseUpdateConsoleCommandParser.CheckOfflineBundleSwitch,
                path,
                ReleaseUpdateConsoleCommandParser.InstalledVersionSwitch,
                "8.1.0",
                ReleaseUpdateConsoleCommandParser.UpdateChannelSwitch,
                "stable",
                ReleaseUpdateConsoleCommandParser.ConfigurationSchemaVersionSwitch,
                "1",
                ReleaseUpdateConsoleCommandParser.ProtocolVersionSwitch,
                "2",
                "--urls",
                "http://127.0.0.1:5080"
            ]);

        Assert.Equal(
            ReleaseUpdateConsoleCommandKind.CheckOfflineBundle,
            parsed.Command);
        Assert.Equal(path, parsed.BundleDirectory);
        Assert.Equal("8.1.0", parsed.InstalledVersion);
        Assert.Equal(InstallationUpdateChannel.Stable, parsed.UpdateChannel);
        Assert.Equal(string.Empty, parsed.PinnedReleaseIdentity);
        Assert.Equal(1, parsed.ConfigurationSchemaVersion);
        Assert.Equal(2, parsed.ProtocolVersion);
        Assert.Equal(
            ["--urls", "http://127.0.0.1:5080"],
            parsed.ApplicationArguments);
    }

    [Fact]
    public void CompletePinnedCommandRequiresCanonicalReleaseIdentity()
    {
        ReleaseUpdateConsoleCommandLine parsed = ParseCommand(
            InstallationUpdateChannel.Pinned,
            pinnedIdentity: "aethersdr-8.2.0");

        Assert.Equal(InstallationUpdateChannel.Pinned, parsed.UpdateChannel);
        Assert.Equal("aethersdr-8.2.0", parsed.PinnedReleaseIdentity);
    }

    [Fact]
    public void ReleaseOptionsWithoutCommandFailClosed()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(
            [
                ReleaseUpdateConsoleCommandParser.InstalledVersionSwitch,
                "8.1.0"
            ]));

        Assert.Contains("require", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateCommandFailsClosed()
    {
        string path = CanonicalUnusedPath();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(
            [
                ReleaseUpdateConsoleCommandParser.CheckOfflineBundleSwitch,
                path,
                ReleaseUpdateConsoleCommandParser.CheckOfflineBundleSwitch,
                path
            ]));

        Assert.Contains("Only one", exception.Message);
    }

    [Fact]
    public void DuplicateOptionFailsClosed()
    {
        string path = CanonicalUnusedPath();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(
            [
                ReleaseUpdateConsoleCommandParser.CheckOfflineBundleSwitch,
                path,
                ReleaseUpdateConsoleCommandParser.InstalledVersionSwitch,
                "8.1.0",
                ReleaseUpdateConsoleCommandParser.InstalledVersionSwitch,
                "8.1.0"
            ]));

        Assert.Contains("more than once", exception.Message);
    }

    [Theory]
    [InlineData("installed")]
    [InlineData("channel")]
    [InlineData("configuration")]
    [InlineData("protocol")]
    public void EveryCompatibilityInputIsRequired(string omitted)
    {
        List<string> arguments =
        [
            ReleaseUpdateConsoleCommandParser.CheckOfflineBundleSwitch,
            CanonicalUnusedPath()
        ];
        if (omitted != "installed")
        {
            arguments.Add(ReleaseUpdateConsoleCommandParser.InstalledVersionSwitch);
            arguments.Add("8.1.0");
        }
        if (omitted != "channel")
        {
            arguments.Add(ReleaseUpdateConsoleCommandParser.UpdateChannelSwitch);
            arguments.Add("stable");
        }
        if (omitted != "configuration")
        {
            arguments.Add(
                ReleaseUpdateConsoleCommandParser.ConfigurationSchemaVersionSwitch);
            arguments.Add("1");
        }
        if (omitted != "protocol")
        {
            arguments.Add(ReleaseUpdateConsoleCommandParser.ProtocolVersionSwitch);
            arguments.Add("2");
        }

        Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(arguments));
    }

    [Theory]
    [InlineData("")]
    [InlineData("8.1")]
    [InlineData("08.1.0")]
    [InlineData("8.1.0 ")]
    [InlineData("8.1.0-01")]
    public void InvalidInstalledSemanticVersionsFailClosed(string version)
    {
        List<string> arguments = CompleteArguments();
        int index = arguments.IndexOf(
            ReleaseUpdateConsoleCommandParser.InstalledVersionSwitch);
        arguments[index + 1] = version;

        Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(arguments));
    }

    [Theory]
    [InlineData("Stable")]
    [InlineData("preview")]
    [InlineData("")]
    public void UnsupportedUpdateChannelsFailClosed(string channel)
    {
        List<string> arguments = CompleteArguments();
        int index = arguments.IndexOf(
            ReleaseUpdateConsoleCommandParser.UpdateChannelSwitch);
        arguments[index + 1] = channel;

        Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(arguments));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("1000001")]
    [InlineData("text")]
    public void InvalidCompatibilityVersionsFailClosed(string value)
    {
        List<string> configuration = CompleteArguments();
        int configurationIndex = configuration.IndexOf(
            ReleaseUpdateConsoleCommandParser.ConfigurationSchemaVersionSwitch);
        configuration[configurationIndex + 1] = value;
        Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(configuration));

        List<string> protocol = CompleteArguments();
        int protocolIndex = protocol.IndexOf(
            ReleaseUpdateConsoleCommandParser.ProtocolVersionSwitch);
        protocol[protocolIndex + 1] = value;
        Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(protocol));
    }

    [Fact]
    public void PinnedChannelRequiresPinnedIdentity()
    {
        List<string> arguments = CompleteArguments();
        int channelIndex = arguments.IndexOf(
            ReleaseUpdateConsoleCommandParser.UpdateChannelSwitch);
        arguments[channelIndex + 1] = "pinned";

        Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(arguments));
    }

    [Fact]
    public void NonPinnedChannelRejectsPinnedIdentity()
    {
        List<string> arguments = CompleteArguments();
        arguments.Add(
            ReleaseUpdateConsoleCommandParser.PinnedReleaseIdentitySwitch);
        arguments.Add("aethersdr-8.2.0");

        Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(arguments));
    }

    [Theory]
    [InlineData(" aethersdr-8.2.0")]
    [InlineData("aethersdr-8.2.0 ")]
    public void NonCanonicalPinnedIdentityFailsClosed(string identity)
    {
        List<string> arguments = CompleteArguments();
        int channelIndex = arguments.IndexOf(
            ReleaseUpdateConsoleCommandParser.UpdateChannelSwitch);
        arguments[channelIndex + 1] = "pinned";
        arguments.Add(
            ReleaseUpdateConsoleCommandParser.PinnedReleaseIdentitySwitch);
        arguments.Add(identity);

        Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(arguments));
    }

    [Theory]
    [InlineData("relative/bundle")]
    [InlineData(" /tmp/bundle")]
    [InlineData("/tmp/bundle ")]
    public void NonCanonicalBundlePathsFailAtCliBoundary(string path)
    {
        List<string> arguments = CompleteArguments();
        arguments[1] = path;

        Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(arguments));
    }

    [Fact]
    public void PublicSurfaceContainsOnlyReadOnlyExecutionAndDiagnostics()
    {
        string[] methods = typeof(OfflineReleaseBundleCheckConsole)
            .GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ExecuteAsync", "get_Snapshot"], methods);
        Assert.DoesNotContain(methods, name =>
            name.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Extract", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Stage", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Install", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Activate", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Rollback", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Transmit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnosticsRegisterOnlyReadOnlyCliCheck()
    {
        using ConsoleFixture fixture = new(enabled: false);
        OfflineReleaseBundleCheckConsoleDiagnostics snapshot =
            fixture.Console.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.LocalDirectoryReadRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.ArchiveExtractionRegistered);
        Assert.False(snapshot.StagingRegistered);
        Assert.False(snapshot.InstallationRegistered);
        Assert.False(snapshot.ActivationRegistered);
        Assert.False(snapshot.RollbackRegistered);
        Assert.False(snapshot.MigrationRegistered);
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
    public async Task ValidBundleReturnsSuccessExitCodeAndRedactedJson()
    {
        using ConsoleFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.Freeze();
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(bundle.Path),
            output);
        string json = output.ToString();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(OfflineReleaseBundleCheckConsole.SuccessExitCode, exitCode);
        Assert.True(root.GetProperty("succeeded").GetBoolean());
        Assert.Equal(exitCode, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("none", root.GetProperty("failureCode").GetString());
        Assert.Equal(4, root.GetProperty("packageCount").GetInt32());
        Assert.True(
            root.GetProperty("verification")
                .GetProperty("succeeded")
                .GetBoolean());
        Assert.DoesNotContain(bundle.Path, json, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKeyId, json, StringComparison.Ordinal);
        Assert.DoesNotContain("PUBLIC KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("packages/", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            bundle.Payload.Packages[0].Sha256,
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerificationFailureReturnsDeterministicNonzeroExitCode()
    {
        using ConsoleFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.Freeze();
        using StringWriter output = new();
        ReleaseUpdateConsoleCommandLine command = Command(bundle.Path) with
        {
            InstalledVersion = "7.0.0"
        };

        int exitCode = await fixture.Console.ExecuteAsync(command, output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(
            OfflineReleaseBundleCheckConsole.VerificationFailedExitCode,
            exitCode);
        Assert.False(document.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal(
            "verificationFailed",
            document.RootElement.GetProperty("failureCode").GetString());
        Assert.Equal(
            "unsupportedPreviousVersionTransition",
            document.RootElement
                .GetProperty("verification")
                .GetProperty("failureCode")
                .GetString());
    }

    [Fact]
    public async Task DisabledTrustRejectsBeforeBundleFilesystemAccess()
    {
        using ConsoleFixture fixture = new(enabled: false);
        string missing = CanonicalUnusedPath();
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(missing),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(
            OfflineReleaseBundleCheckConsole.VerificationFailedExitCode,
            exitCode);
        Assert.Equal(
            "verificationFailed",
            document.RootElement.GetProperty("failureCode").GetString());
        Assert.Equal(
            "verificationTrustDisabled",
            document.RootElement
                .GetProperty("verification")
                .GetProperty("failureCode")
                .GetString());
        Assert.DoesNotContain(missing, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentArchitectureIsPartOfVerificationContext()
    {
        using ConsoleFixture fixture = new(
            enabled: true,
            architecture: ReleaseManifestArchitecture.LinuxArm64);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.Freeze();
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(bundle.Path),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(
            OfflineReleaseBundleCheckConsole.VerificationFailedExitCode,
            exitCode);
        Assert.Equal(
            "unsupportedArchitecture",
            document.RootElement
                .GetProperty("verification")
                .GetProperty("failureCode")
                .GetString());
    }

    [Fact]
    public async Task WrongCommandFailsBeforeAnyRead()
    {
        using ConsoleFixture fixture = new(enabled: false);
        using StringWriter output = new();
        ReleaseUpdateConsoleCommandLine command =
            ReleaseUpdateConsoleCommandLine.None([]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Console.ExecuteAsync(command, output));
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task CancellationFailsBeforeAnyRead()
    {
        using ConsoleFixture fixture = new(enabled: false);
        using StringWriter output = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Console.ExecuteAsync(
                Command(CanonicalUnusedPath()),
                output,
                cancellation.Token));
        Assert.Equal(string.Empty, output.ToString());
    }

    private static ReleaseUpdateConsoleCommandLine ParseCommand(
        InstallationUpdateChannel channel,
        string pinnedIdentity = "")
    {
        List<string> arguments = CompleteArguments();
        int channelIndex = arguments.IndexOf(
            ReleaseUpdateConsoleCommandParser.UpdateChannelSwitch);
        arguments[channelIndex + 1] = channel switch
        {
            InstallationUpdateChannel.Stable => "stable",
            InstallationUpdateChannel.Beta => "beta",
            InstallationUpdateChannel.Pinned => "pinned",
            _ => throw new InvalidOperationException()
        };
        if (channel == InstallationUpdateChannel.Pinned)
        {
            arguments.Add(
                ReleaseUpdateConsoleCommandParser.PinnedReleaseIdentitySwitch);
            arguments.Add(pinnedIdentity);
        }
        return ReleaseUpdateConsoleCommandParser.Parse(arguments);
    }

    private static List<string> CompleteArguments() =>
    [
        ReleaseUpdateConsoleCommandParser.CheckOfflineBundleSwitch,
        CanonicalUnusedPath(),
        ReleaseUpdateConsoleCommandParser.InstalledVersionSwitch,
        "8.1.0",
        ReleaseUpdateConsoleCommandParser.UpdateChannelSwitch,
        "stable",
        ReleaseUpdateConsoleCommandParser.ConfigurationSchemaVersionSwitch,
        "1",
        ReleaseUpdateConsoleCommandParser.ProtocolVersionSwitch,
        "2"
    ];

    private static ReleaseUpdateConsoleCommandLine Command(string path) =>
        new(
            ReleaseUpdateConsoleCommandKind.CheckOfflineBundle,
            path,
            "8.1.0",
            InstallationUpdateChannel.Stable,
            string.Empty,
            1,
            2,
            []);

    private static string CanonicalUnusedPath() =>
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"aethersdr-offline-cli-{Guid.NewGuid():N}");

    private sealed class ConsoleFixture : IDisposable
    {
        private readonly string m_keyDirectory;

        public ConsoleFixture(
            bool enabled,
            ReleaseManifestArchitecture architecture =
                ReleaseManifestArchitecture.LinuxX64)
        {
            m_keyDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-offline-cli-trust-{Guid.NewGuid():N}");
            Directory.CreateDirectory(m_keyDirectory);
            string keyPath = System.IO.Path.Combine(
                m_keyDirectory,
                "release-public.pem");
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(TestPublicKeySpkiBase64),
                out _);
            File.WriteAllText(
                keyPath,
                key.ExportSubjectPublicKeyInfoPem(),
                new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    m_keyDirectory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    keyPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            ReleaseManifestTrustSettings settings = new()
            {
                VerificationEnabled = enabled,
                Keys = enabled
                    ?
                    [
                        new ReleaseManifestTrustKeySettings
                        {
                            KeyId = TestKeyId,
                            Algorithm =
                                ReleaseManifestSignatureAlgorithm
                                    .EcdsaP256Sha256,
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
            LocalOfflineReleaseBundleVerificationService bundleService = new(
                manifestService);
            Console = new OfflineReleaseBundleCheckConsole(
                bundleService,
                () => architecture);
        }

        public OfflineReleaseBundleCheckConsole Console { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(m_keyDirectory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed class TestBundle : IDisposable
    {
        private TestBundle()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-offline-cli-bundle-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "packages"));
        }

        public string Path { get; }
        public SignedReleaseManifestPayload Payload { get; private set; } = null!;

        public static TestBundle CreateValid()
        {
            TestBundle bundle = new();
            string[] relativePaths =
            [
                "packages/aethersdr-gateway-linux-x64.tar.gz",
                "packages/aethersdr-broker-linux-x64.tar.gz",
                "packages/aetherremote-agent-linux-x64.tar.gz",
                "packages/aethersdr-station-engine-linux-x64.tar.gz"
            ];
            byte[][] contents =
            [
                Encoding.UTF8.GetBytes("gateway-package-v2"),
                Encoding.UTF8.GetBytes("broker-package-v2"),
                Encoding.UTF8.GetBytes("agent-package-v2"),
                Encoding.UTF8.GetBytes("station-package-v2")
            ];
            for (int index = 0; index < contents.Length; index++)
            {
                bundle.WriteBytes(relativePaths[index], contents[index]);
            }

            bundle.Payload = new SignedReleaseManifestPayload
            {
                SchemaVersion = SignedReleaseManifestPayload.CurrentSchemaVersion,
                ReleaseIdentity = "aethersdr-8.2.0",
                Version = "8.2.0",
                Channel = ReleaseManifestChannel.Stable,
                Architecture = ReleaseManifestArchitecture.LinuxX64,
                Packages =
                [
                    Declaration(
                        "gateway-web",
                        ReleasePackageRole.GatewayWeb,
                        relativePaths[0],
                        contents[0]),
                    Declaration(
                        "broker",
                        ReleasePackageRole.Broker,
                        relativePaths[1],
                        contents[1]),
                    Declaration(
                        "aetherremote-agent",
                        ReleasePackageRole.AetherRemoteAgent,
                        relativePaths[2],
                        contents[2]),
                    Declaration(
                        "station-engine",
                        ReleasePackageRole.StationEngine,
                        relativePaths[3],
                        contents[3])
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
                    Capability = ReleaseTxSupportCapability.Available,
                    EnablesTransmit = false,
                    GrantsTransmitEligibility = false,
                    CreatesBrowserTransmitAuthority = false,
                    ArmsWatchdog = false
                },
                ReleaseNotes = new ReleaseNotesMetadata
                {
                    Title = "AetherSDR 8.2.0",
                    Summary = "Read-only offline bundle CLI test vector."
                }
            };
            bundle.WriteSignedManifest();
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
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);
            }
            File.SetUnixFileMode(
                Path,
                UnixFileMode.UserRead |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
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

        private void WriteSignedManifest()
        {
            byte[] privateKeyBytes = Convert.FromBase64String(
                TestPrivateKeyPkcs8Base64);
            using ECDsa key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(privateKeyBytes, out int bytesRead);
                Assert.Equal(privateKeyBytes.Length, bytesRead);
                byte[] signingBytes = SignedReleaseManifestJson.CreateSigningBytes(
                    Payload,
                    ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                    TestKeyId);
                byte[] signature = key.SignData(
                    signingBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                SignedReleaseManifestDocument document = new()
                {
                    Payload = Payload,
                    Signature = new ReleaseManifestSignature
                    {
                        Algorithm =
                            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                        KeyId = TestKeyId,
                        Value = ToBase64Url(signature)
                    }
                };
                WriteBytes(
                    LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                    SignedReleaseManifestJson.Serialize(document));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKeyBytes);
            }
        }

        private void WriteBytes(string relativePath, byte[] content)
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
            string relativePath,
            byte[] content) =>
            new()
            {
                PackageIdentity = identity,
                Role = role,
                FileName = relativePath,
                Length = content.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(content))
                    .ToLowerInvariant()
            };

        private static string ToBase64Url(ReadOnlySpan<byte> value) =>
            Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
    }
}
