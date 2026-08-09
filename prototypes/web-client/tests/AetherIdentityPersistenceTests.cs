using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AetherSDR.Web.Tests;

public sealed class AetherIdentityPersistenceTests
{
    [Fact]
    public void RegistrationDoesNotCreateOrMigrateTheDatabase()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = InstallationPaths.Resolve(
            temporary.Path,
            InstallationPathLayout.Development);
        ServiceCollection services = new();
        services.AddAetherIdentityPersistence(paths);

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        AetherIdentityDbContext context =
            scope.ServiceProvider.GetRequiredService<AetherIdentityDbContext>();

        _ = context.Model;

        Assert.False(File.Exists(paths.IdentityDatabasePath));
        Assert.False(Directory.Exists(paths.IdentityStoreDirectory));
    }

    [Fact]
    public async Task SchemaPersistsExactLinksRevocableSessionsAndDurableAudit()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = InstallationPaths.Resolve(
            temporary.Path,
            InstallationPathLayout.Development);
        Directory.CreateDirectory(paths.IdentityStoreDirectory);
        ServiceCollection services = new();
        services.AddAetherIdentityPersistence(paths);

        Guid userId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        DateTimeOffset now =
            new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            AetherIdentityDbContext context =
                scope.ServiceProvider
                    .GetRequiredService<AetherIdentityDbContext>();
            Assert.True(await context.Database.EnsureCreatedAsync());

            context.Users.Add(User(userId, "operator"));
            context.ExternalIdentities.Add(new AetherExternalIdentity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ProviderId = "entra-primary",
                Issuer = "https://login.microsoftonline.com/tenant/v2.0",
                Subject = "external-subject",
                LinkedAtUtc = now
            });
            context.AuthenticationSessions.Add(
                new AetherAuthenticationSession
                {
                    Id = sessionId,
                    UserId = userId,
                    AuthenticationMethod =
                        AetherAuthenticationMethod.ExternalOpenIdConnect,
                    ProviderId = "entra-primary",
                    AuthorityVersion = 1,
                    CreatedAtUtc = now,
                    LastSeenAtUtc = now,
                    AbsoluteExpiresAtUtc = now.AddHours(8)
                });
            context.IdentityAuditRecords.Add(new AetherIdentityAuditRecord
            {
                OccurredAtUtc = now,
                ActorUserId = userId,
                SubjectUserId = userId,
                Action = "identity.user.created",
                Outcome = AetherIdentityAuditOutcome.Succeeded,
                CorrelationId = "correlation-a",
                DetailJson = "{}"
            });
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            AetherIdentityDbContext context =
                scope.ServiceProvider
                    .GetRequiredService<AetherIdentityDbContext>();
            AetherAuthenticationSession session =
                await context.AuthenticationSessions.SingleAsync();
            session.RevokedAtUtc = now.AddMinutes(5);
            session.RevocationReason = "administrator-reset";
            await context.SaveChangesAsync();

            context.Users.Remove(await context.Users.SingleAsync());
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            AetherIdentityDbContext context =
                scope.ServiceProvider
                    .GetRequiredService<AetherIdentityDbContext>();

            Assert.Empty(await context.ExternalIdentities.ToArrayAsync());
            Assert.Empty(await context.AuthenticationSessions.ToArrayAsync());
            AetherIdentityAuditRecord audit =
                Assert.Single(await context.IdentityAuditRecords.ToArrayAsync());
            Assert.Equal(userId, audit.ActorUserId);
            Assert.Equal("identity.user.created", audit.Action);
            Assert.Equal(
                [
                    AetherRoles.Admin,
                    AetherRoles.Control,
                    AetherRoles.Observe,
                    AetherRoles.Transmit
                ],
                await context.Roles
                    .OrderBy(role => role.Id)
                    .Select(role => role.Name!)
                    .OrderBy(name => name)
                    .ToArrayAsync());
        }
    }

    [Fact]
    public async Task ExternalIdentityKeyRequiresExactProviderIssuerAndSubject()
    {
        using TemporaryDirectory temporary = new();
        InstallationPaths paths = InstallationPaths.Resolve(
            temporary.Path,
            InstallationPathLayout.Development);
        Directory.CreateDirectory(paths.IdentityStoreDirectory);
        ServiceCollection services = new();
        services.AddAetherIdentityPersistence(paths);

        await using ServiceProvider provider = services.BuildServiceProvider();
        Guid firstUserId = Guid.NewGuid();
        Guid secondUserId = Guid.NewGuid();
        Guid thirdUserId = Guid.NewGuid();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            AetherIdentityDbContext context =
                scope.ServiceProvider
                    .GetRequiredService<AetherIdentityDbContext>();
            _ = await context.Database.EnsureCreatedAsync();
            context.Users.AddRange(
                User(firstUserId, "first"),
                User(secondUserId, "second"),
                User(thirdUserId, "third"));
            context.ExternalIdentities.Add(new AetherExternalIdentity
            {
                Id = Guid.NewGuid(),
                UserId = firstUserId,
                ProviderId = "oidc-primary",
                Issuer = "https://issuer-a.example",
                Subject = "subject-a",
                LinkedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            AetherIdentityDbContext context =
                scope.ServiceProvider
                    .GetRequiredService<AetherIdentityDbContext>();
            context.ExternalIdentities.Add(new AetherExternalIdentity
            {
                Id = Guid.NewGuid(),
                UserId = secondUserId,
                ProviderId = "oidc-primary",
                Issuer = "https://issuer-b.example",
                Subject = "subject-a",
                LinkedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            AetherIdentityDbContext context =
                scope.ServiceProvider
                    .GetRequiredService<AetherIdentityDbContext>();
            context.ExternalIdentities.Add(new AetherExternalIdentity
            {
                Id = Guid.NewGuid(),
                UserId = thirdUserId,
                ProviderId = "oidc-primary",
                Issuer = "https://issuer-a.example",
                Subject = "subject-a",
                LinkedAtUtc = DateTimeOffset.UtcNow
            });

            await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync());
        }
    }

    private static AetherIdentityUser User(Guid id, string name) =>
        new()
        {
            Id = id,
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            DisplayName = name,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            AuthorityVersion = 1,
            Enabled = true
        };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-identity-tests-{Guid.NewGuid():N}");
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
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
