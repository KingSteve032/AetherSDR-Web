using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AetherSDR.Web.Auth.Identity;

internal enum AetherIdentityDatabaseCommandKind
{
    None = 0,
    Plan = 1,
    Validate = 2,
    Apply = 3
}

internal sealed record AetherIdentityDatabaseCommandLine(
    AetherIdentityDatabaseCommandKind Command,
    string ConfirmedPlanId,
    IReadOnlyList<string> ApplicationArguments);

internal static class AetherIdentityDatabaseCommandParser
{
    internal const string PlanSwitch = "--identity-database-plan";
    internal const string ValidateSwitch = "--identity-database-validate";
    internal const string ApplySwitch = "--identity-database-apply";
    internal const string ConfirmPlanSwitch = "--confirm-identity-database-plan";

    internal static AetherIdentityDatabaseCommandLine Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        AetherIdentityDatabaseCommandKind command =
            AetherIdentityDatabaseCommandKind.None;
        string confirmedPlanId = string.Empty;
        List<string> applicationArguments = [];

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case PlanSwitch:
                    SetCommand(ref command, AetherIdentityDatabaseCommandKind.Plan);
                    break;
                case ValidateSwitch:
                    SetCommand(
                        ref command,
                        AetherIdentityDatabaseCommandKind.Validate);
                    break;
                case ApplySwitch:
                    SetCommand(ref command, AetherIdentityDatabaseCommandKind.Apply);
                    break;
                case ConfirmPlanSwitch:
                    if (!string.IsNullOrEmpty(confirmedPlanId))
                    {
                        throw new InvalidOperationException(
                            "Identity database plan confirmation was provided " +
                            "more than once.");
                    }
                    confirmedPlanId = ParsePlanId(
                        RequireValue(arguments, ref index, argument));
                    break;
                default:
                    applicationArguments.Add(argument);
                    break;
            }
        }

        if (command == AetherIdentityDatabaseCommandKind.None &&
            !string.IsNullOrEmpty(confirmedPlanId))
        {
            throw new InvalidOperationException(
                "Identity database plan confirmation requires the apply command.");
        }
        if (command == AetherIdentityDatabaseCommandKind.Apply &&
            string.IsNullOrEmpty(confirmedPlanId))
        {
            throw new InvalidOperationException(
                "Identity database apply requires exact plan confirmation.");
        }
        if (command is AetherIdentityDatabaseCommandKind.Plan or
                AetherIdentityDatabaseCommandKind.Validate &&
            !string.IsNullOrEmpty(confirmedPlanId))
        {
            throw new InvalidOperationException(
                "Identity database plan confirmation is valid only for apply.");
        }

        return new(command, confirmedPlanId, [.. applicationArguments]);
    }

    private static void SetCommand(
        ref AetherIdentityDatabaseCommandKind current,
        AetherIdentityDatabaseCommandKind candidate)
    {
        if (current != AetherIdentityDatabaseCommandKind.None)
        {
            throw new InvalidOperationException(
                "Only one identity database command may be requested.");
        }
        current = candidate;
    }

    private static string RequireValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        if (index + 1 >= arguments.Count ||
            string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            throw new InvalidOperationException(
                $"Identity database option '{option}' requires one value.");
        }
        return arguments[++index].Trim();
    }

    private static string ParsePlanId(string value)
    {
        if (value.Length != 64 ||
            value.Any(character =>
                !char.IsAsciiHexDigit(character) ||
                char.IsAsciiLetterUpper(character)))
        {
            throw new InvalidOperationException(
                "Identity database plan confirmation must be one lowercase " +
                "SHA-256 digest.");
        }
        return value;
    }
}

internal sealed record AetherIdentityDatabaseReport(
    string Outcome,
    string Code,
    int TargetSchemaVersion,
    int? ExistingSchemaVersion,
    string DatabasePath,
    string PlanId,
    bool MutationRequired,
    bool MutationAttempted,
    bool DatabaseCreated,
    bool BackupRequired,
    bool RollbackAttempted,
    bool RollbackSucceeded);

