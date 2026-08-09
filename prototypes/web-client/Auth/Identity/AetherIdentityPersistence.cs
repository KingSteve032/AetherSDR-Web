using AetherSDR.Web.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AetherSDR.Web.Auth.Identity;

internal static class AetherIdentityPersistence
{
    internal static IServiceCollection AddAetherIdentityPersistence(
        this IServiceCollection services,
        InstallationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        InstallationPaths.Validate(paths);

        string databasePath = Path.GetFullPath(paths.IdentityDatabasePath);
        string expectedDirectory = Path.GetFullPath(
            paths.IdentityStoreDirectory);
        string? actualDirectory = Path.GetDirectoryName(databasePath);
        if (!string.Equals(
                actualDirectory,
                expectedDirectory,
                PathComparison()))
        {
            throw new InvalidOperationException(
                "The identity database must remain inside its dedicated " +
                "installation identity directory.");
        }

        SqliteConnectionStringBuilder connection = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true
        };
        services.AddDbContext<AetherIdentityDbContext>(options =>
            options.UseSqlite(connection.ConnectionString));
        return services;
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
