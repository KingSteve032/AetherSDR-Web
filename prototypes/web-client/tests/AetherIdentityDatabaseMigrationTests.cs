using System.Text.Json;
using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AetherSDR.Web.Tests;

public sealed class AetherIdentityDatabaseMigrationTests
{
    [Fact]
    public void ParserRequiresOneCommandAndExactApplyConfirmation()
    {
        AetherIdentityDatabaseCommandLine none =
            AetherIdentityDatabaseCommandParser.Parse(["--urls", "http://localhost"]);

        Assert.Equal(AetherIdentityDatabaseCommandKind.None, none.Command);
        Assert.Equal(["--urls", "http://localhost"], none.ApplicationArguments);

        Assert.Throws<InvalidOperationException>(
            () => AetherIdentityDatabaseCommandParser.Parse(
                [AetherIdentityDatabaseCommandParser.ApplySwitch]));
        Assert.Throws<InvalidOperationException>(
            () => AetherIdentityDatabaseCommandParser.Parse(
                [
                    AetherIdentityDatabaseCommandParser.PlanSwitch,
                    AetherIdentityDatabaseCommandParser.ValidateSwitch
                ]));
        Assert.Throws<InvalidOperationException>(
            () => AetherIdentityDatabaseCommandParser.Parse(
                [
                    AetherIdentityDatabaseCommandParser.PlanSwitch,
                    AetherIdentityDatabaseCommandParser.ConfirmPlanSwitch,
                    new string('0', 64)
                ]));

        AetherIdentityDatabaseCommandLine apply =
            AetherIdentityDatabaseCommandParser.Parse(
                [
                    AetherIdentityDatabaseCommandParser.ApplySwitch,
                    AetherIdentityDatabaseCommandParser.ConfirmPlanSwitch,
                    new string('a', 64)
                ]);

        Assert.Equal(AetherIdentityDatabaseCommandKind.Apply, apply.Command);
        Assert.Equal(new string('a', 64), apply.ConfirmedPlanId);
        Assert.Empty(apply.ApplicationArguments);
    }

    [Fact]
    public async Task ExactPlanInitializesOnceAndThenConvergesWithoutMutation()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = Paths(temporary.Path);

        AetherIdentityDatabaseReport firstPlan =
            await AetherIdentityDatabaseMigration.PlanAsync(paths);
        AetherIdentityDatabaseReport secondPlan =
            await AetherIdentityDatabaseMigration.PlanAsync(paths);

        Assert.Equal("planned", firstPlan.Outcome);
        Assert.Equal(
            "identity-schema-initialization-required",
            firstPlan.Code);
        Assert.Equal(firstPlan.PlanId, secondPlan.PlanId);
        Assert.True(firstPlan.MutationRequired);
        Assert.False(firstPlan.MutationAttempted);
        Assert.False(File.Exists(paths.IdentityDatabasePath));

        AetherIdentityDatabaseReport mismatch =
            await AetherIdentityDatabaseMigration.ApplyAsync(
                paths,
                new string('0', 64));

        Assert.Equal("rejected", mismatch.Outcome);
        Assert.Equal("identity-database-plan-mismatch", mismatch.Code);
        Assert.False(mismatch.MutationAttempted);
        Assert.False(File.Exists(paths.IdentityDatabasePath));

        AetherIdentityDatabaseReport applied =
            await AetherIdentityDatabaseMigration.ApplyAsync(
                paths,
                firstPlan.PlanId);

