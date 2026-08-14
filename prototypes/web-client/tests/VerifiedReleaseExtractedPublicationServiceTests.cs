using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleaseExtractedPublicationServiceTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsButNoPublicationCaller()
    {
        string[] methods = typeof(VerifiedReleaseExtractedPublicationService)
            .GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["get_Snapshot"], methods);
    }

    [Fact]
    public async Task DiagnosticsExposeOnlyCallerlessAtomicPublicationBoundary()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseExtractedPublicationDiagnostics snapshot =
            fixture.Service.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.StatusRevalidationRegistered);
        Assert.True(snapshot.VerifiedPlanInputRegistered);
        Assert.True(snapshot.ImmutableSourceValidationRegistered);
        Assert.True(snapshot.ExecutableIntentValidationRegistered);
        Assert.True(snapshot.RootPermissionTransitionRegistered);
        Assert.True(snapshot.AtomicDirectoryPublishRegistered);
        Assert.True(snapshot.PublishedTreeValidationRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.ArchiveExtractionExecutionRegistered);
        Assert.False(snapshot.FileCopyRegistered);
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
    public async Task SuccessfulPublicationAtomicallyMovesExactRoleTree()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        string sourcePath = fixture.SourcePath;

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(fixture.Composition);

        Assert.True(report.Succeeded, $"{report.FailureCode}: {report.Message}");
        Assert.Equal(
            VerifiedReleaseExtractedPublicationFailureCode.None,
            report.FailureCode);
        Assert.Equal(fixture.Plan.SetupRevision, report.SetupRevision);
        Assert.Equal(fixture.Plan.TargetReleaseIdentity, report.TargetReleaseIdentity);
        Assert.Equal(4, report.PackageCount);
        Assert.Equal(fixture.Files.Count, report.FileCount);
        Assert.Equal(fixture.DirectoryCount, report.DirectoryCount);
        Assert.Equal(fixture.PublicationBytes, report.PublishedBytes);
        Assert.True(report.SourceExtractionTreeConsumed);
        Assert.True(report.TargetPublished);
        Assert.True(report.TargetImmutable);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
        Assert.False(report.ReconciliationRequired);
        Assert.False(Directory.Exists(sourcePath));
        Assert.True(Directory.Exists(fixture.TargetPath));

        VerifiedExtractedPublishedRelease published =
            Assert.IsType<VerifiedExtractedPublishedRelease>(report.PublishedRelease);
        Assert.Same(fixture.PublicationPlan, published.Plan);
        Assert.Equal(fixture.TargetPath, published.PublishedPath);
        Assert.Equal(fixture.PublicationBytes, published.PublishedBytes);
    }

    [Fact]
    public async Task PublishedTreeRetainsExactBytesAndExecutableIntent()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(fixture.Composition);

        Assert.True(report.Succeeded);
        AssertTreeMatchesFixture(
            fixture,
            fixture.TargetPath,
            publishedTree: true);
        Assert.Equal(
            UnixFileMode.UserRead |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute,
            File.GetUnixFileMode(fixture.TargetPath));
    }

    [Fact]
    public async Task SuccessfulPublicationLeavesCurrentPointerUnchanged()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        string? currentBefore = new DirectoryInfo(fixture.CurrentPath).LinkTarget;

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(fixture.Composition);

        Assert.True(report.Succeeded);
        Assert.Equal(
            currentBefore,
            new DirectoryInfo(fixture.CurrentPath).LinkTarget);
        Assert.Equal(
            fixture.Plan.InstalledReleaseIdentity,
            fixture.ReadStatus().ActiveReleaseIdentity);
    }

    [Fact]
    public async Task PublicReportIsPathNameAndDigestRedacted()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(fixture.Composition);
        string json = JsonSerializer.Serialize(report);

        Assert.True(report.Succeeded);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway-web", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AetherSDR.Web", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(fixture.Files[1].Sha256),
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedCompositionCannotPublish()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseExtractedPublicationPlanCompositionResult composition =
            fixture.Composition with
            {
                Succeeded = false,
                FailureCode =
                    VerifiedReleaseExtractedPublicationPlanFailureCode
                        .InvalidExtractedFileInventory
            };

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(composition);

        AssertFailureWithoutMove(
            fixture,
            report,
            VerifiedReleaseExtractedPublicationFailureCode.PlanNotEligible);
    }

    [Fact]
    public async Task SuccessfulSummaryWithoutInternalPlanCannotPublish()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseExtractedPublicationPlanCompositionResult composition =
            fixture.Composition with { Plan = null };

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(composition);

        AssertFailureWithoutMove(
            fixture,
            report,
            VerifiedReleaseExtractedPublicationFailureCode.PlanNotEligible);
    }

    [Fact]
    public async Task CompositionSummaryMustMatchInternalPlan()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseExtractedPublicationPlanCompositionResult composition =
            fixture.Composition with
            {
                PublicationBytes = fixture.Composition.PublicationBytes + 1
            };

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(composition);

        AssertFailureWithoutMove(
            fixture,
            report,
            VerifiedReleaseExtractedPublicationFailureCode.PlanNotEligible);
    }

    [Fact]
    public async Task UnavailableStatusFailsBeforeFilesystemMutation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseExtractedPublicationService service = new(
            _ => Task.FromResult(
                ReleaseStatusReadResult.Failure(
                    ReleaseStatusFailureCode.StatusReadFailed,
                    "unavailable")),
            Directory.Move);

        VerifiedReleaseExtractedPublicationReport report =
            await service.PublishAsync(fixture.Composition);

        AssertFailureWithoutMove(
            fixture,
            report,
            VerifiedReleaseExtractedPublicationFailureCode.StatusUnavailable);
    }

    [Fact]
    public async Task StatusDriftFailsBeforeFilesystemMutation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        ReleaseStatusReadResult drifted = fixture.ReadStatus() with
        {
            SetupRevision = fixture.Plan.SetupRevision + 1
        };
        VerifiedReleaseExtractedPublicationService service = new(
            _ => Task.FromResult(drifted),
            Directory.Move);

        VerifiedReleaseExtractedPublicationReport report =
            await service.PublishAsync(fixture.Composition);

        AssertFailureWithoutMove(
            fixture,
            report,
            VerifiedReleaseExtractedPublicationFailureCode.StatusMismatch);
    }

    [Fact]
    public async Task ExistingTargetIsNeverOverwritten()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        Directory.CreateDirectory(fixture.TargetPath);
        File.SetUnixFileMode(
            fixture.TargetPath,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        ReleaseStatusReadResult before = fixture.ReadStatus(includeTarget: false);
        VerifiedReleaseExtractedPublicationService service = new(
            _ => Task.FromResult(before),
            Directory.Move);

        VerifiedReleaseExtractedPublicationReport report =
            await service.PublishAsync(fixture.Composition);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseExtractedPublicationFailureCode.TargetAlreadyPresent,
            report.FailureCode);
        Assert.True(Directory.Exists(fixture.SourcePath));
        Assert.True(Directory.Exists(fixture.TargetPath));
    }

    [Fact]
    public async Task WritableSourceFileFailsClosed()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        string path = fixture.SourceFile(fixture.Files[1].RelativePath);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(fixture.Composition);

        AssertFailureWithoutMove(
            fixture,
            report,
            VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree);
    }

    [Fact]
    public async Task ExecutableModeMismatchFailsClosed()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        string path = fixture.SourceFile(fixture.Files[1].RelativePath);
        File.SetUnixFileMode(path, UnixFileMode.UserRead);

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(fixture.Composition);

        AssertFailureWithoutMove(
            fixture,
            report,
            VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree);
    }

    [Fact]
    public async Task SymbolicLinkInSourceFailsClosed()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        fixture.MakeSourceRootWritable();
        File.CreateSymbolicLink(
            Path.Combine(fixture.SourcePath, "link"),
            fixture.SourceFile(fixture.Files[1].RelativePath));
        fixture.FreezeSourceRoot();

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(fixture.Composition);

        AssertFailureWithoutMove(
            fixture,
            report,
            VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree);
    }

    [Fact]
    public async Task ExtraDirectoryInSourceFailsClosed()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        fixture.MakeSourceRootWritable();
        string extra = Path.Combine(fixture.SourcePath, "unexpected");
        Directory.CreateDirectory(extra);
        File.SetUnixFileMode(
            extra,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        fixture.FreezeSourceRoot();

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(fixture.Composition);

        AssertFailureWithoutMove(
            fixture,
            report,
            VerifiedReleaseExtractedPublicationFailureCode.UnsafeSourceTree);
    }

    [Fact]
    public async Task UnsafeExtractionRootFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        File.SetUnixFileMode(
            fixture.ExtractionRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupWrite);

        VerifiedReleaseExtractedPublicationReport report =
            await fixture.Service.PublishAsync(fixture.Composition);

        AssertFailureWithoutMove(
            fixture,
            report,
            VerifiedReleaseExtractedPublicationFailureCode.UnsafeDeploymentLayout);
    }

    [Fact]
    public async Task PreCancelledOperationDoesNotMutateFilesystem()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        using CancellationTokenSource source = new();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Service.PublishAsync(fixture.Composition, source.Token));

        Assert.True(Directory.Exists(fixture.SourcePath));
        Assert.False(Directory.Exists(fixture.TargetPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserExecute,
            File.GetUnixFileMode(fixture.SourcePath));
    }

    [Fact]
    public async Task CleanRenameFailureRefreezesAndRetainsSource()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseExtractedPublicationService service = new(
            fixture.ReadStatusAsync,
            (_, _) => throw new IOException("rename failed"));

        VerifiedReleaseExtractedPublicationReport report =
            await service.PublishAsync(fixture.Composition);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseExtractedPublicationFailureCode.AtomicPublishFailed,
            report.FailureCode);
        Assert.False(report.ReconciliationRequired);
        Assert.True(Directory.Exists(fixture.SourcePath));
        Assert.False(Directory.Exists(fixture.TargetPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserExecute,
            File.GetUnixFileMode(fixture.SourcePath));
        AssertTreeMatchesFixture(fixture, fixture.SourcePath);
    }

    [Fact]
    public async Task CompletedButThrewRenameRecoversAfterExactRevalidation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseExtractedPublicationService service = new(
            fixture.ReadStatusAsync,
            (source, target) =>
            {
                Directory.Move(source, target);
                throw new IOException("completion was not reported");
            });

        VerifiedReleaseExtractedPublicationReport report =
            await service.PublishAsync(fixture.Composition);

        Assert.True(report.Succeeded, $"{report.FailureCode}: {report.Message}");
        Assert.False(report.ReconciliationRequired);
        Assert.False(Directory.Exists(fixture.SourcePath));
        Assert.True(Directory.Exists(fixture.TargetPath));
        AssertTreeMatchesFixture(
            fixture,
            fixture.TargetPath,
            publishedTree: true);
    }

    [Fact]
    public async Task BothSourceAndTargetAfterRenameFailureRequireReconciliation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseExtractedPublicationService service = new(
            fixture.ReadStatusAsync,
            (_, target) =>
            {
                Directory.CreateDirectory(target);
                throw new IOException("ambiguous");
            });

        VerifiedReleaseExtractedPublicationReport report =
            await service.PublishAsync(fixture.Composition);

        AssertReconciliation(report);
        Assert.True(Directory.Exists(fixture.SourcePath));
        Assert.True(Directory.Exists(fixture.TargetPath));
    }

    [Fact]
    public async Task MissingSourceAndTargetAfterRenameFailureRequireReconciliation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        string lost = Path.Combine(fixture.Root, "lost-source");
        VerifiedReleaseExtractedPublicationService service = new(
            fixture.ReadStatusAsync,
            (source, _) =>
            {
                Directory.Move(source, lost);
                throw new IOException("ambiguous");
            });

        VerifiedReleaseExtractedPublicationReport report =
            await service.PublishAsync(fixture.Composition);

        AssertReconciliation(report);
        Assert.False(Directory.Exists(fixture.SourcePath));
        Assert.False(Directory.Exists(fixture.TargetPath));
        Assert.True(Directory.Exists(lost));
    }

    [Fact]
    public async Task StatusDriftAfterRenameRequiresReconciliation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        int reads = 0;
        VerifiedReleaseExtractedPublicationService service = new(
            _ => Task.FromResult(
                ++reads == 1
                    ? fixture.ReadStatus(includeTarget: false)
                    : fixture.ReadStatus(includeTarget: true) with
                    {
                        ActiveReleaseIdentity = fixture.Plan.TargetReleaseIdentity
                    }),
            Directory.Move);

        VerifiedReleaseExtractedPublicationReport report =
            await service.PublishAsync(fixture.Composition);

        AssertReconciliation(report);
        Assert.True(report.SourceExtractionTreeConsumed);
        Assert.True(report.TargetPublished);
        Assert.True(report.TargetImmutable);
        Assert.False(Directory.Exists(fixture.SourcePath));
        Assert.True(Directory.Exists(fixture.TargetPath));
    }

    [Fact]
    public async Task TargetTamperingAfterRenameRequiresReconciliation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseExtractedPublicationService service = new(
            fixture.ReadStatusAsync,
            (source, target) =>
            {
                Directory.Move(source, target);
                string file = Path.Combine(
                    target,
                    fixture.Files[1].RelativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar));
                File.SetUnixFileMode(
                    file,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
                File.WriteAllText(file, "tampered");
            });

        VerifiedReleaseExtractedPublicationReport report =
            await service.PublishAsync(fixture.Composition);

        AssertReconciliation(report);
        Assert.True(report.SourceExtractionTreeConsumed);
        Assert.True(report.TargetPublished);
        Assert.False(report.TargetImmutable);
    }

    private static void AssertFailureWithoutMove(
        PublicationFixture fixture,
        VerifiedReleaseExtractedPublicationReport report,
        VerifiedReleaseExtractedPublicationFailureCode failureCode)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.False(report.SourceExtractionTreeConsumed);
        Assert.False(report.TargetPublished);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
        Assert.False(report.ReconciliationRequired);
        Assert.True(Directory.Exists(fixture.SourcePath));
        Assert.False(Directory.Exists(fixture.TargetPath));
    }

    private static void AssertReconciliation(
        VerifiedReleaseExtractedPublicationReport report)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseExtractedPublicationFailureCode
                .PublishedStateRequiresReconciliation,
            report.FailureCode);
        Assert.True(report.ReconciliationRequired);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
    }

    private static void AssertTreeMatchesFixture(
        PublicationFixture fixture,
        string root,
        bool publishedTree = false)
    {
        string[] actual = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Relative(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            fixture.Files
                .Select(file => file.RelativePath)
                .OrderBy(path => path, StringComparer.Ordinal),
            actual);

        foreach (VerifiedExtractedReleaseFile expected in fixture.Files)
        {
            string path = Path.Combine(
                root,
                expected.RelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            Assert.Equal(fixture.Content(expected.RelativePath), File.ReadAllBytes(path));
            Assert.Equal(
                publishedTree
                    ? expected.Executable
                        ? UnixFileMode.UserRead |
                          UnixFileMode.UserExecute |
                          UnixFileMode.GroupRead |
                          UnixFileMode.GroupExecute |
                          UnixFileMode.OtherRead |
                          UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead |
                          UnixFileMode.GroupRead |
                          UnixFileMode.OtherRead
                    : expected.Executable
                        ? UnixFileMode.UserRead | UnixFileMode.UserExecute
                        : UnixFileMode.UserRead,
                File.GetUnixFileMode(path));
        }

        foreach (string directory in Directory.GetDirectories(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            Assert.Equal(
                publishedTree
                    ? UnixFileMode.UserRead |
                      UnixFileMode.UserExecute |
                      UnixFileMode.GroupRead |
                      UnixFileMode.GroupExecute |
                      UnixFileMode.OtherRead |
                      UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory));
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private sealed class PublicationFixture : IAsyncDisposable
    {
        private readonly Dictionary<string, byte[]> m_content;
        private readonly byte[] m_manifest;
        private readonly Dictionary<ReleasePackageRole, byte[]> m_packages;

        private PublicationFixture()
        {
            Root = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    $"aethersdr-extracted-publication-{Guid.NewGuid():N}"));
            DeploymentRoot = Path.Combine(Root, "deployment");
            ReleaseRoot = Path.Combine(DeploymentRoot, "releases");
            ExtractionRoot = Path.Combine(
                DeploymentRoot,
                VerifiedReleaseArchiveExtractionService
                    .ExtractionStagingDirectoryName);
            StagingRoot = Path.Combine(
                DeploymentRoot,
                VerifiedReleaseStagingService.StagingDirectoryName);
            TargetIdentity = "aethersdr-8.2.0";
            TargetPath = Path.Combine(ReleaseRoot, TargetIdentity);
            SourcePath = Path.Combine(
                ExtractionRoot,
                $"{TargetIdentity}.{Guid.NewGuid():N}");
            StagingPath = Path.Combine(
                StagingRoot,
                $"{TargetIdentity}.{Guid.NewGuid():N}");
            CurrentPath = Path.Combine(DeploymentRoot, "current");
            BundlePath = Path.Combine(Root, "bundle");
            m_manifest = "verified-manifest"u8.ToArray();
            m_packages = new Dictionary<ReleasePackageRole, byte[]>
            {
                [ReleasePackageRole.GatewayWeb] = "gateway-archive"u8.ToArray(),
                [ReleasePackageRole.Broker] = "broker-archive"u8.ToArray(),
                [ReleasePackageRole.AetherRemoteAgent] = "agent-archive"u8.ToArray(),
                [ReleasePackageRole.StationEngine] = "station-archive"u8.ToArray()
            };
            m_content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [LocalOfflineReleaseBundleVerificationService.ManifestFileName] =
                    m_manifest,
                ["gateway-web/AetherSDR.Web"] = "gateway-web"u8.ToArray(),
                ["gateway-web/appsettings.json"] = "{}"u8.ToArray(),
                ["gateway-web/watchdog/AetherSDR.TxWatchdog"] =
                    "watchdog"u8.ToArray(),
                ["broker/AetherRemote.Broker"] = "broker"u8.ToArray(),
                ["aetherremote-agent/AetherRemote.Agent"] = "agent"u8.ToArray(),
                ["station-engine/AetherSDR.Web"] = "station"u8.ToArray()
            };

            Plan = CreatePlan();
            Files =
            [
                CreateFileMetadata(
                    ReleasePackageRole.Unknown,
                    LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                    executable: false),
                CreateFileMetadata(
                    ReleasePackageRole.GatewayWeb,
                    "gateway-web/AetherSDR.Web",
                    executable: true),
                CreateFileMetadata(
                    ReleasePackageRole.GatewayWeb,
                    "gateway-web/appsettings.json",
                    executable: false),
                CreateFileMetadata(
                    ReleasePackageRole.GatewayWeb,
                    "gateway-web/watchdog/AetherSDR.TxWatchdog",
                    executable: true),
                CreateFileMetadata(
                    ReleasePackageRole.Broker,
                    "broker/AetherRemote.Broker",
                    executable: true),
                CreateFileMetadata(
                    ReleasePackageRole.AetherRemoteAgent,
                    "aetherremote-agent/AetherRemote.Agent",
                    executable: true),
                CreateFileMetadata(
                    ReleasePackageRole.StationEngine,
                    "station-engine/AetherSDR.Web",
                    executable: true)
            ];
            DirectoryCount = 5;
            PublicationBytes = Files.Sum(file => file.Length);
            VerifiedStagedRelease staged = new(
                Plan,
                StagingPath,
                checked(
                    Plan.ManifestLength +
                    Plan.Packages.Sum(package => package.Length)));
            ExtractedRelease = new VerifiedExtractedRelease(
                staged,
                SourcePath,
                Files,
                DirectoryCount,
                PublicationBytes);
            ExtractionReport =
                VerifiedReleaseArchiveExtractionReport.Success(ExtractedRelease);
            Composition =
                new VerifiedReleaseExtractedPublicationPlanComposer().Compose(
                    ExtractionReport);
            PublicationPlan = Assert.IsType<VerifiedReleaseExtractedPublicationPlan>(
                Composition.Plan);
            Service = new VerifiedReleaseExtractedPublicationService(
                ReadStatusAsync,
                Directory.Move);
        }

        internal string Root { get; }
        internal string DeploymentRoot { get; }
        internal string ReleaseRoot { get; }
        internal string ExtractionRoot { get; }
        internal string StagingRoot { get; }
        internal string TargetIdentity { get; }
        internal string TargetPath { get; }
        internal string SourcePath { get; }
        internal string StagingPath { get; }
        internal string CurrentPath { get; }
        internal string BundlePath { get; }
        internal int DirectoryCount { get; }
        internal long PublicationBytes { get; }
        internal VerifiedReleaseInstallationPlan Plan { get; }
        internal IReadOnlyList<VerifiedExtractedReleaseFile> Files { get; }
        internal VerifiedExtractedRelease ExtractedRelease { get; }
        internal VerifiedReleaseArchiveExtractionReport ExtractionReport { get; }
        internal VerifiedReleaseExtractedPublicationPlanCompositionResult
            Composition
        { get; }
        internal VerifiedReleaseExtractedPublicationPlan PublicationPlan { get; }
        internal VerifiedReleaseExtractedPublicationService Service { get; }

        internal static async Task<PublicationFixture> CreateAsync()
        {
            PublicationFixture fixture = new();
            await fixture.InitializeAsync();
            return fixture;
        }

        internal byte[] Content(string relativePath) => m_content[relativePath];

        internal string SourceFile(string relativePath) =>
            Path.Combine(
                SourcePath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal void MakeSourceRootWritable() =>
            File.SetUnixFileMode(
                SourcePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);

        internal void FreezeSourceRoot() =>
            File.SetUnixFileMode(
                SourcePath,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);

        internal Task<ReleaseStatusReadResult> ReadStatusAsync(
            CancellationToken _) =>
            Task.FromResult(ReadStatus());

        internal ReleaseStatusReadResult ReadStatus(bool? includeTarget = null)
        {
            bool target = includeTarget ?? Directory.Exists(TargetPath);
            string[] identities = target
                ? [Plan.InstalledReleaseIdentity, Plan.TargetReleaseIdentity]
                : [Plan.InstalledReleaseIdentity];
            return new ReleaseStatusReadResult(
                Succeeded: true,
                ReleaseStatusFailureCode.None,
                "status",
                SetupSchemaVersion: 1,
                SetupRevision: Plan.SetupRevision,
                SetupComplete: true,
                SetupLockMode: InstallationSetupLockMode.Complete,
                LastCompletedStep: InstallationSetupStep.Administrator,
                UpdateChannel: Plan.UpdateChannel,
                PinnedReleaseIdentity: Plan.PinnedReleaseIdentity,
                InstallTransmitSupport: Plan.InstallTransmitSupport,
                ReleaseDirectoryPresent: true,
                AvailableReleaseCount: identities.Length,
                AvailableReleaseIdentities: identities,
                CurrentPointerPresent: true,
                ActiveReleaseIdentity: Plan.InstalledReleaseIdentity,
                RollbackCandidateKnown: false);
        }

        private async Task InitializeAsync()
        {
            Directory.CreateDirectory(DeploymentRoot);
            Directory.CreateDirectory(ReleaseRoot);
            Directory.CreateDirectory(ExtractionRoot);
            Directory.CreateDirectory(StagingRoot);
            Directory.CreateDirectory(
                Path.Combine(ReleaseRoot, Plan.InstalledReleaseIdentity));
            Directory.CreateDirectory(SourcePath);

            File.SetUnixFileMode(
                DeploymentRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);
            File.SetUnixFileMode(
                ReleaseRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);
            File.SetUnixFileMode(
                ExtractionRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            File.SetUnixFileMode(
                StagingRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            File.SetUnixFileMode(
                Path.Combine(ReleaseRoot, Plan.InstalledReleaseIdentity),
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(
                SourcePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);

            Directory.CreateSymbolicLink(
                CurrentPath,
                Path.Combine("releases", Plan.InstalledReleaseIdentity));

            foreach (VerifiedExtractedReleaseFile file in Files)
            {
                string path = SourceFile(file.RelativePath);
                string parent = Path.GetDirectoryName(path) ??
                    throw new InvalidOperationException("Missing file parent.");
                Directory.CreateDirectory(parent);
                await File.WriteAllBytesAsync(path, Content(file.RelativePath));
                File.SetUnixFileMode(
                    path,
                    file.Executable
                        ? UnixFileMode.UserRead | UnixFileMode.UserExecute
                        : UnixFileMode.UserRead);
            }

            foreach (string directory in Directory.GetDirectories(
                         SourcePath,
                         "*",
                         SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            FreezeSourceRoot();
        }

        private VerifiedReleaseInstallationPlan CreatePlan()
        {
            VerifiedReleaseInstallationPackagePlan[] packages =
            [
                Package(
                    "gateway",
                    ReleasePackageRole.GatewayWeb,
                    "packages/gateway.tar.gz"),
                Package(
                    "broker",
                    ReleasePackageRole.Broker,
                    "packages/broker.tar.gz"),
                Package(
                    "agent",
                    ReleasePackageRole.AetherRemoteAgent,
                    "packages/agent.tar.gz"),
                Package(
                    "station",
                    ReleasePackageRole.StationEngine,
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
                releaseNotesSummary: "Extracted publication execution test.");
        }

        private VerifiedReleaseInstallationPackagePlan Package(
            string identity,
            ReleasePackageRole role,
            string relativePath)
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
                        TargetPath,
                        relativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar))));
        }

        private VerifiedExtractedReleaseFile CreateFileMetadata(
            ReleasePackageRole role,
            string relativePath,
            bool executable)
        {
            byte[] content = Content(relativePath);
            return new VerifiedExtractedReleaseFile(
                role,
                relativePath,
                content.LongLength,
                SHA256.HashData(content),
                executable);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    MakeWritable(Root);
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Test cleanup is best effort; production cleanup is independently tested.
            }
            return ValueTask.CompletedTask;
        }

        private static void MakeWritable(string root)
        {
            foreach (string directory in Directory.GetDirectories(
                         root,
                         "*",
                         SearchOption.AllDirectories)
                     .OrderBy(path => path.Length))
            {
                try
                {
                    File.SetUnixFileMode(
                        directory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite |
                            UnixFileMode.UserExecute);
                }
                catch
                {
                }
            }
            foreach (string file in Directory.GetFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                try
                {
                    File.SetUnixFileMode(
                        file,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                }
            }
            try
            {
                File.SetUnixFileMode(
                    root,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
            }
            catch
            {
            }
        }
    }
}
