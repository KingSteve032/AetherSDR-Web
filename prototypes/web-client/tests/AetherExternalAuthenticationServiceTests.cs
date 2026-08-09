using System.Security.Claims;
using System.Text.Json;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AetherSDR.Web.Tests;

public sealed class AetherExternalAuthenticationServiceTests
{
    [Fact]
    public async Task ExactLinkIssuesCanonicalSessionFromPersistedRolesOnly()
    {
        await using ExternalAuthenticationFixture fixture =
            await ExternalAuthenticationFixture.CreateAsync();
        Guid userId = await fixture.SeedLinkedUserAsync(
            "linked-subject",
            enabled: true,
            [AetherRoles.Observe, AetherRoles.Control]);

        AetherExternalAuthenticationResult result =
            await fixture.Service.AuthenticateAsync(
                fixture.Provider,
                fixture.ExternalPrincipal(
                    "linked-subject",
                    "shared@example.test",
                    [AetherRoles.Admin, AetherRoles.Transmit]),
                "correlation-success",
                TimeSpan.FromHours(8));

        Assert.True(result.Succeeded);
        Assert.Equal("external-identity-authenticated", result.Code);
        Assert.NotNull(result.SessionId);
        Assert.NotNull(result.Principal);
        Assert.Equal(
            userId.ToString("D"),
            result.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(result.Principal.IsInRole(AetherRoles.Observe));
        Assert.True(result.Principal.IsInRole(AetherRoles.Control));
        Assert.False(result.Principal.IsInRole(AetherRoles.Admin));
        Assert.False(result.Principal.IsInRole(AetherRoles.Transmit));
        Assert.DoesNotContain(
            result.Principal.Claims,
            claim => claim.Type is "roles" or "oid");

        AetherAuthenticationSession session =
            Assert.Single(await fixture.Database.AuthenticationSessions
                .ToArrayAsync());
        Assert.Equal(userId, session.UserId);
        Assert.Equal(
            fixture.Now.AddHours(8),
            session.AbsoluteExpiresAtUtc);
        Assert.Equal(
            fixture.Now.AddMinutes(-2),
            session.ReauthenticatedAtUtc);

        AetherIdentityAuditRecord audit =
            Assert.Single(await fixture.Database.IdentityAuditRecords
                .ToArrayAsync());
        Assert.Equal(AetherIdentityAuditOutcome.Succeeded, audit.Outcome);
        Assert.Equal(userId, audit.ActorUserId);
        Assert.Equal(userId, audit.SubjectUserId);
        Assert.DoesNotContain("linked-subject", audit.DetailJson);
        Assert.DoesNotContain("shared@example.test", audit.DetailJson);
        using JsonDocument details = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal(
            64,
            details.RootElement
                .GetProperty("subjectBinding")
                .GetString()!
                .Length);
    }

    [Fact]
    public async Task SharedEmailNeverLinksASecondExternalSubject()
    {
        await using ExternalAuthenticationFixture fixture =
            await ExternalAuthenticationFixture.CreateAsync();
        _ = await fixture.SeedLinkedUserAsync(
            "linked-subject",
            enabled: true,
            [AetherRoles.Observe]);

        AetherExternalAuthenticationResult result =
            await fixture.Service.AuthenticateAsync(
                fixture.Provider,
                fixture.ExternalPrincipal(
                    "unlinked-subject",
                    "shared@example.test",
                    [AetherRoles.Admin]),
                "correlation-unlinked",
                TimeSpan.FromHours(8));

        Assert.False(result.Succeeded);
        Assert.Equal("external-identity-unlinked", result.Code);
        Assert.Null(result.Principal);
        Assert.Null(result.SessionId);
        Assert.Equal(1, await fixture.Database.Users.CountAsync());
        Assert.Equal(
            1,
            await fixture.Database.ExternalIdentities.CountAsync());
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());

        AetherIdentityAuditRecord audit =
            Assert.Single(await fixture.Database.IdentityAuditRecords
                .ToArrayAsync());
        Assert.Equal(AetherIdentityAuditOutcome.Rejected, audit.Outcome);
        Assert.Null(audit.ActorUserId);
        Assert.Null(audit.SubjectUserId);
        Assert.DoesNotContain("unlinked-subject", audit.DetailJson);
        Assert.DoesNotContain("shared@example.test", audit.DetailJson);
    }

    [Fact]
    public async Task DisabledUserAndMalformedEvidenceFailWithoutSession()
    {
        await using ExternalAuthenticationFixture fixture =
            await ExternalAuthenticationFixture.CreateAsync();
        Guid disabledUserId = await fixture.SeedLinkedUserAsync(
            "disabled-subject",
            enabled: false,
            [AetherRoles.Admin]);

        AetherExternalAuthenticationResult disabled =
            await fixture.Service.AuthenticateAsync(
                fixture.Provider,
                fixture.ExternalPrincipal(
                    "disabled-subject",
                    "disabled@example.test",
                    [AetherRoles.Admin]),
                "correlation-disabled",
                TimeSpan.FromHours(8));
        ClaimsIdentity malformedIdentity = new(
            [
                new("iss", "https://identity.example/tenant"),
                new("sub", "first"),
                new("sub", "second"),
                new(ClaimTypes.Email, "disabled@example.test"),
                new(ClaimTypes.Role, AetherRoles.Admin)
            ],
            authenticationType: "oidc");
        AetherExternalAuthenticationResult malformed =
            await fixture.Service.AuthenticateAsync(
                fixture.Provider,
                new ClaimsPrincipal(malformedIdentity),
                "correlation-malformed",
                TimeSpan.FromHours(8));

        Assert.False(disabled.Succeeded);
        Assert.Equal("external-identity-user-disabled", disabled.Code);
        Assert.False(malformed.Succeeded);
        Assert.Equal(
            "external-identity-evidence-invalid",
            malformed.Code);
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());

