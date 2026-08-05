using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Xunit;

namespace AetherSDR.ReleaseBuilder.Tests;

public sealed class ReleaseManifestBuilderTests
{
    [Fact]
    public void StableX64ManifestSelfVerifiesWithProductionVerifier()
    {
        using Fixture fixture = new("linux-x64");
        ReleaseManifestBuildReport report = fixture.Build();

        Assert.True(report.Succeeded);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(4, report.PackageCount);
        Assert.True(report.TotalPackageBytes > 0);
        Assert.Equal("aethersdr-8.2.0", report.ReleaseIdentity);
        AssertManifestVerifies(
            fixture,
            ReleaseManifestArchitecture.LinuxX64,
            InstallationUpdateChannel.Stable,
            pinnedIdentity: string.Empty,
            expectedChannel: "stable");
    }

    [Fact]
    public void BetaArm64ManifestUsesExactArchitectureAndChannel()
    {
        using Fixture fixture = new(
            "linux-arm64",
            version: "8.2.0-beta.1",
            channel: ReleaseBuilderChannel.Beta);

        ReleaseManifestBuildReport report = fixture.Build();

        Assert.True(report.Succeeded);
        Assert.Equal("linux-arm64", report.Architecture);
        AssertManifestVerifies(
            fixture,
            ReleaseManifestArchitecture.LinuxArm64,
            InstallationUpdateChannel.Beta,
            pinnedIdentity: string.Empty,
            expectedChannel: "beta");
    }

    [Fact]
    public void PinnedManifestBindsExactReleaseIdentity()
    {
        using Fixture fixture = new(
            "linux-x64",
            version: "8.2.0-rc.2",
            channel: ReleaseBuilderChannel.Pinned);

        ReleaseManifestBuildReport report = fixture.Build();

        Assert.True(report.Succeeded);
        AssertManifestVerifies(
            fixture,
            ReleaseManifestArchitecture.LinuxX64,
            InstallationUpdateChannel.Pinned,
            pinnedIdentity: "aethersdr-8.2.0-rc.2",
            expectedChannel: "pinned");
    }

    [Theory]
    [InlineData("8.2.0-beta.1", ReleaseBuilderChannel.Stable)]
    [InlineData("8.2.0", ReleaseBuilderChannel.Beta)]
    [InlineData("08.2.0", ReleaseBuilderChannel.Stable)]
    public void InvalidChannelOrVersionRelationshipFailsClosed(
        string version,
        ReleaseBuilderChannel channel)
    {
        using Fixture fixture = new("linux-x64", version, channel);

        ReleaseManifestBuildReport report = fixture.Build();

        Assert.False(report.Succeeded);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(
            ReleaseManifestBuildFailureCode.InvalidRequest,
            report.FailureCode);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void MinimumPreviousVersionMustBeOlderThanTarget()
    {
        using Fixture fixture = new("linux-x64");
        fixture.Request = fixture.Request with
        {
            MinimumPreviousVersion = "8.2.0"
        };

        ReleaseManifestBuildReport report = fixture.Build();

        Assert.False(report.Succeeded);
        Assert.Equal(
            ReleaseManifestBuildFailureCode.InvalidRequest,
            report.FailureCode);
    }

    [Fact]
    public void MissingOrExtraPackageFailsBeforeSigning()
    {
        using Fixture missing = new("linux-x64");
        File.Delete(Path.Combine(
            missing.AssetDirectory,
            "aethersdr-broker-linux-x64.tar.gz"));
        ReleaseManifestBuildReport missingReport = missing.Build();
        Assert.Equal(
            ReleaseManifestBuildFailureCode.InvalidPackageSet,
            missingReport.FailureCode);

        using Fixture extra = new("linux-x64");
        File.WriteAllText(
            Path.Combine(extra.AssetDirectory, "unexpected.txt"),
            "unexpected",
            Encoding.UTF8);
        ReleaseManifestBuildReport extraReport = extra.Build();
        Assert.Equal(
            ReleaseManifestBuildFailureCode.InvalidPackageSet,
            extraReport.FailureCode);
    }

    [Fact]
    public void ExistingOutputIsNeverOverwritten()
    {
        using Fixture fixture = new("linux-x64");
        File.WriteAllText(fixture.ManifestPath, "preserve", Encoding.UTF8);

        ReleaseManifestBuildReport report = fixture.Build();

        Assert.False(report.Succeeded);
        Assert.Equal(
            ReleaseManifestBuildFailureCode.InvalidRequest,
            report.FailureCode);
        Assert.Equal("preserve", File.ReadAllText(fixture.ManifestPath));
    }

    [Fact]
    public void NonP256PrivateKeyFailsClosed()
    {
        using Fixture fixture = new("linux-x64");
        using ECDsa wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        fixture.ReplacePrivateKey(wrongCurve.ExportPkcs8PrivateKeyPem());

        ReleaseManifestBuildReport report = fixture.Build();

        Assert.False(report.Succeeded);
        Assert.Equal(
            ReleaseManifestBuildFailureCode.InvalidSigningKey,
            report.FailureCode);
        Assert.False(File.Exists(fixture.ManifestPath));
    }

    [Fact]
    public void NonPkcs8PemFailsClosed()
    {
        using Fixture fixture = new("linux-x64");
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        fixture.ReplacePrivateKey(key.ExportECPrivateKeyPem());

        ReleaseManifestBuildReport report = fixture.Build();

        Assert.False(report.Succeeded);
        Assert.Equal(
            ReleaseManifestBuildFailureCode.InvalidSigningKey,
            report.FailureCode);
    }

    [Fact]
    public void GroupReadablePrivateKeyFailsOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using Fixture fixture = new("linux-x64");
        File.SetUnixFileMode(
            fixture.PrivateKeyPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead);

        ReleaseManifestBuildReport report = fixture.Build();

        Assert.False(report.Succeeded);
        Assert.Equal(
            ReleaseManifestBuildFailureCode.InvalidSigningKey,
            report.FailureCode);
    }

