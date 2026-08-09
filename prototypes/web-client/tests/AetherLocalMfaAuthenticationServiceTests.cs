using System.Buffers.Binary;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AetherSDR.Web.Tests;

public sealed class AetherLocalMfaAuthenticationServiceTests
{
    private const string ValidPassword =
        "Correct-Horse-Battery-Staple";
    private static readonly byte[] TotpSecret =
        Encoding.ASCII.GetBytes("12345678901234567890");

    [Fact]
    public async Task TotpIssuesCanonicalSessionOnlyAfterPasswordChallenge()
    {
        await using LocalMfaFixture fixture =
            await LocalMfaFixture.CreateAsync();
        AetherIdentityUser user = await fixture.SeedUserAsync(
            [AetherRoles.Observe, AetherRoles.Control]);

        AetherLocalPasswordVerificationResult password =
            await fixture.Passwords.VerifyAsync(
                user.UserName,
                ValidPassword,
                "mfa-password-success");
        Assert.True(password.ReadyForSecondFactor);
        Assert.NotNull(password.ChallengeToken);
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());

        AetherLocalMfaAuthenticationResult result =
            await fixture.Mfa.AuthenticateAsync(
                password.ChallengeToken,
                CurrentTotp(fixture.Time.GetUtcNow()),
                "mfa-totp-success",
                TimeSpan.FromHours(8));

        Assert.True(result.Succeeded);
        Assert.Equal("local-mfa-authenticated", result.Code);
        Assert.NotNull(result.Principal);
        Assert.NotNull(result.SessionId);
        Assert.Equal(
            user.Id.ToString("D"),
            result.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(result.Principal.IsInRole(AetherRoles.Observe));
        Assert.True(result.Principal.IsInRole(AetherRoles.Control));
        Assert.False(result.Principal.IsInRole(AetherRoles.Admin));
        Assert.False(result.Principal.IsInRole(AetherRoles.Transmit));

        AetherAuthenticationSession session =
            Assert.Single(await fixture.Database.AuthenticationSessions
                .ToArrayAsync());
        Assert.Equal(
            AetherAuthenticationMethod.LocalPasswordWithTotp,
            session.AuthenticationMethod);
        Assert.Null(session.ProviderId);
        Assert.Equal(fixture.Time.GetUtcNow(), session.ReauthenticatedAtUtc);
        Assert.Equal(
            fixture.Time.GetUtcNow().AddHours(8),
            session.AbsoluteExpiresAtUtc);

        AetherIdentityAuditRecord[] audits =
            await fixture.Database.IdentityAuditRecords
                .OrderBy(record => record.Id)
                .ToArrayAsync();
        Assert.Equal(2, audits.Length);
        Assert.Equal(
            "authentication.local.password",
            audits[0].Action);
        Assert.Equal("authentication.local.mfa", audits[1].Action);
        Assert.DoesNotContain(ValidPassword, audits[0].DetailJson);
        Assert.DoesNotContain(
            CurrentTotp(fixture.Time.GetUtcNow()),
            audits[1].DetailJson);
    }

