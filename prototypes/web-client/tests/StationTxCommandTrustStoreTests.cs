using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class StationTxCommandTrustStoreTests
{
    [Fact]
    public void RegistryExposesDiagnosticsButNoSignerOrCommandSubmission()
    {
        string[] publicMethods = typeof(StationTxCommandTrustRegistry)
            .GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Dispose", "get_Snapshot"], publicMethods);
        Assert.False(typeof(IStationTxCommandSignatureVerifier).IsPublic);
        Assert.Null(typeof(StationTxCommandTrustKeySettings).GetProperty(
            "PrivateKeyPath"));
    }

    [Fact]
    public void UnknownConfigurationPropertiesFailClosed()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{StationTxCommandTrustSettings.SectionName}:" +
                    "VerificatonEnabled"] = "true"
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => configuration
                .GetSection(StationTxCommandTrustSettings.SectionName)
                .Get<StationTxCommandTrustSettings>(options =>
                    options.ErrorOnUnknownConfiguration = true));

        Assert.Contains("VerificatonEnabled", exception.Message);
    }

    [Fact]
    public void DefaultsExposeNoVerificationCapability()
    {
        using StationTxCommandTrustRegistry registry = CreateRegistry(
            new StationTxCommandTrustSettings());

        StationTxCommandTrustDiagnostics snapshot = registry.Snapshot;

        Assert.False(snapshot.VerificationEnabled);
        Assert.False(snapshot.SignatureVerificationAvailable);
        Assert.Equal(0, snapshot.TrustedKeyCount);
        Assert.Empty(snapshot.TrustedKeys);
        Assert.Equal("disabled", snapshot.Reason);
        Assert.False(registry.Verifier.IsAvailable);
    }

    [Fact]
    public void NullTrustEntriesFailClosed()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(new StationTxCommandTrustSettings
            {
                Keys = [null!]
            }));

        Assert.Contains("null entry", exception.Message);
    }

    [Fact]
    public void DiagnosticsNeverExposePathsOrPublicKeyMaterial()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePublicKey("station-key.pem", key);
        string pem = key.ExportSubjectPublicKeyInfoPem();
        using StationTxCommandTrustRegistry registry = CreateRegistry(
            Settings(enabled: false, ("station-key", path)));

        string diagnostics = JsonSerializer.Serialize(registry.Snapshot);

        Assert.Contains("station-key", diagnostics);
        Assert.DoesNotContain(path, diagnostics);
        Assert.DoesNotContain("PUBLIC KEY", diagnostics);
        Assert.DoesNotContain(pem.Trim(), diagnostics);
    }

    [Fact]
    public void EnabledTrustRingVerifiesOnlyExactConfiguredKeys()
    {
        using TempDirectory directory = new();
        using ECDsa first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string firstPath = directory.WritePublicKey("first.pem", first);
        string secondPath = directory.WritePublicKey("second.pem", second);
        using StationTxCommandTrustRegistry registry = CreateRegistry(
            Settings(
                enabled: true,
                ("key-a", firstPath),
                ("key-b", secondPath)));
        byte[] payload = Encoding.UTF8.GetBytes("station-command-payload");
        byte[] firstSignature = first.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        byte[] secondSignature = second.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.True(registry.Snapshot.VerificationEnabled);
        Assert.True(registry.Snapshot.SignatureVerificationAvailable);
        Assert.Equal(2, registry.Snapshot.TrustedKeyCount);
        Assert.All(
            registry.Snapshot.TrustedKeys,
            key => Assert.Matches("^[0-9A-F]{24}$", key.Fingerprint));
        Assert.True(registry.Verifier.Verify(
            "key-a",
            payload,
            firstSignature));
        Assert.True(registry.Verifier.Verify(
            "key-b",
            payload,
            secondSignature));
        Assert.False(registry.Verifier.Verify(
            "key-a",
            payload,
            secondSignature));
        Assert.False(registry.Verifier.Verify(
            "missing",
            payload,
            firstSignature));
    }

    [Fact]
    public void ConfiguredKeysRemainUnavailableWhileVerificationIsDisabled()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePublicKey("key.pem", key);
        using StationTxCommandTrustRegistry registry = CreateRegistry(
            Settings(enabled: false, ("key-a", path)));
        byte[] payload = Encoding.UTF8.GetBytes("payload");
        byte[] signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.False(registry.Snapshot.SignatureVerificationAvailable);
        Assert.Equal(1, registry.Snapshot.TrustedKeyCount);
        Assert.Equal("disabled", registry.Snapshot.Reason);
        Assert.False(registry.Verifier.Verify("key-a", payload, signature));
    }

    [Fact]
    public void EnabledVerificationRequiresAtLeastOneTrustAnchor()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(new StationTxCommandTrustSettings
            {
                VerificationEnabled = true
            }));

        Assert.Contains("at least one", exception.Message);
    }

    [Fact]
    public void TrustRingIsBounded()
    {
        StationTxCommandTrustKeySettings[] keys = Enumerable
            .Range(0, StationTxCommandTrustRegistry.MaximumTrustedKeys + 1)
            .Select(index => new StationTxCommandTrustKeySettings
            {
                KeyId = $"key-{index}",
                PublicKeyPath = $"/tmp/key-{index}.pem"
            })
            .ToArray();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(new StationTxCommandTrustSettings
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
                (keyId, "/tmp/key.pem"))));

        Assert.Contains("key ID", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidKeyIdentifiersAreNotEchoedIntoErrors()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("bad\r\nforged-log-entry", "/tmp/key.pem"))));

        Assert.DoesNotContain("forged-log-entry", exception.Message);
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\n', exception.Message);
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
        string path = System.IO.Path.Combine(
            directory.Path,
            "unused",
            "..",
            "key.pem");

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
    public void OversizedTrustAnchorsFailClosed()
    {
        using TempDirectory directory = new();
        string path = directory.WriteBytes(
            "oversized.pem",
            new byte[StationTxCommandTrustRegistry.MaximumPublicKeyFileBytes + 1]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateRegistry(Settings(
                enabled: false,
                ("key-a", path))));

        Assert.Contains("bytes", exception.Message);
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
    public void DisposedTrustRingCannotVerifySignatures()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePublicKey("key.pem", key);
        StationTxCommandTrustRegistry registry = CreateRegistry(
            Settings(enabled: true, ("key-a", path)));
        byte[] payload = Encoding.UTF8.GetBytes("payload");
        byte[] signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.True(registry.Verifier.Verify("key-a", payload, signature));
        registry.Dispose();

        Assert.False(registry.Verifier.IsAvailable);
        Assert.False(registry.Verifier.Verify("key-a", payload, signature));
        registry.Dispose();
    }

    private static StationTxCommandTrustRegistry CreateRegistry(
        StationTxCommandTrustSettings settings) =>
        new(
            Options.Create(settings),
            NullLogger<StationTxCommandTrustRegistry>.Instance);

    private static StationTxCommandTrustSettings Settings(
        bool enabled,
        params (string KeyId, string Path)[] keys) =>
        new()
        {
            VerificationEnabled = enabled,
            Keys = keys
                .Select(key => new StationTxCommandTrustKeySettings
                {
                    KeyId = key.KeyId,
                    PublicKeyPath = key.Path
                })
                .ToArray()
        };

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-command-trust-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