        AetherIdentityAuditRecord[] audits =
            await fixture.Database.IdentityAuditRecords
                .OrderBy(record => record.Id)
                .ToArrayAsync();
        Assert.Equal(2, audits.Length);
        Assert.Equal(disabledUserId, audits[0].SubjectUserId);
        Assert.Null(audits[1].SubjectUserId);
        Assert.All(
            audits,
            audit => Assert.Equal(
                AetherIdentityAuditOutcome.Rejected,
                audit.Outcome));
    }

    private sealed class ExternalAuthenticationFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory temporary;
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private ExternalAuthenticationFixture(
            TemporaryDirectory temporary,
            ServiceProvider provider,
            AsyncServiceScope scope,
            AetherIdentityDbContext database,
            ManualTimeProvider time)
        {
            this.temporary = temporary;
            this.provider = provider;
            this.scope = scope;
            Database = database;
            Time = time;
            Service = new(database, time);
        }

        internal DateTimeOffset Now { get; } = DateTimeOffset.Parse(
            "2026-08-09T14:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        internal AetherIdentityDbContext Database { get; }

        internal ManualTimeProvider Time { get; }

        internal AetherExternalAuthenticationService Service { get; }

        internal AetherExternalProviderDescriptor Provider { get; } =
            new(
                "club-oidc",
                AetherExternalProviderKind.OpenIdConnect,
                new Uri("https://identity.example/tenant"),
                "aethersdr-web",
                "/signin-oidc",
                "/signout-callback-oidc");

        internal static async Task<ExternalAuthenticationFixture> CreateAsync()
        {
            TemporaryDirectory temporary = new();
            try
            {
                InstallationPaths paths = InstallationPaths.Resolve(
                    temporary.Path,
                    InstallationPathLayout.Development);
                AetherIdentityDatabaseReport plan =
                    await AetherIdentityDatabaseMigration.PlanAsync(paths);
                AetherIdentityDatabaseReport applied =
                    await AetherIdentityDatabaseMigration.ApplyAsync(
                        paths,
                        plan.PlanId);
                if (!string.Equals(
                        applied.Outcome,
                        "applied",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The identity test database could not be initialized.");
                }

                ServiceCollection services = new();
                services.AddAetherIdentityPersistence(paths);
                ServiceProvider provider = services.BuildServiceProvider();
                AsyncServiceScope scope = provider.CreateAsyncScope();
                AetherIdentityDbContext database =
                    scope.ServiceProvider
                        .GetRequiredService<AetherIdentityDbContext>();
                ManualTimeProvider time = new(
                    DateTimeOffset.Parse(
                        "2026-08-09T14:00:00Z",
                        System.Globalization.CultureInfo.InvariantCulture));
                return new(temporary, provider, scope, database, time);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        internal ClaimsPrincipal ExternalPrincipal(
            string subject,
            string email,
            string[] externalRoles)
        {
            List<Claim> claims =
            [
                new("iss", "https://identity.example/tenant"),
                new("sub", subject),
                new("oid", "external-object-id"),
                new("name", "Claimed Display Name"),
                new("preferred_username", email),
                new(
                    "auth_time",
                    Now.AddMinutes(-2)
                        .ToUnixTimeSeconds()
                        .ToString(
                            System.Globalization.CultureInfo.InvariantCulture))
            ];
            claims.AddRange(
                externalRoles.Select(role => new Claim("roles", role)));
            claims.AddRange(
                externalRoles.Select(
                    role => new Claim(ClaimTypes.Role, role)));
            return new(
                new ClaimsIdentity(
                    claims,
                    authenticationType: "oidc"));
        }

        internal async Task<Guid> SeedLinkedUserAsync(
            string subject,
            bool enabled,
            string[] roles)
        {
            Guid userId = Guid.NewGuid();
            AetherIdentityUser user = new()
            {
                Id = userId,
                UserName = $"user-{userId:N}",
                NormalizedUserName =
                    $"USER-{userId:N}".ToUpperInvariant(),
                DisplayName = "Persisted Operator",
                Email = "shared@example.test",
                NormalizedEmail = "SHARED@EXAMPLE.TEST",
                EmailConfirmed = true,
                Enabled = enabled,
                AuthorityVersion = 3,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };
            Database.Users.Add(user);
            Database.ExternalIdentities.Add(new AetherExternalIdentity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                User = user,
                ProviderId = Provider.ProviderId,
                Issuer = "https://identity.example/tenant",
                Subject = subject,
                LinkedAtUtc = Now.AddDays(-1)
            });
            foreach (string roleName in roles)
            {
                Guid roleId = await Database.Roles
                    .Where(role => role.Name == roleName)
                    .Select(role => role.Id)
                    .SingleAsync();
                Database.Set<IdentityUserRole<Guid>>().Add(
                    new IdentityUserRole<Guid>
                    {
                        UserId = userId,
                        RoleId = roleId
                    });
            }
            await Database.SaveChangesAsync();
            return userId;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
            temporary.Dispose();
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-external-auth-tests-{Guid.NewGuid():N}");
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

        internal string Path { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
