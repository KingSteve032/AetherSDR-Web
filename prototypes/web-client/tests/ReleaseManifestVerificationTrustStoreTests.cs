using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class ReleaseManifestVerificationTrustStoreTests
{
    private const string TestKeyId = "m8b-release-test-key";
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
    public void PublicSurfaceHasNoSignerDownloaderInstallerOrActivationMethod()
    {
        string[] registryMethods = typeof(ReleaseManifestTrustRegistry)
            .GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] serviceMethods = typeof(SignedReleaseManifestVerificationService)
            .GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["get_Snapshot"], registryMethods);
        Assert.Equal(["VerifyLocal", "get_Snapshot"], serviceMethods);
        Assert.Null(typeof(ReleaseManifestTrustKeySettings).GetProperty(
            "PrivateKeyPath"));
        Assert.DoesNotContain(
            serviceMethods,
            name => name.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Install", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Activate", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Stop", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Radio", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Watchdog", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Transmit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownConfigurationPropertiesFailClosed()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ReleaseManifestTrustSettings.SectionName}:" +
                    "VerificatonEnabled"] = "true"
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => configuration
                .GetSection(ReleaseManifestTrustSettings.SectionName)
                .Get<ReleaseManifestTrustSettings>(options =>
                    options.ErrorOnUnknownConfiguration = true));

        Assert.Contains("VerificatonEnabled", exception.Message);
    }

    [Fact]
    public void DefaultsRegisterLocalBoundaryButExposeNoVerificationCapability()
    {
        ReleaseManifestTrustRegistry registry = CreateRegistry(
            new ReleaseManifestTrustSettings());
        SignedReleaseManifestVerificationService service = CreateService(registry);
        VerificationVector vector = CreateVector();

        ReleaseManifestVerificationReport report = service.VerifyLocal(
            vector.Manifest,
            vector.Packages,
            vector.Context);

        Assert.False(registry.Snapshot.VerificationEnabled);
        Assert.False(registry.Snapshot.SignatureVerificationAvailable);
        Assert.Equal(0, registry.Snapshot.TrustedKeyCount);
        Assert.Empty(registry.Snapshot.TrustedKeys);
        Assert.Equal("disabled", registry.Snapshot.Reason);
        Assert.True(service.Snapshot.Registered);
        Assert.False(service.Snapshot.LocalVerificationAvailable);
        Assert.False(service.Snapshot.NetworkDownloadRegistered);
        Assert.False(service.Snapshot.InstallationRegistered);
        Assert.False(service.Snapshot.ActivationRegistered);
        Assert.False(report.Succeeded);
        Assert.Equal(
            ReleaseManifestFailureCode.VerificationTrustDisabled,
            report.FailureCode);
    }

    [Fact]
    public void EnabledVerificationRequiresAtLeastOneTrustAnchor()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(new ReleaseManifestTrustSettings
            {
                VerificationEnabled = true
            }));

        Assert.Contains("at least one", exception.Message);
    }

    [Fact]
    public void NullTrustEntriesFailClosed()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(new ReleaseManifestTrustSettings
            {
                Keys = [null!]
            }));

        Assert.Contains("null entry", exception.Message);
    }

    [Fact]
    public void TrustStoreIsBounded()
    {
        ReleaseManifestTrustKeySettings[] keys = Enumerable
            .Range(0, ReleaseManifestTrustRegistry.MaximumTrustedKeys + 1)
            .Select(index => new ReleaseManifestTrustKeySettings
            {
                KeyId = $"key-{index}",
                Algorithm = ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                PublicKeyPath = $"/tmp/release-key-{index}.pem"
            })
            .ToArray();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(new ReleaseManifestTrustSettings
            {
                Keys = keys
            }));

        Assert.Contains("at most", exception.Message);
    }

    [Fact]
    public void DuplicateKeyIdentifiersFailClosed()
    {
        using TempDirectory directory = new();
        using ECDsa first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string firstPath = directory.WritePublicKey("first.pem", first);
        string secondPath = directory.WritePublicKey("second.pem", second);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("duplicate", firstPath),
                ("duplicate", secondPath))));

        Assert.Contains("Duplicate", exception.Message);
    }

    [Fact]
    public void DuplicatePublicKeyPathsFailClosed()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePublicKey("key.pem", key);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path),
                ("key-b", path))));

        Assert.Contains("distinct", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("key/name")]
    [InlineData("key name")]
    public void NonCanonicalKeyIdentifiersFailClosed(string keyId)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                (keyId, "/tmp/release-key.pem"))));

        Assert.Contains("key ID", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidKeyIdentifiersAreNotEchoedIntoErrors()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("bad\r\nforged-log-entry", "/tmp/release-key.pem"))));

        Assert.DoesNotContain("forged-log-entry", exception.Message);
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\n', exception.Message);
    }

    [Theory]
    [InlineData(ReleaseManifestSignatureAlgorithm.Unknown)]
    [InlineData(ReleaseManifestSignatureAlgorithm.RsaPssSha256)]
    public void UnsupportedTrustAlgorithmsFailClosed(
        ReleaseManifestSignatureAlgorithm algorithm)
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePublicKey("key.pem", key);
        ReleaseManifestTrustSettings settings = Settings(
            enabled: false,
            ("key-a", path));
        settings.Keys[0].Algorithm = algorithm;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(settings));

        Assert.Contains("unsupported", exception.Message);
    }

    [Fact]
    public void RelativePublicKeyPathsFailClosed()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", "relative/key.pem"))));

        Assert.Contains("absolute", exception.Message);
    }

    [Fact]
    public void RelativeSegmentsInPublicKeyPathsFailClosed()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "unused", "..", "key.pem");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("relative path segments", exception.Message);
    }

    [Fact]
    public void MissingPublicKeyFilesFailClosed()
    {
        using TempDirectory directory = new();
        string missing = Path.Combine(directory.Path, "missing.pem");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", missing))));

        Assert.Contains("does not exist", exception.Message);
    }

    [Fact]
    public void EmptyTrustAnchorsFailClosed()
    {
        using TempDirectory directory = new();
        string path = directory.WriteBytes("empty.pem", []);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("1 through", exception.Message);
    }

    [Fact]
    public void OversizedTrustAnchorsFailClosed()
    {
        using TempDirectory directory = new();
        string path = directory.WriteBytes(
            "oversized.pem",
            new byte[ReleaseManifestTrustRegistry.MaximumPublicKeyFileBytes + 1]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("bytes", exception.Message);
    }

    [Fact]
    public void SymbolicLinkTrustAnchorsFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string target = directory.WritePublicKey("target.pem", key);
        string link = Path.Combine(directory.Path, "link.pem");
        File.CreateSymbolicLink(link, target);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", link))));

        Assert.Contains("non-symlink", exception.Message);
    }

    [Fact]
    public void SymbolicLinkTrustDirectoriesFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempDirectory directory = new();
        string realDirectory = Path.Combine(directory.Path, "real");
        Directory.CreateDirectory(realDirectory);
        File.SetUnixFileMode(
            realDirectory,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string target = Path.Combine(realDirectory, "key.pem");
        File.WriteAllText(target, key.ExportSubjectPublicKeyInfoPem());
        File.SetUnixFileMode(
            target,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string linkDirectory = Path.Combine(directory.Path, "linked");
        Directory.CreateSymbolicLink(linkDirectory, realDirectory);
        string linkedKey = Path.Combine(linkDirectory, "key.pem");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", linkedKey))));

        Assert.Contains("non-symlink directory", exception.Message);
    }

    [Fact]
    public void GroupWritableTrustAnchorsFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePublicKey("key.pem", key);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupWrite);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("writable", exception.Message);
    }

    [Fact]
    public void GroupWritableTrustDirectoriesFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePublicKey("key.pem", key);
        File.SetUnixFileMode(
            directory.Path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("directory", exception.Message);
        Assert.Contains("writable", exception.Message);
    }

    [Fact]
    public void PrivateKeysAreNeverAcceptedAsTrustAnchors()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WriteText(
            "private.pem",
            key.ExportPkcs8PrivateKeyPem());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("private keys are forbidden", exception.Message);
    }

    [Fact]
    public void MultiplePemBlocksAndTrailingDataFailClosed()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportSubjectPublicKeyInfoPem();
        string path = directory.WriteText(
            "multiple.pem",
            pem + Environment.NewLine + pem);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("exactly one", exception.Message);
    }

    [Fact]
    public void MalformedPemTrustAnchorsFailClosed()
    {
        using TempDirectory directory = new();
        string path = directory.WriteText(
            "malformed.pem",
            "-----BEGIN PUBLIC KEY-----\nnot-base64!\n-----END PUBLIC KEY-----\n");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("PEM", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidUtf8TrustAnchorsFailClosed()
    {
        using TempDirectory directory = new();
        string path = directory.WriteBytes("invalid.pem", [0xff, 0xfe, 0xfd]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("could not be loaded", exception.Message);
    }

    [Fact]
    public void NonP256EcKeysFailClosed()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string path = directory.WritePublicKey("p384.pem", key);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("P-256", exception.Message);
    }

    [Fact]
    public void DiagnosticsNeverExposePathsOrPublicKeyMaterial()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePublicKey("release-key.pem", key);
        string pem = key.ExportSubjectPublicKeyInfoPem();
        ReleaseManifestTrustRegistry registry = CreateRegistry(
            Settings(enabled: false, (TestKeyId, path)));

        string diagnostics = JsonSerializer.Serialize(registry.Snapshot);

        Assert.Contains(TestKeyId, diagnostics);
        Assert.Matches(".*[0-9A-F]{24}.*", diagnostics);
        Assert.DoesNotContain(path, diagnostics);
        Assert.DoesNotContain("PUBLIC KEY", diagnostics);
        Assert.DoesNotContain(pem.Trim(), diagnostics);
    }

    [Fact]
    public void ConfiguredKeysRemainUnavailableWhileVerificationIsDisabled()
    {
        using TempDirectory directory = new();
        string path = directory.WriteDeterministicPublicKey("release-key.pem");
        ReleaseManifestTrustRegistry registry = CreateRegistry(
            Settings(enabled: false, (TestKeyId, path)));
        SignedReleaseManifestVerificationService service = CreateService(registry);
        VerificationVector vector = CreateVector();

        ReleaseManifestVerificationReport report = service.VerifyLocal(
            vector.Manifest,
            vector.Packages,
            vector.Context);

        Assert.False(registry.Snapshot.SignatureVerificationAvailable);
        Assert.Equal(1, registry.Snapshot.TrustedKeyCount);
        Assert.False(service.Snapshot.LocalVerificationAvailable);
        Assert.Equal(
            ReleaseManifestFailureCode.VerificationTrustDisabled,
            report.FailureCode);
    }

    [Fact]
    public void EnabledTrustStoreVerifiesDeterministicLocalManifestOnly()
    {
        using TempDirectory directory = new();
        string path = directory.WriteDeterministicPublicKey("release-key.pem");
        ReleaseManifestTrustRegistry registry = CreateRegistry(
            Settings(enabled: true, (TestKeyId, path)));
        SignedReleaseManifestVerificationService service = CreateService(registry);
        VerificationVector vector = CreateVector();

        ReleaseManifestVerificationReport report = service.VerifyLocal(
            vector.Manifest,
            vector.Packages,
            vector.Context);

        Assert.True(registry.Snapshot.VerificationEnabled);
        Assert.True(registry.Snapshot.SignatureVerificationAvailable);
        Assert.True(service.Snapshot.LocalVerificationAvailable);
        Assert.True(report.Succeeded);
        Assert.Equal(ReleaseManifestFailureCode.None, report.FailureCode);
        Assert.True(report.TxSupportCapable);
        Assert.False(service.Snapshot.NetworkDownloadRegistered);
        Assert.False(service.Snapshot.InstallationRegistered);
        Assert.False(service.Snapshot.ActivationRegistered);
    }

    [Fact]
    public void ManifestForUnknownKeyFailsClosedThroughProductionComposition()
    {
        using TempDirectory directory = new();
        string path = directory.WriteDeterministicPublicKey("release-key.pem");
        ReleaseManifestTrustRegistry registry = CreateRegistry(
            Settings(enabled: true, ("different-key", path)));
        SignedReleaseManifestVerificationService service = CreateService(registry);
        VerificationVector vector = CreateVector();

        ReleaseManifestVerificationReport report = service.VerifyLocal(
            vector.Manifest,
            vector.Packages,
            vector.Context);

        Assert.False(report.Succeeded);
        Assert.Equal(
            ReleaseManifestFailureCode.UnknownVerificationKey,
            report.FailureCode);
        Assert.DoesNotContain("different-key", report.Message);
        Assert.DoesNotContain(TestKeyId, report.Message);
    }

    [Fact]
    public void InvalidManifestSignatureFailsClosedThroughProductionComposition()
    {
        using TempDirectory directory = new();
        string path = directory.WriteDeterministicPublicKey("release-key.pem");
        ReleaseManifestTrustRegistry registry = CreateRegistry(
            Settings(enabled: true, (TestKeyId, path)));
        SignedReleaseManifestVerificationService service = CreateService(registry);
        VerificationVector vector = CreateVector();
        byte[] tampered = vector.Manifest.ToArray();
        tampered[^3] = tampered[^3] == (byte)'A' ? (byte)'B' : (byte)'A';

        ReleaseManifestVerificationReport report = service.VerifyLocal(
            tampered,
            vector.Packages,
            vector.Context);

        Assert.False(report.Succeeded);
        Assert.Contains(
            report.FailureCode,
            new[]
            {
                ReleaseManifestFailureCode.InvalidSignature,
                ReleaseManifestFailureCode.MalformedManifest
            });
    }

    private static ReleaseManifestTrustRegistry CreateRegistry(
        ReleaseManifestTrustSettings settings) =>
        new(
            Options.Create(settings),
            NullLogger<ReleaseManifestTrustRegistry>.Instance);

    private static SignedReleaseManifestVerificationService CreateService(
        ReleaseManifestTrustRegistry registry) =>
        new(registry, new SignedReleaseManifestVerifier());

    private static ReleaseManifestTrustSettings Settings(
        bool enabled,
        params (string KeyId, string Path)[] keys) =>
        new()
        {
            VerificationEnabled = enabled,
            Keys = keys
                .Select(key => new ReleaseManifestTrustKeySettings
                {
                    KeyId = key.KeyId,
                    Algorithm =
                        ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                    PublicKeyPath = key.Path
                })
                .ToArray()
        };

    private static VerificationVector CreateVector()
    {
        LocalImmutableReleasePackage[] packages =
        [
            Package("packages/gateway.tar.gz", "gateway"),
            Package("packages/broker.tar.gz", "broker"),
            Package("packages/agent.tar.gz", "agent"),
            Package("packages/station.tar.gz", "station")
        ];
        SignedReleaseManifestPayload payload = new()
        {
            SchemaVersion = SignedReleaseManifestPayload.CurrentSchemaVersion,
            ReleaseIdentity = "aethersdr-8.1.1",
            Version = "8.1.1",
            Channel = ReleaseManifestChannel.Stable,
            Architecture = ReleaseManifestArchitecture.LinuxX64,
            Packages =
            [
                Declaration("gateway", ReleasePackageRole.GatewayWeb, packages[0]),
                Declaration("broker", ReleasePackageRole.Broker, packages[1]),
                Declaration(
                    "agent",
                    ReleasePackageRole.AetherRemoteAgent,
                    packages[2]),
                Declaration(
                    "station",
                    ReleasePackageRole.StationEngine,
                    packages[3])
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
                Title = "AetherSDR 8.1.1",
                Summary = "Production trust composition test vector."
            }
        };
        ReleaseManifestVerificationContext context = new(
            ReleaseManifestArchitecture.LinuxX64,
            InstallationUpdateChannel.Stable,
            string.Empty,
            "8.1.0",
            1,
            2);

        return new VerificationVector(
            CreateManifest(payload),
            packages,
            context);
    }

    private static LocalImmutableReleasePackage Package(
        string path,
        string content) =>
        new(path, Encoding.UTF8.GetBytes(content));

    private static SignedReleasePackage Declaration(
        string identity,
        ReleasePackageRole role,
        LocalImmutableReleasePackage package) =>
        new()
        {
            PackageIdentity = identity,
            Role = role,
            FileName = package.RelativePath,
            Length = package.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(package.Content))
                .ToLowerInvariant()
        };

    private static byte[] CreateManifest(SignedReleaseManifestPayload payload)
    {
        byte[] privateKeyBytes = Convert.FromBase64String(
            TestPrivateKeyPkcs8Base64);
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(privateKeyBytes, out int bytesRead);
            Assert.Equal(privateKeyBytes.Length, bytesRead);
            byte[] signingBytes = SignedReleaseManifestJson.CreateSigningBytes(
                payload,
                ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                TestKeyId);
            byte[] signature = key.SignData(
                signingBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return SignedReleaseManifestJson.Serialize(
                new SignedReleaseManifestDocument
                {
                    Payload = payload,
                    Signature = new ReleaseManifestSignature
                    {
                        Algorithm =
                            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                        KeyId = TestKeyId,
                        Value = Convert.ToBase64String(signature)
                            .TrimEnd('=')
                            .Replace('+', '-')
                            .Replace('/', '_')
                    }
                });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
        }
    }

    private sealed record VerificationVector(
        byte[] Manifest,
        LocalImmutableReleasePackage[] Packages,
        ReleaseManifestVerificationContext Context);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-release-trust-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteDeterministicPublicKey(string name)
        {
            byte[] keyBytes = Convert.FromBase64String(TestPublicKeySpkiBase64);
            try
            {
                return WriteText(
                    name,
                    PemEncoding.WriteString("PUBLIC KEY", keyBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }
        }

        public string WritePublicKey(string name, ECDsa key) =>
            WriteText(name, key.ExportSubjectPublicKeyInfoPem());

        public string WriteText(string name, string content) =>
            WriteBytes(name, new UTF8Encoding(false).GetBytes(content));

        public string WriteBytes(string name, byte[] content)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
