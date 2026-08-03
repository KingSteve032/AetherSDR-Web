using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class ReleaseStatusConsoleTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public void ParserAcceptsOneReleaseStatusCommandAndPreservesAppArguments()
    {
        ReleaseUpdateConsoleCommandLine commandLine =
            ReleaseUpdateConsoleCommandParser.Parse(
                ["--release-status", "--environment=Development"]);

        Assert.Equal(ReleaseUpdateConsoleCommandKind.Status, commandLine.Command);
        Assert.Equal(["--environment=Development"], commandLine.ApplicationArguments);
        Assert.Equal(string.Empty, commandLine.BundleDirectory);
        Assert.Null(commandLine.UpdateChannel);
    }

    [Fact]
    public void ParserRejectsStatusCombinedWithCheckOptions()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReleaseUpdateConsoleCommandParser.Parse(
                [
                    "--release-status",
                    "--release-check-installed-version",
                    "8.1.0"
                ]));
    }

    [Fact]
    public void ParserRejectsMoreThanOneReleaseCommand()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReleaseUpdateConsoleCommandParser.Parse(
                [
                    "--release-status",
                    "--check-offline-release-bundle",
                    Path.GetFullPath("bundle")
                ]));
    }

    [Fact]
    public void PublicSurfaceIsReadOnly()
    {
        string[] readerMethods = typeof(ReleaseInstallationStatusReader)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] consoleMethods = typeof(ReleaseStatusConsole)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ReadAsync"], readerMethods);
        Assert.Equal(["ExecuteAsync", "get_Snapshot"], consoleMethods);
        Assert.DoesNotContain(
            readerMethods.Concat(consoleMethods),
            name =>
                name.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Extract", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Install", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Activate", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Rollback", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Transmit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnosticsRegisterOnlyReadBoundaries()
    {
        await using StatusFixture fixture = new();
        ReleaseStatusConsoleDiagnostics snapshot = fixture.Console.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.SetupStateReadRegistered);
        Assert.True(snapshot.ReleaseInventoryReadRegistered);
        Assert.True(snapshot.CurrentPointerReadRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.ArchiveExtractionRegistered);
        Assert.False(snapshot.StagingRegistered);
        Assert.False(snapshot.InstallationRegistered);
        Assert.False(snapshot.ActivationRegistered);
        Assert.False(snapshot.RollbackRegistered);
        Assert.False(snapshot.MigrationRegistered);
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
    public async Task MissingSetupStateFailsClosedWithoutExposingPath()
    {
        await using StatusFixture fixture = new();
        StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            StatusCommand(),
            output);
        ReleaseStatusConsoleReport report = Deserialize(output);

        Assert.Equal(ReleaseStatusConsole.StatusFailedExitCode, exitCode);
        Assert.False(report.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.SetupStateMissing,
            report.FailureCode);
        Assert.DoesNotContain(fixture.Root, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedSetupStateFailsClosed()
    {
        await using StatusFixture fixture = new();
        fixture.WriteMalformedSetupState();

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ReleaseStatusFailureCode.SetupStateInvalid, result.FailureCode);
    }

    [Fact]
    public async Task IncompleteSetupPathsRejectBeforeReleaseInventoryAccess()
    {
        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync(InstallationSetupStep.BootstrapClaim);
        fixture.CreateReleaseRoot();
        fixture.AddRelease("must-not-be-reported");

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.SetupPathsIncomplete,
            result.FailureCode);
        Assert.Empty(result.AvailableReleaseIdentities);
    }

    [Fact]
    public async Task PersistedPathMismatchFailsClosed()
    {
        await using StatusFixture fixture = new();
        InstallationPaths mismatched = fixture.Paths with
        {
            ReleaseDirectory = Path.Combine(fixture.Root, "other", "releases")
        };
        await fixture.ConfigureAsync(
            InstallationSetupStep.Paths,
            persistedPaths: mismatched);

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.SetupPathsMismatch,
            result.FailureCode);
    }

    [Fact]
    public async Task MissingReleaseDirectoryIsAValidEmptyStatus()
    {
        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.True(result.Succeeded);
        Assert.False(result.ReleaseDirectoryPresent);
        Assert.Equal(0, result.AvailableReleaseCount);
        Assert.False(result.CurrentPointerPresent);
        Assert.Equal(string.Empty, result.ActiveReleaseIdentity);
        Assert.False(result.RollbackCandidateKnown);
    }

    [Fact]
    public async Task DirectReleaseInventoryIsSortedAndRedacted()
    {
        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        fixture.AddRelease("aethersdr-8.2.0");
        fixture.AddRelease("aethersdr-8.1.0");
        StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(StatusCommand(), output);
        ReleaseStatusConsoleReport report = Deserialize(output);

        Assert.Equal(ReleaseStatusConsole.SuccessExitCode, exitCode);
        Assert.True(report.Succeeded);
        Assert.Equal(
            ["aethersdr-8.1.0", "aethersdr-8.2.0"],
            report.AvailableReleaseIdentities);
        Assert.DoesNotContain(fixture.Root, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletePinnedSetupProjectionIsNonAuthoritativeStatusOnly()
    {
        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync(
            InstallationSetupStep.Administrator,
            complete: true,
            channel: InstallationUpdateChannel.Pinned,
            pinnedRelease: "aethersdr-8.2.0",
            installTransmitSupport: true);

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.SetupComplete);
        Assert.Equal(InstallationSetupLockMode.Complete, result.SetupLockMode);
        Assert.Equal(InstallationUpdateChannel.Pinned, result.UpdateChannel);
        Assert.Equal("aethersdr-8.2.0", result.PinnedReleaseIdentity);
        Assert.True(result.InstallTransmitSupport);
        Assert.False(result.RollbackCandidateKnown);
    }

    [Fact]
    public async Task AbsoluteCurrentPointerSelectsOneInventoriedRelease()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        string releasePath = fixture.AddRelease("aethersdr-8.2.0");
        fixture.CreateCurrentPointer(releasePath);

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.CurrentPointerPresent);
        Assert.Equal("aethersdr-8.2.0", result.ActiveReleaseIdentity);
    }

    [Fact]
    public async Task CanonicalRelativeCurrentPointerIsAccepted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        fixture.AddRelease("aethersdr-8.2.0");
        fixture.CreateCurrentPointer("releases/aethersdr-8.2.0");

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("aethersdr-8.2.0", result.ActiveReleaseIdentity);
    }

    [Fact]
    public async Task NonCanonicalRelativeCurrentPointerFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        fixture.AddRelease("aethersdr-8.2.0");
        fixture.CreateCurrentPointer(
            "releases/../releases/aethersdr-8.2.0");

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.UnsafeCurrentPointer,
            result.FailureCode);
    }

    [Fact]
    public async Task CurrentPointerOutsideReleaseRootFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        fixture.SetSafeDirectoryMode(outside);
        fixture.CreateCurrentPointer(outside);

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.UnsafeCurrentPointer,
            result.FailureCode);
    }

    [Fact]
    public async Task CurrentPointerMustNameAnInventoriedRelease()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        fixture.CreateCurrentPointer("releases/aethersdr-8.2.0");

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.UnsafeCurrentPointer,
            result.FailureCode);
    }

    [Fact]
    public async Task CurrentEntryMustBeASymbolicLink()
    {
        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        fixture.AddRelease("aethersdr-8.2.0");
        Directory.CreateDirectory(fixture.CurrentPath);
        fixture.SetSafeDirectoryMode(fixture.CurrentPath);

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.UnsafeCurrentPointer,
            result.FailureCode);
    }

    [Fact]
    public async Task ReleaseInventoryRejectsNonDirectoryEntries()
    {
        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        File.WriteAllText(
            Path.Combine(fixture.Paths.ReleaseDirectory, "unexpected.txt"),
            "unexpected");

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.UnsafeReleaseEntry,
            result.FailureCode);
    }

    [Fact]
    public async Task ReleaseInventoryRejectsNonCanonicalIdentity()
    {
        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        string path = Path.Combine(fixture.Paths.ReleaseDirectory, "bad identity");
        Directory.CreateDirectory(path);
        fixture.SetSafeDirectoryMode(path);

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.UnsafeReleaseEntry,
            result.FailureCode);
    }

    [Fact]
    public async Task ReleaseInventoryRejectsSymbolicLinkEntries()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        string outside = Path.Combine(fixture.Root, "outside-release");
        Directory.CreateDirectory(outside);
        fixture.SetSafeDirectoryMode(outside);
        Directory.CreateSymbolicLink(
            Path.Combine(fixture.Paths.ReleaseDirectory, "aethersdr-8.2.0"),
            outside);

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.UnsafeReleaseEntry,
            result.FailureCode);
    }

    [Fact]
    public async Task ReleaseInventoryIsBounded()
    {
        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        for (int index = 0;
             index <= ReleaseInstallationStatusReader.MaximumReleaseCount;
             index++)
        {
            fixture.AddRelease($"aethersdr-8.2.{index}");
        }

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.ReleaseInventoryTooLarge,
            result.FailureCode);
    }

    [Fact]
    public async Task GroupWritableReleaseRootFailsClosedOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        File.SetUnixFileMode(
            fixture.Paths.ReleaseDirectory,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute);

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.UnsafeReleaseDirectory,
            result.FailureCode);
    }

    [Fact]
    public async Task GroupWritableReleaseEntryFailsClosedOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using StatusFixture fixture = new();
        await fixture.ConfigureAsync();
        fixture.CreateReleaseRoot();
        string release = fixture.AddRelease("aethersdr-8.2.0");
        File.SetUnixFileMode(
            release,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute);

        ReleaseStatusReadResult result = await fixture.Reader.ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(
            ReleaseStatusFailureCode.UnsafeReleaseEntry,
            result.FailureCode);
    }

    [Fact]
    public async Task ConsoleRequiresItsExactCommand()
    {
        await using StatusFixture fixture = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Console.ExecuteAsync(
                ReleaseUpdateConsoleCommandLine.None([]),
                new StringWriter()));
    }

    private static ReleaseUpdateConsoleCommandLine StatusCommand() =>
        ReleaseUpdateConsoleCommandParser.Parse(["--release-status"]);

    private static ReleaseStatusConsoleReport Deserialize(StringWriter output) =>
        JsonSerializer.Deserialize<ReleaseStatusConsoleReport>(
            output.ToString(),
            JsonOptions) ??
        throw new InvalidOperationException("Release status output was empty.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    private sealed class StatusFixture : IAsyncDisposable
    {
        public StatusFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-release-status-{Guid.NewGuid():N}");
            Paths = new InstallationPaths(
                Path.Combine(Root, "config"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(Root, "deployment", "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            Store = new InstallationSetupStore(Paths.SetupStatePath);
            Reader = new ReleaseInstallationStatusReader(Store, Paths);
            Console = new ReleaseStatusConsole(Reader);
        }

        public string Root { get; }
        public InstallationPaths Paths { get; }
        public InstallationSetupStore Store { get; }
        public ReleaseInstallationStatusReader Reader { get; }
        public ReleaseStatusConsole Console { get; }
        public string CurrentPath =>
            Path.Combine(Path.GetDirectoryName(Paths.ReleaseDirectory)!, "current");

        public async Task ConfigureAsync(
            InstallationSetupStep step = InstallationSetupStep.Paths,
            InstallationPaths? persistedPaths = null,
            bool complete = false,
            InstallationUpdateChannel channel = InstallationUpdateChannel.Stable,
            string pinnedRelease = "",
            bool installTransmitSupport = false)
        {
            InstallationSetupState initial = await Store.LoadOrCreateAsync();
            InstallationSetupStep completedStep = complete
                ? InstallationSetupStep.Administrator
                : step;
            _ = await Store.UpdateAsync(
                initial.Revision,
                current => current with
                {
                    LastCompletedStep = completedStep,
                    Lock = new InstallationSetupLock
                    {
                        Mode = complete
                            ? InstallationSetupLockMode.Complete
                            : InstallationSetupLockMode.Claimed,
                        ClaimedAt = current.CreatedAt,
                        CompletedAt = complete ? current.CreatedAt : null
                    },
                    Topology = completedStep >= InstallationSetupStep.Topology
                        ? InstallationTopologyKind.PersonalSingleStation
                        : null,
                    CanonicalPublicUrl =
                        completedStep >= InstallationSetupStep.PublicUrl
                            ? "https://radio.example.org"
                            : string.Empty,
                    Paths = completedStep >= InstallationSetupStep.Paths
                        ? persistedPaths ?? Paths
                        : null,
                    UpdateChannel = channel,
                    PinnedRelease = pinnedRelease,
                    InstallTransmitSupport = installTransmitSupport
                });
        }

        public void WriteMalformedSetupState()
        {
            string directory = Path.GetDirectoryName(Paths.SetupStatePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Paths.SetupStatePath, "{ malformed");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    Paths.SetupStatePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public void CreateReleaseRoot()
        {
            Directory.CreateDirectory(Paths.ReleaseDirectory);
            SetSafeDirectoryMode(Paths.ReleaseDirectory);
        }

        public string AddRelease(string identity)
        {
            string path = Path.Combine(Paths.ReleaseDirectory, identity);
            Directory.CreateDirectory(path);
            SetSafeDirectoryMode(path);
            return path;
        }

        public void CreateCurrentPointer(string target)
        {
            string parent = Path.GetDirectoryName(CurrentPath)!;
            Directory.CreateDirectory(parent);
            Directory.CreateSymbolicLink(CurrentPath, target);
        }

        public void SetSafeDirectoryMode(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
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
}
