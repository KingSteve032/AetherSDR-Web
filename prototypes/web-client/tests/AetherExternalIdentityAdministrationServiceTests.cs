using System.Globalization;
using System.Security.Claims;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AetherSDR.Web.Tests;

public sealed class AetherExternalIdentityAdministrationServiceTests
{
    [Fact]
    public async Task LinkBindsVerifiedSubjectRotatesAuthorityAndRevokesSessions()
    {
        await using ExternalIdentityFixture fixture =
            await ExternalIdentityFixture.CreateAsync();
        AetherIdentityUser target = await fixture.AddUserAsync(
            "linked-operator",
            hasLocalMethod: true);
        AetherAuthenticationSession targetSession =
            await fixture.AddSessionAsync(target);

        AetherExternalIdentityLinkAuthorization authorization =
            await fixture.Service.AuthorizeLinkAsync(
                fixture.AdministratorPrincipal,
                target.Id);
        await Assert.ThrowsAsync<
            AetherAdministratorReauthenticationRequiredException>(
            () => fixture.Service.LinkAsync(
                fixture.AdministratorPrincipal,
                authorization.ActorUserId,
                Guid.NewGuid(),
                target.Id,
                fixture.ProviderDescriptor,
                fixture.ExternalPrincipal("verified-subject"),
                "external-link-binding-rejected"));

        AetherExternalIdentityMutationResult linked =
            await fixture.Service.LinkAsync(
                fixture.AdministratorPrincipal,
                authorization.ActorUserId,
                authorization.ActorSessionId,
                target.Id,
                fixture.ProviderDescriptor,
                fixture.ExternalPrincipal("verified-subject"),
                "external-link");
        AetherExternalIdentityMutationResult converged =
            await fixture.Service.LinkAsync(
                fixture.AdministratorPrincipal,
                authorization.ActorUserId,
                authorization.ActorSessionId,
                target.Id,
                fixture.ProviderDescriptor,
                fixture.ExternalPrincipal("verified-subject"),
                "external-link-converged");

        Assert.True(linked.Succeeded);
        Assert.True(linked.MutationAttempted);
        Assert.Equal(3, linked.AuthorityVersion);
        Assert.Equal(1, linked.RevokedSessionCount);
        Assert.True(converged.Succeeded);
        Assert.False(converged.MutationAttempted);
        Assert.Equal(3, converged.AuthorityVersion);

        AetherExternalIdentity stored =
            await fixture.Database.ExternalIdentities.SingleAsync();
        Assert.Equal(target.Id, stored.UserId);
        Assert.Equal("club-oidc", stored.ProviderId);
        Assert.Equal(
            "https://identity.example/tenant",
            stored.Issuer);
        Assert.Equal("verified-subject", stored.Subject);

        AetherAuthenticationSession revoked =
            await fixture.Database.AuthenticationSessions.SingleAsync(
                session => session.Id == targetSession.Id);
        Assert.NotNull(revoked.RevokedAtUtc);
        Assert.Equal(
            "administrator-external-identity-link",
            revoked.RevocationReason);

        AetherIdentityAuditRecord[] audits =
            await fixture.Database.IdentityAuditRecords
                .Where(record =>
                    record.Action == "identity.external-identity.linked")
                .OrderBy(record => record.Id)
                .ToArrayAsync();
        Assert.Equal(2, audits.Length);
        Assert.All(
            audits,
            audit => Assert.DoesNotContain(
                "verified-subject",
                audit.DetailJson,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LinkRejectsSubjectAndUserProviderConflictsWithoutAuthorityMutation()
    {
        await using ExternalIdentityFixture fixture =
            await ExternalIdentityFixture.CreateAsync();
        AetherIdentityUser first = await fixture.AddUserAsync(
            "first-operator",
            hasLocalMethod: true);
        AetherIdentityUser second = await fixture.AddUserAsync(
            "second-operator",
            hasLocalMethod: true);
        AetherExternalIdentityLinkAuthorization firstAuthorization =
            await fixture.Service.AuthorizeLinkAsync(
                fixture.AdministratorPrincipal,
                first.Id);
        _ = await fixture.Service.LinkAsync(
            fixture.AdministratorPrincipal,
            firstAuthorization.ActorUserId,
            firstAuthorization.ActorSessionId,
            first.Id,
            fixture.ProviderDescriptor,
            fixture.ExternalPrincipal("shared-subject"),
            "external-link-first");

        AetherExternalIdentityLinkAuthorization secondAuthorization =
            await fixture.Service.AuthorizeLinkAsync(
                fixture.AdministratorPrincipal,
                second.Id);
        AetherExternalIdentityMutationResult conflict =
            await fixture.Service.LinkAsync(
                fixture.AdministratorPrincipal,
                secondAuthorization.ActorUserId,
                secondAuthorization.ActorSessionId,
                second.Id,
                fixture.ProviderDescriptor,
                fixture.ExternalPrincipal("shared-subject"),
                "external-link-conflict");

        Assert.False(conflict.Succeeded);
        Assert.False(conflict.MutationAttempted);
        Assert.Equal(2, conflict.AuthorityVersion);
        Assert.Single(await fixture.Database.ExternalIdentities.ToArrayAsync());
        AetherIdentityAuditRecord rejected =
            await fixture.Database.IdentityAuditRecords.SingleAsync(
                record =>
                    record.CorrelationId == "external-link-conflict");
        Assert.Equal(AetherIdentityAuditOutcome.Rejected, rejected.Outcome);
        Assert.DoesNotContain(
            "shared-subject",
            rejected.DetailJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnlinkProtectsLastMethodThenAllowsUsableLocalRecovery()
    {
        await using ExternalIdentityFixture fixture =
            await ExternalIdentityFixture.CreateAsync();
        AetherIdentityUser target = await fixture.AddUserAsync(
            "external-only-operator",
            hasLocalMethod: false);
        AetherAuthenticationSession targetSession =
            await fixture.AddSessionAsync(
                target,
                AetherAuthenticationMethod.ExternalOpenIdConnect);
        await fixture.AddExternalIdentityAsync(
            target,
            "only-subject");

        AetherExternalIdentityMutationResult protectedResult =
            await fixture.Service.UnlinkAsync(
                fixture.AdministratorPrincipal,
                target.Id,
                fixture.ProviderDescriptor.ProviderId,
                "external-unlink-protected");

        Assert.False(protectedResult.Succeeded);
        Assert.Equal(
            "external-identity-last-sign-in-method-protected",
            protectedResult.Code);
        Assert.False(protectedResult.MutationAttempted);
        Assert.Single(await fixture.Database.ExternalIdentities.ToArrayAsync());
        Assert.Null(targetSession.RevokedAtUtc);

        target.PasswordHash = "bounded-test-password-hash";
        target.TwoFactorEnabled = true;
        target.LockoutEnabled = true;
        fixture.Database.Set<IdentityUserToken<Guid>>().Add(
            AetherLocalMfaCredentialProtector
                .GenerateRecoveryCredential(target.Id)
                .Token);
        await fixture.Database.SaveChangesAsync();

        AetherExternalIdentityMutationResult unlinked =
            await fixture.Service.UnlinkAsync(
                fixture.AdministratorPrincipal,
                target.Id,
                fixture.ProviderDescriptor.ProviderId,
                "external-unlink");

        Assert.True(unlinked.Succeeded);
        Assert.True(unlinked.MutationAttempted);
        Assert.Equal(3, unlinked.AuthorityVersion);
        Assert.Equal(1, unlinked.RevokedSessionCount);
        Assert.Empty(await fixture.Database.ExternalIdentities.ToArrayAsync());
        Assert.NotNull(targetSession.RevokedAtUtc);
        Assert.Equal(
            "administrator-external-identity-unlink",
            targetSession.RevocationReason);
    }

    [Fact]
    public async Task ExternalOnlyModeDoesNotTreatDisabledLocalLoginAsFallback()
    {
        await using ExternalIdentityFixture fixture =
            await ExternalIdentityFixture.CreateAsync();
        AetherIdentityUser target = await fixture.AddUserAsync(
            "configured-external-only",
            hasLocalMethod: true);
        await fixture.AddExternalIdentityAsync(target, "external-subject");
        AetherAuthenticationTopology externalOnly =
            AetherAuthenticationConfiguration.Validate(
                new AuthSettings
                {
                    Mode = "OpenIdConnect",
                    ProviderId = "club-oidc",
                    Authority = "https://identity.example/tenant",
                    ClientId = "aethersdr-web",
                    ClientSecret = "test-secret"
                },
                isDevelopmentEnvironment: false);
        AetherExternalIdentityAdministrationService service = new(
            fixture.Database,
            externalOnly,
            fixture.CredentialProtector,
            fixture.AdministrationLock,
            fixture.Time);

        AetherExternalIdentityMutationResult result =
            await service.UnlinkAsync(
                fixture.AdministratorPrincipal,
                target.Id,
                fixture.ProviderDescriptor.ProviderId,
                "external-only-unlink");

        Assert.False(result.Succeeded);
        Assert.Equal(
            "external-identity-last-sign-in-method-protected",
            result.Code);
        Assert.Single(await fixture.Database.ExternalIdentities.ToArrayAsync());
    }

    [Fact]
    public async Task OidcLinkCallbackUsesCurrentAdminAndNeverSignsInLinkedSubject()
    {
        await using ExternalIdentityFixture fixture =
            await ExternalIdentityFixture.CreateAsync();
        AetherIdentityUser target = await fixture.AddUserAsync(
            "callback-target",
            hasLocalMethod: true);
        AetherExternalIdentityLinkAuthorization authorization =
            await fixture.Service.AuthorizeLinkAsync(
                fixture.AdministratorPrincipal,
                target.Id);
        AuthenticationProperties properties = new()
        {
            RedirectUri = "/admin.html"
        };
        properties.Items[
            AetherOpenIdConnectEvents.ExternalIdentityLinkItem] =
            AetherOpenIdConnectEvents.RequiredValue;
        properties.Items[
            AetherOpenIdConnectEvents.LinkActorUserIdItem] =
            authorization.ActorUserId.ToString("D");
        properties.Items[
            AetherOpenIdConnectEvents.LinkActorSessionIdItem] =
            authorization.ActorSessionId.ToString("D");
        properties.Items[
            AetherOpenIdConnectEvents.LinkTargetUserIdItem] =
            authorization.TargetUserId.ToString("D");
        properties.Items[
            AetherOpenIdConnectEvents.LinkProviderIdItem] =
            authorization.ProviderId;

        ServiceProvider callbackServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(
                new FixedAuthenticationService(
                    fixture.AdministratorPrincipal))
            .BuildServiceProvider();
        try
        {
            DefaultHttpContext httpContext = new()
            {
                RequestServices = callbackServices,
                TraceIdentifier = "oidc-link-callback"
            };
            AetherOpenIdConnectEvents events = new(
                new(
                    fixture.Database,
                    fixture.Topology.LocalPolicy,
                    fixture.Time),
                fixture.Service,
                new(fixture.Database, fixture.Time),
                fixture.Topology);
            TokenValidatedContext context = new(
                httpContext,
                new(
                    OpenIdConnectDefaults.AuthenticationScheme,
                    displayName: null,
                    typeof(OpenIdConnectHandler)),
                new OpenIdConnectOptions(),
                fixture.ExternalPrincipal("callback-subject"),
                properties);

            await events.TokenValidated(context);

            Assert.True(context.Result?.Handled);
            Assert.Equal(
                StatusCodes.Status302Found,
                httpContext.Response.StatusCode);
            Assert.Equal(
                "/admin.html?externalIdentityLink=external-identity-linked",
                httpContext.Response.Headers.Location);
            Assert.Equal(
                "no-store, max-age=0",
                httpContext.Response.Headers.CacheControl);
            Assert.Single(
                await fixture.Database.ExternalIdentities.ToArrayAsync());
            Assert.Single(
                await fixture.Database.AuthenticationSessions.ToArrayAsync());
            Assert.Equal(
                "callback-subject",
                (await fixture.Database.ExternalIdentities.SingleAsync())
                    .Subject);
        }
        finally
        {
            await callbackServices.DisposeAsync();
        }
    }

    [Fact]
    public async Task LinkAuthorizationRequiresFreshAdministrator()
    {
        await using ExternalIdentityFixture fixture =
            await ExternalIdentityFixture.CreateAsync();
        AetherIdentityUser target = await fixture.AddUserAsync(
            "freshness-target",
            hasLocalMethod: true);
        fixture.Time.Advance(
            fixture.Topology.LocalPolicy
                .AdministratorReauthenticationLifetime +
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<
            AetherAdministratorReauthenticationRequiredException>(
            () => fixture.Service.AuthorizeLinkAsync(
                fixture.AdministratorPrincipal,
                target.Id));
    }

    private sealed class FixedAuthenticationService(
        ClaimsPrincipal principal) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(
            HttpContext context,
            string? scheme) =>
            Task.FromResult(
                AuthenticateResult.Success(
                    new(
                        principal,
                        new AuthenticationProperties(),
                        CookieAuthenticationDefaults.AuthenticationScheme)));

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal signedInPrincipal,
            AuthenticationProperties? properties) =>
            throw new InvalidOperationException(
                "OIDC identity linking must not sign in the linked subject.");

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) =>
            Task.CompletedTask;
    }

    private sealed class ExternalIdentityFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory temporary;
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private ExternalIdentityFixture(
            TemporaryDirectory temporary,
            ServiceProvider provider,
            AsyncServiceScope scope,
            AetherIdentityDbContext database,
            AetherExternalIdentityAdministrationService service,
            AetherLocalMfaCredentialProtector credentialProtector,
            AetherIdentityAdministrationLock administrationLock,
            AetherAuthenticationTopology topology,
            ManualTimeProvider time,
            ClaimsPrincipal administratorPrincipal)
        {
            this.temporary = temporary;
            this.provider = provider;
            this.scope = scope;
            Database = database;
            Service = service;
            CredentialProtector = credentialProtector;
            AdministrationLock = administrationLock;
            Topology = topology;
            Time = time;
            AdministratorPrincipal = administratorPrincipal;
        }

        internal AetherIdentityDbContext Database { get; }

        internal AetherExternalIdentityAdministrationService Service { get; }

        internal AetherLocalMfaCredentialProtector CredentialProtector { get; }

        internal AetherIdentityAdministrationLock AdministrationLock { get; }

        internal AetherAuthenticationTopology Topology { get; }

        internal AetherExternalProviderDescriptor ProviderDescriptor =>
            Topology.ExternalProvider!;

        internal ManualTimeProvider Time { get; }

        internal ClaimsPrincipal AdministratorPrincipal { get; }

        internal static async Task<ExternalIdentityFixture> CreateAsync()
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
                        "The external identity test database failed.");
                }

                AetherAuthenticationTopology topology =
                    AetherAuthenticationConfiguration.Validate(
                        new AuthSettings
                        {
                            Mode = "Combined",
                            ProviderId = "club-oidc",
                            ProviderType = "OpenIdConnect",
                            Authority =
                                "https://identity.example/tenant",
                            ClientId = "aethersdr-web",
                            ClientSecret = "test-secret"
                        },
                        isDevelopmentEnvironment: false);
                ManualTimeProvider time = new(
                    DateTimeOffset.Parse(
                        "2026-08-11T12:00:00Z",
                        CultureInfo.InvariantCulture));
                ServiceCollection services = new();
                services.AddAetherIdentityPersistence(paths);
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<IDataProtectionProvider>(
                    new EphemeralDataProtectionProvider());
                services.AddSingleton(topology);
                services.AddAetherLocalAuthenticationFoundation(
                    topology.LocalPolicy);
                ServiceProvider provider = services.BuildServiceProvider();
                AsyncServiceScope scope = provider.CreateAsyncScope();
                IServiceProvider scoped = scope.ServiceProvider;
                AetherIdentityDbContext database =
                    scoped.GetRequiredService<AetherIdentityDbContext>();

                Guid adminRoleId = await database.Roles
                    .Where(role => role.Name == AetherRoles.Admin)
                    .Select(role => role.Id)
                    .SingleAsync();
                Guid observeRoleId = await database.Roles
                    .Where(role => role.Name == AetherRoles.Observe)
                    .Select(role => role.Id)
                    .SingleAsync();
                AetherIdentityUser administrator = User(
                    "administrator",
                    authorityVersion: 4);
                administrator.PasswordHash = "administrator-test-hash";
                administrator.TwoFactorEnabled = true;
                database.Users.Add(administrator);
                database.Set<IdentityUserRole<Guid>>().AddRange(
                    new IdentityUserRole<Guid>
                    {
                        UserId = administrator.Id,
                        RoleId = adminRoleId
                    },
                    new IdentityUserRole<Guid>
                    {
                        UserId = administrator.Id,
                        RoleId = observeRoleId
                    });
                AetherAuthenticationSession administratorSession = new()
                {
                    Id = Guid.NewGuid(),
                    UserId = administrator.Id,
                    User = administrator,
                    AuthenticationMethod =
                        AetherAuthenticationMethod.LocalPasswordWithTotp,
                    AuthorityVersion = administrator.AuthorityVersion,
                    CreatedAtUtc = time.GetUtcNow().AddMinutes(-1),
                    LastSeenAtUtc = time.GetUtcNow(),
                    AbsoluteExpiresAtUtc = time.GetUtcNow().AddHours(8),
                    ReauthenticatedAtUtc = time.GetUtcNow()
                };
                database.AuthenticationSessions.Add(administratorSession);
                await database.SaveChangesAsync();
                ClaimsPrincipal administratorPrincipal =
                    AetherCanonicalPrincipalFactory.Create(
                        administrator,
                        administratorSession,
                        [AetherRoles.Admin, AetherRoles.Observe],
                        time.GetUtcNow());
                return new(
                    temporary,
                    provider,
                    scope,
                    database,
                    scoped.GetRequiredService<
                        AetherExternalIdentityAdministrationService>(),
                    scoped.GetRequiredService<
                        AetherLocalMfaCredentialProtector>(),
                    scoped.GetRequiredService<
                        AetherIdentityAdministrationLock>(),
                    topology,
                    time,
                    administratorPrincipal);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        internal async Task<AetherIdentityUser> AddUserAsync(
            string userName,
            bool hasLocalMethod)
        {
            AetherIdentityUser user = User(userName, authorityVersion: 2);
            if (hasLocalMethod)
            {
                user.PasswordHash = "bounded-test-password-hash";
                user.TwoFactorEnabled = true;
                Database.Set<IdentityUserToken<Guid>>().Add(
                    new()
                    {
                        UserId = user.Id,
                        LoginProvider =
                            AetherLocalMfaCredentialProtector.LoginProvider,
                        Name =
                            AetherLocalMfaCredentialProtector.TotpSecretName,
                        Value = "protected-test-value"
                    });
            }
            Database.Users.Add(user);
            Guid observeRoleId = await Database.Roles
                .Where(role => role.Name == AetherRoles.Observe)
                .Select(role => role.Id)
                .SingleAsync();
            Database.Set<IdentityUserRole<Guid>>().Add(
                new()
                {
                    UserId = user.Id,
                    RoleId = observeRoleId
                });
            await Database.SaveChangesAsync();
            return user;
        }

        internal async Task<AetherAuthenticationSession> AddSessionAsync(
            AetherIdentityUser user,
            AetherAuthenticationMethod method =
                AetherAuthenticationMethod.LocalPasswordWithTotp)
        {
            AetherAuthenticationSession session = new()
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                User = user,
                AuthenticationMethod = method,
                ProviderId =
                    method == AetherAuthenticationMethod.ExternalOpenIdConnect
                        ? ProviderDescriptor.ProviderId
                        : null,
                AuthorityVersion = user.AuthorityVersion,
                CreatedAtUtc = Time.GetUtcNow().AddMinutes(-1),
                LastSeenAtUtc = Time.GetUtcNow(),
                AbsoluteExpiresAtUtc = Time.GetUtcNow().AddHours(8),
                ReauthenticatedAtUtc = Time.GetUtcNow()
            };
            Database.AuthenticationSessions.Add(session);
            await Database.SaveChangesAsync();
            return session;
        }