        Assert.Equal("applied", applied.Outcome);
        Assert.Equal("identity-schema-initialized", applied.Code);
        Assert.True(applied.MutationAttempted);
        Assert.True(applied.DatabaseCreated);
        Assert.False(applied.BackupRequired);
        Assert.False(applied.RollbackAttempted);
        Assert.True(File.Exists(paths.IdentityDatabasePath));
        Assert.Equal(1, applied.ExistingSchemaVersion);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute,
                File.GetUnixFileMode(paths.IdentityStoreDirectory));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(paths.IdentityDatabasePath));
        }

        AetherIdentityDatabaseReport validate =
            await AetherIdentityDatabaseMigration.ValidateAsync(paths);
        AetherIdentityDatabaseReport convergedPlan =
            await AetherIdentityDatabaseMigration.PlanAsync(paths);
        AetherIdentityDatabaseReport convergedApply =
            await AetherIdentityDatabaseMigration.ApplyAsync(
                paths,
                convergedPlan.PlanId);

        Assert.Equal("converged", validate.Outcome);
        Assert.Equal("identity-schema-converged", validate.Code);
        Assert.NotEqual(firstPlan.PlanId, convergedPlan.PlanId);
        Assert.Equal("converged", convergedApply.Outcome);
        Assert.False(convergedApply.MutationRequired);
        Assert.False(convergedApply.MutationAttempted);
        Assert.False(convergedApply.DatabaseCreated);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                paths.IdentityDatabasePath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.GroupRead);
            AetherIdentityDatabaseReport insecure =
                await AetherIdentityDatabaseMigration.ValidateAsync(paths);
            Assert.Equal("rejected", insecure.Outcome);
            Assert.Equal(
                "identity-database-validation-rejected",
                insecure.Code);
            Assert.False(insecure.MutationAttempted);
            File.SetUnixFileMode(
                paths.IdentityDatabasePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task FailureAfterSchemaCreationRollsBackOnlyTheOwnedDatabase()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = Paths(temporary.Path);
        AetherIdentityDatabaseReport plan =
            await AetherIdentityDatabaseMigration.PlanAsync(paths);

        AetherIdentityDatabaseReport result =
            await AetherIdentityDatabaseMigration.ApplyAsync(
                paths,
                plan.PlanId,
                async (context, cancellationToken) =>
                {
                    _ = await context.Database.EnsureCreatedAsync(
                        cancellationToken);
                    throw new IOException("Injected migration failure.");
                });

        Assert.Equal("rejected", result.Outcome);
        Assert.Equal("identity-database-apply-failed", result.Code);
        Assert.True(result.MutationAttempted);
        Assert.True(result.RollbackAttempted);
        Assert.True(result.RollbackSucceeded);
        Assert.False(File.Exists(paths.IdentityDatabasePath));
        Assert.False(File.Exists(paths.IdentityDatabasePath + "-wal"));
        Assert.False(File.Exists(paths.IdentityDatabasePath + "-shm"));
    }

    [Fact]
    public async Task UnmanagedOrActiveDatabaseIsRejectedWithoutMutation()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths unmanagedPaths =
            Paths(Path.Combine(temporary.Path, "unmanaged"));
        Directory.CreateDirectory(unmanagedPaths.IdentityStoreDirectory);
        await CreateUnmanagedDatabaseAsync(unmanagedPaths.IdentityDatabasePath);

        AetherIdentityDatabaseReport unmanaged =
            await AetherIdentityDatabaseMigration.ValidateAsync(unmanagedPaths);

        Assert.Equal("rejected", unmanaged.Outcome);
        Assert.Equal(
            "identity-database-validation-rejected",
            unmanaged.Code);
        Assert.False(unmanaged.MutationAttempted);
        Assert.True(File.Exists(unmanagedPaths.IdentityDatabasePath));

        InstallationPaths activePaths =
            Paths(Path.Combine(temporary.Path, "active"));
        Directory.CreateDirectory(activePaths.IdentityStoreDirectory);
        await File.WriteAllBytesAsync(
            activePaths.IdentityDatabasePath + "-wal",
            [1, 2, 3]);

        AetherIdentityDatabaseReport active =
            await AetherIdentityDatabaseMigration.PlanAsync(activePaths);

        Assert.Equal("rejected", active.Outcome);
        Assert.Equal("identity-database-plan-rejected", active.Code);
        Assert.False(active.MutationAttempted);
        Assert.True(File.Exists(activePaths.IdentityDatabasePath + "-wal"));
    }

    [Fact]
    public async Task ConsoleReportsMachineReadableNonMutatingPlan()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = Paths(temporary.Path);
        StringWriter output = new();
        AetherIdentityDatabaseCommandLine command = new(
            AetherIdentityDatabaseCommandKind.Plan,
            ConfirmedPlanId: string.Empty,
            ApplicationArguments: []);

        int exitCode = await AetherIdentityDatabaseConsole.ExecuteAsync(
            command,
            paths,
            output);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("planned", root.GetProperty("outcome").GetString());
        Assert.Equal(
            "identity-schema-initialization-required",
            root.GetProperty("code").GetString());
        Assert.False(root.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(
            64,
            root.GetProperty("planId").GetString()!.Length);
        Assert.False(File.Exists(paths.IdentityDatabasePath));
    }

    private static InstallationPaths Paths(string root) =>
        InstallationPaths.Resolve(
            Path.GetFullPath(root),
            InstallationPathLayout.Development);

    private static async Task CreateUnmanagedDatabaseAsync(string path)
    {
        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        await using SqliteConnection connection =
            new(connectionString.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Unmanaged (Id INTEGER PRIMARY KEY);";
        _ = await command.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-identity-migration-tests-{Guid.NewGuid():N}");
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
