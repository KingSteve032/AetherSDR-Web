using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseExtractedPublicationPlanCompositionTests
{
    [Fact]
    public void PublicSurfaceExposesOnlyDiagnosticsAndPureComposition()
    {
        string[] methods = typeof(VerifiedReleaseExtractedPublicationPlanComposer)
            .GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Compose", "get_Snapshot"], methods);
    }

    [Fact]
    public void DiagnosticsExposeOnlyCallerlessCompositionBoundary()
    {
        VerifiedReleaseExtractedPublicationPlanDiagnostics snapshot =
            new VerifiedReleaseExtractedPublicationPlanComposer().Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.VerifiedExtractionInputRegistered);
        Assert.True(snapshot.ExtractionSummaryValidationRegistered);
        Assert.True(snapshot.ImmutableFileInventoryCompositionRegistered);
        Assert.True(snapshot.ExecutableIntentCompositionRegistered);
        Assert.True(snapshot.SourcePathCompositionRegistered);
        Assert.True(snapshot.TargetPathCompositionRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.ArchiveExtractionExecutionRegistered);
        Assert.False(snapshot.FileWriteRegistered);
        Assert.False(snapshot.AtomicDirectoryPublishExecutionRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.ActivationRegistered);
        Assert.False(snapshot.RollbackRegistered);
        Assert.False(snapshot.MigrationExecutionRegistered);
        Assert.False(snapshot.ServiceControlRegistered);
        Assert.False(snapshot.CliCallerRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public void SuccessfulCompositionProducesExactSourceAndTargetFileMappings()
    {
        CompositionFixture fixture = new();
        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(fixture.ExtractionReport);

        Assert.True(result.Succeeded, $"{result.FailureCode}: {result.Message}");
        Assert.Equal(
            VerifiedReleaseExtractedPublicationPlanFailureCode.None,
            result.FailureCode);
        Assert.Equal(fixture.Plan.SetupRevision, result.SetupRevision);
        Assert.Equal(fixture.Plan.InstalledReleaseIdentity, result.InstalledReleaseIdentity);
        Assert.Equal(fixture.Plan.TargetReleaseIdentity, result.TargetReleaseIdentity);
        Assert.Equal(4, result.PackageCount);
        Assert.Equal(fixture.Files.Count, result.FileCount);
        Assert.Equal(fixture.DirectoryCount, result.DirectoryCount);
        Assert.Equal(fixture.ExpandedBytes, result.PublicationBytes);
        Assert.True(result.ManifestIncluded);
        Assert.True(result.ImmutableSourceRequired);
        Assert.True(result.AtomicDirectoryPublishRequired);
        Assert.False(result.CurrentPointerChanged);
        Assert.False(result.ActivationPerformed);

        VerifiedReleaseExtractedPublicationPlan plan =
            Assert.IsType<VerifiedReleaseExtractedPublicationPlan>(result.Plan);
        Assert.Same(fixture.ExtractedRelease, plan.Source);
        Assert.Equal(fixture.ExtractionPath, plan.SourcePath);
        Assert.Equal(fixture.Plan.TargetReleasePath, plan.TargetPath);
        Assert.Equal(fixture.DirectoryCount, plan.DirectoryCount);
        Assert.Equal(fixture.ExpandedBytes, plan.PublicationBytes);
        Assert.Equal(
            fixture.Files
                .Select(file => file.RelativePath)
                .OrderBy(path => path, StringComparer.Ordinal),
            plan.Files.Select(file => file.RelativePath));

        foreach (VerifiedReleaseExtractedPublicationFilePlan file in plan.Files)
        {
            VerifiedExtractedReleaseFile expected = fixture.Files.Single(
                candidate => string.Equals(
                    candidate.RelativePath,
                    file.RelativePath,
                    StringComparison.Ordinal));
            Assert.Equal(expected.Role, file.Role);
            Assert.Equal(expected.Length, file.Length);
            Assert.Equal(expected.Executable, file.Executable);
            Assert.True(expected.Sha256.SequenceEqual(file.Sha256));
            Assert.Equal(
                Path.GetFullPath(
                    Path.Combine(
                        fixture.ExtractionPath,
                        expected.RelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar))),
                file.SourcePath);
            Assert.Equal(
                Path.GetFullPath(
                    Path.Combine(
                        fixture.Plan.TargetReleasePath,
                        expected.RelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar))),
                file.TargetPath);
        }
    }

    [Fact]
    public void CompositionPerformsNoFilesystemIo()
    {
        CompositionFixture fixture = new();
        Assert.False(Directory.Exists(fixture.Root));

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(fixture.ExtractionReport);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public void PublicResultIsPathNameAndDigestRedacted()
    {
        CompositionFixture fixture = new();
        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(fixture.ExtractionReport);
        string json = JsonSerializer.Serialize(result);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("AetherSDR.Web", json, StringComparison.Ordinal);
        Assert.DoesNotContain("release-manifest.json", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(fixture.Files[1].Sha256),
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedExtractionCannotCompose()
    {
        CompositionFixture fixture = new();
        VerifiedReleaseArchiveExtractionReport extraction =
            fixture.ExtractionReport with
            {
                Succeeded = false,
                FailureCode =
                    VerifiedReleaseArchiveExtractionFailureCode.InvalidArchive
            };

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(extraction);

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode.ExtractionNotEligible);
    }

    [Fact]
    public void SuccessfulSummaryWithoutInternalTokenCannotCompose()
    {
        CompositionFixture fixture = new();
        VerifiedReleaseArchiveExtractionReport extraction =
            fixture.ExtractionReport with { ExtractedRelease = null };

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(extraction);

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .ExtractedReleaseUnavailable);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("file-count")]
    [InlineData("directory-count")]
    [InlineData("bytes")]
    public void ExtractionSummaryMustMatchInternalToken(string mismatch)
    {
        CompositionFixture fixture = new();
        VerifiedReleaseArchiveExtractionReport extraction = mismatch switch
        {
            "identity" => fixture.ExtractionReport with
            {
                TargetReleaseIdentity = "aethersdr-8.3.0"
            },
            "file-count" => fixture.ExtractionReport with
            {
                ExtractedFileCount = fixture.ExtractionReport.ExtractedFileCount - 1
            },
            "directory-count" => fixture.ExtractionReport with
            {
                ExtractedDirectoryCount =
                    fixture.ExtractionReport.ExtractedDirectoryCount - 1
            },
            "bytes" => fixture.ExtractionReport with
            {
                ExpandedBytes = fixture.ExtractionReport.ExpandedBytes - 1
            },
            _ => throw new InvalidOperationException("Unknown mismatch.")
        };

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(extraction);

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .ExtractionSummaryMismatch);
    }

    [Fact]
    public void ExtractionPathOutsidePrivateRootFailsClosed()
    {
        CompositionFixture fixture = new();
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(
            extractionPath: Path.Combine(fixture.Root, "outside"));

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .InvalidPublicationPaths);
    }

    [Fact]
    public void NonCanonicalTargetPathFailsClosed()
    {
        CompositionFixture fixture = new();
        VerifiedReleaseInstallationPlan invalidPlan = fixture.CreatePlan(
            targetPath: Path.Combine(
                fixture.ReleaseRoot,
                fixture.TargetIdentity,
                "nested"));
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(
            plan: invalidPlan);

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode.InvalidSourcePlan);
    }

    [Fact]
    public void InvalidPinnedPlanFailsClosed()
    {
        CompositionFixture fixture = new();
        VerifiedReleaseInstallationPlan invalidPlan = fixture.CreatePlan(
            updateChannel: InstallationUpdateChannel.Pinned,
            pinnedReleaseIdentity: "aethersdr-8.3.0");
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(
            plan: invalidPlan);

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode.InvalidSourcePlan);
    }

    [Fact]
    public void ManifestMustMatchRetainedSignedMetadata()
    {
        CompositionFixture fixture = new();
        List<VerifiedExtractedReleaseFile> files = fixture.Files.ToList();
        files[0] = fixture.File(
            ReleasePackageRole.Unknown,
            LocalOfflineReleaseBundleVerificationService.ManifestFileName,
            "different-manifest"u8.ToArray(),
            executable: false);
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(
            files: files,
            expandedBytes: files.Sum(file => file.Length));

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .InvalidExtractedFileInventory);
    }

    [Fact]
    public void ManifestCannotBeExecutable()
    {
        CompositionFixture fixture = new();
        List<VerifiedExtractedReleaseFile> files = fixture.Files.ToList();
        files[0] = new VerifiedExtractedReleaseFile(
            ReleasePackageRole.Unknown,
            files[0].RelativePath,
            files[0].Length,
            files[0].Sha256,
            executable: true);
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(files: files);

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .InvalidExtractedFileInventory);
    }

    [Fact]
    public void DuplicateExtractedPathFailsClosed()
    {
        CompositionFixture fixture = new();
        List<VerifiedExtractedReleaseFile> files = fixture.Files.ToList();
        files.Add(files[1]);
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(
            files: files,
            expandedBytes: files.Sum(file => file.Length));

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .InvalidExtractedFileInventory);
    }

    [Fact]
    public void RoleFileMustRemainUnderItsExactRoleRoot()
    {
        CompositionFixture fixture = new();
        List<VerifiedExtractedReleaseFile> files = fixture.Files.ToList();
        int brokerIndex = files.FindIndex(
            file => file.Role == ReleasePackageRole.Broker);
        VerifiedExtractedReleaseFile broker = files[brokerIndex];
        files[brokerIndex] = new VerifiedExtractedReleaseFile(
            broker.Role,
            "gateway-web/AetherRemote.Broker",
            broker.Length,
            broker.Sha256,
            broker.Executable);
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(files: files);

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .InvalidExtractedFileInventory);
    }

    [Fact]
    public void EveryRequiredRoleMustContainAtLeastOneFile()
    {
        CompositionFixture fixture = new();
        List<VerifiedExtractedReleaseFile> files = fixture.Files
            .Where(file => file.Role != ReleasePackageRole.StationEngine)
            .ToList();
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(
            files: files,
            directoryCount: fixture.DirectoryCount - 1,
            expandedBytes: files.Sum(file => file.Length));

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .InvalidExtractedFileInventory);
    }

    [Fact]
    public void DirectoryCountMustEqualExactFileParentInventory()
    {
        CompositionFixture fixture = new();
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(
            directoryCount: fixture.DirectoryCount + 1);

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .InvalidExtractedFileInventory);
    }

    [Fact]
    public void ExpandedByteTotalMustEqualExactFileInventory()
    {
        CompositionFixture fixture = new();
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(
            expandedBytes: fixture.ExpandedBytes + 1);

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .ExtractionSummaryMismatch);
    }

    [Fact]
    public void UnsafeRelativeFilePathFailsClosed()
    {
        CompositionFixture fixture = new();
        List<VerifiedExtractedReleaseFile> files = fixture.Files.ToList();
        VerifiedExtractedReleaseFile gateway = files[1];
        files[1] = new VerifiedExtractedReleaseFile(
            gateway.Role,
            "gateway-web/../escape",
            gateway.Length,
            gateway.Sha256,
            gateway.Executable);
        VerifiedExtractedRelease source = fixture.CreateExtractedRelease(files: files);

        VerifiedReleaseExtractedPublicationPlanCompositionResult result =
            fixture.Composer.Compose(
                VerifiedReleaseArchiveExtractionReport.Success(source));

        AssertFailure(
            result,
            VerifiedReleaseExtractedPublicationPlanFailureCode
                .InvalidExtractedFileInventory);
    }

    private static void AssertFailure(
        VerifiedReleaseExtractedPublicationPlanCompositionResult result,
        VerifiedReleaseExtractedPublicationPlanFailureCode failureCode)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(failureCode, result.FailureCode);
        Assert.Null(result.Plan);
        Assert.False(result.CurrentPointerChanged);
        Assert.False(result.ActivationPerformed);
    }

    private sealed class CompositionFixture
    {
        private readonly byte[] m_manifest = "verified-manifest"u8.ToArray();
        private readonly Dictionary<ReleasePackageRole, byte[]> m_packages;

        internal CompositionFixture()
        {
            Root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"aethersdr-extracted-publication-plan-{Guid.NewGuid():N}"));
            DeploymentRoot = Path.Combine(Root, "deployment");
            ReleaseRoot = Path.Combine(DeploymentRoot, "releases");
            BundlePath = Path.Combine(Root, "bundle");
            TargetIdentity = "aethersdr-8.2.0";
            TargetPath = Path.Combine(ReleaseRoot, TargetIdentity);
            StagingPath = Path.Combine(
                DeploymentRoot,
                VerifiedReleaseStagingService.StagingDirectoryName,
                $"{TargetIdentity}.{Guid.NewGuid():N}");
            ExtractionPath = Path.Combine(
                DeploymentRoot,
                VerifiedReleaseArchiveExtractionService
                    .ExtractionStagingDirectoryName,
                $"{TargetIdentity}.{Guid.NewGuid():N}");
            m_packages = new Dictionary<ReleasePackageRole, byte[]>
            {
                [ReleasePackageRole.GatewayWeb] = "gateway-archive"u8.ToArray(),
                [ReleasePackageRole.Broker] = "broker-archive"u8.ToArray(),
                [ReleasePackageRole.AetherRemoteAgent] = "agent-archive"u8.ToArray(),
                [ReleasePackageRole.StationEngine] = "station-archive"u8.ToArray()
            };
            Plan = CreatePlan();
            Files =
            [
                File(
                    ReleasePackageRole.Unknown,
                    LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                    m_manifest,
                    executable: false),
                File(
                    ReleasePackageRole.GatewayWeb,
                    "gateway-web/AetherSDR.Web",
                    "gateway-web"u8.ToArray(),
                    executable: true),
                File(
                    ReleasePackageRole.GatewayWeb,
                    "gateway-web/appsettings.json",
                    "{}"u8.ToArray(),
                    executable: false),
                File(
                    ReleasePackageRole.GatewayWeb,
                    "gateway-web/watchdog/AetherSDR.TxWatchdog",
                    "watchdog"u8.ToArray(),
                    executable: true),
                File(
                    ReleasePackageRole.Broker,
                    "broker/AetherRemote.Broker",
                    "broker"u8.ToArray(),
                    executable: true),
                File(
                    ReleasePackageRole.AetherRemoteAgent,
                    "aetherremote-agent/AetherRemote.Agent",
                    "agent"u8.ToArray(),
                    executable: true),
                File(
                    ReleasePackageRole.StationEngine,
                    "station-engine/AetherSDR.Web",
                    "station"u8.ToArray(),
                    executable: true)
            ];
            DirectoryCount = 5;
            ExpandedBytes = Files.Sum(file => file.Length);
            ExtractedRelease = CreateExtractedRelease();
            ExtractionReport =
                VerifiedReleaseArchiveExtractionReport.Success(ExtractedRelease);
            Composer = new VerifiedReleaseExtractedPublicationPlanComposer();
        }

        internal string Root { get; }
        internal string DeploymentRoot { get; }
        internal string ReleaseRoot { get; }
        internal string BundlePath { get; }
        internal string TargetIdentity { get; }
        internal string TargetPath { get; }
        internal string StagingPath { get; }
        internal string ExtractionPath { get; }
        internal int DirectoryCount { get; }
        internal long ExpandedBytes { get; }
        internal VerifiedReleaseInstallationPlan Plan { get; }
        internal IReadOnlyList<VerifiedExtractedReleaseFile> Files { get; }
        internal VerifiedExtractedRelease ExtractedRelease { get; }
        internal VerifiedReleaseArchiveExtractionReport ExtractionReport { get; }
        internal VerifiedReleaseExtractedPublicationPlanComposer Composer { get; }

        internal VerifiedReleaseInstallationPlan CreatePlan(
            string? targetPath = null,
            InstallationUpdateChannel updateChannel =
                InstallationUpdateChannel.Stable,
            string pinnedReleaseIdentity = "")
        {
            string resolvedTargetPath = targetPath ?? TargetPath;
            VerifiedReleaseInstallationPackagePlan[] packages =
            [
                Package(
                    "gateway",
                    ReleasePackageRole.GatewayWeb,
                    "packages/gateway.tar.gz",
                    resolvedTargetPath),
                Package(
                    "broker",
                    ReleasePackageRole.Broker,
                    "packages/broker.tar.gz",
                    resolvedTargetPath),
                Package(
                    "agent",
                    ReleasePackageRole.AetherRemoteAgent,
                    "packages/agent.tar.gz",
                    resolvedTargetPath),
                Package(
                    "station",
                    ReleasePackageRole.StationEngine,
                    "packages/station.tar.gz",
                    resolvedTargetPath)
            ];
            return new VerifiedReleaseInstallationPlan(
                setupRevision: 42,
                installedReleaseIdentity: "aethersdr-8.1.0",
                TargetIdentity,
                targetVersion: "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                updateChannel,
                pinnedReleaseIdentity,
                installTransmitSupport: false,
                BundlePath,
                m_manifest.LongLength,
                SHA256.HashData(m_manifest),
                ReleaseRoot,
                DeploymentRoot,
                resolvedTargetPath,
                packages,
                targetConfigurationSchemaVersion: 1,
                ReleaseMigrationKind.None,
                migrationFromConfigurationSchemaVersion: null,
                migrationToConfigurationSchemaVersion: null,
                migrationIdentity: string.Empty,
                restartGatewayWeb: true,
                restartBroker: true,
                restartAetherRemoteAgent: true,
                restartStationEngine: true,
                restartHost: false,
                txSupportCapable: false,
                releaseNotesTitle: "Release",
                releaseNotesSummary: "Extracted publication plan test.");
        }

        internal VerifiedExtractedRelease CreateExtractedRelease(
            VerifiedReleaseInstallationPlan? plan = null,
            string? extractionPath = null,
            IReadOnlyList<VerifiedExtractedReleaseFile>? files = null,
            int? directoryCount = null,
            long? expandedBytes = null)
        {
            VerifiedReleaseInstallationPlan resolvedPlan = plan ?? Plan;
            VerifiedStagedRelease staged = new(
                resolvedPlan,
                StagingPath,
                checked(
                    resolvedPlan.ManifestLength +
                    resolvedPlan.Packages.Sum(package => package.Length)));
            IReadOnlyList<VerifiedExtractedReleaseFile> resolvedFiles =
                files ?? Files;
            return new VerifiedExtractedRelease(
                staged,
                extractionPath ?? ExtractionPath,
                resolvedFiles,
                directoryCount ?? DirectoryCount,
                expandedBytes ?? resolvedFiles.Sum(file => file.Length));
        }

        internal VerifiedExtractedReleaseFile File(
            ReleasePackageRole role,
            string relativePath,
            byte[] content,
            bool executable) =>
            new(
                role,
                relativePath,
                content.LongLength,
                SHA256.HashData(content),
                executable);

        private VerifiedReleaseInstallationPackagePlan Package(
            string identity,
            ReleasePackageRole role,
            string relativePath,
            string targetPath)
        {
            byte[] content = m_packages[role];
            SignedReleasePackage package = new()
            {
                PackageIdentity = identity,
                Role = role,
                FileName = relativePath,
                Length = content.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(content))
            };
            return new VerifiedReleaseInstallationPackagePlan(
                new VerifiedReleasePackageSnapshot(package),
                Path.GetFullPath(
                    Path.Combine(
                        targetPath,
                        relativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar))));
        }
    }
}