        internal async Task AddExternalIdentityAsync(
            AetherIdentityUser user,
            string subject)
        {
            Database.ExternalIdentities.Add(
                new()
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    User = user,
                    ProviderId = ProviderDescriptor.ProviderId,
                    Issuer = "https://identity.example/tenant",
                    Subject = subject,
                    LinkedAtUtc = Time.GetUtcNow()
                });
            await Database.SaveChangesAsync();
        }

        internal ClaimsPrincipal ExternalPrincipal(
            string subject,
            int authenticationAgeMinutes = 1) =>
            new(
                new ClaimsIdentity(
                    [
                        new("iss", "https://identity.example/tenant"),
                        new("sub", subject),
                        new(
                            "auth_time",
                            Time.GetUtcNow()
                                .AddMinutes(-authenticationAgeMinutes)
                                .ToUnixTimeSeconds()
                                .ToString(CultureInfo.InvariantCulture))
                    ],
                    authenticationType: "oidc"));

        private static AetherIdentityUser User(
            string userName,
            long authorityVersion) =>
            new()
            {
                Id = Guid.NewGuid(),
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                DisplayName = userName,
                Enabled = true,
                AuthorityVersion = authorityVersion,
                TwoFactorEnabled = false,
                LockoutEnabled = true,
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
            temporary.Dispose();
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        internal void Advance(TimeSpan duration) =>
            current = current.Add(duration);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-external-identity-tests-{Guid.NewGuid():N}");
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
