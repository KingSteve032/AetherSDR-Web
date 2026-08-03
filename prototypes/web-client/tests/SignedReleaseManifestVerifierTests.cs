using System.Security.Cryptography;
using System.Text;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class SignedReleaseManifestVerifierTests
{
    private const string TestKeyId = "m8b-test-key-1";
    private const string TestPrivateKeyPkcs8Base64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg" +
        "EjRWeJq83vEjRWeJq83vEjRWeJq83vEjRWeJq83vEjShRAN" +
        "CAARawLjuCeZXZ7tsfTRAu+FcuRLUr+ELbhoX/6Hs0fLlSZe" +
        "0NNZYPUqZa65oYGMMs9Ud19Qc/RZMzn4vZv5+EakU";
    private const string TestPublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWsC47gnmV2e" +
        "7bH00QLvhXLkS1K/hC24aF/+h7NHy5UmXtDTWWD1KmWuuaG" +
        "BjDLPVHdfUHP0WTM5+L2b+fhGpFA==";

    private readonly SignedReleaseManifestVerifier m_verifier = new();

    [Fact]
    public void DeterministicLocalVectorVerifiesAndReportsNoAuthority()
    {
        TestVector vector = CreateVector();

        ReleaseManifestVerificationReport report = Verify(vector);

        Assert.True(report.Succeeded);
        Assert.Equal(ReleaseManifestFailureCode.None, report.FailureCode);
        Assert.Equal("aethersdr-8.1.0", report.ReleaseIdentity);
        Assert.Equal("8.1.0", report.Version);
        Assert.Equal(4, report.DeclaredPackageCount);
        Assert.True(report.TxSupportCapable);
        Assert.DoesNotContain("key", report.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", report.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanonicalSigningBytesAreDeterministic()
    {
        SignedReleaseManifestPayload payload = CreateVector().Payload;

        byte[] first = SignedReleaseManifestJson.CreateSigningBytes(
            payload,
            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
            TestKeyId);
        byte[] second = SignedReleaseManifestJson.CreateSigningBytes(
            payload,
            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
            TestKeyId);

        Assert.Equal(first, second);
        Assert.Equal(SHA256.HashData(first), SHA256.HashData(second));
    }

    [Fact]
    public void ImmutablePackageInputCopiesCallerBytes()
    {
        TestVector vector = CreateVector();
        byte[] mutable = Encoding.UTF8.GetBytes("gateway-package-v1");
        LocalImmutableReleasePackage copied = new(
            "packages/aethersdr-gateway-linux-x64.tar.gz",
            mutable);
        mutable[0] ^= 0x7f;
        vector = vector with
        {
            Packages =
            [
                copied,
                .. vector.Packages.Skip(1)
            ]
        };

        Assert.True(Verify(vector).Succeeded);
    }

    [Fact]
    public void UnknownJsonFieldFailsClosed()
    {
        TestVector vector = CreateVector();
        byte[] valid = CreateManifest(vector.Payload);
        string json = Encoding.UTF8.GetString(valid);
        byte[] malformed = Encoding.UTF8.GetBytes(
            json[..^1] + ",\"unexpected\":true}");

        ReleaseManifestVerificationReport report = m_verifier.Verify(
            malformed,
            vector.Packages,
            vector.Context,
            vector.Keys);

        AssertFailure(report, ReleaseManifestFailureCode.MalformedManifest);
    }

    [Fact]
    public void DuplicateJsonPropertyFailsClosed()
    {
        TestVector vector = CreateVector();
        string json = Encoding.UTF8.GetString(CreateManifest(vector.Payload));
        json = json.Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":1,\"schemaVersion\":1",
            StringComparison.Ordinal);

        ReleaseManifestVerificationReport report = m_verifier.Verify(
            Encoding.UTF8.GetBytes(json),
            vector.Packages,
            vector.Context,
            vector.Keys);

        AssertFailure(report, ReleaseManifestFailureCode.MalformedManifest);
    }

    [Fact]
    public void UnknownManifestSchemaFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with { SchemaVersion = 2 }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.UnsupportedManifestSchema);
    }

    [Fact]
    public void DuplicatePackageIdentityFailsClosed()
    {
        TestVector vector = CreateVector();
        SignedReleasePackage[] packages = ClonePackages(vector.Payload.Packages);
        packages[1] = packages[1] with
        {
            PackageIdentity = packages[0].PackageIdentity
        };
        vector = vector with
        {
            Payload = vector.Payload with { Packages = packages }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.DuplicatePackageIdentity);
    }

    [Fact]
    public void DuplicatePackagePathFailsClosed()
    {
        TestVector vector = CreateVector();
        SignedReleasePackage[] packages = ClonePackages(vector.Payload.Packages);
        packages[1] = packages[1] with { FileName = packages[0].FileName };
        vector = vector with
        {
            Payload = vector.Payload with { Packages = packages }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.DuplicatePackagePath);
    }

    [Fact]
    public void DuplicatePackageRoleFailsClosed()
    {
        TestVector vector = CreateVector();
        SignedReleasePackage[] packages = ClonePackages(vector.Payload.Packages);
        packages[1] = packages[1] with { Role = packages[0].Role };
        vector = vector with
        {
            Payload = vector.Payload with { Packages = packages }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.DuplicatePackageRole);
    }

    [Theory]
    [InlineData("../gateway.tar.gz")]
    [InlineData("packages/../../gateway.tar.gz")]
    [InlineData("/tmp/gateway.tar.gz")]
    [InlineData("C:/gateway.tar.gz")]
    [InlineData("packages\\gateway.tar.gz")]
    public void UnsafePackagePathFailsClosed(string path)
    {
        TestVector vector = CreateVector();
        SignedReleasePackage[] packages = ClonePackages(vector.Payload.Packages);
        packages[0] = packages[0] with { FileName = path };
        vector = vector with
        {
            Payload = vector.Payload with { Packages = packages }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.InvalidPackagePath);
    }

    [Fact]
    public void UnsupportedArchitectureFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with
            {
                Architecture = ReleaseManifestArchitecture.LinuxArm64
            }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.UnsupportedArchitecture);
    }

    [Fact]
    public void MissingRequiredPackageRoleFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with
            {
                Packages = vector.Payload.Packages[..3]
            }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.MissingPackageRole);
    }

    [Fact]
    public void UnexpectedPackageRoleFailsClosed()
    {
        TestVector vector = CreateVector();
        SignedReleasePackage[] packages = ClonePackages(vector.Payload.Packages);
        packages[3] = packages[3] with { Role = ReleasePackageRole.Unknown };
        vector = vector with
        {
            Payload = vector.Payload with { Packages = packages }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.UnexpectedPackageRole);
    }

    [Fact]
    public void PackageSizeMismatchFailsClosed()
    {
        TestVector vector = CreateVector();
        LocalImmutableReleasePackage[] packages = vector.Packages.ToArray();
        packages[0] = new LocalImmutableReleasePackage(
            packages[0].RelativePath,
            Encoding.UTF8.GetBytes("short"));
        vector = vector with { Packages = packages };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.PackageSizeMismatch);
    }

    [Fact]
    public void PackageSha256MismatchFailsClosed()
    {
        TestVector vector = CreateVector();
        byte[] changed = Encoding.UTF8.GetBytes("gateway-package-v2");
        Assert.Equal(vector.Packages[0].Length, changed.LongLength);
        LocalImmutableReleasePackage[] packages = vector.Packages.ToArray();
        packages[0] = new LocalImmutableReleasePackage(
            packages[0].RelativePath,
            changed);
        vector = vector with { Packages = packages };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.PackageSha256Mismatch);
    }

    [Fact]
    public void UnsupportedSignatureAlgorithmFailsClosed()
    {
        TestVector vector = CreateVector();
        byte[] manifest = CreateManifest(
            vector.Payload,
            ReleaseManifestSignatureAlgorithm.RsaPssSha256,
            TestKeyId);

        ReleaseManifestVerificationReport report = m_verifier.Verify(
            manifest,
            vector.Packages,
            vector.Context,
            vector.Keys);

        AssertFailure(
            report,
            ReleaseManifestFailureCode.UnsupportedSignatureAlgorithm);
    }

    [Fact]
    public void UnknownVerificationKeyFailsClosed()
    {
        TestVector vector = CreateVector();
        byte[] manifest = CreateManifest(
            vector.Payload,
            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
            "unknown-test-key");

        ReleaseManifestVerificationReport report = m_verifier.Verify(
            manifest,
            vector.Packages,
            vector.Context,
            vector.Keys);

        AssertFailure(
            report,
            ReleaseManifestFailureCode.UnknownVerificationKey);
    }

    [Fact]
    public void InvalidVerificationKeyFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Keys =
            [
                new ReleaseManifestVerificationKey(
                    TestKeyId,
                    ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                    [0x01, 0x02, 0x03])
            ]
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.InvalidVerificationKey);
    }

    [Fact]
    public void InvalidSignatureFailsClosedAndRedactsUntrustedMetadata()
    {
        TestVector vector = CreateVector();
        SignedReleaseManifestDocument document = Deserialize(
            CreateManifest(vector.Payload));
        document = document with
        {
            Signature = document.Signature with
            {
                Value = new string('A', 86)
            }
        };

        ReleaseManifestVerificationReport report = m_verifier.Verify(
            SignedReleaseManifestJson.Serialize(document),
            vector.Packages,
            vector.Context,
            vector.Keys);

        AssertFailure(report, ReleaseManifestFailureCode.InvalidSignature);
        Assert.Empty(report.ReleaseIdentity);
        Assert.Empty(report.Version);
        Assert.DoesNotContain(TestKeyId, report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompatibleConfigurationSchemaFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Context = vector.Context with { ConfigurationSchemaVersion = 3 }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.IncompatibleConfigurationSchema);
    }

    [Fact]
    public void IncompatibleProtocolVersionFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Context = vector.Context with { ProtocolVersion = 4 }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.IncompatibleProtocolVersion);
    }

    [Fact]
    public void UnsupportedPreviousVersionTransitionFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Context = vector.Context with { InstalledVersion = "7.9.9" }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.UnsupportedPreviousVersionTransition);
    }

    [Fact]
    public void SameOrNewerInstalledVersionFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Context = vector.Context with { InstalledVersion = "8.1.0" }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.UnsupportedPreviousVersionTransition);
    }

    [Fact]
    public void LargeNumericPrereleaseIdentifiersCompareWithoutOverflow()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with
            {
                Version = "8.1.0-beta.999999999999999999999999999999",
                Channel = ReleaseManifestChannel.Beta,
                MinimumPreviousVersion = "8.1.0-beta.1"
            },
            Context = vector.Context with
            {
                UpdateChannel = InstallationUpdateChannel.Beta,
                InstalledVersion = "8.1.0-beta.2"
            }
        };

        Assert.True(Verify(vector).Succeeded);
    }

    [Fact]
    public void StableChannelRejectsPrereleaseVersion()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with { Version = "8.1.0-beta.1" }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.InvalidChannelRelationship);
    }

    [Fact]
    public void PinnedChannelRequiresExactReleaseIdentity()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with { Channel = ReleaseManifestChannel.Pinned },
            Context = vector.Context with
            {
                UpdateChannel = InstallationUpdateChannel.Pinned,
                PinnedReleaseIdentity = "aethersdr-8.1.1"
            }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.InvalidChannelRelationship);
    }

    [Fact]
    public void ExactPinnedReleaseVerifies()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with { Channel = ReleaseManifestChannel.Pinned },
            Context = vector.Context with
            {
                UpdateChannel = InstallationUpdateChannel.Pinned,
                PinnedReleaseIdentity = vector.Payload.ReleaseIdentity
            }
        };

        Assert.True(Verify(vector).Succeeded);
    }

    [Fact]
    public void ContradictoryHostRestartFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with
            {
                Restart = vector.Payload.Restart with
                {
                    Host = true,
                    Broker = false
                }
            }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.ContradictoryRestartDeclaration);
    }

    [Fact]
    public void MissingRequiredMigrationFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with
            {
                Configuration = vector.Payload.Configuration with
                {
                    TargetSchemaVersion = 2
                }
            }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.InvalidMigrationDeclaration);
    }

    [Fact]
    public void ConsistentDeclaredMigrationVerifiesWithoutRunningMigration()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with
            {
                Configuration = vector.Payload.Configuration with
                {
                    TargetSchemaVersion = 2
                },
                Migration = new ReleaseMigrationDeclaration
                {
                    Kind = ReleaseMigrationKind.Required,
                    FromConfigurationSchemaVersion = 1,
                    ToConfigurationSchemaVersion = 2,
                    MigrationIdentity = "config-schema-1-to-2"
                }
            }
        };

        Assert.True(Verify(vector).Succeeded);
    }

    [Theory]
    [InlineData(nameof(ReleaseTxSupportDeclaration.EnablesTransmit))]
    [InlineData(nameof(ReleaseTxSupportDeclaration.GrantsTransmitEligibility))]
    [InlineData(nameof(ReleaseTxSupportDeclaration.CreatesBrowserTransmitAuthority))]
    [InlineData(nameof(ReleaseTxSupportDeclaration.ArmsWatchdog))]
    public void TxSupportDeclarationCannotGrantAuthority(string property)
    {
        TestVector vector = CreateVector();
        ReleaseTxSupportDeclaration declaration = vector.Payload.TxSupport;
        declaration = property switch
        {
            nameof(ReleaseTxSupportDeclaration.EnablesTransmit) =>
                declaration with { EnablesTransmit = true },
            nameof(ReleaseTxSupportDeclaration.GrantsTransmitEligibility) =>
                declaration with { GrantsTransmitEligibility = true },
            nameof(ReleaseTxSupportDeclaration.CreatesBrowserTransmitAuthority) =>
                declaration with { CreatesBrowserTransmitAuthority = true },
            nameof(ReleaseTxSupportDeclaration.ArmsWatchdog) =>
                declaration with { ArmsWatchdog = true },
            _ => throw new InvalidOperationException("Unknown test property.")
        };
        vector = vector with
        {
            Payload = vector.Payload with { TxSupport = declaration }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.InvalidTxSupportDeclaration);
    }

    [Fact]
    public void MissingLocalPackageFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with { Packages = vector.Packages[..3] };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.MissingPackageInput);
    }

    [Fact]
    public void UndeclaredLocalPackageFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Packages =
            [
                .. vector.Packages,
                new LocalImmutableReleasePackage(
                    "packages/undeclared.tar.gz",
                    Encoding.UTF8.GetBytes("undeclared"))
            ]
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.UnexpectedPackageInput);
    }

    [Fact]
    public void InvalidSemanticVersionFailsClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with { Version = "08.1.0" }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.InvalidSemanticVersion);
    }

    [Fact]
    public void OversizedReleaseNotesFailClosed()
    {
        TestVector vector = CreateVector();
        vector = vector with
        {
            Payload = vector.Payload with
            {
                ReleaseNotes = vector.Payload.ReleaseNotes with
                {
                    Summary = new string('x', 2049)
                }
            }
        };

        AssertFailure(
            Verify(vector),
            ReleaseManifestFailureCode.InvalidReleaseNotes);
    }

    private ReleaseManifestVerificationReport Verify(TestVector vector) =>
        m_verifier.Verify(
            CreateManifest(vector.Payload),
            vector.Packages,
            vector.Context,
            vector.Keys);

    private static TestVector CreateVector()
    {
        LocalImmutableReleasePackage[] packages =
        [
            Package(
                "packages/aethersdr-gateway-linux-x64.tar.gz",
                "gateway-package-v1"),
            Package(
                "packages/aethersdr-broker-linux-x64.tar.gz",
                "broker-package-v1"),
            Package(
                "packages/aetherremote-agent-linux-x64.tar.gz",
                "agent-package-v1"),
            Package(
                "packages/aethersdr-station-engine-linux-x64.tar.gz",
                "station-package-v1")
        ];

        SignedReleaseManifestPayload payload = new()
        {
            SchemaVersion = SignedReleaseManifestPayload.CurrentSchemaVersion,
            ReleaseIdentity = "aethersdr-8.1.0",
            Version = "8.1.0",
            Channel = ReleaseManifestChannel.Stable,
            Architecture = ReleaseManifestArchitecture.LinuxX64,
            Packages =
            [
                Declaration("gateway-web", ReleasePackageRole.GatewayWeb, packages[0]),
                Declaration("broker", ReleasePackageRole.Broker, packages[1]),
                Declaration(
                    "aetherremote-agent",
                    ReleasePackageRole.AetherRemoteAgent,
                    packages[2]),
                Declaration(
                    "station-engine",
                    ReleasePackageRole.StationEngine,
                    packages[3])
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
            MinimumPreviousVersion = "8.0.0",
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
                Title = "AetherSDR 8.1.0",
                Summary = "Deterministic M8B local verification test vector."
            }
        };

        return new TestVector(
            payload,
            packages,
            new ReleaseManifestVerificationContext(
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                string.Empty,
                "8.0.0",
                1,
                2),
            [
                new ReleaseManifestVerificationKey(
                    TestKeyId,
                    ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                    Convert.FromBase64String(TestPublicKeySpkiBase64))
            ]);
    }

    private static LocalImmutableReleasePackage Package(
        string path,
        string content) =>
        new(path, Encoding.UTF8.GetBytes(content));

    private static SignedReleasePackage Declaration(
        string identity,
        ReleasePackageRole role,
        LocalImmutableReleasePackage package)
    {
        byte[] hash = SHA256.HashData(package.Content);
        return new SignedReleasePackage
        {
            PackageIdentity = identity,
            Role = role,
            FileName = package.RelativePath,
            Length = package.Length,
            Sha256 = Convert.ToHexString(hash).ToLowerInvariant()
        };
    }

    private static byte[] CreateManifest(
        SignedReleaseManifestPayload payload,
        ReleaseManifestSignatureAlgorithm algorithm =
            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
        string keyId = TestKeyId)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(
            Convert.FromBase64String(TestPrivateKeyPkcs8Base64),
            out int bytesRead);
        Assert.Equal(
            Convert.FromBase64String(TestPrivateKeyPkcs8Base64).Length,
            bytesRead);

        byte[] signingBytes = SignedReleaseManifestJson.CreateSigningBytes(
            payload,
            algorithm,
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
                Algorithm = algorithm,
                KeyId = keyId,
                Value = ToBase64Url(signature)
            }
        };
        return SignedReleaseManifestJson.Serialize(document);
    }

    private static SignedReleaseManifestDocument Deserialize(byte[] manifest) =>
        SignedReleaseManifestJson.Deserialize(manifest) ??
        throw new InvalidOperationException("The test manifest did not deserialize.");

    private static string ToBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static SignedReleasePackage[] ClonePackages(
        SignedReleasePackage[] packages) =>
        packages.Select(package => package with { }).ToArray();

    private static void AssertFailure(
        ReleaseManifestVerificationReport report,
        ReleaseManifestFailureCode expected)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(expected, report.FailureCode);
    }

    private sealed record TestVector(
        SignedReleaseManifestPayload Payload,
        LocalImmutableReleasePackage[] Packages,
        ReleaseManifestVerificationContext Context,
        ReleaseManifestVerificationKey[] Keys);
}
