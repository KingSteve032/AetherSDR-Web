using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleasePublicationServiceTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsButNoPublicationCaller()
    {
        string[] methods = typeof(VerifiedReleasePublicationService)
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
    public async Task DiagnosticsExposeOnlyAtomicPublicationBoundary()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleasePublicationDiagnostics snapshot =
            fixture.PublicationService.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.StatusRevalidationRegistered);
        Assert.True(snapshot.FrozenStagingValidationRegistered);
        Assert.True(snapshot.RootPermissionTransitionRegistered);
        Assert.True(snapshot.AtomicDirectoryPublishRegistered);
        Assert.True(snapshot.PublishedTreeValidationRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.ArchiveExtractionRegistered);
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
    public async Task SuccessfulPublicationAtomicallyMovesStagingIntoReleaseInventory()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        string stagingPath = fixture.StagingPath;

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        Assert.True(
            report.Succeeded,
            $"{report.FailureCode}: {report.Message}");
        Assert.Equal(VerifiedReleasePublicationFailureCode.None, report.FailureCode);
        Assert.Equal(fixture.SetupRevision, report.SetupRevision);
        Assert.Equal(fixture.Plan.TargetReleaseIdentity, report.TargetReleaseIdentity);
        Assert.Equal(4, report.PackageCount);
        Assert.Equal(fixture.ExpectedBytes, report.PublishedBytes);
        Assert.True(report.SourceStagingTreeConsumed);
        Assert.True(report.TargetPublished);
        Assert.True(report.TargetImmutable);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
        Assert.False(report.ReconciliationRequired);
        Assert.False(Directory.Exists(stagingPath));
        Assert.True(Directory.Exists(fixture.Plan.TargetReleasePath));

        VerifiedPublishedRelease published =
            Assert.IsType<VerifiedPublishedRelease>(report.PublishedRelease);
        Assert.Same(fixture.Plan, published.Plan);
        Assert.Equal(fixture.Plan.TargetReleasePath, published.PublishedPath);
        Assert.Equal(fixture.ExpectedBytes, published.PublishedBytes);
    }

    [Fact]
    public async Task PublishedTreeRetainsExactVerifiedBytesAndImmutableModes()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        Assert.True(report.Succeeded);
        string[] files = Directory.GetFiles(
                fixture.Plan.TargetReleasePath,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Relative(fixture.Plan.TargetReleasePath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(fixture.ExpectedRelativePaths, files);
        foreach (string relativePath in files)
        {
            Assert.Equal(
                await File.ReadAllBytesAsync(fixture.BundleFile(relativePath)),
                await File.ReadAllBytesAsync(
                    Path.Combine(
                        fixture.Plan.TargetReleasePath,
                        relativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar))));
        }
        AssertTreeImmutable(fixture.Plan.TargetReleasePath);
    }

    [Fact]
    public async Task SuccessfulPublicationLeavesCurrentAndSetupUnchanged()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        string? currentBefore = new DirectoryInfo(fixture.CurrentPath).LinkTarget;
        byte[] setupBefore = await File.ReadAllBytesAsync(
            fixture.Paths.SetupStatePath);

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        Assert.True(report.Succeeded);
        Assert.Equal(
            currentBefore,
            new DirectoryInfo(fixture.CurrentPath).LinkTarget);
        Assert.Equal(
            setupBefore,
            await File.ReadAllBytesAsync(fixture.Paths.SetupStatePath));
        Assert.Equal(
            ["aethersdr-8.1.0", "aethersdr-8.2.0"],
            Directory.GetDirectories(fixture.Paths.ReleaseDirectory)
                .Select(path => Path.GetFileName(path)!)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task PublicReportIsPathDigestAndPackageNameRedacted()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);
        string json = JsonSerializer.Serialize(report);

        Assert.True(report.Succeeded);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.StagingPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway.tar", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(fixture.Plan.ManifestSha256),
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedStagingReportCannotPublish()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = fixture.StagingReport with
        {
            Succeeded = false,
            FailureCode = VerifiedReleaseStagingFailureCode.StagingWriteFailed
        };

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(staging);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.StagingNotEligible);
    }

    [Fact]
    public async Task SuccessfulSummaryWithoutInternalStagedReleaseCannotPublish()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = fixture.StagingReport with
        {
            StagedRelease = null
        };

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(staging);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.StagingNotEligible);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("identity")]
    [InlineData("count")]
    [InlineData("cleanup")]
    [InlineData("published")]
    public async Task StagingSummaryMustMatchInternalToken(string mismatch)
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleaseStagingReport staging = mismatch switch
        {
            "bytes" => fixture.StagingReport with
            {
                StagedBytes = fixture.StagingReport.StagedBytes + 1
            },
            "identity" => fixture.StagingReport with
            {
                TargetReleaseIdentity = "aethersdr-8.3.0"
            },
            "count" => fixture.StagingReport with { PackageCount = 3 },
            "cleanup" => fixture.StagingReport with { CleanupRequired = true },
            "published" => fixture.StagingReport with { TargetPublished = true },
            _ => throw new InvalidOperationException("Unknown mismatch.")
        };

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(staging);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.StagingNotEligible);
    }

    [Fact]
    public async Task StagingPathOutsidePrivateRootFailsClosed()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedStagedRelease staged = new(
            fixture.Plan,
            fixture.BundlePath,
            fixture.ExpectedBytes);
        VerifiedReleaseStagingReport staging = fixture.StagingReport with
        {
            StagedRelease = staged
        };

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(staging);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.StagingNotEligible);
    }

    [Fact]
    public async Task UnavailableStatusFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleasePublicationService service = new(
            _ => Task.FromResult(
                ReleaseStatusReadResult.Failure(
                    ReleaseStatusFailureCode.StatusReadFailed,
                    "unavailable")),
            Directory.Move);

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.StatusUnavailable);
    }

    [Fact]
    public async Task SetupRevisionDriftFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        await fixture.BumpSetupRevisionAsync();

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.StatusMismatch);
    }

    [Fact]
    public async Task UpdateChannelDriftFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        ReleaseStatusReadResult status = await fixture.Reader.ReadAsync();
        status = status with { UpdateChannel = InstallationUpdateChannel.Beta };
        VerifiedReleasePublicationService service = new(
            _ => Task.FromResult(status),
            Directory.Move);

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.StatusMismatch);
    }

    [Fact]
    public async Task TxSupportPolicyDriftFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        ReleaseStatusReadResult status = await fixture.Reader.ReadAsync();
        status = status with { InstallTransmitSupport = true };
        VerifiedReleasePublicationService service = new(
            _ => Task.FromResult(status),
            Directory.Move);

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.StatusMismatch);
    }

    [Fact]
    public async Task ActivePointerDriftFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        ReleaseStatusReadResult status = await fixture.Reader.ReadAsync();
        status = status with { ActiveReleaseIdentity = "aethersdr-8.0.0" };
        VerifiedReleasePublicationService service = new(
            _ => Task.FromResult(status),
            Directory.Move);

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.StatusMismatch);
    }

    [Fact]
    public async Task ExistingTargetFailsWithoutOverwrite()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        ReleaseStatusReadResult stable = await fixture.StatusBeforePublishAsync();
        Directory.CreateDirectory(fixture.Plan.TargetReleasePath);
        File.WriteAllText(
            Path.Combine(fixture.Plan.TargetReleasePath, "sentinel"),
            "preserve");
        VerifiedReleasePublicationService service = new(
            _ => Task.FromResult(stable),
            Directory.Move);

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleasePublicationFailureCode.TargetAlreadyPresent,
            report.FailureCode);
        Assert.Equal(
            "preserve",
            File.ReadAllText(
                Path.Combine(fixture.Plan.TargetReleasePath, "sentinel")));
        Assert.True(Directory.Exists(fixture.StagingPath));
    }

    [Fact]
    public async Task MissingStagingTreeFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        fixture.DeleteStagingTree();

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleasePublicationFailureCode.UnsafeStagingTree,
            report.FailureCode);
        Assert.False(Directory.Exists(fixture.Plan.TargetReleasePath));
    }

    [Fact]
    public async Task WritableStagedFileFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        string file = fixture.StagedFile("packages/gateway.tar");
        File.SetUnixFileMode(
            file,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.UnsafeStagingTree);
    }

    [Fact]
    public async Task StagedDigestDriftFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        fixture.ReplaceStagedFile("packages/broker.tar", "changed"u8.ToArray());

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.UnsafeStagingTree);
    }

    [Fact]
    public async Task ExtraStagedFileFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        fixture.AddStagedFile("extra.bin", "extra"u8.ToArray());

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.UnsafeStagingTree);
    }

    [Fact]
    public async Task StagedSymlinkFailsBeforeRenameWithoutTouchingTarget()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        string external = Path.Combine(fixture.Root, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "sentinel"), "preserve");
        fixture.AddStagedDirectorySymlink("unsafe-link", external);

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.UnsafeStagingTree);
        Assert.Equal(
            "preserve",
            File.ReadAllText(Path.Combine(external, "sentinel")));
    }

    [Fact]
    public async Task UnsafePrivateStagingRootFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        File.SetUnixFileMode(
            fixture.StagingRoot,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute);

        VerifiedReleasePublicationReport report =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.UnsafeDeploymentLayout);
    }

    [Fact]
    public async Task SharedWritableReleaseRootFailsBeforeRename()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        ReleaseStatusReadResult stable = await fixture.StatusBeforePublishAsync();
        File.SetUnixFileMode(
            fixture.Paths.ReleaseDirectory,
            PublicationFixture.SafeReleaseDirectoryMode |
                UnixFileMode.GroupWrite);
        VerifiedReleasePublicationService service = new(
            _ => Task.FromResult(stable),
            Directory.Move);

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.UnsafeDeploymentLayout);
    }

    [Fact]
    public async Task CancellationBeforeAtomicBoundaryLeavesStagingUntouched()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.PublicationService.PublishAsync(
                fixture.StagingReport,
                cancellation.Token));

        Assert.True(Directory.Exists(fixture.StagingPath));
        Assert.False(Directory.Exists(fixture.Plan.TargetReleasePath));
    }

    [Fact]
    public async Task RenameFailureWithUnchangedPathsReturnsAtomicFailure()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleasePublicationService service = new(
            fixture.Reader.ReadAsync,
            (_, _) => throw new IOException("failed"));

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertFailureWithoutMutation(
            fixture,
            report,
            VerifiedReleasePublicationFailureCode.AtomicPublishFailed);
    }

    [Fact]
    public async Task RenameDelegateReturningWithoutMoveRefreezesSource()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleasePublicationService service = new(
            fixture.Reader.ReadAsync,
            (_, _) => { });

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertReconciliation(report);
        Assert.False(report.SourceStagingTreeConsumed);
        Assert.False(report.TargetPublished);
        Assert.True(Directory.Exists(fixture.StagingPath));
        Assert.Equal(
            0,
            (int)(File.GetUnixFileMode(fixture.StagingPath) &
                (UnixFileMode.UserWrite |
                 UnixFileMode.GroupWrite |
                 UnixFileMode.OtherWrite)));
    }

    [Fact]
    public async Task RenameThenThrowReturnsReconciliationRequired()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleasePublicationService service = new(
            fixture.Reader.ReadAsync,
            (source, target) =>
            {
                Directory.Move(source, target);
                throw new IOException("ambiguous");
            });

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertReconciliation(report);
        Assert.True(report.SourceStagingTreeConsumed);
        Assert.True(report.TargetPublished);
        Assert.False(report.TargetImmutable);
        Assert.False(Directory.Exists(fixture.StagingPath));
        Assert.True(Directory.Exists(fixture.Plan.TargetReleasePath));
        Assert.Equal("releases/aethersdr-8.1.0", fixture.CurrentTarget());
    }

    [Fact]
    public async Task TargetAppearsWhileSourceRemainsRequiresReconciliation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleasePublicationService service = new(
            fixture.Reader.ReadAsync,
            (_, target) =>
            {
                Directory.CreateDirectory(target);
                throw new IOException("race");
            });

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertReconciliation(report);
        Assert.False(report.SourceStagingTreeConsumed);
        Assert.True(report.TargetPublished);
        Assert.True(Directory.Exists(fixture.StagingPath));
        Assert.True(Directory.Exists(fixture.Plan.TargetReleasePath));
    }

    [Fact]
    public async Task PostRenameDigestTamperRequiresReconciliation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        VerifiedReleasePublicationService service = new(
            fixture.Reader.ReadAsync,
            (source, target) =>
            {
                Directory.Move(source, target);
                string file = Path.Combine(target, "packages", "engine.tar");
                File.SetUnixFileMode(
                    file,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.WriteAllText(file, "tampered");
                File.SetUnixFileMode(file, PublicationFixture.ImmutableFileMode);
            });

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertReconciliation(report);
        Assert.True(report.TargetPublished);
        Assert.False(report.TargetImmutable);
        Assert.True(Directory.Exists(fixture.Plan.TargetReleasePath));
        Assert.Equal("releases/aethersdr-8.1.0", fixture.CurrentTarget());
    }

    [Fact]
    public async Task StatusUnavailableAfterRenameRequiresReconciliation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        ReleaseStatusReadResult before = await fixture.StatusBeforePublishAsync();
        Queue<ReleaseStatusReadResult> statuses = new(
        [
            before,
            ReleaseStatusReadResult.Failure(
                ReleaseStatusFailureCode.StatusReadFailed,
                "unavailable")
        ]);
        VerifiedReleasePublicationService service = new(
            _ => Task.FromResult(statuses.Dequeue()),
            Directory.Move);

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertReconciliation(report);
        Assert.True(report.TargetPublished);
        Assert.True(report.TargetImmutable);
        Assert.Equal("releases/aethersdr-8.1.0", fixture.CurrentTarget());
    }

    [Fact]
    public async Task MissingInventoryAdditionAfterRenameRequiresReconciliation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        ReleaseStatusReadResult before = await fixture.StatusBeforePublishAsync();
        Queue<ReleaseStatusReadResult> statuses = new([before, before]);
        VerifiedReleasePublicationService service = new(
            _ => Task.FromResult(statuses.Dequeue()),
            Directory.Move);

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertReconciliation(report);
        Assert.True(report.TargetPublished);
        Assert.True(report.TargetImmutable);
    }

    [Fact]
    public async Task CurrentPointerDriftAfterRenameRequiresReconciliation()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        ReleaseStatusReadResult before = await fixture.StatusBeforePublishAsync();
        ReleaseStatusReadResult after = fixture.PublishedStatus(before) with
        {
            ActiveReleaseIdentity = fixture.Plan.TargetReleaseIdentity
        };
        Queue<ReleaseStatusReadResult> statuses = new([before, after]);
        VerifiedReleasePublicationService service = new(
            _ => Task.FromResult(statuses.Dequeue()),
            Directory.Move);

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        AssertReconciliation(report);
        Assert.True(report.TargetPublished);
        Assert.False(report.CurrentPointerChanged);
    }

    [Fact]
    public async Task ExactInjectedPublishedStatusSucceedsDeterministically()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();
        ReleaseStatusReadResult before = await fixture.StatusBeforePublishAsync();
        ReleaseStatusReadResult after = fixture.PublishedStatus(before);
        Queue<ReleaseStatusReadResult> statuses = new([before, after]);
        VerifiedReleasePublicationService service = new(
            _ => Task.FromResult(statuses.Dequeue()),
            Directory.Move);

        VerifiedReleasePublicationReport report =
            await service.PublishAsync(fixture.StagingReport);

        Assert.True(report.Succeeded);
        Assert.False(report.ReconciliationRequired);
        Assert.True(report.TargetPublished);
        Assert.True(report.TargetImmutable);
    }

    [Fact]
    public async Task ReusingConsumedStagingTokenCannotPublishAgain()
    {
        await using PublicationFixture fixture =
            await PublicationFixture.CreateAsync();

        VerifiedReleasePublicationReport first =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);
        VerifiedReleasePublicationReport second =
            await fixture.PublicationService.PublishAsync(fixture.StagingReport);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Contains(
            second.FailureCode,
            new[]
            {
                VerifiedReleasePublicationFailureCode.StatusMismatch,
                VerifiedReleasePublicationFailureCode.TargetAlreadyPresent,
                VerifiedReleasePublicationFailureCode.UnsafeStagingTree
            });
        Assert.Equal("releases/aethersdr-8.1.0", fixture.CurrentTarget());
    }

    private static void AssertFailureWithoutMutation(
        PublicationFixture fixture,
        VerifiedReleasePublicationReport report,
        VerifiedReleasePublicationFailureCode expected)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(expected, report.FailureCode);
        Assert.False(report.SourceStagingTreeConsumed);
        Assert.False(report.TargetPublished);
        Assert.False(report.TargetImmutable);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
        Assert.False(report.ReconciliationRequired);
        Assert.True(Directory.Exists(fixture.StagingPath));
        Assert.False(Directory.Exists(fixture.Plan.TargetReleasePath));
        Assert.Equal("releases/aethersdr-8.1.0", fixture.CurrentTarget());
    }

    private static void AssertReconciliation(
        VerifiedReleasePublicationReport report)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(
            VerifiedReleasePublicationFailureCode.PublishedStateRequiresReconciliation,
            report.FailureCode);
        Assert.True(report.ReconciliationRequired);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationPerformed);
        Assert.Null(report.PublishedRelease);
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

    private sealed class PublicationFixture : IAsyncDisposable
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

        private PublicationFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-release-publication-{Guid.NewGuid():N}");
            Paths = new InstallationPaths(
                Path.Combine(Root, "config"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(Root, "deployment", "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            DeploymentRoot = Path.GetDirectoryName(Paths.ReleaseDirectory)!;
            CurrentPath = Path.Combine(DeploymentRoot, "current");
            StagingRoot = Path.Combine(
                DeploymentRoot,
                VerifiedReleaseStagingService.StagingDirectoryName);
            BundlePath = Path.Combine(Root, "bundle");
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
            ExpectedBytes = m_bundleFiles.Values.Sum(value => value.LongLength);
            m_store = new InstallationSetupStore(Paths.SetupStatePath);
            Reader = new ReleaseInstallationStatusReader(m_store, Paths);
            StagingService = new VerifiedReleaseStagingService(Reader);
            PublicationService = new VerifiedReleasePublicationService(Reader);
            Plan = null!;
            StagingReport = null!;
        }

        internal string Root { get; }
        internal InstallationPaths Paths { get; }
        internal string DeploymentRoot { get; }
        internal string CurrentPath { get; }
        internal string StagingRoot { get; }
        internal string BundlePath { get; }
        internal string[] ExpectedRelativePaths { get; }
        internal long ExpectedBytes { get; }
        internal long SetupRevision { get; private set; }
        internal ReleaseInstallationStatusReader Reader { get; }
        internal VerifiedReleaseStagingService StagingService { get; }
        internal VerifiedReleasePublicationService PublicationService { get; }
        internal VerifiedReleaseInstallationPlan Plan { get; private set; }
        internal VerifiedReleaseStagingReport StagingReport { get; private set; }
        internal string StagingPath => StagingReport.StagedRelease!.StagingPath;

        internal static async Task<PublicationFixture> CreateAsync()
        {
            PublicationFixture fixture = new();
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
            StagingReport = await StagingService.StageAsync(Plan);
            if (!StagingReport.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Publication fixture staging failed: {StagingReport.FailureCode}");
            }
        }

        internal async Task BumpSetupRevisionAsync()
        {
            InstallationSetupState current = await m_store.LoadAsync();
            await m_store.UpdateAsync(
                current.Revision,
                state => state with
                {
                    CanonicalPublicUrl = state.CanonicalPublicUrl
                });
        }

        internal Task<ReleaseStatusReadResult> StatusBeforePublishAsync() =>
            Reader.ReadAsync();

        internal ReleaseStatusReadResult PublishedStatus(
            ReleaseStatusReadResult before)
        {
            string[] identities = before.AvailableReleaseIdentities
                .Append(Plan.TargetReleaseIdentity)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            return before with
            {
                AvailableReleaseCount = identities.Length,
                AvailableReleaseIdentities = identities,
                ReleaseDirectoryPresent = true
            };
        }

        internal string CurrentTarget() =>
            new DirectoryInfo(CurrentPath).LinkTarget ?? string.Empty;

        internal string BundleFile(string relativePath) =>
            Path.Combine(
                BundlePath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal string StagedFile(string relativePath) =>
            Path.Combine(
                StagingPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal void ReplaceStagedFile(string relativePath, byte[] content)
        {
            string file = StagedFile(relativePath);
            File.SetUnixFileMode(
                file,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllBytes(file, content);
            File.SetUnixFileMode(file, ImmutableFileMode);
        }

        internal void AddStagedFile(string relativePath, byte[] content)
        {
            File.SetUnixFileMode(StagingPath, PrivateWritableDirectoryMode);
            string file = StagedFile(relativePath);
            File.WriteAllBytes(file, content);
            File.SetUnixFileMode(file, ImmutableFileMode);
            File.SetUnixFileMode(StagingPath, ImmutableDirectoryMode);
        }

        internal void AddStagedDirectorySymlink(
            string relativePath,
            string target)
        {
            File.SetUnixFileMode(StagingPath, PrivateWritableDirectoryMode);
            Directory.CreateSymbolicLink(
                Path.Combine(StagingPath, relativePath),
                target);
            File.SetUnixFileMode(StagingPath, ImmutableDirectoryMode);
        }

        internal void DeleteStagingTree()
        {
            MakeTreeWritable(StagingPath);
            Directory.Delete(StagingPath, recursive: true);
        }

        private VerifiedReleaseInstallationPlan CreatePlan()
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
                SetupRevision,
                "aethersdr-8.1.0",
                targetIdentity,
                "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                pinnedReleaseIdentity: string.Empty,
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
                releaseNotesSummary: "Publication test release.");
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
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
            if (!Directory.Exists(root))
            {
                return;
            }
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
