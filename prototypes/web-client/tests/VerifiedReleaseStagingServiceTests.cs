using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleaseStagingServiceTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsButNoStagingCaller()
    {
        string[] methods = typeof(VerifiedReleaseStagingService)
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
    public async Task DiagnosticsExposeOnlyStagingMutationBoundary()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        VerifiedReleaseStagingDiagnostics snapshot = fixture.Service.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.StatusRevalidationRegistered);
        Assert.True(snapshot.VerifiedBundleReadRegistered);
        Assert.True(snapshot.FileWriteRegistered);
        Assert.True(snapshot.StagingExecutionRegistered);
        Assert.True(snapshot.ImmutableFreezeRegistered);
        Assert.True(snapshot.CleanupRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.ArchiveExtractionRegistered);
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
    public async Task SuccessfulStageCopiesExactFilesIntoPrivateImmutableTree()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.True(report.Succeeded);
        Assert.Equal(VerifiedReleaseStagingFailureCode.None, report.FailureCode);
        Assert.Equal(4, report.PackageCount);
        Assert.Equal(fixture.ExpectedStagedBytes, report.StagedBytes);
        Assert.True(report.ManifestStaged);
        Assert.True(report.ImmutableStagingTree);
        Assert.False(report.TargetPublished);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.CleanupRequired);

        VerifiedStagedRelease staged = Assert.IsType<VerifiedStagedRelease>(
            report.StagedRelease);
        Assert.Equal(fixture.Plan, staged.Plan);
        Assert.Equal(fixture.ExpectedStagedBytes, staged.StagedBytes);
        Assert.Equal(
            Path.Combine(
                fixture.DeploymentRoot,
                VerifiedReleaseStagingService.StagingDirectoryName),
            Path.GetDirectoryName(staged.StagingPath));
        Assert.StartsWith(
            fixture.Plan.TargetReleaseIdentity + ".",
            Path.GetFileName(staged.StagingPath),
            StringComparison.Ordinal);

        string[] actualFiles = Directory.GetFiles(
                staged.StagingPath,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Relative(staged.StagingPath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(fixture.ExpectedRelativePaths, actualFiles);

        foreach (string relativePath in fixture.ExpectedRelativePaths)
        {
            byte[] expected = await File.ReadAllBytesAsync(
                Path.Combine(
                    fixture.BundlePath,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            byte[] actual = await File.ReadAllBytesAsync(
                Path.Combine(
                    staged.StagingPath,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(expected, actual);
        }

        AssertTreeImmutable(staged.StagingPath);
    }

    [Fact]
    public async Task SuccessfulStageDoesNotPublishTargetOrChangeCurrentPointer()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        string? currentBefore = new DirectoryInfo(fixture.CurrentPath).LinkTarget;
        byte[] setupBefore = await File.ReadAllBytesAsync(
            fixture.Paths.SetupStatePath);

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.True(report.Succeeded);
        Assert.False(Directory.Exists(fixture.Plan.TargetReleasePath));
        Assert.False(File.Exists(fixture.Plan.TargetReleasePath));
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
    public async Task PublicReportIsPathRedacted()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);
        string json = JsonSerializer.Serialize(report);

        Assert.True(report.Succeeded);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.BundlePath, json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            report.StagedRelease!.StagingPath,
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestDigestMismatchFailsAndRemovesTemporaryTree()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        fixture.ReplaceManifest("changed-manifest"u8.ToArray());

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.IntegrityMismatch,
            report.FailureCode);
        Assert.False(report.CleanupRequired);
        AssertStagingRootEmpty(fixture);
    }

    [Fact]
    public async Task PackageDigestMismatchFailsAndRemovesTemporaryTree()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        fixture.ReplacePackage("packages/gateway.tar", "changed"u8.ToArray());

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Contains(
            report.FailureCode,
            new[]
            {
                VerifiedReleaseStagingFailureCode.IntegrityMismatch,
                VerifiedReleaseStagingFailureCode.SourceChanged
            });
        Assert.False(report.CleanupRequired);
        AssertStagingRootEmpty(fixture);
    }

    [Fact]
    public async Task WritableSourceFileFailsBeforeCopy()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        string path = fixture.BundleFile("packages/broker.tar");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.UnsafeBundle,
            report.FailureCode);
        AssertStagingRootEmpty(fixture);
    }

    [Fact]
    public async Task WritableSourceDirectoryFailsBeforeCopy()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        File.SetUnixFileMode(
            fixture.BundlePath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.UnsafeBundle,
            report.FailureCode);
        AssertStagingRootEmpty(fixture);
    }

    [Fact]
    public async Task ExtraBundleFileFailsClosed()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        fixture.AddExtraFile("unexpected.bin", "unexpected"u8.ToArray());

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.UnsafeBundle,
            report.FailureCode);
        AssertStagingRootEmpty(fixture);
    }

    [Fact]
    public async Task MissingBundleFileFailsClosed()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        fixture.RemoveFile("packages/agent.tar");

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.UnsafeBundle,
            report.FailureCode);
        AssertStagingRootEmpty(fixture);
    }

    [Fact]
    public async Task BundleSymbolicLinkFailsClosed()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        string path = fixture.BundleFile("packages/engine.tar");
        fixture.MakeBundleParentWritable(path);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Delete(path);
        string outside = Path.Combine(fixture.Root, "outside.bin");
        await File.WriteAllBytesAsync(outside, "outside"u8.ToArray());
        File.CreateSymbolicLink(path, outside);
        fixture.FreezeBundleParent(path);

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.UnsafeBundle,
            report.FailureCode);
        AssertStagingRootEmpty(fixture);
    }

    [Fact]
    public async Task EmptyBundleDirectoryFailsClosed()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        string empty = Path.Combine(fixture.BundlePath, "empty");
        fixture.MakeBundleRootWritable();
        Directory.CreateDirectory(empty);
        File.SetUnixFileMode(empty, StagingFixture.ImmutableDirectoryMode);
        fixture.FreezeBundleRoot();

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.UnsafeBundle,
            report.FailureCode);
        AssertStagingRootEmpty(fixture);
    }

    [Fact]
    public async Task ExistingTargetFailsBeforeStagingWrite()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        ReleaseStatusReadResult stableStatus = await fixture.Reader.ReadAsync();
        VerifiedReleaseStagingService service = new(
            _ => Task.FromResult(stableStatus));
        Directory.CreateDirectory(fixture.Plan.TargetReleasePath);
        File.SetUnixFileMode(
            fixture.Plan.TargetReleasePath,
            StagingFixture.SafeReleaseDirectoryMode);

        VerifiedReleaseStagingReport report =
            await service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.TargetAlreadyPresent,
            report.FailureCode);
        Assert.False(Directory.Exists(fixture.StagingRoot));
    }

    [Fact]
    public async Task UnsafeExistingStagingRootFailsClosed()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        Directory.CreateDirectory(fixture.StagingRoot);
        File.SetUnixFileMode(
            fixture.StagingRoot,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute);

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.UnsafeStagingRoot,
            report.FailureCode);
        Assert.Empty(Directory.GetDirectories(fixture.StagingRoot));
    }

    [Fact]
    public async Task SymbolicLinkStagingRootFailsClosed()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        string outside = Path.Combine(fixture.Root, "outside-stage");
        Directory.CreateDirectory(outside);
        File.SetUnixFileMode(outside, StagingFixture.PrivateWritableDirectoryMode);
        Directory.CreateSymbolicLink(fixture.StagingRoot, outside);

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.UnsafeStagingRoot,
            report.FailureCode);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task UnsafeReleaseRootFailsBeforeStagingWrite()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        File.SetUnixFileMode(
            fixture.Paths.ReleaseDirectory,
            StagingFixture.SafeReleaseDirectoryMode |
                UnixFileMode.GroupWrite);

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.StatusUnavailable,
            report.FailureCode);
        Assert.False(Directory.Exists(fixture.StagingRoot));
    }

    [Fact]
    public async Task SetupRevisionMismatchFailsBeforeStagingWrite()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        VerifiedReleaseInstallationPlan plan = fixture.CreatePlan(
            setupRevision: fixture.SetupRevision + 1);

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.StatusMismatch,
            report.FailureCode);
        Assert.False(Directory.Exists(fixture.StagingRoot));
    }

    [Fact]
    public async Task ActiveReleaseMismatchFailsBeforeStagingWrite()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        VerifiedReleaseInstallationPlan plan = fixture.CreatePlan(
            installedReleaseIdentity: "aethersdr-8.0.0");

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.StatusMismatch,
            report.FailureCode);
        Assert.False(Directory.Exists(fixture.StagingRoot));
    }

    [Fact]
    public async Task TargetInventoryConflictFailsBeforeStagingWrite()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        Directory.CreateDirectory(fixture.Plan.TargetReleasePath);
        File.SetUnixFileMode(
            fixture.Plan.TargetReleasePath,
            StagingFixture.SafeReleaseDirectoryMode);

        VerifiedReleaseStagingReport report =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.StatusMismatch,
            report.FailureCode);
        Assert.False(Directory.Exists(fixture.StagingRoot));
    }

    [Fact]
    public async Task StatusDriftAfterCopyRemovesCompletedTemporaryTree()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        ReleaseStatusReadResult first = await fixture.Reader.ReadAsync();
        ReleaseStatusReadResult second = first with
        {
            SetupRevision = first.SetupRevision + 1
        };
        Queue<ReleaseStatusReadResult> statuses = new([first, second]);
        VerifiedReleaseStagingService service = new(
            _ => Task.FromResult(statuses.Dequeue()));

        VerifiedReleaseStagingReport report =
            await service.StageAsync(fixture.Plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.StatusChangedDuringStaging,
            report.FailureCode);
        Assert.False(report.CleanupRequired);
        AssertStagingRootEmpty(fixture);
        Assert.False(Directory.Exists(fixture.Plan.TargetReleasePath));
    }

    [Fact]
    public async Task PreCancelledOperationCreatesNoStagingState()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Service.StageAsync(fixture.Plan, cancellation.Token));

        Assert.False(Directory.Exists(fixture.StagingRoot));
        Assert.False(Directory.Exists(fixture.Plan.TargetReleasePath));
    }

    [Fact]
    public async Task InvalidPinnedPlanFailsBeforeStatusOrFilesystemAccess()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        int statusReads = 0;
        VerifiedReleaseStagingService service = new(
            _ =>
            {
                statusReads++;
                return Task.FromResult(
                    ReleaseStatusReadResult.Failure(
                        ReleaseStatusFailureCode.StatusReadFailed,
                        "must not be read"));
            });
        VerifiedReleaseInstallationPlan plan = fixture.CreatePlan(
            updateChannel: InstallationUpdateChannel.Pinned,
            pinnedReleaseIdentity: "aethersdr-8.3.0");

        VerifiedReleaseStagingReport report =
            await service.StageAsync(plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.InvalidPlan,
            report.FailureCode);
        Assert.Equal(0, statusReads);
        Assert.False(Directory.Exists(fixture.StagingRoot));
    }

    [Fact]
    public async Task TxSupportMismatchInPlanFailsBeforeStatusAccess()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        int statusReads = 0;
        VerifiedReleaseStagingService service = new(
            _ =>
            {
                statusReads++;
                return Task.FromResult(
                    ReleaseStatusReadResult.Failure(
                        ReleaseStatusFailureCode.StatusReadFailed,
                        "must not be read"));
            });
        VerifiedReleaseInstallationPlan plan = fixture.CreatePlan(
            installTransmitSupport: true,
            txSupportCapable: false);

        VerifiedReleaseStagingReport report =
            await service.StageAsync(plan);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseStagingFailureCode.InvalidPlan,
            report.FailureCode);
        Assert.Equal(0, statusReads);
    }

    [Fact]
    public async Task FreezeRefusesNestedSymlinkWithoutChangingExternalDirectory()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        string staging = Path.Combine(fixture.Root, "freeze-symlink-stage");
        string outside = Path.Combine(fixture.Root, "freeze-outside");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(outside);
        File.SetUnixFileMode(
            staging,
            StagingFixture.PrivateWritableDirectoryMode);
        File.SetUnixFileMode(
            outside,
            StagingFixture.PrivateWritableDirectoryMode);
        Directory.CreateSymbolicLink(
            Path.Combine(staging, "escape"),
            outside);
        MethodInfo freeze = typeof(VerifiedReleaseStagingService)
            .GetMethod(
                "FreezeStagingTree",
                BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Freeze method was not found.");

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
            () => freeze.Invoke(null, [staging]));

        Assert.NotNull(exception.InnerException);
        Assert.Equal(
            StagingFixture.PrivateWritableDirectoryMode,
            File.GetUnixFileMode(outside));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task CleanupRefusesNestedSymlinkWithoutDeletingExternalDirectory()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();
        string staging = Path.Combine(fixture.Root, "cleanup-symlink-stage");
        string outside = Path.Combine(fixture.Root, "cleanup-outside");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(outside);
        File.SetUnixFileMode(
            staging,
            StagingFixture.PrivateWritableDirectoryMode);
        File.SetUnixFileMode(
            outside,
            StagingFixture.PrivateWritableDirectoryMode);
        string marker = Path.Combine(outside, "marker.txt");
        await File.WriteAllTextAsync(marker, "preserve");
        Directory.CreateSymbolicLink(
            Path.Combine(staging, "escape"),
            outside);
        MethodInfo cleanup = typeof(VerifiedReleaseStagingService)
            .GetMethod(
                "TryCleanup",
                BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Cleanup method was not found.");

        bool cleaned = Assert.IsType<bool>(cleanup.Invoke(null, [staging]));

        Assert.False(cleaned);
        Assert.True(Directory.Exists(staging));
        Assert.True(Directory.Exists(outside));
        Assert.Equal("preserve", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task TwoStagesCreateSeparatePrivateImmutableTreesWithoutPublishing()
    {
        await using StagingFixture fixture = await StagingFixture.CreateAsync();

        VerifiedReleaseStagingReport first =
            await fixture.Service.StageAsync(fixture.Plan);
        VerifiedReleaseStagingReport second =
            await fixture.Service.StageAsync(fixture.Plan);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotEqual(
            first.StagedRelease!.StagingPath,
            second.StagedRelease!.StagingPath);
        AssertTreeImmutable(first.StagedRelease.StagingPath);
        AssertTreeImmutable(second.StagedRelease.StagingPath);
        Assert.False(Directory.Exists(fixture.Plan.TargetReleasePath));
    }

    private static void AssertStagingRootEmpty(StagingFixture fixture)
    {
        if (!Directory.Exists(fixture.StagingRoot))
        {
            return;
        }
        Assert.Empty(Directory.GetFileSystemEntries(fixture.StagingRoot));
    }

    private static void AssertTreeImmutable(string root)
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
                0,
                (int)(File.GetUnixFileMode(directory) &
                    (UnixFileMode.UserWrite |
                     UnixFileMode.GroupWrite |
                     UnixFileMode.OtherWrite)));
        }
        foreach (string file in Directory.GetFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            Assert.Equal(
                0,
                (int)(File.GetUnixFileMode(file) &
                    (UnixFileMode.UserWrite |
                     UnixFileMode.GroupWrite |
                     UnixFileMode.OtherWrite)));
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private sealed class StagingFixture : IAsyncDisposable
    {
        internal const UnixFileMode PrivateWritableDirectoryMode =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute;
        internal const UnixFileMode ImmutableDirectoryMode =
            UnixFileMode.UserRead | UnixFileMode.UserExecute;
        internal const UnixFileMode ImmutableFileMode = UnixFileMode.UserRead;
        internal const UnixFileMode SafeReleaseDirectoryMode =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute;

        private readonly Dictionary<string, byte[]> m_bundleFiles;
        private readonly InstallationSetupStore m_store;

        private StagingFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-release-staging-{Guid.NewGuid():N}");
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
            StagingRoot = Path.Combine(
                DeploymentRoot,
                VerifiedReleaseStagingService.StagingDirectoryName);
            m_bundleFiles = new(StringComparer.Ordinal)
            {
                [LocalOfflineReleaseBundleVerificationService.ManifestFileName] =
                    "verified-manifest"u8.ToArray(),
                ["packages/gateway.tar"] = "gateway-package"u8.ToArray(),
                ["packages/broker.tar"] = "broker-package"u8.ToArray(),
                ["packages/agent.tar"] = "agent-package"u8.ToArray(),
                ["packages/engine.tar"] = "engine-package"u8.ToArray()
            };
            ExpectedRelativePaths = m_bundleFiles.Keys
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            ExpectedStagedBytes = m_bundleFiles.Values.Sum(value => value.LongLength);
            m_store = new InstallationSetupStore(Paths.SetupStatePath);
            Reader = new ReleaseInstallationStatusReader(m_store, Paths);
            Service = new VerifiedReleaseStagingService(Reader);
            Plan = null!;
        }

        internal string Root { get; }
        internal InstallationPaths Paths { get; }
        internal string DeploymentRoot { get; }
        internal string BundlePath { get; }
        internal string CurrentPath { get; }
        internal string StagingRoot { get; }
        internal string[] ExpectedRelativePaths { get; }
        internal long ExpectedStagedBytes { get; }
        internal long SetupRevision { get; private set; }
        internal ReleaseInstallationStatusReader Reader { get; }
        internal VerifiedReleaseStagingService Service { get; }
        internal VerifiedReleaseInstallationPlan Plan { get; private set; }

        internal static async Task<StagingFixture> CreateAsync()
        {
            StagingFixture fixture = new();
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

        internal VerifiedReleaseInstallationPlan CreatePlan(
            long? setupRevision = null,
            string installedReleaseIdentity = "aethersdr-8.1.0",
            InstallationUpdateChannel updateChannel =
                InstallationUpdateChannel.Stable,
            string pinnedReleaseIdentity = "",
            bool installTransmitSupport = false,
            bool txSupportCapable = false)
        {
            string targetIdentity = "aethersdr-8.2.0";
            string targetPath = Path.Combine(
                Paths.ReleaseDirectory,
                targetIdentity);
            VerifiedReleaseInstallationPackagePlan[] packages =
            [
                PackagePlan(
                    "gateway",
                    ReleasePackageRole.GatewayWeb,
                    "packages/gateway.tar",
                    targetPath),
                PackagePlan(
                    "broker",
                    ReleasePackageRole.Broker,
                    "packages/broker.tar",
                    targetPath),
                PackagePlan(
                    "agent",
                    ReleasePackageRole.AetherRemoteAgent,
                    "packages/agent.tar",
                    targetPath),
                PackagePlan(
                    "engine",
                    ReleasePackageRole.StationEngine,
                    "packages/engine.tar",
                    targetPath)
            ];

            byte[] manifest = m_bundleFiles[
                LocalOfflineReleaseBundleVerificationService.ManifestFileName];
            return new VerifiedReleaseInstallationPlan(
                setupRevision ?? SetupRevision,
                installedReleaseIdentity,
                targetIdentity,
                "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                updateChannel,
                pinnedReleaseIdentity,
                installTransmitSupport,
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
                txSupportCapable,
                releaseNotesTitle: "Release",
                releaseNotesSummary: "Staging test release.");
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
            Directory.CreateDirectory(BundlePath);
            foreach ((string relativePath, byte[] content) in m_bundleFiles)
            {
                string path = BundleFile(relativePath);
                string parent = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(parent);
                File.WriteAllBytes(path, content);
                File.SetUnixFileMode(path, ImmutableFileMode);
            }
            foreach (string directory in Directory.GetDirectories(
                         BundlePath,
                         "*",
                         SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            {
                File.SetUnixFileMode(directory, ImmutableDirectoryMode);
            }
            File.SetUnixFileMode(BundlePath, ImmutableDirectoryMode);
        }

        internal string BundleFile(string relativePath) =>
            Path.Combine(
                BundlePath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal void ReplaceManifest(byte[] content)
        {
            ReplacePackage(
                LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                content);
        }

        internal void ReplacePackage(string relativePath, byte[] content)
        {
            string path = BundleFile(relativePath);
            MakeBundleParentWritable(path);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllBytes(path, content);
            File.SetUnixFileMode(path, ImmutableFileMode);
            FreezeBundleParent(path);
        }

        internal void AddExtraFile(string relativePath, byte[] content)
        {
            MakeBundleRootWritable();
            string path = BundleFile(relativePath);
            File.WriteAllBytes(path, content);
            File.SetUnixFileMode(path, ImmutableFileMode);
            FreezeBundleRoot();
        }

        internal void RemoveFile(string relativePath)
        {
            string path = BundleFile(relativePath);
            MakeBundleParentWritable(path);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(path);
            FreezeBundleParent(path);
        }

        internal void MakeBundleRootWritable() =>
            File.SetUnixFileMode(BundlePath, PrivateWritableDirectoryMode);

        internal void FreezeBundleRoot() =>
            File.SetUnixFileMode(BundlePath, ImmutableDirectoryMode);

        internal void MakeBundleParentWritable(string path)
        {
            MakeBundleRootWritable();
            string parent = Path.GetDirectoryName(path)!;
            if (!string.Equals(parent, BundlePath, StringComparison.Ordinal))
            {
                File.SetUnixFileMode(parent, PrivateWritableDirectoryMode);
            }
        }

        internal void FreezeBundleParent(string path)
        {
            string parent = Path.GetDirectoryName(path)!;
            if (!string.Equals(parent, BundlePath, StringComparison.Ordinal))
            {
                File.SetUnixFileMode(parent, ImmutableDirectoryMode);
            }
            FreezeBundleRoot();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    MakeTreeWritable(Root);
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
            return ValueTask.CompletedTask;
        }

        private static void MakeTreeWritable(string root)
        {
            DirectoryInfo rootDirectory = new(root);
            List<DirectoryInfo> directories = [];
            Stack<DirectoryInfo> pending = new();
            pending.Push(rootDirectory);
            while (pending.Count > 0)
            {
                DirectoryInfo directory = pending.Pop();
                directory.Refresh();
                if (!directory.Exists || directory.LinkTarget is not null)
                {
                    continue;
                }
                directories.Add(directory);
                foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
                {
                    entry.Refresh();
                    if (entry.LinkTarget is not null ||
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child);
                    }
                    else if (entry is FileInfo file)
                    {
                        File.SetUnixFileMode(
                            file.FullName,
                            UnixFileMode.UserRead |
                            UnixFileMode.UserWrite);
                    }
                }
            }
            foreach (DirectoryInfo directory in directories
                         .OrderBy(info => info.FullName.Length))
            {
                File.SetUnixFileMode(
                    directory.FullName,
                    PrivateWritableDirectoryMode);
            }
        }
    }
}