    [Fact]
    public async Task TotpMatchesRfc6238Sha1Vector()
    {
        await using LocalMfaFixture fixture =
            await LocalMfaFixture.CreateAsync(
                DateTimeOffset.FromUnixTimeSeconds(59));
        AetherIdentityUser user = await fixture.SeedUserAsync(
            [AetherRoles.Observe]);
        string challenge = await fixture.PasswordChallengeAsync(user);

        AetherLocalMfaAuthenticationResult result =
            await fixture.Mfa.AuthenticateAsync(
                challenge,
                "287082",
                "mfa-rfc-vector",
                TimeSpan.FromHours(8));

        Assert.True(result.Succeeded);
        Assert.Single(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
    }

    [Fact]
    public async Task TotpStepAndChallengeAreBothSingleUse()
    {
        await using LocalMfaFixture fixture =
            await LocalMfaFixture.CreateAsync();
        AetherIdentityUser user = await fixture.SeedUserAsync(
            [AetherRoles.Observe]);
        string code = CurrentTotp(fixture.Time.GetUtcNow());

        string firstChallenge = await fixture.PasswordChallengeAsync(user);
        AetherLocalMfaAuthenticationResult first =
            await fixture.Mfa.AuthenticateAsync(
                firstChallenge,
                code,
                "mfa-first-totp",
                TimeSpan.FromHours(8));
        AetherLocalMfaAuthenticationResult challengeReplay =
            await fixture.Mfa.AuthenticateAsync(
                firstChallenge,
                code,
                "mfa-challenge-replay",
                TimeSpan.FromHours(8));

        string secondChallenge = await fixture.PasswordChallengeAsync(user);
        AetherLocalMfaAuthenticationResult totpReplay =
            await fixture.Mfa.AuthenticateAsync(
                secondChallenge,
                code,
                "mfa-totp-replay",
                TimeSpan.FromHours(8));

        Assert.True(first.Succeeded);
        Assert.False(challengeReplay.Succeeded);
        Assert.Equal("local-mfa-rejected", challengeReplay.Code);
        Assert.False(totpReplay.Succeeded);
        Assert.Equal("local-mfa-rejected", totpReplay.Code);
        Assert.Single(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
        AetherIdentityUser persisted =
            await fixture.Database.Users.SingleAsync();
        Assert.Equal(1, persisted.AccessFailedCount);
    }

    [Fact]
    public async Task RecoveryCodeIsHashedAtRestAndConsumedAtomically()
    {
        await using LocalMfaFixture fixture =
            await LocalMfaFixture.CreateAsync();
        AetherIdentityUser user = await fixture.SeedUserAsync(
            [AetherRoles.Observe],
            includeRecoveryCode: true);
        string recoveryCode = Assert.IsType<string>(
            fixture.RecoveryCode);
        IdentityUserToken<Guid> stored =
            await fixture.Database.Set<IdentityUserToken<Guid>>()
                .SingleAsync(token =>
                    token.Name.StartsWith(
                        AetherLocalMfaCredentialProtector
                            .RecoveryCodeNamePrefix));
        Assert.DoesNotContain(
            recoveryCode.Replace("-", string.Empty),
            stored.Name,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            recoveryCode,
            stored.Value ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        string firstChallenge = await fixture.PasswordChallengeAsync(user);
        AetherLocalMfaAuthenticationResult first =
            await fixture.Mfa.AuthenticateAsync(
                firstChallenge,
                recoveryCode.ToLowerInvariant(),
                "mfa-recovery-success",
                TimeSpan.FromHours(8));

        Assert.True(first.Succeeded);
        AetherAuthenticationSession session =
            Assert.Single(await fixture.Database.AuthenticationSessions
                .ToArrayAsync());
        Assert.Equal(
            AetherAuthenticationMethod.LocalPasswordWithRecoveryCode,
            session.AuthenticationMethod);
        Assert.Empty(
            await fixture.Database.Set<IdentityUserToken<Guid>>()
                .Where(token =>
                    token.Name.StartsWith(
                        AetherLocalMfaCredentialProtector
                            .RecoveryCodeNamePrefix))
                .ToArrayAsync());

        string secondChallenge = await fixture.PasswordChallengeAsync(user);
        AetherLocalMfaAuthenticationResult replay =
            await fixture.Mfa.AuthenticateAsync(
                secondChallenge,
                recoveryCode,
                "mfa-recovery-replay",
                TimeSpan.FromHours(8));

        Assert.False(replay.Succeeded);
        Assert.Single(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
    }

    [Fact]
    public async Task CorruptProtectedTotpCredentialFailsClosed()
    {
        await using LocalMfaFixture fixture =
            await LocalMfaFixture.CreateAsync();
        AetherIdentityUser user = await fixture.SeedUserAsync(
            [AetherRoles.Observe]);
        IdentityUserToken<Guid> secretToken =
            await fixture.Database.Set<IdentityUserToken<Guid>>()
                .SingleAsync(token =>
                    token.Name ==
                        AetherLocalMfaCredentialProtector.TotpSecretName);
        secretToken.Value = "not-protected-secret-material";
        await fixture.Database.SaveChangesAsync();
        string challenge = await fixture.PasswordChallengeAsync(user);

        AetherLocalMfaAuthenticationResult result =
            await fixture.Mfa.AuthenticateAsync(
                challenge,
                CurrentTotp(fixture.Time.GetUtcNow()),
                "mfa-corrupt-credential",
                TimeSpan.FromHours(8));

        Assert.False(result.Succeeded);
        Assert.Equal("local-mfa-rejected", result.Code);
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
        AetherIdentityUser persisted =
            await fixture.Database.Users.SingleAsync();
        Assert.Equal(1, persisted.AccessFailedCount);
    }

    [Fact]
    public async Task ExpiredAndAuthorityStaleChallengesNeverIssueSessions()
    {
        await using LocalMfaFixture fixture =
            await LocalMfaFixture.CreateAsync();
        AetherIdentityUser user = await fixture.SeedUserAsync(
            [AetherRoles.Observe]);

        string expiredChallenge =
            await fixture.PasswordChallengeAsync(user);
        fixture.Time.Advance(TimeSpan.FromMinutes(6));
        AetherLocalMfaAuthenticationResult expired =
            await fixture.Mfa.AuthenticateAsync(
                expiredChallenge,
                CurrentTotp(fixture.Time.GetUtcNow()),
                "mfa-expired",
                TimeSpan.FromHours(8));

        string staleChallenge = await fixture.PasswordChallengeAsync(user);
        user.AuthorityVersion++;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await fixture.Database.SaveChangesAsync();
        AetherLocalMfaAuthenticationResult stale =
            await fixture.Mfa.AuthenticateAsync(
                staleChallenge,
                CurrentTotp(fixture.Time.GetUtcNow()),
                "mfa-authority-stale",
                TimeSpan.FromHours(8));

        Assert.False(expired.Succeeded);
        Assert.False(stale.Succeeded);
        Assert.Equal("local-mfa-rejected", expired.Code);
        Assert.Equal("local-mfa-rejected", stale.Code);
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
    }

    [Fact]
    public async Task FailedSecondFactorsCreateDurableBoundedLockout()
    {
        await using LocalMfaFixture fixture =
            await LocalMfaFixture.CreateAsync();
        AetherIdentityUser user = await fixture.SeedUserAsync(
            [AetherRoles.Observe]);

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            string challenge = await fixture.PasswordChallengeAsync(user);
            AetherLocalMfaAuthenticationResult rejected =
                await fixture.Mfa.AuthenticateAsync(
                    challenge,
                    "invalid-code",
                    $"mfa-failure-{attempt}",
                    TimeSpan.FromHours(8));
            Assert.False(rejected.Succeeded);
            Assert.Equal("local-mfa-rejected", rejected.Code);
        }

        AetherIdentityUser locked =
            await fixture.Database.Users.SingleAsync();
        Assert.Equal(0, locked.AccessFailedCount);
        Assert.Equal(
            fixture.Time.GetUtcNow().AddMinutes(15),
            locked.LockoutEnd);
        AetherLocalPasswordVerificationResult passwordRejected =
            await fixture.Passwords.VerifyAsync(
                user.UserName,
                ValidPassword,
                "mfa-locked-password");
        Assert.False(passwordRejected.ReadyForSecondFactor);
        Assert.Null(passwordRejected.ChallengeToken);
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
    }

    private static string CurrentTotp(DateTimeOffset now)
    {
        long step = now.ToUnixTimeSeconds() / 30;
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hash = HMACSHA1.HashData(TotpSecret, counter);
        try
        {
            int offset = hash[^1] & 0x0f;
            int binaryCode =
                ((hash[offset] & 0x7f) << 24) |
                ((hash[offset + 1] & 0xff) << 16) |
                ((hash[offset + 2] & 0xff) << 8) |
                (hash[offset + 3] & 0xff);
            return (binaryCode % 1_000_000).ToString(
                "D6",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private sealed class LocalMfaFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory temporary;
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private LocalMfaFixture(
            TemporaryDirectory temporary,
            ServiceProvider provider,
            AsyncServiceScope scope,
            AetherIdentityDbContext database,
            AetherLocalPasswordAuthenticationService passwords,
            AetherLocalMfaAuthenticationService mfa,
            AetherLocalMfaCredentialProtector credentialProtector,
            IPasswordHasher<AetherIdentityUser> passwordHasher,
            ManualTimeProvider time)
        {
            this.temporary = temporary;
            this.provider = provider;
            this.scope = scope;
            Database = database;
            Passwords = passwords;
            Mfa = mfa;
            CredentialProtector = credentialProtector;
            PasswordHasher = passwordHasher;
            Time = time;
        }

        internal AetherIdentityDbContext Database { get; }

        internal AetherLocalPasswordAuthenticationService Passwords { get; }

        internal AetherLocalMfaAuthenticationService Mfa { get; }

        internal AetherLocalMfaCredentialProtector CredentialProtector
        {
            get;
        }

        internal IPasswordHasher<AetherIdentityUser> PasswordHasher { get; }

        internal string? RecoveryCode { get; private set; }

        internal ManualTimeProvider Time { get; }

        internal static async Task<LocalMfaFixture> CreateAsync(
            DateTimeOffset? now = null)
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
                        "The local MFA test database could not be initialized.");
                }

                AetherAuthenticationTopology topology =
                    AetherAuthenticationConfiguration.Validate(
                        new AuthSettings { Mode = "Local" },
                        isDevelopmentEnvironment: false);
                ManualTimeProvider time = new(
                    now ?? DateTimeOffset.Parse(
                        "2026-08-09T20:30:00Z",
                        System.Globalization.CultureInfo.InvariantCulture));
                ServiceCollection services = new();
                services.AddAetherIdentityPersistence(paths);
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<IDataProtectionProvider>(
                    new EphemeralDataProtectionProvider());
                services.AddAetherLocalAuthenticationFoundation(
                    topology.LocalPolicy);
                ServiceProvider provider = services.BuildServiceProvider();
                AsyncServiceScope scope = provider.CreateAsyncScope();
                IServiceProvider scoped = scope.ServiceProvider;
                return new(
                    temporary,
                    provider,
                    scope,
                    scoped.GetRequiredService<AetherIdentityDbContext>(),
                    scoped.GetRequiredService<
                        AetherLocalPasswordAuthenticationService>(),
                    scoped.GetRequiredService<
                        AetherLocalMfaAuthenticationService>(),
                    scoped.GetRequiredService<
                        AetherLocalMfaCredentialProtector>(),
                    scoped.GetRequiredService<
                        IPasswordHasher<AetherIdentityUser>>(),
                    time);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        internal async Task<AetherIdentityUser> SeedUserAsync(
            string[] roles,
            bool includeRecoveryCode = false)
        {
            AetherIdentityUser user = new()
            {
                Id = Guid.NewGuid(),
                UserName = $"operator-{Guid.NewGuid():N}",
                NormalizedUserName = null,
                DisplayName = "Persisted Local Operator",
                Enabled = true,
                AuthorityVersion = 3,
                EmailConfirmed = true,
                TwoFactorEnabled = true,
                LockoutEnabled = true,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };
            user.NormalizedUserName =
                user.UserName.ToUpperInvariant();
            user.PasswordHash =
                PasswordHasher.HashPassword(user, ValidPassword);
            Database.Users.Add(user);
            Database.Set<IdentityUserToken<Guid>>().Add(
                CredentialProtector.CreateTotpSecretToken(
                    user.Id,
                    TotpSecret));
            if (includeRecoveryCode)
            {
                AetherLocalRecoveryCredential recovery =
                    AetherLocalMfaCredentialProtector
                        .GenerateRecoveryCredential(user.Id);
                RecoveryCode = recovery.Code;
                Database.Set<IdentityUserToken<Guid>>().Add(
                    recovery.Token);
            }

            foreach (string roleName in roles)
            {
                Guid roleId = await Database.Roles
                    .Where(role => role.Name == roleName)
                    .Select(role => role.Id)
                    .SingleAsync();
                Database.Set<IdentityUserRole<Guid>>().Add(
                    new()
                    {
                        UserId = user.Id,
                        RoleId = roleId
                    });
            }
            await Database.SaveChangesAsync();
            return user;
        }

        internal async Task<string> PasswordChallengeAsync(
            AetherIdentityUser user)
        {
            AetherLocalPasswordVerificationResult result =
                await Passwords.VerifyAsync(
                    user.UserName,
                    ValidPassword,
                    $"password-{Guid.NewGuid():N}");
            Assert.True(result.ReadyForSecondFactor);
            return Assert.IsType<string>(result.ChallengeToken);
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
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        internal void Advance(TimeSpan duration)
        {
            current = current.Add(duration);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-local-mfa-tests-{Guid.NewGuid():N}");
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
