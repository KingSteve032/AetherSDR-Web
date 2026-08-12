using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Setup;
using Microsoft.Data.Sqlite;

namespace AetherSDR.Web.Tests;

public sealed class AetherSetupIdentityDatabaseBootstrapTests
{
    [Fact]
    public async Task CleanSetupInitializesOnceAndThenConverges()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = Paths(temporary.Path);

        AetherIdentityDatabaseReport applied =
            await AetherSetupIdentityDatabaseBootstrap
                .EnsureInitializedAsync(paths);
        AetherIdentityDatabaseReport converged =
            await AetherSetupIdentityDatabaseBootstrap
                .EnsureInitializedAsync(paths);
        AetherIdentityDatabaseReport validated =
            await AetherIdentityDatabaseMigration.ValidateAsync(paths);

        Assert.Equal("applied", applied.Outcome);
        Assert.Equal("identity-schema-initialized", applied.Code);
        Assert.True(applied.MutationAttempted);
        Assert.True(applied.DatabaseCreated);
        Assert.Equal("converged", converged.Outcome);
        Assert.False(converged.MutationAttempted);
        Assert.False(converged.DatabaseCreated);
        Assert.Equal("converged", validated.Outcome);
        Assert.Equal(
            AetherIdentityDbContext.CurrentSchemaVersion,
            validated.ExistingSchemaVersion);
    }

    [Fact]
    public async Task ExistingInvalidStoreIsRejectedWithoutReplacement()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = Paths(temporary.Path);
        Directory.CreateDirectory(paths.IdentityStoreDirectory);
        await File.WriteAllTextAsync(
            paths.IdentityDatabasePath,
            "not-an-identity-database");
        byte[] before = await File.ReadAllBytesAsync(paths.IdentityDatabasePath);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AetherSetupIdentityDatabaseBootstrap
                .EnsureInitializedAsync(paths));

        Assert.Equal(
            before,
            await File.ReadAllBytesAsync(paths.IdentityDatabasePath));
    }

    private static InstallationPaths Paths(string root) =>
        new(
            Path.Combine(root, "configuration"),
            Path.Combine(root, "state"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "releases"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "logs"));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-setup-identity-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    Path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
