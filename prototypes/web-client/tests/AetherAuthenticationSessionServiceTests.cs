using System.Security.Claims;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AetherSDR.Web.Tests;

public sealed class AetherAuthenticationSessionServiceTests
{
    [Fact]
    public async Task CurrentSessionRebuildsPrincipalAndBoundsLastSeenWrites()
    {
        await using SessionFixture fixture = await SessionFixture.CreateAsync();

        AetherAuthenticationSessionValidationResult first =
            await fixture.Service.ValidateAsync(fixture.Principal);
        DateTimeOffset firstLastSeen =
            (await fixture.Database.AuthenticationSessions.SingleAsync())
                .LastSeenAtUtc;
        AetherAuthenticationSessionValidationResult second =
            await fixture.Service.ValidateAsync(first.Principal!);

        Assert.True(first.Succeeded);
        Assert.Equal("canonical-session-current", first.Code);
        Assert.NotNull(first.Principal);
        Assert.True(first.Principal.IsInRole(AetherRoles.Observe));
        Assert.False(first.Principal.IsInRole(AetherRoles.Admin));
        Assert.Equal(fixture.Now, firstLastSeen);
        Assert.True(second.Succeeded);
        Assert.Equal(
            firstLastSeen,
            (await fixture.Database.AuthenticationSessions.SingleAsync())
                .LastSeenAtUtc);
    }

    [Fact]
    public async Task AuthorityChangeOrRevocationInvalidatesCookieAuthority()
    {
        await using SessionFixture fixture = await SessionFixture.CreateAsync();

        fixture.User.AuthorityVersion++;
        await fixture.Database.SaveChangesAsync();
        AetherAuthenticationSessionValidationResult stale =
            await fixture.Service.ValidateAsync(fixture.Principal);

        Assert.False(stale.Succeeded);
        Assert.Equal("canonical-session-authority-stale", stale.Code);
        Assert.Null(stale.Principal);

        fixture.User.AuthorityVersion = fixture.Session.AuthorityVersion;
        fixture.Session.RevokedAtUtc = fixture.Now;
        fixture.Session.RevocationReason = "administrator-reset";
        await fixture.Database.SaveChangesAsync();
        AetherAuthenticationSessionValidationResult revoked =
            await fixture.Service.ValidateAsync(fixture.Principal);

        Assert.False(revoked.Succeeded);
        Assert.Equal("canonical-session-inactive", revoked.Code);
        Assert.Null(revoked.Principal);
    }

    [Fact]
    public async Task LogoutRevokesOnlyTheExactCanonicalSessionAndAuditsOnce()
    {
        await using SessionFixture fixture = await SessionFixture.CreateAsync();
        ClaimsPrincipal malformed = new(
            new ClaimsIdentity(
                [
                    new(
                        ClaimTypes.NameIdentifier,
                        fixture.User.Id.ToString("D")),
                    new(
                        AetherIdentityClaimTypes.SessionId,
                        Guid.NewGuid().ToString("D"))
                ],
                AetherCanonicalPrincipalFactory.AuthenticationType));
        ClaimsPrincipal stitched = new(
            [
                new ClaimsIdentity(
                    [
                        new(
                            ClaimTypes.NameIdentifier,
                            fixture.User.Id.ToString("D")),
                        new(
                            AetherIdentityClaimTypes.SessionId,
                            fixture.Session.Id.ToString("D"))
                    ],
                    AetherCanonicalPrincipalFactory.AuthenticationType),
                new ClaimsIdentity(
                    [
                        new(
                            AetherIdentityClaimTypes.AuthorityVersion,
                            fixture.User.AuthorityVersion.ToString(
                                System.Globalization.CultureInfo
                                    .InvariantCulture))
                    ],
                    "UntrustedSecondaryIdentity")
            ]);

        AetherAuthenticationSessionRevocationResult rejected =
            await fixture.Service.RevokeAsync(
                malformed,
                "user-logout");
        AetherAuthenticationSessionValidationResult stitchedRejected =
            await fixture.Service.ValidateAsync(stitched);
        AetherAuthenticationSessionRevocationResult revoked =
            await fixture.Service.RevokeAsync(
                fixture.Principal,
                "user-logout");
        AetherAuthenticationSessionRevocationResult converged =
            await fixture.Service.RevokeAsync(
                fixture.Principal,
                "user-logout");

        Assert.False(rejected.Succeeded);
        Assert.False(rejected.MutationAttempted);
        Assert.False(stitchedRejected.Succeeded);
        Assert.Equal(
            "canonical-session-claims-invalid",
            stitchedRejected.Code);
        Assert.True(revoked.Succeeded);
        Assert.True(revoked.MutationAttempted);
        Assert.Equal("canonical-session-revoked", revoked.Code);
        Assert.True(converged.Succeeded);
        Assert.False(converged.MutationAttempted);
        Assert.Equal(
            "canonical-session-already-revoked",
            converged.Code);

        AetherAuthenticationSession persisted =
            await fixture.Database.AuthenticationSessions.SingleAsync();
        Assert.Equal(fixture.Now, persisted.RevokedAtUtc);
        Assert.Equal("user-logout", persisted.RevocationReason);
        AetherIdentityAuditRecord audit =
            Assert.Single(await fixture.Database.IdentityAuditRecords
                .ToArrayAsync());
        Assert.Equal(
            "authentication.session.revoked",
            audit.Action);
        Assert.Equal(AetherIdentityAuditOutcome.Succeeded, audit.Outcome);
        Assert.Equal(fixture.User.Id, audit.ActorUserId);
        Assert.Equal(fixture.User.Id, audit.SubjectUserId);
    }

