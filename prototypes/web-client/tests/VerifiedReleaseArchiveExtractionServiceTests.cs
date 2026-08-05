using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleaseArchiveExtractionServiceTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsButNoExtractionCaller()
    {
        string[] methods = typeof(VerifiedReleaseArchiveExtractionService)
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
    public async Task DiagnosticsExposeOnlyArchiveExtractionBoundary()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        VerifiedReleaseArchiveExtractionDiagnostics snapshot =
            fixture.ExtractionService.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.StatusRevalidationRegistered);
        Assert.True(snapshot.VerifiedStagingInputRegistered);
        Assert.True(snapshot.SourceArchiveDigestVerificationRegistered);
        Assert.True(snapshot.GzipDecompressionRegistered);
        Assert.True(snapshot.TarArchiveReadRegistered);
        Assert.True(snapshot.ArchiveExtractionRegistered);
        Assert.True(snapshot.PrivateStagingWriteRegistered);
        Assert.True(snapshot.ExpandedContentHashRegistered);
        Assert.True(snapshot.ImmutableFreezeRegistered);
        Assert.True(snapshot.CleanupRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.PersistentDownloadRegistered);
        Assert.False(snapshot.PublicationRegistered);
        Assert.False(snapshot.InstallationExecutionRegistered);
        Assert.False(snapshot.ActivationRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
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
    public async Task SuccessfulExtractionCreatesExactPrivateImmutableRoleTrees()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseArchiveExtractionFailureCode.None,
            report.FailureCode);
        Assert.Equal(4, report.PackageCount);
        Assert.Equal(fixture.ExpectedExtractedFiles.Count, report.ExtractedFileCount);
        Assert.Equal(
            fixture.ExpectedExtractedDirectories.Count,
            report.ExtractedDirectoryCount);
        Assert.Equal(fixture.ExpectedExpandedBytes, report.ExpandedBytes);
        Assert.True(report.SourceArchivesVerified);
        Assert.True(report.ManifestCopied);
        Assert.True(report.ImmutableExtractionTree);
        Assert.False(report.TargetPublished);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.CleanupRequired);

        VerifiedExtractedRelease extracted = Assert.IsType<VerifiedExtractedRelease>(
            report.ExtractedRelease);
        Assert.Equal(fixture.Plan, extracted.Plan);
        Assert.Equal(
            fixture.ExtractionRoot,
            Path.GetDirectoryName(extracted.ExtractionPath));
        Assert.StartsWith(
            fixture.Plan.TargetReleaseIdentity + ".",
            Path.GetFileName(extracted.ExtractionPath),
            StringComparison.Ordinal);

        string[] files = Directory.GetFiles(
                extracted.ExtractionPath,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Relative(extracted.ExtractionPath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            fixture.ExpectedExtractedFiles.Keys
                .OrderBy(path => path, StringComparer.Ordinal),
            files);
        foreach ((string relativePath, byte[] expected) in
                 fixture.ExpectedExtractedFiles)
        {
            byte[] actual = await File.ReadAllBytesAsync(
                Path.Combine(
                    extracted.ExtractionPath,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(expected, actual);
        }

        AssertTreeImmutableAndPrivate(extracted.ExtractionPath);
        foreach (string executable in fixture.ExpectedExecutableFiles)
        {
            UnixFileMode mode = File.GetUnixFileMode(
                Path.Combine(
                    extracted.ExtractionPath,
                    executable.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserExecute,
                mode);
        }
    }

    [Fact]
    public async Task ExtractionDoesNotPublishTargetOrChangeCurrentPointer()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();
        string? currentBefore = new DirectoryInfo(fixture.CurrentPath).LinkTarget;
        byte[] setupBefore = await File.ReadAllBytesAsync(
            fixture.Paths.SetupStatePath);

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.True(report.Succeeded);
        Assert.False(Directory.Exists(fixture.Plan.TargetReleasePath));
        Assert.Equal(
            currentBefore,
            new DirectoryInfo(fixture.CurrentPath).LinkTarget);
        Assert.Equal(
            setupBefore,
            await File.ReadAllBytesAsync(fixture.Paths.SetupStatePath));
        Assert.Equal(
            [fixture.Plan.InstalledReleaseIdentity],
            Directory.GetDirectories(fixture.Paths.ReleaseDirectory)
                .Select(path => Path.GetFileName(path)!)
                .ToArray());
    }

    [Fact]
    public async Task PublicReportIsPathAndDigestRedacted()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);
        string json = JsonSerializer.Serialize(report);

        Assert.True(report.Succeeded);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            staging.StagedRelease!.StagingPath,
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            report.ExtractedRelease!.ExtractionPath,
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain("gateway-web/", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(
                report.ExtractedRelease.Files[0].Sha256),
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidStagingReportFailsBeforeStatusOrFilesystemAccess()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        int statusReads = 0;
        VerifiedReleaseArchiveExtractionService service = new(
            _ =>
            {
                statusReads++;
                throw new InvalidOperationException("must not read status");
            });
        VerifiedReleaseStagingReport invalid =
            VerifiedReleaseStagingReport.Failure(
                VerifiedReleaseStagingFailureCode.InvalidPlan,
                "invalid");

        VerifiedReleaseArchiveExtractionReport report =
            await service.ExtractAsync(invalid);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseArchiveExtractionFailureCode.StagingNotEligible,
            report.FailureCode);
        Assert.Equal(0, statusReads);
        Assert.False(Directory.Exists(fixture.ExtractionRoot));
    }

    [Fact]
    public async Task MalformedGzipFailsAndRemovesPartialExtractionTree()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        fixture.ReplacePackageBeforeStage(
            ReleasePackageRole.GatewayWeb,
            "not-gzip"u8.ToArray());
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.False(report.Succeeded);
        Assert.Contains(
            report.FailureCode,
            new[]
            {
                VerifiedReleaseArchiveExtractionFailureCode.ExtractionWriteFailed,
                VerifiedReleaseArchiveExtractionFailureCode.InvalidArchive
            });
        Assert.False(report.CleanupRequired);
        AssertExtractionRootEmpty(fixture);
    }

    [Fact]
    public async Task TraversalEntryFailsWithoutCreatingOutsideFile()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        fixture.ReplacePackageBeforeStage(
            ReleasePackageRole.Broker,
            ExtractionFixture.CreateArchive(
            [
                ArchiveItem.File("../escape.txt", "escape"u8.ToArray())
            ]));
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();
        string outside = Path.Combine(fixture.Root, "escape.txt");

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeArchiveEntry,
            report.FailureCode);
        Assert.False(File.Exists(outside));
        AssertExtractionRootEmpty(fixture);
    }

    [Fact]
    public async Task SymbolicLinkEntryFailsWithoutTouchingExternalDirectory()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        fixture.ReplacePackageBeforeStage(
            ReleasePackageRole.AetherRemoteAgent,
            ExtractionFixture.CreateArchive(
            [
                ArchiveItem.SymbolicLink("./escape", "../../outside")
            ]));
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        string marker = Path.Combine(outside, "marker.txt");
        await File.WriteAllTextAsync(marker, "preserve");

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeArchiveEntry,
            report.FailureCode);
        Assert.Equal("preserve", await File.ReadAllTextAsync(marker));
        AssertExtractionRootEmpty(fixture);
    }

    [Fact]
    public async Task DuplicateArchivePathFailsClosed()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        fixture.ReplacePackageBeforeStage(
            ReleasePackageRole.StationEngine,
            ExtractionFixture.CreateArchive(
            [
                ArchiveItem.File("./duplicate", "first"u8.ToArray()),
                ArchiveItem.File("./duplicate", "second"u8.ToArray())
            ]));
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeArchiveEntry,
            report.FailureCode);
        AssertExtractionRootEmpty(fixture);
    }

    [Fact]
    public async Task ArchiveWithoutRegularFileFailsClosed()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        fixture.ReplacePackageBeforeStage(
            ReleasePackageRole.Broker,
            ExtractionFixture.CreateArchive(
            [
                ArchiveItem.Directory("./empty")
            ]));
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseArchiveExtractionFailureCode.InvalidArchive,
            report.FailureCode);
        AssertExtractionRootEmpty(fixture);
    }

    [Fact]
    public async Task SourceArchiveDigestDriftFailsBeforeDecompression()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();
        fixture.ReplaceStagedPackage(
            staging,
            ReleasePackageRole.GatewayWeb,
            ExtractionFixture.CreateDefaultArchive("changed"));

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseArchiveExtractionFailureCode.IntegrityMismatch,
            report.FailureCode);
        AssertExtractionRootEmpty(fixture);
    }

    [Fact]
    public async Task WritableSourceStagingFailsBeforeExtractionRootCreation()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();
        File.SetUnixFileMode(
            staging.StagedRelease!.StagingPath,
            ExtractionFixture.PrivateWritableDirectoryMode);

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeSourceStaging,
            report.FailureCode);
        Assert.False(Directory.Exists(fixture.ExtractionRoot));
    }

    [Fact]
    public async Task UnsafeExistingExtractionRootFailsWithoutTransactionTree()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();
        Directory.CreateDirectory(fixture.ExtractionRoot);
        File.SetUnixFileMode(
            fixture.ExtractionRoot,
            ExtractionFixture.PrivateWritableDirectoryMode |
                UnixFileMode.GroupRead);

        VerifiedReleaseArchiveExtractionReport report =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseArchiveExtractionFailureCode.UnsafeExtractionRoot,
            report.FailureCode);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ExtractionRoot));
    }

    [Fact]
    public async Task StatusDriftAfterExtractionRemovesCompletedTree()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();
        ReleaseStatusReadResult first = await fixture.Reader.ReadAsync();
        ReleaseStatusReadResult second = first with
        {
            SetupRevision = first.SetupRevision + 1
        };
        Queue<ReleaseStatusReadResult> statuses = new([first, second]);
        VerifiedReleaseArchiveExtractionService service = new(
            _ => Task.FromResult(statuses.Dequeue()));

        VerifiedReleaseArchiveExtractionReport report =
            await service.ExtractAsync(staging);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseArchiveExtractionFailureCode
                .StatusChangedDuringExtraction,
            report.FailureCode);
        Assert.False(report.CleanupRequired);
        AssertExtractionRootEmpty(fixture);
    }

    [Fact]
    public async Task PreCancelledOperationCreatesNoExtractionState()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.ExtractionService.ExtractAsync(
                staging,
                cancellation.Token));

        Assert.False(Directory.Exists(fixture.ExtractionRoot));
    }

    [Fact]
    public async Task CleanupRefusesNestedSymlinkAndPreservesExternalTree()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        string extraction = Path.Combine(fixture.Root, "cleanup-extraction");
        string outside = Path.Combine(fixture.Root, "cleanup-outside");
        Directory.CreateDirectory(extraction);
        Directory.CreateDirectory(outside);
        File.SetUnixFileMode(
            extraction,
            ExtractionFixture.PrivateWritableDirectoryMode);
        File.SetUnixFileMode(
            outside,
            ExtractionFixture.PrivateWritableDirectoryMode);
        string marker = Path.Combine(outside, "marker.txt");
        await File.WriteAllTextAsync(marker, "preserve");
        Directory.CreateSymbolicLink(
            Path.Combine(extraction, "escape"),
            outside);
        MethodInfo cleanup = typeof(VerifiedReleaseArchiveExtractionService)
            .GetMethod(
                "TryCleanup",
                BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Cleanup method not found.");

        bool cleaned = Assert.IsType<bool>(cleanup.Invoke(null, [extraction]));

        Assert.False(cleaned);
        Assert.True(Directory.Exists(extraction));
        Assert.True(Directory.Exists(outside));
        Assert.Equal("preserve", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task TwoExtractionsCreateSeparateImmutableTreesWithoutPublishing()
    {
        await using ExtractionFixture fixture =
            await ExtractionFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = await fixture.StageAsync();

        VerifiedReleaseArchiveExtractionReport first =
            await fixture.ExtractionService.ExtractAsync(staging);
        VerifiedReleaseArchiveExtractionReport second =
            await fixture.ExtractionService.ExtractAsync(staging);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotEqual(
            first.ExtractedRelease!.ExtractionPath,
            second.ExtractedRelease!.ExtractionPath);
        AssertTreeImmutableAndPrivate(first.ExtractedRelease.ExtractionPath);
        AssertTreeImmutableAndPrivate(second.ExtractedRelease.ExtractionPath);
        Assert.False(Directory.Exists(fixture.Plan.TargetReleasePath));
    }

    private static void AssertExtractionRootEmpty(ExtractionFixture fixture)
    {
        if (!Directory.Exists(fixture.ExtractionRoot))
        {
            return;
        }
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ExtractionRoot));
    }

    private static void AssertTreeImmutableAndPrivate(string root)
    {
        IEnumerable<string> directories =
            new[] { root }.Concat(
                Directory.GetDirectories(
                    root,
                    "*",
                    SearchOption.AllDirectories));
        foreach (string directory in directories)
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory));
        }
        foreach (string file in Directory.GetFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            UnixFileMode mode = File.GetUnixFileMode(file);
            Assert.Contains(
                mode,
                new[]
                {
                    UnixFileMode.UserRead,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute
                });
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private sealed class ExtractionFixture : IAsyncDisposable
    {
        internal const UnixFileMode PrivateWritableDirectoryMode =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute;
        private const UnixFileMode ImmutableDirectoryMode =
            UnixFileMode.UserRead | UnixFileMode.UserExecute;
        private const UnixFileMode ImmutableFileMode = UnixFileMode.UserRead;
        private const UnixFileMode SafeReleaseDirectoryMode =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute;

        private readonly Dictionary<ReleasePackageRole, string> m_packagePaths;
        private readonly Dictionary<string, byte[]> m_bundleFiles;
        private readonly InstallationSetupStore m_store;

        private ExtractionFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-release-extraction-{Guid.NewGuid():N}");
            Paths = new InstallationPaths(
                Path.Combine(Root, "config"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(Root, "deployment", "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            BundlePath = Path.Combine(Root, "bundle");
            DeploymentRoot = Path.GetDirectoryName(Paths.ReleaseDirectory)!;
            CurrentPath = Path.Combine(DeploymentRoot, "current");
            ExtractionRoot = Path.Combine(
                DeploymentRoot,
                VerifiedReleaseArchiveExtractionService
                    .ExtractionStagingDirectoryName);
            m_packagePaths = new()
            {
                [ReleasePackageRole.GatewayWeb] =
                    "packages/aethersdr-gateway-linux-x64.tar.gz",
                [ReleasePackageRole.Broker] =
                    "packages/aethersdr-broker-linux-x64.tar.gz",
                [ReleasePackageRole.AetherRemoteAgent] =
                    "packages/aetherremote-agent-linux-x64.tar.gz",
                [ReleasePackageRole.StationEngine] =
                    "packages/aethersdr-station-engine-linux-x64.tar.gz"
            };
            m_bundleFiles = new(StringComparer.Ordinal)
            {
                [LocalOfflineReleaseBundleVerificationService.ManifestFileName] =
                    "verified-manifest"u8.ToArray()
            };
            foreach ((ReleasePackageRole role, string path) in m_packagePaths)
            {
                m_bundleFiles[path] = CreateDefaultArchive(RoleToken(role));
            }

            ExpectedExtractedFiles = new(StringComparer.Ordinal)
            {
                [LocalOfflineReleaseBundleVerificationService.ManifestFileName] =
                    m_bundleFiles[
                        LocalOfflineReleaseBundleVerificationService.ManifestFileName]
            };
            ExpectedExtractedDirectories = [];
            ExpectedExecutableFiles = [];
            foreach (ReleasePackageRole role in m_packagePaths.Keys.Order())
            {
                string root = RoleDirectory(role);
                string token = RoleToken(role);
                ExpectedExtractedDirectories.Add(root);
                ExpectedExtractedDirectories.Add($"{root}/bin");
                ExpectedExtractedDirectories.Add($"{root}/config");
                ExpectedExtractedFiles[$"{root}/bin/{token}"] =
                    Encoding.UTF8.GetBytes($"executable-{token}");
                ExpectedExtractedFiles[$"{root}/config/settings.json"] =
                    Encoding.UTF8.GetBytes($"{{\"role\":\"{token}\"}}");
                ExpectedExtractedFiles[$"{root}/empty.txt"] = [];
                ExpectedExecutableFiles.Add($"{root}/bin/{token}");
            }
            ExpectedExpandedBytes = ExpectedExtractedFiles.Values.Sum(
                value => value.LongLength);

            m_store = new InstallationSetupStore(Paths.SetupStatePath);
            Reader = new ReleaseInstallationStatusReader(m_store, Paths);
            StagingService = new VerifiedReleaseStagingService(Reader);
            ExtractionService = new VerifiedReleaseArchiveExtractionService(Reader);
            Plan = null!;
        }

        internal string Root { get; }
        internal InstallationPaths Paths { get; }
        internal string BundlePath { get; }
        internal string DeploymentRoot { get; }
        internal string CurrentPath { get; }
        internal string ExtractionRoot { get; }
        internal Dictionary<string, byte[]> ExpectedExtractedFiles { get; }
        internal HashSet<string> ExpectedExtractedDirectories { get; }
        internal HashSet<string> ExpectedExecutableFiles { get; }
        internal long ExpectedExpandedBytes { get; }
        internal long SetupRevision { get; private set; }
        internal ReleaseInstallationStatusReader Reader { get; }
        internal VerifiedReleaseStagingService StagingService { get; }
        internal VerifiedReleaseArchiveExtractionService ExtractionService { get; }
        internal VerifiedReleaseInstallationPlan Plan { get; private set; }

        internal static async Task<ExtractionFixture> CreateAsync()
        {
            ExtractionFixture fixture = new();
            await fixture.InitializeAsync();
            return fixture;
        }

        private async Task InitializeAsync()
        {
            Directory.CreateDirectory(DeploymentRoot);
            Directory.CreateDirectory(Paths.ReleaseDirectory);
            Directory.CreateDirectory(
                Path.Combine(Paths.ReleaseDirectory, "aethersdr-8.1.0"));
            File.SetUnixFileMode(DeploymentRoot, SafeReleaseDirectoryMode);
            File.SetUnixFileMode(Paths.ReleaseDirectory, SafeReleaseDirectoryMode);
            File.SetUnixFileMode(
                Path.Combine(Paths.ReleaseDirectory, "aethersdr-8.1.0"),
                SafeReleaseDirectoryMode);
            Directory.CreateSymbolicLink(
                CurrentPath,
                "releases/aethersdr-8.1.0");

            InstallationSetupState initial = await m_store.LoadOrCreateAsync();
            InstallationSetupState complete = await m_store.UpdateAsync(
                initial.Revision,
                current => current with
                {
                    LastCompletedStep = InstallationSetupStep.Administrator,
                    Lock = new InstallationSetupLock
                    {
                        Mode = InstallationSetupLockMode.Complete,
                        ClaimedAt = current.CreatedAt,
                        CompletedAt = current.CreatedAt
                    },
                    Topology = InstallationTopologyKind.PersonalSingleStation,
                    CanonicalPublicUrl = "https://radio.example.org",
                    Paths = Paths,
                    UpdateChannel = InstallationUpdateChannel.Stable,
                    PinnedRelease = string.Empty,
                    InstallTransmitSupport = false
                });
            SetupRevision = complete.Revision;
            WriteBundle();
            Plan = CreatePlan();
        }

        internal async Task<VerifiedReleaseStagingReport> StageAsync()
        {
            VerifiedReleaseStagingReport report =
                await StagingService.StageAsync(Plan);
            Assert.True(report.Succeeded, report.Message);
            return report;
        }

        internal void ReplacePackageBeforeStage(
            ReleasePackageRole role,
            byte[] content)
        {
            MakeTreeWritable(BundlePath);
            m_bundleFiles[m_packagePaths[role]] = content;
            WriteBundle();
            Plan = CreatePlan();
        }

        internal void ReplaceStagedPackage(
            VerifiedReleaseStagingReport staging,
            ReleasePackageRole role,
            byte[] content)
        {
            string sourceRoot = staging.StagedRelease!.StagingPath;
            string path = Path.Combine(
                sourceRoot,
                m_packagePaths[role].Replace('/', Path.DirectorySeparatorChar));
            MakeTreeWritable(sourceRoot);
            File.WriteAllBytes(path, content);
            FreezeTree(sourceRoot);
        }

        internal static byte[] CreateDefaultArchive(string token) =>
            CreateArchive(
            [
                ArchiveItem.Directory("./bin"),
                ArchiveItem.Directory("./config"),
                ArchiveItem.File(
                    $"./bin/{token}",
                    Encoding.UTF8.GetBytes($"executable-{token}"),
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute),
                ArchiveItem.File(
                    "./config/settings.json",
                    Encoding.UTF8.GetBytes($"{{\"role\":\"{token}\"}}")),
                ArchiveItem.File("./empty.txt", [])
            ]);

        internal static byte[] CreateArchive(IReadOnlyList<ArchiveItem> items)
        {
            using MemoryStream output = new();
            using (GZipStream gzip = new(
                       output,
                       CompressionLevel.SmallestSize,
                       leaveOpen: true))
            using (TarWriter writer = new(
                       gzip,
                       TarEntryFormat.Gnu,
                       leaveOpen: true))
            {
                GnuTarEntry root = new(TarEntryType.Directory, ".")
                {
                    Mode = UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead |
                        UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead |
                        UnixFileMode.OtherExecute
                };
                writer.WriteEntry(root);
                foreach (ArchiveItem item in items)
                {
                    GnuTarEntry entry = new(item.EntryType, item.Name)
                    {
                        Mode = item.Mode
                    };
                    if (item.EntryType is TarEntryType.SymbolicLink or
                        TarEntryType.HardLink)
                    {
                        entry.LinkName = item.LinkName;
                    }
                    if (item.EntryType is TarEntryType.RegularFile or
                        TarEntryType.V7RegularFile)
                    {
                        entry.DataStream = new MemoryStream(
                            item.Content,
                            writable: false);
                    }
                    writer.WriteEntry(entry);
                }
            }
            return output.ToArray();
        }

        private VerifiedReleaseInstallationPlan CreatePlan()
        {
            string targetIdentity = "aethersdr-8.2.0";
            string targetPath = Path.Combine(
                Paths.ReleaseDirectory,
                targetIdentity);
            VerifiedReleaseInstallationPackagePlan[] packages =
                m_packagePaths
                    .OrderBy(pair => pair.Key)
                    .Select(pair => PackagePlan(
                        RoleToken(pair.Key),
                        pair.Key,
                        pair.Value,
                        targetPath))
                    .ToArray();
            byte[] manifest = m_bundleFiles[
                LocalOfflineReleaseBundleVerificationService.ManifestFileName];
            return new VerifiedReleaseInstallationPlan(
                SetupRevision,
                "aethersdr-8.1.0",
                targetIdentity,
                "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                string.Empty,
                installTransmitSupport: false,
                BundlePath,
                manifest.LongLength,
                SHA256.HashData(manifest),
                Paths.ReleaseDirectory,
                DeploymentRoot,
                targetPath,
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
                releaseNotesSummary: "Extraction test release.");
        }

        private VerifiedReleaseInstallationPackagePlan PackagePlan(
            string identity,
            ReleasePackageRole role,
            string relativePath,
            string targetPath)
        {
            byte[] content = m_bundleFiles[relativePath];
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

        private void WriteBundle()
        {
            if (Directory.Exists(BundlePath))
            {
                MakeTreeWritable(BundlePath);
                Directory.Delete(BundlePath, recursive: true);
            }
            Directory.CreateDirectory(BundlePath);
            foreach ((string relativePath, byte[] content) in m_bundleFiles)
            {
                string path = Path.Combine(
                    BundlePath,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
                File.SetUnixFileMode(path, ImmutableFileMode);
            }
            FreezeTree(BundlePath);
        }

        private static string RoleDirectory(ReleasePackageRole role) =>
            role switch
            {
                ReleasePackageRole.GatewayWeb => "gateway-web",
                ReleasePackageRole.Broker => "broker",
                ReleasePackageRole.AetherRemoteAgent => "aetherremote-agent",
                ReleasePackageRole.StationEngine => "station-engine",
                _ => throw new InvalidOperationException()
            };

        private static string RoleToken(ReleasePackageRole role) =>
            role switch
            {
                ReleasePackageRole.GatewayWeb => "gateway",
                ReleasePackageRole.Broker => "broker",
                ReleasePackageRole.AetherRemoteAgent => "agent",
                ReleasePackageRole.StationEngine => "engine",
                _ => throw new InvalidOperationException()
            };

        private static void FreezeTree(string root)
        {
            foreach (string file in Directory.GetFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(file, ImmutableFileMode);
            }
            foreach (string directory in Directory.GetDirectories(
                         root,
                         "*",
                         SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            {
                File.SetUnixFileMode(directory, ImmutableDirectoryMode);
            }
            File.SetUnixFileMode(root, ImmutableDirectoryMode);
        }

        private static void MakeTreeWritable(string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            File.SetUnixFileMode(root, PrivateWritableDirectoryMode);
            foreach (string directory in Directory.GetDirectories(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(directory, PrivateWritableDirectoryMode);
            }
            foreach (string file in Directory.GetFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(
                    file,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                MakeTreeWritable(Root);
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ArchiveItem(
        TarEntryType EntryType,
        string Name,
        byte[] Content,
        UnixFileMode Mode,
        string LinkName)
    {
        internal static ArchiveItem Directory(string name) =>
            new(
                TarEntryType.Directory,
                name,
                [],
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute,
                string.Empty);

        internal static ArchiveItem File(
            string name,
            byte[] content,
            UnixFileMode mode =
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.GroupRead |
                UnixFileMode.OtherRead) =>
            new(
                TarEntryType.RegularFile,
                name,
                content,
                mode,
                string.Empty);

        internal static ArchiveItem SymbolicLink(
            string name,
            string target) =>
            new(
                TarEntryType.SymbolicLink,
                name,
                [],
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute,
                target);
    }
}