internal static class AetherIdentityDatabaseConsole
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static async Task<int> ExecuteAsync(
        AetherIdentityDatabaseCommandLine commandLine,
        InstallationPaths paths,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(output);

        AetherIdentityDatabaseReport report = commandLine.Command switch
        {
            AetherIdentityDatabaseCommandKind.Plan =>
                await AetherIdentityDatabaseMigration.PlanAsync(
                    paths,
                    cancellationToken),
            AetherIdentityDatabaseCommandKind.Validate =>
                await AetherIdentityDatabaseMigration.ValidateAsync(
                    paths,
                    cancellationToken),
            AetherIdentityDatabaseCommandKind.Apply =>
                await AetherIdentityDatabaseMigration.ApplyAsync(
                    paths,
                    commandLine.ConfirmedPlanId,
                    cancellationToken),
            _ => throw new InvalidOperationException(
                "An identity database console command is required.")
        };

        await output.WriteLineAsync(
            JsonSerializer.Serialize(report, JsonOptions));
        return string.Equals(
                report.Outcome,
                "rejected",
                StringComparison.Ordinal)
            ? 2
            : 0;
    }
}

internal static class AetherIdentityDatabaseMigration
{
    private const string SchemaTable = "IdentitySchemaVersions";

    private static readonly string[] RequiredTables =
    [
        "AuthenticationSessions",
        "ExternalIdentities",
        "IdentityAuditRecords",
        "IdentityRoleClaims",
        "IdentityRoles",
        SchemaTable,
        "IdentityUserClaims",
        "IdentityUserLogins",
        "IdentityUserPasskeys",
        "IdentityUserRoles",
        "IdentityUsers",
        "IdentityUserTokens"
    ];

