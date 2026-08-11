using System.Buffers.Binary;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AetherSDR.Web.Tests;

public sealed class AetherLocalAccountAdministrationServiceTests
{
    private const string InitialPassword =
        "Correct-Horse-Battery-Staple-42";
    private const string ResetPassword =
        "Reset-Correct-Horse-Battery-Staple-84";

    [Fact]
    public async Task EnrollmentRemainsDisabledUntilAdminConfirmedTotp()
    {
        await using AdministrationFixture fixture =
            await AdministrationFixture.CreateAsync();
        AetherLocalAccountEnrollmentRequest request = CreateEnrollment();

        AetherLocalAccountEnrollmentIssue issue =
            await fixture.Service.BeginEnrollmentAsync(
                fixture.AdministratorPrincipal,
                request);

        AetherIdentityUser pending = await fixture.Database.Users.SingleAsync(
            user => user.Id == issue.UserId);
        Assert.False(pending.Enabled);
        Assert.False(pending.TwoFactorEnabled);
        Assert.NotNull(pending.DisabledAtUtc);
        Assert.Equal(1, pending.AuthorityVersion);
        Assert.NotEqual(InitialPassword, pending.PasswordHash);
        Assert.Equal(
            PasswordVerificationResult.Success,
            fixture.PasswordHasher.VerifyHashedPassword(
                pending,
                Assert.IsType<string>(pending.PasswordHash),
                InitialPassword));
        Assert.Equal(
            [AetherRoles.Control, AetherRoles.Observe],
            await fixture.ReadRolesAsync(issue.UserId));
        Assert.Equal(10, issue.RecoveryCodes.Count);

        string requestText = request.ToString();
        string issueText = issue.ToString();
        Assert.DoesNotContain(InitialPassword, requestText);
        Assert.DoesNotContain("new-operator", requestText);
        Assert.DoesNotContain(issue.SharedSecretBase32, issueText);
        foreach (string recoveryCode in issue.RecoveryCodes)
        {
            Assert.DoesNotContain(recoveryCode, issueText);
        }

        AetherIdentityAccountMutationResult confirmation =
            await fixture.Service.ConfirmEnrollmentAsync(
                fixture.AdministratorPrincipal,
                issue.UserId,
                issue.EnrollmentId,
                CurrentTotp(issue.SharedSecretBase32, fixture.Time.GetUtcNow()),
                "confirm-local-account");

        Assert.True(confirmation.Succeeded);
        Assert.True(confirmation.MutationAttempted);
        AetherIdentityUser enabled = await fixture.Database.Users.SingleAsync(
            user => user.Id == issue.UserId);
        Assert.True(enabled.Enabled);
        Assert.True(enabled.TwoFactorEnabled);
        Assert.Null(enabled.DisabledAtUtc);
        AetherIdentityAccountPage page = await fixture.Service.ListAsync(
            fixture.AdministratorPrincipal,
            offset: 0,
            limit: 50);
        AetherIdentityAccountSummary listed = Assert.Single(
            page.Accounts,
            account => account.UserId == issue.UserId);
        Assert.Equal("new-operator", listed.UserName);
        Assert.True(listed.HasLocalPassword);
        Assert.Equal(
            [AetherRoles.Control, AetherRoles.Observe],
            listed.Roles);
        Assert.Equal(
            2,
            await fixture.Database.IdentityAuditRecords.CountAsync());
        Assert.DoesNotContain(
            InitialPassword,
            string.Join(
                "\n",
                await fixture.Database.IdentityAuditRecords
                    .Select(audit => audit.DetailJson)
                    .ToArrayAsync()));
        Assert.DoesNotContain(
            issue.SharedSecretBase32,
            string.Join(
                "\n",
                await fixture.Database.IdentityAuditRecords
                    .Select(audit => audit.DetailJson)
                    .ToArrayAsync()));
    }

