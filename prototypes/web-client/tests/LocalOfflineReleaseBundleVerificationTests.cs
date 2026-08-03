using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class LocalOfflineReleaseBundleVerificationTests
{
    private const string TestKeyId = "m8b-offline-test-key";
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
    public void PublicSurfaceCannotDownloadExtractInstallActivateOrReachTx()
    {
        string[] methods = typeof(LocalOfflineReleaseBundleVerificationService)
            .GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["VerifyDirectory", "get_Snapshot"], methods);
        Assert.DoesNotContain(methods, name =>
            name.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Extract", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Install", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Activate", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Transmit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnosticsRegisterOnlyLocalDirectoryReading()
    {
        using VerificationFixture fixture = new(enabled: false);

        LocalOfflineReleaseBundleVerificationDiagnostics snapshot =
            fixture.Service.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.DirectoryReadRegistered);
        Assert.False(snapshot.ArchiveExtractionRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.InstallationRegistered);
        Assert.False(snapshot.ActivationRegistered);
        Assert.False(snapshot.CliCallerRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
    }

    [Fact]
    public void ImmutableDirectoryBundleVerifiesThroughProductionTrust()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        Assert.True(report.Succeeded);
        Assert.Equal(LocalOfflineReleaseBundleFailureCode.None, report.FailureCode);
        Assert.Equal(4, report.PackageCount);
        Assert.True(report.TotalPackageBytes > 0);
        Assert.NotNull(report.Verification);
        Assert.True(report.Verification!.Succeeded);
        Assert.Equal("aethersdr-8.2.0", report.Verification.ReleaseIdentity);
    }

    [Fact]
    public void SuccessfulReportIsRedacted()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());
        string json = JsonSerializer.Serialize(report);

        Assert.True(report.Succeeded);
        Assert.DoesNotContain(bundle.Path, json, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKeyId, json, StringComparison.Ordinal);
        Assert.DoesNotContain("PUBLIC KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("packages/", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            bundle.Payload.Packages[0].Sha256,
            json,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/bundle")]
    [InlineData(" bundle")]
    [InlineData("bundle ")]
    public void NonCanonicalBundlePathsFailClosed(string path)
    {
        using VerificationFixture fixture = new(enabled: true);

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.InvalidBundleDirectory);
    }

    [Fact]
    public void RelativeSegmentsInBundlePathFailClosed()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        string path = System.IO.Path.Combine(bundle.Path, "packages", "..", ".");

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.InvalidBundleDirectory);
    }

    [Fact]
    public void MissingBundleDirectoryFailsClosed()
    {
        using VerificationFixture fixture = new(enabled: true);
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"aethersdr-missing-bundle-{Guid.NewGuid():N}");

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.BundleDirectoryMissing);
    }

    [Fact]
    public void MutableBundleDirectoriesFailClosedOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.BundleNotImmutable);
    }

    [Fact]
    public void MutableBundleFilesFailClosedOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.Freeze();
        string package = bundle.PackagePaths[0];
        File.SetUnixFileMode(
            package,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.BundleNotImmutable);
    }

    [Fact]
    public void SymbolicLinkBundleRootsFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.Freeze();
        string link = bundle.Path + "-link";
        Directory.CreateSymbolicLink(link, bundle.Path);
        try
        {
            LocalOfflineReleaseBundleVerificationReport report =
                fixture.Service.VerifyDirectory(link, Context());

            AssertFailure(
                report,
                LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void SymbolicLinkPackageFilesFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        string target = bundle.PackagePaths[0];
        string link = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(target)!,
            "linked-package.tar.gz");
        File.CreateSymbolicLink(link, target);
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry);
    }

    [Fact]
    public void SymbolicLinkPackageDirectoriesFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        string link = System.IO.Path.Combine(bundle.Path, "linked");
        Directory.CreateSymbolicLink(
            link,
            System.IO.Path.Combine(bundle.Path, "packages"));
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry);
    }

    [Fact]
    public void MissingManifestFailsClosed()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        File.Delete(bundle.ManifestPath);
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.MissingManifest);
    }

    [Fact]
    public void ManifestNameIsExactAndCaseSensitive()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        File.Move(
            bundle.ManifestPath,
            System.IO.Path.Combine(bundle.Path, "Release-Manifest.json"));
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.MissingManifest);
    }

    [Fact]
    public void ExtraFilesFailClosed()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.WriteText("unexpected.txt", "unexpected");
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.UnexpectedBundleContents);
    }

    [Fact]
    public void MissingPackageFilesFailClosedBeforeManifestVerification()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        File.Delete(bundle.PackagePaths[0]);
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.UnexpectedBundleContents);
    }

    [Fact]
    public void EmptyDirectoriesFailClosed()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        Directory.CreateDirectory(System.IO.Path.Combine(bundle.Path, "empty"));
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.UnexpectedBundleContents);
    }

    [Fact]
    public void ControlCharactersInEntryNamesFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.WriteText("packages/bad\nname.tar.gz", "bad");
        File.Delete(bundle.PackagePaths[0]);
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry);
    }

    [Fact]
    public void OversizedManifestFailsClosed()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.WriteBytes(
            LocalOfflineReleaseBundleVerificationService.ManifestFileName,
            new byte[SignedReleaseManifestJson.MaximumManifestBytes + 1]);
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.ManifestTooLarge);
    }

    [Fact]
    public void EmptyManifestFailsClosed()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.WriteBytes(
            LocalOfflineReleaseBundleVerificationService.ManifestFileName,
            []);
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.ManifestTooLarge);
    }

    [Fact]
    public void EmptyPackageFailsClosed()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.WriteBytes(bundle.RelativePackagePaths[0], []);
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.PackageTooLarge);
    }

    [Fact]
    public void OversizedSparsePackageFailsClosedWithoutReadingContent()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        string path = bundle.PackagePaths[0];
        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(
                SignedReleaseManifestVerifier.MaximumDeclaredPackageLength + 1);
        }
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertFailure(
            report,
            LocalOfflineReleaseBundleFailureCode.PackageTooLarge);
    }

    [Fact]
    public void SignedLengthMismatchIsReportedByExistingVerifier()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        SignedReleasePackage[] declarations = bundle.Payload.Packages
            .Select(package => package with { })
            .ToArray();
        declarations[0] = declarations[0] with
        {
            Length = declarations[0].Length + 1
        };
        bundle.WriteSignedManifest(
            bundle.Payload with { Packages = declarations });
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertVerificationFailure(
            report,
            ReleaseManifestFailureCode.PackageSizeMismatch);
    }

    [Fact]
    public void SignedChecksumMismatchIsReportedByExistingVerifier()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        byte[] tampered = Encoding.UTF8.GetBytes("gateway-package-v2");
        tampered[0] ^= 0x20;
        bundle.WriteBytes(bundle.RelativePackagePaths[0], tampered);
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertVerificationFailure(
            report,
            ReleaseManifestFailureCode.PackageSha256Mismatch);
    }

    [Fact]
    public void UnknownSigningKeyIsReportedWithoutKeyDisclosure()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.WriteSignedManifest(bundle.Payload, keyId: "unknown-key");
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());
        string json = JsonSerializer.Serialize(report);

        AssertVerificationFailure(
            report,
            ReleaseManifestFailureCode.UnknownVerificationKey);
        Assert.DoesNotContain("unknown-key", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledProductionTrustFailsBeforeFilesystemAccess()
    {
        using VerificationFixture fixture = new(enabled: false);

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory("not-an-absolute-path", Context());

        AssertVerificationFailure(
            report,
            ReleaseManifestFailureCode.VerificationTrustDisabled);
    }

    [Fact]
    public void MalformedManifestIsReportedByExistingVerifier()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.WriteText(
            LocalOfflineReleaseBundleVerificationService.ManifestFileName,
            "{not-json}");
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        AssertVerificationFailure(
            report,
            ReleaseManifestFailureCode.MalformedManifest);
    }

    [Fact]
    public void IncompatibleLocalContextIsReportedByExistingVerifier()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid();
        bundle.Freeze();
        ReleaseManifestVerificationContext context = Context() with
        {
            Architecture = ReleaseManifestArchitecture.LinuxArm64
        };

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, context);

        AssertVerificationFailure(
            report,
            ReleaseManifestFailureCode.UnsupportedArchitecture);
    }

    [Fact]
    public void MultiMegabytePackagesAreStreamedAndVerified()
    {
        using VerificationFixture fixture = new(enabled: true);
        using TestBundle bundle = TestBundle.CreateValid(
            gatewayContent: new byte[2 * 1024 * 1024]);
        bundle.Freeze();

        LocalOfflineReleaseBundleVerificationReport report =
            fixture.Service.VerifyDirectory(bundle.Path, Context());

        Assert.True(report.Succeeded);
        Assert.True(report.TotalPackageBytes >= 2 * 1024 * 1024);
    }

    private static ReleaseManifestVerificationContext Context() =>
        new(
            ReleaseManifestArchitecture.LinuxX64,
            InstallationUpdateChannel.Stable,
            string.Empty,
            "8.1.0",
            1,
            2);

    private static void AssertFailure(
        LocalOfflineReleaseBundleVerificationReport report,
        LocalOfflineReleaseBundleFailureCode expected)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(expected, report.FailureCode);
        Assert.Null(report.Verification);
    }

    private static void AssertVerificationFailure(
        LocalOfflineReleaseBundleVerificationReport report,
        ReleaseManifestFailureCode expected)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(
            LocalOfflineReleaseBundleFailureCode.VerificationFailed,
            report.FailureCode);
        Assert.NotNull(report.Verification);
        Assert.False(report.Verification!.Succeeded);
        Assert.Equal(expected, report.Verification.FailureCode);
    }

    private sealed class VerificationFixture : IDisposable
    {
        private readonly string m_keyDirectory;

        public VerificationFixture(bool enabled)
        {
            m_keyDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-offline-trust-{Guid.NewGuid():N}");
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
            Service = new LocalOfflineReleaseBundleVerificationService(
                manifestService);
        }

        public LocalOfflineReleaseBundleVerificationService Service { get; }

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
                $"aethersdr-offline-bundle-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "packages"));
        }

        public string Path { get; }
        public string ManifestPath => System.IO.Path.Combine(
            Path,
            LocalOfflineReleaseBundleVerificationService.ManifestFileName);
        public SignedReleaseManifestPayload Payload { get; private set; } = null!;
        public string[] RelativePackagePaths { get; private set; } = [];
        public string[] PackagePaths => RelativePackagePaths
            .Select(relative => System.IO.Path.Combine(
                Path,
                relative.Replace('/', System.IO.Path.DirectorySeparatorChar)))
            .ToArray();

        public static TestBundle CreateValid(byte[]? gatewayContent = null)
        {
            TestBundle bundle = new();
            bundle.RelativePackagePaths =
            [
                "packages/aethersdr-gateway-linux-x64.tar.gz",
                "packages/aethersdr-broker-linux-x64.tar.gz",
                "packages/aetherremote-agent-linux-x64.tar.gz",
                "packages/aethersdr-station-engine-linux-x64.tar.gz"
            ];
            byte[][] contents =
            [
                gatewayContent ?? Encoding.UTF8.GetBytes("gateway-package-v2"),
                Encoding.UTF8.GetBytes("broker-package-v2"),
                Encoding.UTF8.GetBytes("agent-package-v2"),
                Encoding.UTF8.GetBytes("station-package-v2")
            ];
            for (int index = 0; index < contents.Length; index++)
            {
                bundle.WriteBytes(bundle.RelativePackagePaths[index], contents[index]);
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
                        bundle.RelativePackagePaths[0],
                        contents[0]),
                    Declaration(
                        "broker",
                        ReleasePackageRole.Broker,
                        bundle.RelativePackagePaths[1],
                        contents[1]),
                    Declaration(
                        "aetherremote-agent",
                        ReleasePackageRole.AetherRemoteAgent,
                        bundle.RelativePackagePaths[2],
                        contents[2]),
                    Declaration(
                        "station-engine",
                        ReleasePackageRole.StationEngine,
                        bundle.RelativePackagePaths[3],
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
                    Summary = "Offline bundle reader deterministic test vector."
                }
            };
            bundle.WriteSignedManifest(bundle.Payload);
            return bundle;
        }

        public void WriteSignedManifest(
            SignedReleaseManifestPayload payload,
            string keyId = TestKeyId)
        {
            Payload = payload;
            byte[] privateKeyBytes = Convert.FromBase64String(
                TestPrivateKeyPkcs8Base64);
            using ECDsa key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(privateKeyBytes, out int bytesRead);
                Assert.Equal(privateKeyBytes.Length, bytesRead);
                byte[] signingBytes = SignedReleaseManifestJson.CreateSigningBytes(
                    payload,
                    ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                    keyId);
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
                        KeyId = keyId,
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

        public void WriteText(string relativePath, string content) =>
            WriteBytes(relativePath, new UTF8Encoding(false).GetBytes(content));

        public void WriteBytes(string relativePath, byte[] content)
        {
            string path = System.IO.Path.Combine(
                Path,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
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