    internal static async Task<AetherIdentityDatabaseReport> PlanAsync(
        InstallationPaths paths,
        CancellationToken cancellationToken = default)
    {
        try
        {
            AetherIdentityDatabaseObservation observation =
                await ObserveAsync(paths, cancellationToken);
            return Report(
                observation.Exists ? "converged" : "planned",
                observation.Exists
                    ? "identity-schema-converged"
                    : "identity-schema-initialization-required",
                observation,
                mutationRequired: !observation.Exists,
                mutationAttempted: false,
                databaseCreated: false,
                rollbackAttempted: false,
                rollbackSucceeded: false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Rejected(paths, "identity-database-plan-rejected");
        }
    }

    internal static async Task<AetherIdentityDatabaseReport> ValidateAsync(
        InstallationPaths paths,
        CancellationToken cancellationToken = default)
    {
        try
        {
            AetherIdentityDatabaseObservation observation =
                await ObserveAsync(paths, cancellationToken);
            return Report(
                observation.Exists ? "converged" : "incomplete",
                observation.Exists
                    ? "identity-schema-converged"
                    : "identity-schema-not-initialized",
                observation,
                mutationRequired: !observation.Exists,
                mutationAttempted: false,
                databaseCreated: false,
                rollbackAttempted: false,
                rollbackSucceeded: false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Rejected(paths, "identity-database-validation-rejected");
        }
    }

    internal static Task<AetherIdentityDatabaseReport> ApplyAsync(
        InstallationPaths paths,
        string confirmedPlanId,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(
            paths,
            confirmedPlanId,
            static (context, token) =>
                context.Database.EnsureCreatedAsync(token),
            cancellationToken);

    internal static async Task<AetherIdentityDatabaseReport> ApplyAsync(
        InstallationPaths paths,
        string confirmedPlanId,
        Func<AetherIdentityDbContext, CancellationToken, Task<bool>> initialize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedPlanId);
        ArgumentNullException.ThrowIfNull(initialize);
        AetherIdentityDatabaseObservation observation;
        try
        {
            observation = await ObserveAsync(paths, cancellationToken);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Rejected(paths, "identity-database-apply-preflight-rejected");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(observation.PlanId),
                Encoding.ASCII.GetBytes(confirmedPlanId)))
        {
            return Report(
                "rejected",
                "identity-database-plan-mismatch",
                observation,
                mutationRequired: !observation.Exists,
                mutationAttempted: false,
                databaseCreated: false,
                rollbackAttempted: false,
                rollbackSucceeded: false);
        }

        if (observation.Exists)
        {
            return Report(
                "converged",
                "identity-schema-converged",
                observation,
                mutationRequired: false,
                mutationAttempted: false,
                databaseCreated: false,
                rollbackAttempted: false,
                rollbackSucceeded: false);
        }

        string databasePath = Path.GetFullPath(paths.IdentityDatabasePath);
        bool ownsDatabase = false;
        bool rollbackAttempted = false;
        bool rollbackSucceeded = false;
        try
        {
            PreparePrivateIdentityDirectory(paths.IdentityStoreDirectory);
            await using (FileStream reservation = new(
                databasePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous))
            {
                ownsDatabase = true;
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        databasePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                await reservation.FlushAsync(cancellationToken);
            }

            ServiceCollection services = new();
            services.AddAetherIdentityPersistence(paths);
            await using (ServiceProvider provider =
                services.BuildServiceProvider())
            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                AetherIdentityDbContext context =
                    scope.ServiceProvider
                        .GetRequiredService<AetherIdentityDbContext>();
                if (!await initialize(context, cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The reserved identity database was not initialized.");
                }
            }
            SqliteConnection.ClearAllPools();

            AetherIdentityDatabaseObservation completed =
                await ObserveAsync(paths, cancellationToken);
            return Report(
                "applied",
                "identity-schema-initialized",
                completed,
                mutationRequired: false,
                mutationAttempted: true,
                databaseCreated: true,
                rollbackAttempted: false,
                rollbackSucceeded: false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SqliteConnection.ClearAllPools();
            if (ownsDatabase)
            {
                rollbackAttempted = true;
                rollbackSucceeded = DeleteOwnedDatabase(databasePath);
            }
            return Report(
                "rejected",
                "identity-database-apply-failed",
                observation,
                mutationRequired: true,
                mutationAttempted: true,
                databaseCreated: ownsDatabase && !rollbackSucceeded,
                rollbackAttempted,
                rollbackSucceeded);
        }
    }

    private static async Task<AetherIdentityDatabaseObservation> ObserveAsync(
        InstallationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InstallationPaths.Validate(paths);
        string databasePath = Path.GetFullPath(paths.IdentityDatabasePath);
        string identityDirectory = Path.GetFullPath(
            paths.IdentityStoreDirectory);
        if (!string.Equals(
                Path.GetDirectoryName(databasePath),
                identityDirectory,
                PathComparison()))
        {
            throw new InvalidOperationException(
                "The identity database path escaped its dedicated directory.");
        }

        RejectReparsePoint(identityDirectory, allowMissing: true);
        if (!File.Exists(databasePath))
        {
            RejectDatabaseSidecars(databasePath);
            string missingPlan = ComputePlanId(
                databasePath,
                exists: false,
                existingSchemaVersion: null,
                digest: string.Empty);
            return new(
                databasePath,
                Exists: false,
                ExistingSchemaVersion: null,
                missingPlan);
        }

        RejectReparsePoint(databasePath, allowMissing: false);
        ValidatePrivatePermissions(identityDirectory, databasePath);
        RejectDatabaseSidecars(databasePath);
        int schemaVersion = await ValidateDatabaseAsync(
            databasePath,
            cancellationToken);
        string digest = await ComputeFileDigestAsync(
            databasePath,
            cancellationToken);
        string planId = ComputePlanId(
            databasePath,
            exists: true,
            schemaVersion,
            digest);
        return new(databasePath, Exists: true, schemaVersion, planId);
    }

    private static async Task<int> ValidateDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        };
        await using SqliteConnection connection =
            new(connectionString.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (SqliteCommand quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA quick_check;";
            object? result = await quickCheck.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(
                    Convert.ToString(result, CultureInfo.InvariantCulture),
                    "ok",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The identity database failed its integrity check.");
            }
        }

        HashSet<string> tables = new(StringComparer.Ordinal);
        await using (SqliteCommand tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table';";
            await using SqliteDataReader reader =
                await tableCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }
        if (RequiredTables.Any(table => !tables.Contains(table)))
        {
            throw new InvalidOperationException(
                "The identity database table inventory is incomplete.");
        }

        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText =
            "SELECT Version FROM IdentitySchemaVersions WHERE Id = 1;";
        object? versionResult =
            await versionCommand.ExecuteScalarAsync(cancellationToken);
        if (versionResult is not long version ||
            version != AetherIdentityDbContext.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                "The identity database schema version is unsupported.");
        }
        return checked((int)version);
    }