    [Fact]
    public async Task StaleOrNonAdministratorSessionCannotCreateAccount()
    {
        await using AdministrationFixture fixture =
            await AdministrationFixture.CreateAsync();
        fixture.Time.Advance(
            fixture.Policy.AdministratorReauthenticationLifetime +
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<
            AetherAdministratorReauthenticationRequiredException>(
            () => fixture.Service.BeginEnrollmentAsync(
                fixture.AdministratorPrincipal,
                CreateEnrollment()));

        fixture.Time.Rewind(
            fixture.Policy.AdministratorReauthenticationLifetime +
            TimeSpan.FromSeconds(1));
        ClaimsPrincipal withoutAdmin = AetherCanonicalPrincipalFactory.Create(
            fixture.Administrator,
            fixture.AdministratorSession,
            [AetherRoles.Observe],
            fixture.Time.GetUtcNow());

        await Assert.ThrowsAsync<
            AetherAdministratorReauthenticationRequiredException>(
            () => fixture.Service.BeginEnrollmentAsync(
                withoutAdmin,
                CreateEnrollment()));

        Assert.Single(await fixture.Database.Users.ToArrayAsync());
        Assert.Empty(await fixture.Database.IdentityAuditRecords.ToArrayAsync());
    }

    [Fact]
    public async Task LocalAdministratorReauthenticationCreatesSameUserFreshSession()
    {
        await using AdministrationFixture fixture =
            await AdministrationFixture.CreateAsync();
        fixture.Time.Advance(
            fixture.Policy.AdministratorReauthenticationLifetime +
            TimeSpan.FromSeconds(1));

        AetherLocalAdministratorReauthenticationChallenge challenge =
            await fixture.Reauthentication.BeginAsync(
                fixture.AdministratorPrincipal,
                InitialPassword,
                "admin-reauth-password");
        AetherAdministratorReauthenticationResult completed =
            await fixture.Reauthentication.CompleteAsync(
                fixture.AdministratorPrincipal,
                challenge.ChallengeToken,
                fixture.AdministratorRecoveryCode,
                "admin-reauth-mfa");

        Assert.True(challenge.ReadyForSecondFactor);
        Assert.True(completed.Succeeded);
        Assert.NotNull(completed.Principal);
        Assert.Equal(
            fixture.Administrator.Id.ToString("D"),
            completed.Principal.FindFirstValue(
                System.Security.Claims.ClaimTypes.NameIdentifier));
        AetherAuthenticationSession fresh =
            await fixture.Database.AuthenticationSessions.SingleAsync(
                session => session.Id == completed.SessionId);
        Assert.Equal(fixture.Time.GetUtcNow(), fresh.ReauthenticatedAtUtc);
        Assert.Equal(
            "authentication.administrator.reauthenticated",
            (await fixture.Database.IdentityAuditRecords.SingleAsync(
                audit =>
                    audit.Action ==
                    "authentication.administrator.reauthenticated")).Action);
        _ = await fixture.Service.ListAsync(
            completed.Principal,
            offset: 0,
            limit: 50);
    }

    [Fact]
    public async Task AdministratorPasswordResetRotatesAuthorityAndRevokesSessions()
    {
        await using AdministrationFixture fixture =
            await AdministrationFixture.CreateAsync();
        AetherLocalAccountEnrollmentIssue issue =
            await fixture.CreateConfirmedOperatorAsync();
        AetherAuthenticationSession first =
            await fixture.AddOperatorSessionAsync(issue.UserId);
        AetherAuthenticationSession second =
            await fixture.AddOperatorSessionAsync(issue.UserId);

        AetherLocalAccountPasswordResetRequest request = new(
            issue.UserId,
            ResetPassword,
            "admin-password-reset");
        AetherIdentityAccountMutationResult result =
            await fixture.Service.ResetPasswordAsync(
                fixture.AdministratorPrincipal,
                request);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AuthorityVersion);
        Assert.Equal(2, result.RevokedSessionCount);
        AetherIdentityUser user = await fixture.Database.Users.SingleAsync(
            candidate => candidate.Id == issue.UserId);
        Assert.Equal(
            PasswordVerificationResult.Success,
            fixture.PasswordHasher.VerifyHashedPassword(
                user,
                Assert.IsType<string>(user.PasswordHash),
                ResetPassword));
        Assert.Equal(fixture.Time.GetUtcNow(), first.RevokedAtUtc);
        Assert.Equal(fixture.Time.GetUtcNow(), second.RevokedAtUtc);
        Assert.Equal(
            "administrator-password-reset",
            first.RevocationReason);
        string auditJson = (await fixture.Database.IdentityAuditRecords
            .SingleAsync(
                audit =>
                    audit.Action ==
                    "identity.local-account.password-reset"))
            .DetailJson;
        Assert.DoesNotContain(InitialPassword, auditJson);
        Assert.DoesNotContain(ResetPassword, auditJson);
        Assert.DoesNotContain(ResetPassword, request.ToString());
    }