    [Fact]
    public void SymlinkPrivateKeyFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using Fixture fixture = new("linux-x64");
        string target = fixture.PrivateKeyPath + ".target";
        File.Move(fixture.PrivateKeyPath, target);
        File.CreateSymbolicLink(fixture.PrivateKeyPath, target);

        ReleaseManifestBuildReport report = fixture.Build();

        Assert.False(report.Succeeded);
        Assert.Equal(
            ReleaseManifestBuildFailureCode.InvalidSigningKey,
            report.FailureCode);
    }

    [Fact]
    public void ReportsAndConsoleOutputRedactPathsAndKeyMaterial()
    {
        using Fixture fixture = new("linux-x64");
        ReleaseManifestBuildReport report = fixture.Build();
        string serialized = JsonSerializer.Serialize(report);

        Assert.True(report.Succeeded);
        Assert.DoesNotContain(fixture.Root, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.PrivateKeyPemPrefix,
            serialized,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsoleWritesOneRedactedJsonReport()
    {
        using Fixture fixture = new("linux-x64");
        using StringWriter output = new();

        int exitCode = await ReleaseBuilderConsole.ExecuteAsync(
            fixture.CommandLine(),
            output);
        string json = output.ToString();
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(0, exitCode);
        Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidConsoleArgumentsFailBeforeFilesystemAccess()
    {
        using StringWriter output = new();

        int exitCode = await ReleaseBuilderConsole.ExecuteAsync(
            ["--version", "8.2.0"],
            output);

        Assert.Equal(2, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "invalidRequest",
            document.RootElement.GetProperty("failureCode").GetString());
    }

    [Fact]
    public void ParserRejectsDuplicatesAndUnknownOptions()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReleaseBuilderCommandLine.Parse(
                ["--version", "8.2.0", "--version", "8.2.0"]));
        Assert.Throws<InvalidOperationException>(() =>
            ReleaseBuilderCommandLine.Parse(["--unknown", "value"]));
    }

    private static void AssertManifestVerifies(
        Fixture fixture,
        ReleaseManifestArchitecture architecture,
        InstallationUpdateChannel channel,
        string pinnedIdentity,
        string expectedChannel)
    {
        byte[] manifest = File.ReadAllBytes(fixture.ManifestPath);
        LocalImmutableReleasePackage[] packages = fixture.PackageFiles()
            .Select(package => new LocalImmutableReleasePackage(
                $"packages/{Path.GetFileName(package)}",
                File.ReadAllBytes(package)))
            .ToArray();
        ReleaseManifestVerificationReport verification =
            new SignedReleaseManifestVerifier().Verify(
                manifest,
                packages,
                new ReleaseManifestVerificationContext(
                    architecture,
                    channel,
                    pinnedIdentity,
                    "8.1.0",
                    1,
                    2),
                [
                    new ReleaseManifestVerificationKey(
                        Fixture.KeyId,
                        ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                        fixture.PublicKey)
                ]);

        Assert.True(verification.Succeeded, verification.Message);
        Assert.Equal(expectedChannel, verification.Channel?.ToString().ToLowerInvariant());
        Assert.Equal(4, verification.DeclaredPackageCount);
        Assert.True(verification.TxSupportCapable);

        string text = Encoding.UTF8.GetString(manifest);
        Assert.Contains("\"enablesTransmit\":false", text, StringComparison.Ordinal);
        Assert.Contains("\"armsWatchdog\":false", text, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.PrivateKeyPath, text, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string KeyId = "m8b-release-builder-test";

        private readonly ECDsa m_key;
        private readonly string m_architecture;

        internal Fixture(
            string architecture,
            string version = "8.2.0",
            ReleaseBuilderChannel channel = ReleaseBuilderChannel.Stable)
        {
            m_architecture = architecture;
            Root = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-release-builder-{Guid.NewGuid():N}");
            AssetDirectory = Path.Combine(Root, "assets");
            string keyDirectory = Path.Combine(Root, "key");
            Directory.CreateDirectory(AssetDirectory);
            Directory.CreateDirectory(keyDirectory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    Root,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    AssetDirectory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    keyDirectory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }

            foreach ((string stem, string content) in new[]
            {
                ("aethersdr-gateway", "gateway"),
                ("aethersdr-broker", "broker"),
                ("aetherremote-agent", "agent"),
                ("aethersdr-station-engine", "station")
            })
            {
                File.WriteAllBytes(
                    Path.Combine(AssetDirectory, $"{stem}-{architecture}.tar.gz"),
                    Encoding.UTF8.GetBytes($"{content}-{architecture}-{version}"));
            }

            m_key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            PublicKey = m_key.ExportSubjectPublicKeyInfo();
            PrivateKeyPath = Path.Combine(keyDirectory, "release-private.pem");
            ReplacePrivateKey(m_key.ExportPkcs8PrivateKeyPem());
            PrivateKeyPemPrefix = "MIGH";
            ManifestPath = Path.Combine(
                AssetDirectory,
                $"release-manifest-{architecture}.json");
            Request = new ReleaseManifestBuildRequest(
                AssetDirectory,
                ManifestPath,
                PrivateKeyPath,
                KeyId,
                version,
                channel,
                architecture == "linux-x64"
                    ? ReleaseBuilderArchitecture.LinuxX64
                    : ReleaseBuilderArchitecture.LinuxArm64,
                "8.1.0",
                1,
                1,
                1,
                2,
                3,
                $"AetherSDR {version}",
                "Release builder test vector.");
        }

        internal string Root { get; }
        internal string AssetDirectory { get; }
        internal string ManifestPath { get; }
        internal string PrivateKeyPath { get; }
        internal string PrivateKeyPemPrefix { get; }
        internal byte[] PublicKey { get; }
        internal ReleaseManifestBuildRequest Request { get; set; }

        internal ReleaseManifestBuildReport Build() =>
            new ReleaseManifestBuilder().Build(Request);

        internal string[] PackageFiles() =>
            Directory.EnumerateFiles(AssetDirectory, "*.tar.gz")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

        internal IReadOnlyList<string> CommandLine() =>
        [
            ReleaseBuilderCommandLine.AssetDirectorySwitch,
            AssetDirectory,
            ReleaseBuilderCommandLine.OutputManifestSwitch,
            ManifestPath,
            ReleaseBuilderCommandLine.PrivateKeySwitch,
            PrivateKeyPath,
            ReleaseBuilderCommandLine.KeyIdSwitch,
            KeyId,
            ReleaseBuilderCommandLine.VersionSwitch,
            Request.Version,
            ReleaseBuilderCommandLine.ChannelSwitch,
            Request.Channel switch
            {
                ReleaseBuilderChannel.Stable => "stable",
                ReleaseBuilderChannel.Beta => "beta",
                ReleaseBuilderChannel.Pinned => "pinned",
                _ => throw new InvalidOperationException()
            },
            ReleaseBuilderCommandLine.ArchitectureSwitch,
            m_architecture,
            ReleaseBuilderCommandLine.MinimumPreviousVersionSwitch,
            Request.MinimumPreviousVersion,
            ReleaseBuilderCommandLine.TargetConfigurationSchemaSwitch,
            Request.TargetConfigurationSchemaVersion.ToString(),
            ReleaseBuilderCommandLine.MinimumConfigurationSchemaSwitch,
            Request.MinimumCompatibleConfigurationSchemaVersion.ToString(),
            ReleaseBuilderCommandLine.MaximumConfigurationSchemaSwitch,
            Request.MaximumCompatibleConfigurationSchemaVersion.ToString(),
            ReleaseBuilderCommandLine.MinimumProtocolSwitch,
            Request.MinimumProtocolVersion.ToString(),
            ReleaseBuilderCommandLine.MaximumProtocolSwitch,
            Request.MaximumProtocolVersion.ToString(),
            ReleaseBuilderCommandLine.ReleaseTitleSwitch,
            Request.ReleaseTitle,
            ReleaseBuilderCommandLine.ReleaseSummarySwitch,
            Request.ReleaseSummary
        ];

        internal void ReplacePrivateKey(string pem)
        {
            File.WriteAllText(
                PrivateKeyPath,
                pem,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    PrivateKeyPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public void Dispose()
        {
            m_key.Dispose();
            CryptographicOperations.ZeroMemory(PublicKey);
            try
            {
                if (!OperatingSystem.IsWindows() && Directory.Exists(Root))
                {
                    foreach (string directory in Directory.EnumerateDirectories(
                        Root,
                        "*",
                        SearchOption.AllDirectories)
                        .OrderBy(value => value.Length))
                    {
                        File.SetUnixFileMode(
                            directory,
                            UnixFileMode.UserRead |
                            UnixFileMode.UserWrite |
                            UnixFileMode.UserExecute);
                    }
                    foreach (string file in Directory.EnumerateFiles(
                        Root,
                        "*",
                        SearchOption.AllDirectories))
                    {
                        File.SetUnixFileMode(
                            file,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }
                Directory.Delete(Root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
