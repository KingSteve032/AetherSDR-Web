using System.Security.Cryptography;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseExtractedActivationAdaptationTests
{
    [Fact]
    public void ExtractedInactivePublicationAdaptsToExactActivationPlan()
    {
        AdaptationFixture fixture = new();

        VerifiedReleaseActivationPlanCompositionResult result =
            new VerifiedReleaseActivationPlanComposer().Compose(
                fixture.Publication);

        Assert.True(result.Succeeded, $"{result.FailureCode}: {result.Message}");
        VerifiedReleaseActivationPlan plan =
            Assert.IsType<VerifiedReleaseActivationPlan>(result.Plan);
        Assert.True(plan.UsesExtractedRoleTree);
        Assert.Equal(fixture.Files.Count, plan.Files.Count);
        Assert.Equal(fixture.DirectoryCount, plan.ExtractedDirectoryCount);
        Assert.Equal(fixture.Plan.TargetReleasePath, plan.TargetReleasePath);
        Assert.Equal(
            [
                "gateway-web",
                "broker",
                "aetherremote-agent",
                "station-engine"
            ],
            plan.Packages.Select(package =>
                Path.GetFileName(package.PublishedPath)));
        Assert.Equal(
            fixture.Files
                .Select(file => file.RelativePath)
                .OrderBy(path => path, StringComparer.Ordinal),
            plan.Files.Select(file => file.RelativePath));
        Assert.All(
            plan.Files,
            file => Assert.StartsWith(
                fixture.Plan.TargetReleasePath + Path.DirectorySeparatorChar,
                file.PublishedPath,
                StringComparison.Ordinal));
        Assert.False(result.CurrentPointerMutationPerformed);
        Assert.False(result.ActivationPerformed);
    }

    [Fact]
    public void ExtractedPublicationSummaryDriftFailsBeforeActivationPlan()
    {
        AdaptationFixture fixture = new();
        VerifiedReleaseExtractedPublicationReport drifted =
            fixture.Publication with
            {
                FileCount = fixture.Publication.FileCount - 1
            };

        VerifiedReleaseActivationPlanCompositionResult result =
            new VerifiedReleaseActivationPlanComposer().Compose(drifted);

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.PublicationNotEligible,
            result.FailureCode);
        Assert.Null(result.Plan);
        Assert.False(result.CurrentPointerMutationPerformed);
        Assert.False(result.ActivationPerformed);
    }

    [Fact]
    public void ExtractedRoleFileCannotCrossServiceRoot()
    {
        AdaptationFixture fixture = new();
        List<VerifiedReleaseExtractedPublicationFilePlan> plans =
            fixture.PublicationPlan.Files.ToList();
        VerifiedReleaseExtractedPublicationFilePlan broker = plans.Single(
            file => file.Role == ReleasePackageRole.Broker);
        int index = plans.IndexOf(broker);
        plans[index] = new VerifiedReleaseExtractedPublicationFilePlan(
            broker.Role,
            "gateway-web/AetherRemote.Broker",
            Path.Combine(fixture.ExtractionPath, "gateway-web", "AetherRemote.Broker"),
            Path.Combine(fixture.TargetPath, "gateway-web", "AetherRemote.Broker"),
            broker.Length,
            broker.Sha256,
            broker.Executable);
        VerifiedReleaseExtractedPublicationPlan tamperedPlan = new(
            fixture.ExtractedRelease,
            fixture.ExtractionPath,
            fixture.TargetPath,
            plans,
            fixture.DirectoryCount,
            fixture.ExpandedBytes);
        VerifiedReleaseExtractedPublicationReport report =
            VerifiedReleaseExtractedPublicationReport.Success(
                new VerifiedExtractedPublishedRelease(
                    tamperedPlan,
                    fixture.TargetPath,
                    fixture.ExpandedBytes));

        VerifiedReleaseActivationPlanCompositionResult result =
            new VerifiedReleaseActivationPlanComposer().Compose(report);

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.InvalidPackagePlan,
            result.FailureCode);
    }

    private sealed class AdaptationFixture
    {
        private readonly byte[] m_manifest = "verified-manifest"u8.ToArray();
        private readonly Dictionary<ReleasePackageRole, byte[]> m_archives;

        internal AdaptationFixture()
        {
            Root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"aethersdr-activation-adaptation-{Guid.NewGuid():N}"));
            DeploymentRoot = Path.Combine(Root, "deployment");
            ReleaseRoot = Path.Combine(DeploymentRoot, "releases");
            TargetIdentity = "aethersdr-8.2.0";
            TargetPath = Path.Combine(ReleaseRoot, TargetIdentity);
            BundlePath = Path.Combine(Root, "bundle");
            StagingPath = Path.Combine(
                DeploymentRoot,
                VerifiedReleaseStagingService.StagingDirectoryName,
                $"{TargetIdentity}.{Guid.NewGuid():N}");
            ExtractionPath = Path.Combine(
                DeploymentRoot,
                VerifiedReleaseArchiveExtractionService
                    .ExtractionStagingDirectoryName,
                $"{TargetIdentity}.{Guid.NewGuid():N}");
            m_archives = new Dictionary<ReleasePackageRole, byte[]>
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
                    "gateway"u8.ToArray(),
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
            DirectoryCount = 4;
            ExpandedBytes = Files.Sum(file => file.Length);
            VerifiedStagedRelease staged = new(
                Plan,
                StagingPath,
                Plan.ManifestLength + Plan.Packages.Sum(package => package.Length));
            ExtractedRelease = new VerifiedExtractedRelease(
                staged,
                ExtractionPath,
                Files,
                DirectoryCount,
                ExpandedBytes);
            VerifiedReleaseExtractedPublicationFilePlan[] publicationFiles =
                Files.Select(file =>
                    new VerifiedReleaseExtractedPublicationFilePlan(
                        file.Role,
                        file.RelativePath,
                        Canonical(ExtractionPath, file.RelativePath),
                        Canonical(TargetPath, file.RelativePath),
                        file.Length,
                        file.Sha256,
                        file.Executable))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray();
            PublicationPlan = new VerifiedReleaseExtractedPublicationPlan(
                ExtractedRelease,
                ExtractionPath,
                TargetPath,
                publicationFiles,
                DirectoryCount,
                ExpandedBytes);
            Publication = VerifiedReleaseExtractedPublicationReport.Success(
                new VerifiedExtractedPublishedRelease(
                    PublicationPlan,
                    TargetPath,
                    ExpandedBytes));
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
        internal VerifiedReleaseExtractedPublicationPlan PublicationPlan { get; }
        internal VerifiedReleaseExtractedPublicationReport Publication { get; }

        private VerifiedReleaseInstallationPlan CreatePlan()
        {
            VerifiedReleaseInstallationPackagePlan[] packages =
            [
                Package("gateway", ReleasePackageRole.GatewayWeb,
                    "packages/gateway.tar.gz"),
                Package("broker", ReleasePackageRole.Broker,
                    "packages/broker.tar.gz"),
                Package("agent", ReleasePackageRole.AetherRemoteAgent,
                    "packages/agent.tar.gz"),
                Package("station", ReleasePackageRole.StationEngine,
                    "packages/station.tar.gz")
            ];
            return new VerifiedReleaseInstallationPlan(
                setupRevision: 42,
                installedReleaseIdentity: "aethersdr-8.1.0",
                TargetIdentity,
                targetVersion: "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                pinnedReleaseIdentity: string.Empty,
                installTransmitSupport: false,
                BundlePath,
                m_manifest.LongLength,
                SHA256.HashData(m_manifest),
                ReleaseRoot,
                DeploymentRoot,
                TargetPath,
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
                releaseNotesSummary: "Extracted activation adaptation test.");
        }

        private VerifiedReleaseInstallationPackagePlan Package(
            string identity,
            ReleasePackageRole role,
            string relativePath)
        {
            byte[] bytes = m_archives[role];
            SignedReleasePackage package = new()
            {
                PackageIdentity = identity,
                Role = role,
                FileName = relativePath,
                Length = bytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            };
            return new VerifiedReleaseInstallationPackagePlan(
                new VerifiedReleasePackageSnapshot(package),
                Canonical(TargetPath, relativePath));
        }

        private static VerifiedExtractedReleaseFile File(
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

        private static string Canonical(string root, string relative) =>
            Path.GetFullPath(
                Path.Combine(
                    root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}