    [Fact]
    public async Task RoleChangesRevokeAuthorityAndPreserveFinalAdministrator()
    {
        await using AdministrationFixture fixture =
            await AdministrationFixture.CreateAsync();
        AetherLocalAccountEnrollmentIssue issue =
            await fixture.CreateConfirmedOperatorAsync();
        AetherAuthenticationSession operatorSession =
            await fixture.AddOperatorSessionAsync(issue.UserId);

        AetherIdentityAccountMutationResult changed =
            await fixture.Service.ReplaceRolesAsync(
                fixture.AdministratorPrincipal,
                issue.UserId,
                [AetherRoles.Observe, AetherRoles.Transmit],
                "replace-operator-roles");

        Assert.True(changed.Succeeded);
        Assert.True(changed.MutationAttempted);
        Assert.Equal(2, changed.AuthorityVersion);
        Assert.Equal(1, changed.RevokedSessionCount);
        Assert.Equal(
            [AetherRoles.Observe, AetherRoles.Transmit],
            await fixture.ReadRolesAsync(issue.UserId));
        Assert.Equal(fixture.Time.GetUtcNow(), operatorSession.RevokedAtUtc);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.ReplaceRolesAsync(
                fixture.AdministratorPrincipal,
                fixture.Administrator.Id,
                [AetherRoles.Observe],
                "remove-final-administrator"));

