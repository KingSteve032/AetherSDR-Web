using System.Security.Cryptography;
using System.Text;
using AetherSDR.Web.Operations;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Data.Sqlite;

namespace AetherSDR.Web.Tests;

public sealed class InstallationBackupTests
{
    private const string Passphrase = "correct horse battery staple";
    private const string ReleaseIdentity = "aethersdr-8.6.0-beta.1";

    [Fact]
    public async Task EncryptedBackupAuthenticatesAndDoesNotExposeProtectedContent()
    {
        using TemporaryDirectory temporary = new();
        BackupFixture fixture = await BackupFixture.CreateAsync(
            Path.Combine(temporary.Path, "source"));

        (string path, InstallationBackupSummary summary) =
            await fixture.Service.CreateAsync(Passphrase);

        Assert.True(File.Exists(path));
        Assert.Equal(ReleaseIdentity, summary.CurrentReleaseIdentity);
        Assert.True(summary.ProtectedFileCount >= 5);
        Assert.Contains(
            "local-users-passwords-mfa-and-sessions",
            summary.IncludedAuthorities);
        byte[] encrypted = await File.ReadAllBytesAsync(path);
        string printable = Encoding.UTF8.GetString(encrypted);
        Assert.DoesNotContain("TOP-SECRET-CONFIG", printable, StringComparison.Ordinal);
        Assert.DoesNotContain("STATION-CREDENTIAL-SECRET", printable, StringComparison.Ordinal);
        Assert.DoesNotContain("MFA-SEED-SECRET", printable, StringComparison.Ordinal);

        InstallationBackupSummary inspected =
            await fixture.Service.InspectAsync(path, Passphrase);
        Assert.Equal(summary.BackupId, inspected.BackupId);
        Assert.Equal(summary.ProtectedFileCount, inspected.ProtectedFileCount);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.InspectAsync(path, "this is the wrong passphrase"));
    }

    [Fact]
    public async Task BackupExcludesGroupWritableReleaseSupervisorIpcDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using TemporaryDirectory temporary = new();
        BackupFixture fixture = await BackupFixture.CreateAsync(
            Path.Combine(temporary.Path, "source"));
        string supervisor = Path.Combine(
            fixture.Paths.StateDirectory,
            ReleaseUpdateSupervisor.DirectoryName);
        Directory.CreateDirectory(supervisor);
        File.SetUnixFileMode(
            supervisor,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute);
        string socketFixture = Path.Combine(
            supervisor,
            ReleaseUpdateSupervisor.SocketFileName);
        await File.WriteAllTextAsync(socketFixture, "ephemeral-ipc");
        File.SetUnixFileMode(
            socketFixture,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite);

        (string path, InstallationBackupSummary summary) =
            await fixture.Service.CreateAsync(Passphrase);

        Assert.True(File.Exists(path));
        Assert.True(summary.ProtectedFileCount >= 5);
    }

    [Fact]
    public async Task BackupRejectsLinkedReleaseSupervisorRuntimePath()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using TemporaryDirectory temporary = new();
        BackupFixture fixture = await BackupFixture.CreateAsync(
            Path.Combine(temporary.Path, "source"));
        string linkedTarget = Path.Combine(temporary.Path, "linked-runtime");
        Directory.CreateDirectory(linkedTarget);
        string supervisor = Path.Combine(
            fixture.Paths.StateDirectory,
            ReleaseUpdateSupervisor.DirectoryName);
        Directory.CreateSymbolicLink(supervisor, linkedTarget);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Service.CreateAsync(Passphrase));

        Assert.Contains("transient release-updater runtime", exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupStillRejectsUnrelatedGroupWritableStateDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using TemporaryDirectory temporary = new();
        BackupFixture fixture = await BackupFixture.CreateAsync(
            Path.Combine(temporary.Path, "source"));
        string unsafeDirectory = Path.Combine(
            fixture.Paths.StateDirectory,
            "unexpected-shared-state");
        Directory.CreateDirectory(unsafeDirectory);
        File.SetUnixFileMode(
            unsafeDirectory,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Service.CreateAsync(Passphrase));

        Assert.Contains("shared-writable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupAcceptsInstallerManagedCaddyOwnershipMarkerFormat()
    {
        using TemporaryDirectory temporary = new();
        BackupFixture fixture = await BackupFixture.CreateAsync(
            Path.Combine(temporary.Path, "source"));
        string proxyState = Path.Combine(
            fixture.Paths.StateDirectory,
            "installer-proxy");
        string caddyDirectory = Path.Combine(
            fixture.Paths.ConfigurationDirectory,
            "managed-caddy");
        Directory.CreateDirectory(proxyState);
        Directory.CreateDirectory(caddyDirectory);
        string caddyPath = Path.Combine(caddyDirectory, "Caddyfile");
        const string CaddyContent = "https://radio.example.org { reverse_proxy 127.0.0.1:5080 }\n";
        await File.WriteAllTextAsync(caddyPath, CaddyContent);
        string digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(CaddyContent)));
        string markerPath = Path.Combine(proxyState, "managed-caddy.sha256");
        await File.WriteAllTextAsync(
            markerPath,
            $"sha256={digest}\nplan=m8h-test-plan\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                proxyState,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
            File.SetUnixFileMode(
                caddyDirectory,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
            File.SetUnixFileMode(
                caddyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.SetUnixFileMode(
                markerPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        (string path, InstallationBackupSummary summary) =
            await fixture.Service.CreateAsync(Passphrase);

        Assert.True(File.Exists(path));
        Assert.True(summary.ProtectedFileCount >= 7);
    }

    [Fact]
    public async Task BackupRejectsManagedCaddyMarkerWithWrongDigest()
    {
        using TemporaryDirectory temporary = new();
        BackupFixture fixture = await BackupFixture.CreateAsync(
            Path.Combine(temporary.Path, "source"));
        string proxyState = Path.Combine(
            fixture.Paths.StateDirectory,
            "installer-proxy");
        string caddyDirectory = Path.Combine(
            fixture.Paths.ConfigurationDirectory,
            "managed-caddy");
        Directory.CreateDirectory(proxyState);
        Directory.CreateDirectory(caddyDirectory);
        string caddyPath = Path.Combine(caddyDirectory, "Caddyfile");
        await File.WriteAllTextAsync(caddyPath, "managed caddy\n");
        string markerPath = Path.Combine(proxyState, "managed-caddy.sha256");
        await File.WriteAllTextAsync(
            markerPath,
            $"sha256={new string('0', 64)}\nplan=m8h-test-plan\n");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Service.CreateAsync(Passphrase));

        Assert.Contains(
            "does not match installer-owned state",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TamperedEncryptedBackupFailsAuthentication()
    {
        using TemporaryDirectory temporary = new();
        BackupFixture fixture = await BackupFixture.CreateAsync(
            Path.Combine(temporary.Path, "source"));
        (string path, _) = await fixture.Service.CreateAsync(Passphrase);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^8] ^= 0x5a;
        string tampered = Path.Combine(fixture.Paths.BackupDirectory, "tampered.aebak");
        await File.WriteAllBytesAsync(tampered, bytes);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                tampered,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.InspectAsync(tampered, Passphrase));
    }

    [Fact]
    public async Task RestoreRemapsPathsAndRecoversProtectedAuthority()
    {
        using TemporaryDirectory temporary = new();
        BackupFixture source = await BackupFixture.CreateAsync(
            Path.Combine(temporary.Path, "source"));
        (string backupPath, InstallationBackupSummary backup) =
            await source.Service.CreateAsync(Passphrase);

        string targetRoot = Path.Combine(temporary.Path, "replacement");
        InstallationPaths targetPaths = BackupFixture.CreatePaths(targetRoot);
        BackupFixture.PrepareDirectories(targetPaths);
        Directory.CreateDirectory(
            Path.Combine(targetPaths.ReleaseDirectory, ReleaseIdentity));
        string oldIdentity = "aethersdr-8.5.0-beta.1";
        Directory.CreateDirectory(
            Path.Combine(targetPaths.ReleaseDirectory, oldIdentity));
        string current = Path.Combine(targetRoot, "current");
        Directory.CreateSymbolicLink(
            current,
            Path.Combine(targetPaths.ReleaseDirectory, oldIdentity));
        await File.WriteAllTextAsync(
            targetPaths.ConfigurationFilePath,
            "REPLACEMENT-CONFIG");
        await File.WriteAllTextAsync(
            Path.Combine(targetPaths.StateDirectory, "stale-state.txt"),
            "stale");
        await File.WriteAllTextAsync(
            Path.Combine(targetPaths.SecretDirectory, "stale-secret.txt"),
            "stale");

        InstallationSetupStore targetStore = new(targetPaths.SetupStatePath);
        ReleaseInstallationStatusReader targetStatus = new(targetStore, targetPaths);
        InstallationBackupService targetService = new(
            targetPaths,
            targetStore,
            targetStatus);

        InstallationRestoreSummary restored = await targetService.RestoreAsync(
            backupPath,
            Passphrase);

        Assert.Equal(backup.BackupId, restored.BackupId);
        Assert.True(restored.ReplacementHostCompatible);
        Assert.Equal(
            "TOP-SECRET-CONFIG",
            await File.ReadAllTextAsync(targetPaths.ConfigurationFilePath));
        Assert.Equal(
            "STATION-CREDENTIAL-SECRET",
            await File.ReadAllTextAsync(
                Path.Combine(targetPaths.SecretDirectory, "station-credential")));
        Assert.False(File.Exists(
            Path.Combine(targetPaths.StateDirectory, "stale-state.txt")));
        Assert.False(File.Exists(
            Path.Combine(targetPaths.SecretDirectory, "stale-secret.txt")));
        InstallationSetupState restoredSetup = await targetStore.LoadAsync();
        Assert.Equal(targetPaths, restoredSetup.Paths);
        Assert.Equal(backup.SetupRevision, restoredSetup.Revision);
        string? linkTarget = new DirectoryInfo(current).LinkTarget;
        Assert.NotNull(linkTarget);
        Assert.Equal(
            Path.Combine("releases", ReleaseIdentity),
            linkTarget);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(targetPaths.ReleaseDirectory, ReleaseIdentity)),
            Path.GetFullPath(linkTarget!, targetRoot));
        Assert.False(File.Exists(
            Path.Combine(targetPaths.BackupDirectory, ".restore-active.json")));
    }

    [Fact]
    public void ConsoleParserNeverAcceptsPassphraseArgumentsOrAmbiguousCommands()
    {
        OperationsConsoleCommandLine create = OperationsConsoleCommandParser.Parse(
            ["--create-encrypted-backup", "--urls", "http://127.0.0.1:1"]);
        Assert.Equal(OperationsConsoleCommandKind.CreateBackup, create.Command);
        Assert.Equal(["--urls", "http://127.0.0.1:1"], create.ApplicationArguments);

        Assert.Throws<InvalidOperationException>(() =>
            OperationsConsoleCommandParser.Parse(
                ["--create-encrypted-backup", "--restore-encrypted-backup"]));
        Assert.Throws<InvalidOperationException>(() =>
            OperationsConsoleCommandParser.Parse(
                ["--inspect-encrypted-backup"]));
        OperationsConsoleCommandLine unknown = OperationsConsoleCommandParser.Parse(
            ["--backup-passphrase", "should-never-be-special"]);
        Assert.Equal(OperationsConsoleCommandKind.None, unknown.Command);
        Assert.Equal(
            ["--backup-passphrase", "should-never-be-special"],
            unknown.ApplicationArguments);
    }

    private sealed class BackupFixture(
        InstallationPaths paths,
        InstallationSetupStore store,
        InstallationBackupService service)
    {
        internal InstallationPaths Paths { get; } = paths;
        internal InstallationSetupStore Store { get; } = store;
        internal InstallationBackupService Service { get; } = service;

        internal static async Task<BackupFixture> CreateAsync(string root)
        {
            InstallationPaths paths = CreatePaths(root);
            PrepareDirectories(paths);
            InstallationSetupStore store = new(paths.SetupStatePath);
            InstallationSetupState initial = await store.LoadOrCreateAsync();
            DateTimeOffset now = initial.CreatedAt.AddMinutes(1);
            InstallationSetupState completed = await store.UpdateAsync(
                initial.Revision,
                state => state with
                {
                    LastCompletedStep = InstallationSetupStep.Administrator,
                    Lock = new InstallationSetupLock
                    {
                        Mode = InstallationSetupLockMode.Complete,
                        ClaimedAt = state.CreatedAt,
                        CompletedAt = now
                    },
                    Topology = InstallationTopologyKind.LocalStationGateway,
                    CanonicalPublicUrl = "https://radio.example.org",
                    Paths = paths,
                    UpdateChannel = InstallationUpdateChannel.Stable,
                    PinnedRelease = string.Empty,
                    InstallTransmitSupport = false
                });
            Assert.True(completed.Revision > 0);

            await File.WriteAllTextAsync(
                paths.ConfigurationFilePath,
                "TOP-SECRET-CONFIG");
            await File.WriteAllTextAsync(
                Path.Combine(paths.SecretDirectory, "station-credential"),
                "STATION-CREDENTIAL-SECRET");
            Directory.CreateDirectory(paths.DataProtectionKeyDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(paths.DataProtectionKeyDirectory, "key.xml"),
                "DATA-PROTECTION-KEY-SECRET");
            Directory.CreateDirectory(Path.GetDirectoryName(paths.RadioAccessPolicyPath)!);
            await File.WriteAllTextAsync(paths.RadioAccessPolicyPath, "RADIO-POLICY");
            await File.WriteAllTextAsync(paths.AdministrativeAuditPath, "AUDIT-EVENT");
            await CreateIdentityDatabaseAsync(paths.IdentityDatabasePath);

            Directory.CreateDirectory(
                Path.Combine(paths.ReleaseDirectory, ReleaseIdentity));
            Directory.CreateSymbolicLink(
                Path.Combine(root, "current"),
                Path.Combine(paths.ReleaseDirectory, ReleaseIdentity));

            ReleaseInstallationStatusReader status = new(store, paths);
            InstallationBackupService service = new(paths, store, status);
            InstallationBackupReadiness readiness =
                await service.InspectReadinessAsync();
            Assert.True(readiness.Ready, readiness.Message);
            return new BackupFixture(paths, store, service);
        }

        internal static InstallationPaths CreatePaths(string root) =>
            new(
                Path.Combine(root, "config"),
                Path.Combine(root, "state"),
                Path.Combine(root, "secrets"),
                Path.Combine(root, "releases"),
                Path.Combine(root, "backups"),
                Path.Combine(root, "logs"));

        internal static void PrepareDirectories(InstallationPaths paths)
        {
            foreach (string directory in new[]
            {
                paths.ConfigurationDirectory,
                paths.StateDirectory,
                paths.SecretDirectory,
                paths.ReleaseDirectory,
                paths.BackupDirectory,
                paths.LogDirectory,
                paths.IdentityStoreDirectory
            })
            {
                Directory.CreateDirectory(directory);
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(
                        directory,
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead |
                        UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead |
                        UnixFileMode.OtherExecute);
                }
            }
        }

        private static async Task CreateIdentityDatabaseAsync(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using SqliteConnection connection = new($"Data Source={path}");
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE authority(id INTEGER PRIMARY KEY, secret TEXT NOT NULL);" +
                "INSERT INTO authority(secret) VALUES ('MFA-SEED-SECRET');";
            await command.ExecuteNonQueryAsync();
            await connection.CloseAsync();
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-backup-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
