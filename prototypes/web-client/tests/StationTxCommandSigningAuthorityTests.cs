using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class StationTxCommandSigningAuthorityTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-01T03:15:00Z");

    [Fact]
    public void AuthorityExposesDiagnosticsButNoPublicSigningOrSubmissionMethod()
    {
        string[] publicMethods = typeof(StationTxCommandSigningAuthority)
            .GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Dispose", "get_Snapshot"], publicMethods);
        Assert.False(typeof(IStationTxCommandSigner).IsPublic);
        Assert.False(typeof(StationTxCommandSigningRequest).IsPublic);
        Assert.Null(typeof(StationTxCommandSigningAuthority).GetMethod(
            "Submit",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance));
    }

    [Fact]
    public void SigningRequestCannotSupplyAuthorityOwnedEnvelopeFields()
    {
        string[] properties = typeof(StationTxCommandSigningRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("CommandId", properties);
        Assert.DoesNotContain("Sequence", properties);
        Assert.DoesNotContain("IssuedAt", properties);
        Assert.DoesNotContain("ExpiresAt", properties);
        Assert.DoesNotContain("Signature", properties);
        Assert.DoesNotContain("KeyId", properties);
    }

    [Fact]
    public void UnknownConfigurationPropertiesFailClosed()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{StationTxCommandSigningSettings.SectionName}:" +
                    "SignngEnabled"] = "true"
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => configuration
                .GetSection(StationTxCommandSigningSettings.SectionName)
                .Get<StationTxCommandSigningSettings>(options =>
                    options.ErrorOnUnknownConfiguration = true));

        Assert.Contains("SignngEnabled", exception.Message);
    }

    [Fact]
    public void DefaultsExposeNoSigningCapability()
    {
        using StationTxCommandSigningAuthority authority = CreateAuthority(
            new StationTxCommandSigningSettings());

        StationTxCommandSigningDiagnostics snapshot = authority.Snapshot;

        Assert.False(snapshot.SigningEnabled);
        Assert.False(snapshot.SigningAvailable);
        Assert.False(snapshot.KeyConfigured);
        Assert.Null(snapshot.KeyId);
        Assert.Null(snapshot.PublicKeyFingerprint);
        Assert.Equal("disabled", snapshot.Reason);
        Assert.False(authority.Signer.IsAvailable);
    }

    [Fact]
    public void EnabledSigningRequiresAConfiguredPrivateKey()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateAuthority(new StationTxCommandSigningSettings
            {
                SigningEnabled = true
            }));

        Assert.Contains("requires a private signing key", exception.Message);
    }

    [Theory]
    [InlineData("key-a", "")]
    [InlineData("", "/tmp/key.pem")]
    public void PartialSigningConfigurationFailsClosed(
        string keyId,
        string privateKeyPath)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateAuthority(new StationTxCommandSigningSettings
            {
                KeyId = keyId,
                PrivateKeyPath = privateKeyPath
            }));

        Assert.Contains(
            string.IsNullOrEmpty(keyId) ? "key ID" : "PrivateKeyPath",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticsNeverExposePrivateKeyPathOrMaterial()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePrivateKey("station-key.pem", key);
        string pem = key.ExportPkcs8PrivateKeyPem();
        using StationTxCommandSigningAuthority authority = CreateAuthority(
            Settings(enabled: false, "station-key", path));

        string diagnostics = JsonSerializer.Serialize(authority.Snapshot);

        Assert.Contains("station-key", diagnostics);
        Assert.Matches(".*[0-9A-F]{24}.*", diagnostics);
        Assert.DoesNotContain(path, diagnostics);
        Assert.DoesNotContain("PRIVATE KEY", diagnostics);
        Assert.DoesNotContain(pem.Trim(), diagnostics);
    }

    [Fact]
    public void ConfiguredPrivateKeyLoadsButCannotSignWhileDisabled()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePrivateKey("key.pem", key);
        using StationTxCommandSigningAuthority authority = CreateAuthority(
            Settings(enabled: false, "key-a", path));

        Assert.False(authority.Snapshot.SigningEnabled);
        Assert.False(authority.Snapshot.SigningAvailable);
        Assert.True(authority.Snapshot.KeyConfigured);
        Assert.Equal("key-a", authority.Snapshot.KeyId);
        Assert.False(authority.Signer.IsAvailable);
        Assert.Throws<InvalidOperationException>(
            () => authority.Signer.CreateEnvelope(Request()));
    }

    [Fact]
    public void EnabledAuthorityConstructsExactSignedEnvelopes()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePrivateKey("key.pem", key);
        ManualTimeProvider time = new(Now);
        using StationTxCommandSigningAuthority authority = CreateAuthority(
            Settings(enabled: true, "key-a", path),
            time);
        using StationTxEcdsaCommandSignatureVerifier verifier = new(
            "key-a",
            key.ExportSubjectPublicKeyInfo());

        StationTxCommandSigningRequest request = Request();
        StationTxCommandEnvelope first = authority.Signer.CreateEnvelope(request);
        StationTxCommandEnvelope second = authority.Signer.CreateEnvelope(request);

        Assert.True(authority.Snapshot.SigningAvailable);
        Assert.Equal(StationTxCommandBoundary.ProtocolVersion, first.ProtocolVersion);
        Assert.Equal("key-a", first.KeyId);
        Assert.Matches("^[0-9a-f]{32}$", first.CommandId);
        Assert.NotEqual(first.CommandId, second.CommandId);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(Now, first.IssuedAt);
        Assert.Equal(
            Now + StationTxEcdsaCommandSigner.EnvelopeLifetime,
            first.ExpiresAt);
        Assert.Equal(request.StationId, first.StationId);
        Assert.Equal(request.RadioId, first.RadioId);
        Assert.Equal(request.SessionId, first.SessionId);
        Assert.Equal(request.BrowserClientId, first.BrowserClientId);
        Assert.Equal(request.LeaseId, first.LeaseId);
        Assert.Equal(request.GatewayInstanceId, first.GatewayInstanceId);
        Assert.Equal(request.EngineInstanceId, first.EngineInstanceId);
        Assert.Equal(request.ClientHandle, first.ClientHandle);
        Assert.Equal(request.Action, first.Action);
        Assert.Equal(request.Enabled, first.Enabled);
        Assert.DoesNotContain('=', first.Signature);
        byte[] signature = DecodeBase64Url(first.Signature);
        Assert.Equal(64, signature.Length);
        Assert.True(verifier.Verify(
            first.KeyId,
            StationTxCommandBoundary.CreateSigningPayload(first),
            signature));
    }

    [Fact]
    public void GeneratedTimesAreCanonicalToSignedMillisecondPrecision()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePrivateKey("key.pem", key);
        DateTimeOffset observed = Now.AddTicks(4321);
        using StationTxCommandSigningAuthority authority = CreateAuthority(
            Settings(enabled: true, "key-a", path),
            new ManualTimeProvider(observed));

        StationTxCommandEnvelope envelope =
            authority.Signer.CreateEnvelope(Request());

        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(observed.ToUnixTimeMilliseconds()),
            envelope.IssuedAt);
        Assert.Equal(0, envelope.IssuedAt.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(
            StationTxEcdsaCommandSigner.EnvelopeLifetime,
            envelope.ExpiresAt - envelope.IssuedAt);
    }

    [Fact]
    public void EnvelopeSignatureBindsEveryAuthorityField()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePrivateKey("key.pem", key);
        using StationTxCommandSigningAuthority authority = CreateAuthority(
            Settings(enabled: true, "key-a", path));
        using StationTxEcdsaCommandSignatureVerifier verifier = new(
            "key-a",
            key.ExportSubjectPublicKeyInfo());
        StationTxCommandEnvelope envelope =
            authority.Signer.CreateEnvelope(Request());
        byte[] signature = DecodeBase64Url(envelope.Signature);

        StationTxCommandEnvelope changed = envelope with
        {
            EngineInstanceId = "engine-other"
        };

        Assert.False(verifier.Verify(
            changed.KeyId,
            StationTxCommandBoundary.CreateSigningPayload(changed),
            signature));
    }

    [Fact]
    public void ConcurrentSigningProducesUniqueMonotonicSequences()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePrivateKey("key.pem", key);
        using StationTxCommandSigningAuthority authority = CreateAuthority(
            Settings(enabled: true, "key-a", path));

        StationTxCommandEnvelope[] envelopes = Enumerable.Range(0, 64)
            .AsParallel()
            .Select(_ => authority.Signer.CreateEnvelope(Request()))
            .ToArray();

        Assert.Equal(64, envelopes.Select(item => item.CommandId).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(1, 64).Select(value => (long)value),
            envelopes.Select(item => item.Sequence).Order());
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
            () => CreateAuthority(Settings(
                enabled: false,
                keyId,
                "/tmp/key.pem")));

        Assert.Contains("key ID", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidKeyIdentifiersAreNotEchoedIntoErrors()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateAuthority(Settings(
                enabled: false,
                "bad\r\nforged-log-entry",
                "/tmp/key.pem")));

        Assert.DoesNotContain("forged-log-entry", exception.Message);
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\n', exception.Message);
    }

    [Fact]
    public void InvalidSigningRequestsFailBeforeCryptography()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePrivateKey("key.pem", key);
        using StationTxCommandSigningAuthority authority = CreateAuthority(
            Settings(enabled: true, "key-a", path));

        Assert.Throws<ArgumentException>(() =>
            authority.Signer.CreateEnvelope(Request() with { LeaseId = "bad lease" }));
        Assert.Throws<ArgumentException>(() =>
            authority.Signer.CreateEnvelope(Request() with { ClientHandle = 0 }));
        Assert.Throws<ArgumentException>(() =>
            authority.Signer.CreateEnvelope(Request() with
            {
                Action = (StationTxCommandAction)99
            }));
    }

    [Fact]
    public void RelativePrivateKeyPathsFailClosed()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateAuthority(Settings(
                enabled: false,
                "key-a",
                "relative/key.pem")));

        Assert.Contains("absolute", exception.Message);
    }

    [Fact]
    public void RelativeSegmentsInPrivateKeyPathsFailClosed()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "unused", "..", "key.pem");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateAuthority(Settings(enabled: false, "key-a", path)));

        Assert.Contains("relative path segments", exception.Message);
    }

    [Fact]
    public void MissingAndEmptyPrivateKeysFailClosed()
    {
        using TempDirectory directory = new();
        string missing = Path.Combine(directory.Path, "missing.pem");
        InvalidOperationException missingException =
            Assert.Throws<InvalidOperationException>(
                () => CreateAuthority(Settings(false, "key-a", missing)));
        string empty = directory.WriteBytes("empty.pem", []);
        InvalidOperationException emptyException =
            Assert.Throws<InvalidOperationException>(
                () => CreateAuthority(Settings(false, "key-a", empty)));

        Assert.Contains("does not exist", missingException.Message);
        Assert.Contains("1 through", emptyException.Message);
    }

    [Fact]
    public void SymbolicLinkPrivateKeysFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string target = directory.WritePrivateKey("target.pem", key);
        string link = Path.Combine(directory.Path, "link.pem");
        File.CreateSymbolicLink(link, target);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateAuthority(Settings(false, "key-a", link)));

        Assert.Contains("non-symlink", exception.Message);
    }

    [Fact]
    public void SymbolicLinkPrivateKeyDirectoriesFailClosed()
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
        File.WriteAllText(target, key.ExportPkcs8PrivateKeyPem());
        File.SetUnixFileMode(
            target,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string linkDirectory = Path.Combine(directory.Path, "linked");
        Directory.CreateSymbolicLink(linkDirectory, realDirectory);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateAuthority(Settings(
                false,
                "key-a",
                Path.Combine(linkDirectory, "key.pem"))));

        Assert.Contains("non-symlink directory", exception.Message);
    }

    [Fact]
    public void PrivateKeyPermissionsMustBeOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePrivateKey("key.pem", key);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.GroupRead);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateAuthority(Settings(false, "key-a", path)));

        Assert.Contains("0400 or 0600", exception.Message);
    }

    [Fact]
    public void GroupWritablePrivateKeyDirectoriesFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePrivateKey("key.pem", key);
        File.SetUnixFileMode(
            directory.Path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateAuthority(Settings(false, "key-a", path)));

        Assert.Contains("directory", exception.Message);
        Assert.Contains("writable", exception.Message);
    }

    [Fact]
    public void OversizedPrivateKeysFailClosed()
    {
        using TempDirectory directory = new();
        string path = directory.WriteBytes(
            "oversized.pem",
            new byte[StationTxCommandSigningAuthority.MaximumPrivateKeyFileBytes + 1]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CreateAuthority(Settings(false, "key-a", path)));

        Assert.Contains("bytes", exception.Message);
    }

    [Fact]
    public void PublicAndEncryptedKeysAreNotAccepted()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicPath = directory.WriteText(
            "public.pem",
            key.ExportSubjectPublicKeyInfoPem());
        string encryptedPath = directory.WriteText(
            "encrypted.pem",
            key.ExportEncryptedPkcs8PrivateKeyPem(
                "password",
                new PbeParameters(
                    PbeEncryptionAlgorithm.Aes256Cbc,
                    HashAlgorithmName.SHA256,
                    1000)));

        InvalidOperationException publicException =
            Assert.Throws<InvalidOperationException>(
                () => CreateAuthority(Settings(false, "key-a", publicPath)));
        InvalidOperationException encryptedException =
            Assert.Throws<InvalidOperationException>(
                () => CreateAuthority(Settings(false, "key-a", encryptedPath)));

        Assert.Contains("PRIVATE KEY", publicException.Message);
        Assert.Contains("unencrypted", encryptedException.Message);
    }

    [Fact]
    public void MultiplePemBlocksAndMalformedDataFailClosed()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportPkcs8PrivateKeyPem();
        string multiple = directory.WriteText(
            "multiple.pem",
            pem + Environment.NewLine + pem);
        string malformed = directory.WriteText(
            "malformed.pem",
            "-----BEGIN PRIVATE KEY-----\nnot-base64!\n-----END PRIVATE KEY-----\n");

        InvalidOperationException multipleException =
            Assert.Throws<InvalidOperationException>(
                () => CreateAuthority(Settings(false, "key-a", multiple)));
        InvalidOperationException malformedException =
            Assert.Throws<InvalidOperationException>(
                () => CreateAuthority(Settings(false, "key-a", malformed)));

        Assert.Contains("exactly one", multipleException.Message);
        Assert.Contains("PEM", malformedException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidUtf8AndNonP256KeysFailClosed()
    {
        using TempDirectory directory = new();
        string invalidUtf8 = directory.WriteBytes("invalid.pem", [0xff, 0xfe, 0xfd]);
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string p384Path = directory.WritePrivateKey("p384.pem", p384);

        InvalidOperationException utf8Exception =
            Assert.Throws<InvalidOperationException>(
                () => CreateAuthority(Settings(false, "key-a", invalidUtf8)));
        InvalidOperationException curveException =
            Assert.Throws<InvalidOperationException>(
                () => CreateAuthority(Settings(false, "key-a", p384Path)));

        Assert.Contains("could not be loaded", utf8Exception.Message);
        Assert.Contains("P-256", curveException.Message);
    }

    [Fact]
    public void DisposedAuthorityCannotSign()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = directory.WritePrivateKey("key.pem", key);
        StationTxCommandSigningAuthority authority = CreateAuthority(
            Settings(true, "key-a", path));

        Assert.True(authority.Signer.IsAvailable);
        authority.Dispose();

        Assert.False(authority.Signer.IsAvailable);
        Assert.Throws<InvalidOperationException>(
            () => authority.Signer.CreateEnvelope(Request()));
        authority.Dispose();
    }

    private static StationTxCommandSigningAuthority CreateAuthority(
        StationTxCommandSigningSettings settings,
        TimeProvider? timeProvider = null) =>
        new(
            Options.Create(settings),
            NullLogger<StationTxCommandSigningAuthority>.Instance,
            timeProvider ?? new ManualTimeProvider(Now));

    private static StationTxCommandSigningSettings Settings(
        bool enabled,
        string keyId,
        string path) =>
        new()
        {
            SigningEnabled = enabled,
            KeyId = keyId,
            PrivateKeyPath = path
        };

    private static StationTxCommandSigningRequest Request() =>
        new(
            StationId: "station-a",
            RadioId: "RADIO-A",
            SessionId: "session-a",
            BrowserClientId: "browser-a",
            LeaseId: "lease-a",
            GatewayInstanceId: "gateway-a",
            EngineInstanceId: "engine-a",
            ClientHandle: 0x12345678,
            Action: StationTxCommandAction.SetTransmit,
            Enabled: true);

    private static byte[] DecodeBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(
            padded.Length + ((4 - padded.Length % 4) % 4),
            '=');
        return Convert.FromBase64String(padded);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-command-signing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WritePrivateKey(string name, ECDsa key) =>
            WriteText(name, key.ExportPkcs8PrivateKeyPem());

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