    private sealed class SessionFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory temporary;
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private SessionFixture(
            TemporaryDirectory temporary,
            ServiceProvider provider,
            AsyncServiceScope scope,
            AetherIdentityDbContext database,
            ManualTimeProvider time,
            AetherIdentityUser user,
            AetherAuthenticationSession session,
            ClaimsPrincipal principal)
        {
            this.temporary = temporary;
            this.provider = provider;
            this.scope = scope;
            Database = database;
            Time = time;
            User = user;
            Session = session;
            Principal = principal;
            Service = new(database, time);
        }

        internal DateTimeOffset Now { get; } = DateTimeOffset.Parse(
            "2026-08-09T15:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        internal AetherIdentityDbContext Database { get; }

        internal ManualTimeProvider Time { get; }

        internal AetherIdentityUser User { get; }

        internal AetherAuthenticationSession Session { get; }

        internal ClaimsPrincipal Principal { get; }

        internal AetherAuthenticationSessionService Service { get; }

        internal static async Task<SessionFixture> CreateAsync()
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
                        "The session test database could not be initialized.");
                }

                ServiceCollection services = new();
                services.AddAetherIdentityPersistence(paths);
                ServiceProvider provider = services.BuildServiceProvider();
                AsyncServiceScope scope = provider.CreateAsyncScope();
                AetherIdentityDbContext database =
                    scope.ServiceProvider
                        .GetRequiredService<AetherIdentityDbContext>();
                DateTimeOffset now = DateTimeOffset.Parse(
                    "2026-08-09T15:00:00Z",
                    System.Globalization.CultureInfo.InvariantCulture);
                ManualTimeProvider time = new(now);
                AetherIdentityUser user = new()
                {
                    Id = Guid.NewGuid(),
                    UserName = "session-operator",
                    NormalizedUserName = "SESSION-OPERATOR",
                    DisplayName = "Session Operator",
                    Email = "session@example.test",
                    NormalizedEmail = "SESSION@EXAMPLE.TEST",
                    EmailConfirmed = true,
                    Enabled = true,
                    AuthorityVersion = 4,
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                };
                Guid observeRoleId = await database.Roles
                    .Where(role => role.Name == AetherRoles.Observe)
                    .Select(role => role.Id)
                    .SingleAsync();
                AetherAuthenticationSession session = new()
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    User = user,
                    AuthenticationMethod =
                        AetherAuthenticationMethod
                            .ExternalOpenIdConnect,
                    ProviderId = "club-oidc",
                    AuthorityVersion = user.AuthorityVersion,
                    CreatedAtUtc = now.AddHours(-1),
                    LastSeenAtUtc = now.AddMinutes(-10),
                    AbsoluteExpiresAtUtc = now.AddHours(7),
                    ReauthenticatedAtUtc = now.AddMinutes(-5)
                };
                database.Users.Add(user);
                database.Set<IdentityUserRole<Guid>>().Add(
                    new IdentityUserRole<Guid>
                    {
                        UserId = user.Id,
                        RoleId = observeRoleId
                    });
                database.AuthenticationSessions.Add(session);
                await database.SaveChangesAsync();
                ClaimsPrincipal principal =
                    AetherCanonicalPrincipalFactory.Create(
                        user,
                        session,
                        [AetherRoles.Observe],
                        now);
                return new(
                    temporary,
                    provider,
                    scope,
                    database,
                    time,
                    user,
                    session,
                    principal);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
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
                $"aethersdr-session-tests-{Guid.NewGuid():N}");
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