    private static async Task<string> ComputeFileDigestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(digest);
    }

    private static string ComputePlanId(
        string databasePath,
        bool exists,
        int? existingSchemaVersion,
        string digest)
    {
        string payload = string.Join(
            '\n',
            "aethersdr-identity-database-plan-v1",
            $"database={databasePath}",
            $"exists={exists.ToString(CultureInfo.InvariantCulture)}",
            $"existing-schema={existingSchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "none"}",
            $"target-schema={AetherIdentityDbContext.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)}",
            $"digest={digest}");
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static void PreparePrivateIdentityDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        RejectReparsePoint(fullPath, allowMissing: true);
        Directory.CreateDirectory(fullPath);
        RejectReparsePoint(fullPath, allowMissing: false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fullPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    private static void RejectReparsePoint(string path, bool allowMissing)
    {
        bool exists = File.Exists(path) || Directory.Exists(path);
        if (!exists)
        {
            if (allowMissing)
            {
                return;
            }
            throw new InvalidOperationException(
                "The identity database path is missing.");
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Identity database paths cannot be symbolic links or reparse points.");
        }
    }

    private static void ValidatePrivatePermissions(
        string identityDirectory,
        string databasePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode expectedDirectory =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute;
        UnixFileMode expectedDatabase =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite;
        if (File.GetUnixFileMode(identityDirectory) != expectedDirectory ||
            File.GetUnixFileMode(databasePath) != expectedDatabase)
        {
            throw new InvalidOperationException(
                "The identity database and its directory must be owner-only.");
        }
    }

    private static void RejectDatabaseSidecars(string databasePath)
    {
        if (File.Exists(databasePath + "-wal") ||
            File.Exists(databasePath + "-shm") ||
            File.Exists(databasePath + "-journal"))
        {
            throw new InvalidOperationException(
                "Identity database migration requires an offline database " +
                "without SQLite sidecar files.");
        }
    }

    private static bool DeleteOwnedDatabase(string databasePath)
    {
        try
        {
            string[] ownedPaths =
            [
                databasePath + "-wal",
                databasePath + "-shm",
                databasePath + "-journal",
                databasePath
            ];
            foreach (string path in ownedPaths)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            return ownedPaths.All(path => !File.Exists(path));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    private static AetherIdentityDatabaseReport Report(
        string outcome,
        string code,
        AetherIdentityDatabaseObservation observation,
        bool mutationRequired,
        bool mutationAttempted,
        bool databaseCreated,
        bool rollbackAttempted,
        bool rollbackSucceeded) =>
        new(
            outcome,
            code,
            AetherIdentityDbContext.CurrentSchemaVersion,
            observation.ExistingSchemaVersion,
            observation.DatabasePath,
            observation.PlanId,
            mutationRequired,
            mutationAttempted,
            databaseCreated,
            BackupRequired: false,
            rollbackAttempted,
            rollbackSucceeded);

    private static AetherIdentityDatabaseReport Rejected(
        InstallationPaths paths,
        string code) =>
        new(
            "rejected",
            code,
            AetherIdentityDbContext.CurrentSchemaVersion,
            ExistingSchemaVersion: null,
            Path.GetFullPath(paths.IdentityDatabasePath),
            PlanId: string.Empty,
            MutationRequired: false,
            MutationAttempted: false,
            DatabaseCreated: false,
            BackupRequired: false,
            RollbackAttempted: false,
            RollbackSucceeded: false);

    private static bool IsExpected(Exception exception) =>
        exception is InvalidOperationException or IOException or
            UnauthorizedAccessException or SqliteException or
            CryptographicException;

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record AetherIdentityDatabaseObservation(
        string DatabasePath,
        bool Exists,
        int? ExistingSchemaVersion,
        string PlanId);
}