        Assert.Equal(
            [AetherRoles.Admin, AetherRoles.Observe],
            await fixture.ReadRolesAsync(fixture.Administrator.Id));
        Assert.Null(fixture.AdministratorSession.RevokedAtUtc);
    }

    [Fact]
    public async Task ExternalProvisioningRequiresVerifiedMethodBeforeEnable()
    {
        await using AdministrationFixture fixture =
            await AdministrationFixture.CreateAsync(combined: true);
        AetherIdentityAccountProvisioningRequest request = new(
            "external-operator",
            "External Operator",
            "external-operator@example.test",
            [AetherRoles.Observe, AetherRoles.Control],
            "provision-external-account");

        AetherIdentityAccountMutationResult provisioned =
            await fixture.Service.ProvisionExternalAccountAsync(
                fixture.AdministratorPrincipal,
                request);
        AetherIdentityUser pending = await fixture.Database.Users.SingleAsync(
            user => user.Id == provisioned.UserId);

        Assert.True(provisioned.Succeeded);
        Assert.False(pending.Enabled);
        Assert.Null(pending.PasswordHash);
        Assert.False(pending.TwoFactorEnabled);
        Assert.DoesNotContain("external-operator", request.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.SetEnabledAsync(
                fixture.AdministratorPrincipal,
                pending.Id,
                enabled: true,
                "enable-without-provider"));

        await fixture.AddExternalIdentityAsync(pending.Id);
        AetherIdentityAccountMutationResult enabled =
            await fixture.Service.SetEnabledAsync(
                fixture.AdministratorPrincipal,
                pending.Id,
                enabled: true,
                "enable-external-account");

        Assert.True(enabled.Succeeded);
        Assert.True(enabled.MutationAttempted);
        Assert.Equal(2, enabled.AuthorityVersion);
        Assert.True(pending.Enabled);
        Assert.Equal(
            [AetherRoles.Control, AetherRoles.Observe],
            await fixture.ReadRolesAsync(pending.Id));
        Assert.Equal(
            "identity.external-account.provisioned",
            (await fixture.Database.IdentityAuditRecords.SingleAsync(
                audit => audit.CorrelationId ==
                    "provision-external-account")).Action);
    }

    [Fact]
    public async Task EnabledStateAndExplicitRevocationRotateAuthoritySafely()
    {
        await using AdministrationFixture fixture =
            await AdministrationFixture.CreateAsync();
        AetherLocalAccountEnrollmentIssue issue =
            await fixture.CreateConfirmedOperatorAsync();
        AetherAuthenticationSession first =
            await fixture.AddOperatorSessionAsync(issue.UserId);

        AetherIdentityAccountMutationResult disabled =
            await fixture.Service.SetEnabledAsync(
                fixture.AdministratorPrincipal,
                issue.UserId,
                enabled: false,
                "disable-operator");
        Assert.Equal(2, disabled.AuthorityVersion);
        Assert.Equal(1, disabled.RevokedSessionCount);
        Assert.False((await fixture.Database.Users.SingleAsync(
            user => user.Id == issue.UserId)).Enabled);
        Assert.Equal(
            "administrator-account-disabled",
            first.RevocationReason);

        AetherIdentityAccountMutationResult enabled =
            await fixture.Service.SetEnabledAsync(
                fixture.AdministratorPrincipal,
                issue.UserId,
                enabled: true,
                "enable-operator");
        Assert.Equal(3, enabled.AuthorityVersion);
        AetherAuthenticationSession second =
            await fixture.AddOperatorSessionAsync(issue.UserId);
        AetherIdentityAccountMutationResult revoked =
            await fixture.Service.RevokeSessionsAsync(
                fixture.AdministratorPrincipal,
                issue.UserId,
                "revoke-operator-sessions");
        Assert.Equal(4, revoked.AuthorityVersion);
        Assert.Equal(1, revoked.RevokedSessionCount);
        Assert.Equal(
            "administrator-session-revocation",
            second.RevocationReason);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.SetEnabledAsync(
                fixture.AdministratorPrincipal,
                fixture.Administrator.Id,
                enabled: false,
                "disable-final-administrator"));
        Assert.True(fixture.Administrator.Enabled);
        Assert.Null(fixture.AdministratorSession.RevokedAtUtc);
    }

    private static AetherLocalAccountEnrollmentRequest CreateEnrollment() =>
        new(
            "new-operator",
            "New Operator",
            "new-operator@example.test",
            InitialPassword,
            [AetherRoles.Observe, AetherRoles.Control],
            "begin-local-account");

    private static string CurrentTotp(
        string sharedSecretBase32,
        DateTimeOffset now)
    {
        byte[] secret = DecodeBase32(sharedSecretBase32);
        byte[] movingFactor = new byte[sizeof(long)];
        byte[] hash = [];
        try
        {
            BinaryPrimitives.WriteInt64BigEndian(
                movingFactor,
                now.ToUnixTimeSeconds() / 30);
            using HMACSHA1 hmac = new(secret);
            hash = hmac.ComputeHash(movingFactor);
            int offset = hash[^1] & 0x0f;
            int binary =
                ((hash[offset] & 0x7f) << 24) |
                ((hash[offset + 1] & 0xff) << 16) |
                ((hash[offset + 2] & 0xff) << 8) |
                (hash[offset + 3] & 0xff);
            return (binary % 1_000_000).ToString(
                "D6",
                CultureInfo.InvariantCulture);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(movingFactor);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static byte[] DecodeBase32(string value)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> bytes = [];
        int buffer = 0;
        int bits = 0;
        foreach (char character in value)
        {
            int digit = Alphabet.IndexOf(character);
            if (digit < 0)
            {
                throw new InvalidOperationException(
                    "The enrollment secret is not canonical Base32.");
            }
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        return bytes.ToArray();
    }

    private sealed class AdministrationFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory temporary;
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private AdministrationFixture(
            TemporaryDirectory temporary,
            ServiceProvider provider,
            AsyncServiceScope scope,
            AetherIdentityDbContext database,
            AetherLocalAccountAdministrationService service,
            AetherLocalAdministratorReauthenticationService reauthentication,
            IPasswordHasher<AetherIdentityUser> passwordHasher,
            AetherLocalAuthenticationPolicy policy,
            ManualTimeProvider time,
            AetherIdentityUser administrator,
            AetherAuthenticationSession administratorSession,
            ClaimsPrincipal administratorPrincipal,
            string administratorRecoveryCode)
        {
            this.temporary = temporary;
            this.provider = provider;
            this.scope = scope;
            Database = database;
            Service = service;
            Reauthentication = reauthentication;
            PasswordHasher = passwordHasher;
            Policy = policy;
            Time = time;
            Administrator = administrator;
            AdministratorSession = administratorSession;
            AdministratorPrincipal = administratorPrincipal;
            AdministratorRecoveryCode = administratorRecoveryCode;
        }

        internal AetherIdentityDbContext Database { get; }

        internal AetherLocalAccountAdministrationService Service { get; }

        internal AetherLocalAdministratorReauthenticationService Reauthentication
        {
            get;
        }

        internal IPasswordHasher<AetherIdentityUser> PasswordHasher { get; }

        internal AetherLocalAuthenticationPolicy Policy { get; }

        internal ManualTimeProvider Time { get; }

        internal AetherIdentityUser Administrator { get; }

        internal AetherAuthenticationSession AdministratorSession { get; }

        internal ClaimsPrincipal AdministratorPrincipal { get; }

        internal string AdministratorRecoveryCode { get; }

        internal static async Task<AdministrationFixture> CreateAsync(
            bool combined = false)
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
                        "The account administration test database failed.");
                }

                AetherAuthenticationTopology topology =
                    AetherAuthenticationConfiguration.Validate(
                        combined
                            ? new AuthSettings
                            {
                                Mode = "Combined",
                                ProviderId = "club-oidc",
                                ProviderType = "OpenIdConnect",
                                Authority =
                                    "https://identity.example/tenant",
                                ClientId = "aethersdr-web",
                                ClientSecret = "test-secret"
                            }
                            : new AuthSettings { Mode = "Local" },
                        isDevelopmentEnvironment: false);
                ManualTimeProvider time = new(
                    DateTimeOffset.Parse(
                        "2026-08-10T15:00:00Z",
                        CultureInfo.InvariantCulture));
                ServiceCollection services = new();
                services.AddAetherIdentityPersistence(paths);
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<IDataProtectionProvider>(
                    new EphemeralDataProtectionProvider());
                services.AddSingleton(topology);
                services.AddAetherLocalAuthenticationFoundation(
                    topology.LocalPolicy);
                services.AddScoped<AetherAuthenticationSessionService>();
                ServiceProvider provider = services.BuildServiceProvider();
                AsyncServiceScope scope = provider.CreateAsyncScope();
                IServiceProvider scoped = scope.ServiceProvider;
                AetherIdentityDbContext database =
                    scoped.GetRequiredService<AetherIdentityDbContext>();

                IPasswordHasher<AetherIdentityUser> passwordHasher =
                    scoped.GetRequiredService<
                        IPasswordHasher<AetherIdentityUser>>();
                AetherLocalMfaCredentialProtector credentialProtector =
                    scoped.GetRequiredService<
                        AetherLocalMfaCredentialProtector>();
                AetherIdentityUser administrator = new()
                {
                    Id = Guid.NewGuid(),
                    UserName = "administrator",
                    NormalizedUserName = "ADMINISTRATOR",
                    DisplayName = "Administrator",
                    Enabled = true,
                    AuthorityVersion = 3,
                    TwoFactorEnabled = true,
                    LockoutEnabled = true,
                    EmailConfirmed = true,
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                };
                administrator.PasswordHash = passwordHasher.HashPassword(
                    administrator,
                    InitialPassword);
                AetherLocalRecoveryCredential administratorRecovery =
                    AetherLocalMfaCredentialProtector
                        .GenerateRecoveryCredential(administrator.Id);
                database.Set<IdentityUserToken<Guid>>().Add(
                    administratorRecovery.Token);
                Guid adminRoleId = await database.Roles
                    .Where(role => role.Name == AetherRoles.Admin)
                    .Select(role => role.Id)
                    .SingleAsync();
                Guid observeRoleId = await database.Roles
                    .Where(role => role.Name == AetherRoles.Observe)
                    .Select(role => role.Id)
                    .SingleAsync();
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
                ClaimsPrincipal principal =
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
                        AetherLocalAccountAdministrationService>(),
                    scoped.GetRequiredService<
                        AetherLocalAdministratorReauthenticationService>(),
                    passwordHasher,
                    topology.LocalPolicy,
                    time,
                    administrator,
                    administratorSession,
                    principal,
                    administratorRecovery.Code);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        internal async Task<AetherLocalAccountEnrollmentIssue>
            CreateConfirmedOperatorAsync()
        {
            AetherLocalAccountEnrollmentIssue issue =
                await Service.BeginEnrollmentAsync(
                    AdministratorPrincipal,
                    CreateEnrollment());
            AetherIdentityAccountMutationResult confirmation =
                await Service.ConfirmEnrollmentAsync(
                    AdministratorPrincipal,
                    issue.UserId,
                    issue.EnrollmentId,
                    CurrentTotp(
                        issue.SharedSecretBase32,
                        Time.GetUtcNow()),
                    "confirm-local-account");
            Assert.True(confirmation.Succeeded);
            return issue;
        }

        internal async Task<AetherAuthenticationSession> AddOperatorSessionAsync(
            Guid userId)
        {
            AetherIdentityUser user = await Database.Users.SingleAsync(
                candidate => candidate.Id == userId);
            AetherAuthenticationSession session = new()
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                User = user,
                AuthenticationMethod =
                    AetherAuthenticationMethod.LocalPasswordWithTotp,
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

        internal async Task AddExternalIdentityAsync(Guid userId)
        {
            AetherIdentityUser user = await Database.Users.SingleAsync(
                candidate => candidate.Id == userId);
            Database.ExternalIdentities.Add(
                new()
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    User = user,
                    ProviderId = "club-oidc",
                    Issuer = "https://identity.example/tenant",
                    Subject = $"subject-{userId:N}",
                    LinkedAtUtc = Time.GetUtcNow()
                });
            await Database.SaveChangesAsync();
        }

        internal async Task<string[]> ReadRolesAsync(Guid userId) =>
            await (
                from assignment in Database.Set<IdentityUserRole<Guid>>()
                join role in Database.Roles
                    on assignment.RoleId equals role.Id
                where assignment.UserId == userId && role.Name != null
                orderby role.Name
                select role.Name!)
                .ToArrayAsync();

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

        internal void Rewind(TimeSpan duration) =>
            current = current.Subtract(duration);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-account-admin-tests-{Guid.NewGuid():N}");
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
