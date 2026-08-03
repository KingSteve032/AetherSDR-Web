using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseInstallationPlanCompositionTests
{
    [Fact]
    public void DetailedVerifierRetainsTrustedSnapshotOnlyAfterFullSuccess()
    {
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LocalImmutableReleasePackage[] packages = CreatePackages();
        SignedReleaseManifestPayload payload = CreatePayload(packages);
        byte[] manifest = Sign(payload, signingKey);
        SignedReleaseManifestVerifier verifier = new();

        SignedReleaseManifestVerificationResult result = verifier.VerifyDetailed(
            manifest,
            packages,
            Context(),
            [VerificationKey(signingKey)]);

        Assert.True(result.Report.Succeeded);
        VerifiedReleaseManifestSnapshot snapshot =
            Assert.IsType<VerifiedReleaseManifestSnapshot>(result.VerifiedManifest);
        Assert.Equal(payload.ReleaseIdentity, snapshot.ReleaseIdentity);
        Assert.Equal(payload.Version, snapshot.Version);
        Assert.Equal(4, snapshot.Packages.Count);
        Assert.Equal(
            ReleaseMigrationKind.Required,
            snapshot.MigrationKind);
        Assert.Equal("schema-1-to-2", snapshot.MigrationIdentity);
    }

    [Fact]
    public void DetailedVerifierNeverRetainsSnapshotForInvalidSignature()
    {
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LocalImmutableReleasePackage[] packages = CreatePackages();
        SignedReleaseManifestPayload payload = CreatePayload(packages);
        byte[] manifest = Sign(payload, signingKey, corruptSignature: true);
        SignedReleaseManifestVerifier verifier = new();

        SignedReleaseManifestVerificationResult result = verifier.VerifyDetailed(
            manifest,
            packages,
            Context(),
            [VerificationKey(signingKey)]);

        Assert.False(result.Report.Succeeded);
        Assert.Equal(
            ReleaseManifestFailureCode.InvalidSignature,
            result.Report.FailureCode);
        Assert.Null(result.VerifiedManifest);
    }

    [Fact]
    public void SnapshotCopiesPackageArrayBeforeRetention()
    {
        LocalImmutableReleasePackage[] packages = CreatePackages();
        SignedReleaseManifestPayload payload = CreatePayload(packages);
        VerifiedReleaseManifestSnapshot snapshot =
            VerifiedReleaseManifestSnapshot.Create(payload);
        payload.Packages[0] = payload.Packages[1];

        Assert.Equal(ReleasePackageRole.GatewayWeb, snapshot.Packages[0].Role);
        Assert.Equal("packages/gateway.tar", snapshot.Packages[0].RelativePath);
    }

    [Fact]
    public void DiagnosticsRegisterOnlyPureComposition()
    {
        VerifiedReleaseInstallationPlanDiagnostics snapshot =
            new VerifiedReleaseInstallationPlanComposer().Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.VerifiedManifestInputRegistered);
        Assert.True(snapshot.InstallationPathCompositionRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.ArchiveExtractionRegistered);
        Assert.False(snapshot.FileWriteRegistered);
        Assert.False(snapshot.StagingExecutionRegistered);
        Assert.False(snapshot.InstallationExecutionRegistered);
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
    public void PublicSurfaceHasNoExecutionOrMutationMethod()
    {
        string[] methods = typeof(VerifiedReleaseInstallationPlanComposer)
            .GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Compose", "get_Snapshot"], methods);
        Assert.DoesNotContain(
            methods,
            name =>
                name.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Stage", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Install", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Activate", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Rollback", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Transmit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SuccessfulCompositionProducesCanonicalInternalPlan()
    {
        SignedReleaseManifestPayload payload = CreatePayload(CreatePackages());
        OfflineReleaseInstallPreflightResult preflight = SuccessfulPreflight(payload);
        InstallationPaths paths = CreatePaths();
        VerifiedReleaseInstallationPlanComposer composer = new();

        VerifiedReleaseInstallationPlanCompositionResult result =
            composer.Compose(preflight, paths);

        Assert.True(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseInstallationPlanFailureCode.None,
            result.FailureCode);
        Assert.Equal(4, result.PackageCount);
        Assert.Equal(2, result.TargetConfigurationSchemaVersion);
        Assert.True(result.MigrationRequired);
        Assert.Equal(4, result.RestartServiceCount);
        Assert.True(result.HostRestartRequired);
        Assert.True(result.ImmutableTargetRequired);
        Assert.True(result.TemporaryStagingRequired);
        Assert.True(result.AtomicDirectoryPublishRequired);
        Assert.True(result.AtomicCurrentPointerSwitchRequired);
        Assert.True(result.StablePreflightRevalidationRequired);

        VerifiedReleaseInstallationPlan plan =
            Assert.IsType<VerifiedReleaseInstallationPlan>(result.Plan);
        Assert.Equal(
            Path.Combine(paths.ReleaseDirectory, payload.ReleaseIdentity),
            plan.TargetReleasePath);
        Assert.Equal(
            Path.GetDirectoryName(paths.ReleaseDirectory),
            plan.DeploymentRootPath);
        Assert.Equal(
            [
                ReleasePackageRole.GatewayWeb,
                ReleasePackageRole.Broker,
                ReleasePackageRole.AetherRemoteAgent,
                ReleasePackageRole.StationEngine
            ],
            plan.Packages.Select(package => package.Role));
        Assert.All(
            plan.Packages,
            package => Assert.StartsWith(
                plan.TargetReleasePath + Path.DirectorySeparatorChar,
                package.TargetPath,
                StringComparison.Ordinal));
    }

    [Fact]
    public void SignedRestartMigrationAndNotesMetadataSurviveComposition()
    {
        SignedReleaseManifestPayload payload = CreatePayload(CreatePackages());
        VerifiedReleaseInstallationPlanCompositionResult result =
            new VerifiedReleaseInstallationPlanComposer().Compose(
                SuccessfulPreflight(payload),
                CreatePaths());

        VerifiedReleaseInstallationPlan plan = result.Plan!;
        Assert.Equal(ReleaseMigrationKind.Required, plan.MigrationKind);
        Assert.Equal(1, plan.MigrationFromConfigurationSchemaVersion);
        Assert.Equal(2, plan.MigrationToConfigurationSchemaVersion);
        Assert.Equal("schema-1-to-2", plan.MigrationIdentity);
        Assert.True(plan.RestartGatewayWeb);
        Assert.True(plan.RestartBroker);
        Assert.True(plan.RestartAetherRemoteAgent);
        Assert.True(plan.RestartStationEngine);
        Assert.True(plan.RestartHost);
        Assert.Equal("AetherSDR 8.2.0", plan.ReleaseNotesTitle);
        Assert.Equal("Verified release planning metadata.", plan.ReleaseNotesSummary);
    }

    [Fact]
    public void PublicCompositionResultDoesNotSerializePathsPackagesOrDigests()
    {
        SignedReleaseManifestPayload payload = CreatePayload(CreatePackages());
        InstallationPaths paths = CreatePaths();
        VerifiedReleaseInstallationPlanCompositionResult result =
            new VerifiedReleaseInstallationPlanComposer().Compose(
                SuccessfulPreflight(payload),
                paths);

        string json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain(paths.ReleaseDirectory, json, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway.tar", json, StringComparison.Ordinal);
        Assert.DoesNotContain(payload.Packages[0].Sha256, json, StringComparison.Ordinal);
        Assert.DoesNotContain("targetReleasePath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedPreflightCannotComposePlan()
    {
        SignedReleaseManifestPayload payload = CreatePayload(CreatePackages());
        OfflineReleaseInstallPreflightResult preflight =
            SuccessfulPreflight(payload) with
            {
                Succeeded = false,
                FailureCode =
                    OfflineReleaseInstallPreflightFailureCode.BundleVerificationFailed
            };

        VerifiedReleaseInstallationPlanCompositionResult result =
            new VerifiedReleaseInstallationPlanComposer().Compose(
                preflight,
                CreatePaths());

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseInstallationPlanFailureCode.PreflightNotEligible,
            result.FailureCode);
        Assert.Null(result.Plan);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void RequiredPreflightEvidenceCannotBeMissing(
        bool currentPointerVerified,
        bool targetAbsent,
        bool stable)
    {
        SignedReleaseManifestPayload payload = CreatePayload(CreatePackages());
        OfflineReleaseInstallPreflightResult preflight =
            SuccessfulPreflight(payload) with
            {
                CurrentPointerVerified = currentPointerVerified,
                TargetAbsentFromInventory = targetAbsent,
                StatusStable = stable
            };

        VerifiedReleaseInstallationPlanCompositionResult result =
            new VerifiedReleaseInstallationPlanComposer().Compose(
                preflight,
                CreatePaths());

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseInstallationPlanFailureCode.PreflightNotEligible,
            result.FailureCode);
    }

    [Fact]
    public void SuccessfulSummaryWithoutVerifiedManifestFailsClosed()
    {
        SignedReleaseManifestPayload payload = CreatePayload(CreatePackages());
        OfflineReleaseInstallPreflightResult preflight =
            SuccessfulPreflight(payload) with { VerifiedManifest = null };

        VerifiedReleaseInstallationPlanCompositionResult result =
            new VerifiedReleaseInstallationPlanComposer().Compose(
                preflight,
                CreatePaths());

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseInstallationPlanFailureCode.VerifiedManifestUnavailable,
            result.FailureCode);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("version")]
    [InlineData("architecture")]
    [InlineData("channel")]
    [InlineData("package-count")]
    [InlineData("tx-support")]
    public void PreflightAndManifestMustMatchExactly(string mismatch)
    {
        SignedReleaseManifestPayload payload = CreatePayload(CreatePackages());
        OfflineReleaseInstallPreflightResult preflight = SuccessfulPreflight(payload);
        preflight = mismatch switch
        {
            "identity" => preflight with
            {
                TargetReleaseIdentity = "aethersdr-8.3.0"
            },
            "version" => preflight with { TargetVersion = "8.3.0" },
            "architecture" => preflight with
            {
                Architecture = ReleaseManifestArchitecture.LinuxArm64
            },
            "channel" => preflight with
            {
                UpdateChannel = InstallationUpdateChannel.Beta
            },
            "package-count" => preflight with { PackageCount = 3 },
            "tx-support" => preflight with { TargetTxSupportCapable = true },
            _ => throw new InvalidOperationException("Unknown mismatch.")
        };

        VerifiedReleaseInstallationPlanCompositionResult result =
            new VerifiedReleaseInstallationPlanComposer().Compose(
                preflight,
                CreatePaths());

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseInstallationPlanFailureCode.PreflightManifestMismatch,
            result.FailureCode);
    }

    [Fact]
    public void InvalidInstallationPathsFailWithoutFilesystemAccess()
    {
        SignedReleaseManifestPayload payload = CreatePayload(CreatePackages());
        InstallationPaths originalPaths = CreatePaths();
        InstallationPaths paths = originalPaths with
        {
            BackupDirectory = originalPaths.ReleaseDirectory
        };

        VerifiedReleaseInstallationPlanCompositionResult result =
            new VerifiedReleaseInstallationPlanComposer().Compose(
                SuccessfulPreflight(payload),
                paths);

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseInstallationPlanFailureCode.InvalidInstallationPaths,
            result.FailureCode);
    }

    [Theory]
    [InlineData("unsafe-path")]
    [InlineData("duplicate-role")]
    [InlineData("duplicate-identity")]
    [InlineData("duplicate-path")]
    [InlineData("zero-length")]
    public void InvalidVerifiedPackagePlanFailsClosed(string invalidity)
    {
        SignedReleaseManifestPayload payload = CreatePayload(CreatePackages());
        SignedReleasePackage[] packages = payload.Packages
            .Select(package => package with { })
            .ToArray();
        packages = invalidity switch
        {
            "unsafe-path" => Replace(
                packages,
                0,
                packages[0] with { FileName = "../gateway.tar" }),
            "duplicate-role" => Replace(
                packages,
                1,
                packages[1] with { Role = ReleasePackageRole.GatewayWeb }),
            "duplicate-identity" => Replace(
                packages,
                1,
                packages[1] with
                {
                    PackageIdentity = packages[0].PackageIdentity
                }),
            "duplicate-path" => Replace(
                packages,
                1,
                packages[1] with { FileName = packages[0].FileName }),
            "zero-length" => Replace(
                packages,
                0,
                packages[0] with { Length = 0 }),
            _ => throw new InvalidOperationException("Unknown invalidity.")
        };
        payload = payload with { Packages = packages };
        OfflineReleaseInstallPreflightResult preflight =
            SuccessfulPreflight(payload);

        VerifiedReleaseInstallationPlanCompositionResult result =
            new VerifiedReleaseInstallationPlanComposer().Compose(
                preflight,
                CreatePaths());

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseInstallationPlanFailureCode.InvalidPackagePlan,
            result.FailureCode);
    }

    [Fact]
    public void PackageDigestIsCopiedIntoPlan()
    {
        SignedReleaseManifestPayload payload = CreatePayload(CreatePackages());
        VerifiedReleaseInstallationPlanCompositionResult result =
            new VerifiedReleaseInstallationPlanComposer().Compose(
                SuccessfulPreflight(payload),
                CreatePaths());

        VerifiedReleaseInstallationPackagePlan package = result.Plan!.Packages[0];
        Assert.Equal(
            Convert.FromHexString(payload.Packages[0].Sha256),
            package.Sha256.ToArray());
    }

    private static OfflineReleaseInstallPreflightResult SuccessfulPreflight(
        SignedReleaseManifestPayload payload)
    {
        VerifiedReleaseManifestSnapshot manifest =
            VerifiedReleaseManifestSnapshot.Create(payload);
        return new OfflineReleaseInstallPreflightResult(
            true,
            OfflineReleaseInstallPreflightFailureCode.None,
            "eligible",
            SetupRevision: 7,
            StatusFailureCode: null,
            BundleFailureCode: null,
            ManifestFailureCode: null,
            InstallationUpdateChannel.Stable,
            "aethersdr-8.1.0",
            payload.ReleaseIdentity,
            payload.Version,
            payload.Architecture,
            payload.Packages.Length,
            payload.Packages.Sum(package => package.Length),
            SetupInstallTransmitSupport: false,
            TargetTxSupportCapable: false,
            CurrentPointerVerified: true,
            TargetAbsentFromInventory: true,
            StatusStable: true)
        {
            VerifiedManifest = manifest
        };
    }

    private static InstallationPaths CreatePaths()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"aethersdr-install-plan-{Guid.NewGuid():N}");
        return new InstallationPaths(
            Path.Combine(root, "config"),
            Path.Combine(root, "state"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "deployment", "releases"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "logs"));
    }

    private static LocalImmutableReleasePackage[] CreatePackages() =>
    [
        new("packages/gateway.tar", "gateway"u8),
        new("packages/broker.tar", "broker"u8),
        new("packages/agent.tar", "agent"u8),
        new("packages/engine.tar", "engine"u8)
    ];

    private static SignedReleaseManifestPayload CreatePayload(
        IReadOnlyList<LocalImmutableReleasePackage> packages) =>
        new()
        {
            SchemaVersion = SignedReleaseManifestPayload.CurrentSchemaVersion,
            ReleaseIdentity = "aethersdr-8.2.0",
            Version = "8.2.0",
            Channel = ReleaseManifestChannel.Stable,
            Architecture = ReleaseManifestArchitecture.LinuxX64,
            Packages =
            [
                Package(
                    "gateway",
                    ReleasePackageRole.GatewayWeb,
                    packages[0]),
                Package(
                    "broker",
                    ReleasePackageRole.Broker,
                    packages[1]),
                Package(
                    "agent",
                    ReleasePackageRole.AetherRemoteAgent,
                    packages[2]),
                Package(
                    "engine",
                    ReleasePackageRole.StationEngine,
                    packages[3])
            ],
            Configuration = new ReleaseConfigurationCompatibility
            {
                TargetSchemaVersion = 2,
                MinimumCompatibleSchemaVersion = 1,
                MaximumCompatibleSchemaVersion = 2
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
                Host = true
            },
            Migration = new ReleaseMigrationDeclaration
            {
                Kind = ReleaseMigrationKind.Required,
                FromConfigurationSchemaVersion = 1,
                ToConfigurationSchemaVersion = 2,
                MigrationIdentity = "schema-1-to-2"
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
                Title = "AetherSDR 8.2.0",
                Summary = "Verified release planning metadata."
            }
        };

    private static SignedReleasePackage Package(
        string identity,
        ReleasePackageRole role,
        LocalImmutableReleasePackage package) =>
        new()
        {
            PackageIdentity = identity,
            Role = role,
            FileName = package.RelativePath,
            Length = package.Length,
            Sha256 = Convert.ToHexString(package.Sha256).ToLowerInvariant()
        };

    private static ReleaseManifestVerificationContext Context() =>
        new(
            ReleaseManifestArchitecture.LinuxX64,
            InstallationUpdateChannel.Stable,
            string.Empty,
            "8.1.0",
            ConfigurationSchemaVersion: 1,
            ProtocolVersion: 2);

    private static ReleaseManifestVerificationKey VerificationKey(ECDsa key) =>
        new(
            "test-key",
            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
            key.ExportSubjectPublicKeyInfo());

    private static byte[] Sign(
        SignedReleaseManifestPayload payload,
        ECDsa key,
        bool corruptSignature = false)
    {
        byte[] signingBytes = SignedReleaseManifestJson.CreateSigningBytes(
            payload,
            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
            "test-key");
        byte[] signature = key.SignData(
            signingBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (corruptSignature)
        {
            signature[0] ^= 0xff;
        }

        return SignedReleaseManifestJson.Serialize(
            new SignedReleaseManifestDocument
            {
                Payload = payload,
                Signature = new ReleaseManifestSignature
                {
                    Algorithm =
                        ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                    KeyId = "test-key",
                    Value = Convert.ToBase64String(signature)
                        .TrimEnd('=')
                        .Replace('+', '-')
                        .Replace('/', '_')
                }
            });
    }

    private static SignedReleasePackage[] Replace(
        SignedReleasePackage[] packages,
        int index,
        SignedReleasePackage replacement)
    {
        packages[index] = replacement;
        return packages;
    }
}
