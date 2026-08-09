using System.Buffers.Binary;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Auth.Identity;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class AetherLocalPasswordAuthenticationServiceTests
{
    private const string ValidPassword = "Correct-Horse-Battery-Staple";

    [Fact]
    public async Task CorrectPasswordRequiresMfaWithoutIssuingSession()
    {
        await using LocalPasswordFixture fixture =
            await LocalPasswordFixture.CreateAsync();
        AetherIdentityUser user = await fixture.SeedUserAsync(
            "operator",
            ValidPassword,
            enabled: true,
            twoFactorEnabled: true);

        AetherLocalPasswordVerificationResult result =
            await fixture.Service.VerifyAsync(
                "operator",
                ValidPassword,
                "local-correlation-success");

        Assert.True(result.ReadyForSecondFactor);
        Assert.Equal("local-second-factor-required", result.Code);
        Assert.Equal(user.Id, result.UserId);
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
        AetherIdentityAuditRecord audit =
            Assert.Single(await fixture.Database.IdentityAuditRecords
                .ToArrayAsync());
        Assert.Equal(
            "authentication.local.password",
            audit.Action);
        Assert.Equal(AetherIdentityAuditOutcome.Succeeded, audit.Outcome);
        Assert.Null(audit.ActorUserId);
        Assert.Equal(user.Id, audit.SubjectUserId);
        Assert.Contains("local-password-verified", audit.DetailJson);
        Assert.DoesNotContain(ValidPassword, audit.DetailJson);
        Assert.DoesNotContain("operator", audit.DetailJson);
    }

    [Fact]
    public async Task FailedPasswordsStartBoundedDurableLockout()
    {
        await using LocalPasswordFixture fixture =
            await LocalPasswordFixture.CreateAsync();
        AetherIdentityUser user = await fixture.SeedUserAsync(
            "locked-operator",
            ValidPassword,
            enabled: true,
            twoFactorEnabled: true);

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            AetherLocalPasswordVerificationResult rejected =
                await fixture.Service.VerifyAsync(
                    "locked-operator",
                    $"Wrong-Password-{attempt}",
                    $"local-correlation-failed-{attempt}");

            Assert.False(rejected.ReadyForSecondFactor);
            Assert.Equal("local-password-rejected", rejected.Code);
            Assert.Null(rejected.UserId);
        }

        AetherIdentityUser locked =
            await fixture.Database.Users.SingleAsync(
                candidate => candidate.Id == user.Id);
        Assert.Equal(0, locked.AccessFailedCount);
        Assert.Equal(
            fixture.Now.AddMinutes(15),
            locked.LockoutEnd);

        AetherLocalPasswordVerificationResult stillRejected =
            await fixture.Service.VerifyAsync(
                "locked-operator",
                ValidPassword,
                "local-correlation-locked");

        Assert.False(stillRejected.ReadyForSecondFactor);
        Assert.Equal("local-password-rejected", stillRejected.Code);
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
        AetherIdentityAuditRecord[] audits =
            await fixture.Database.IdentityAuditRecords
                .OrderBy(record => record.Id)
                .ToArrayAsync();
        Assert.Equal(6, audits.Length);
        Assert.Contains(
            "local-password-lockout-started",
            audits[4].DetailJson);
        Assert.Contains("\"failedAttempts\":5", audits[4].DetailJson);
        Assert.Contains("local-password-locked", audits[5].DetailJson);
        Assert.All(
            audits,
            audit =>
            {
                Assert.DoesNotContain(ValidPassword, audit.DetailJson);
                Assert.DoesNotContain(
                    "locked-operator",
                    audit.DetailJson);
            });
    }

    [Fact]
    public async Task UnknownAndDisabledUsersShareGenericRejection()
    {
        await using LocalPasswordFixture fixture =
            await LocalPasswordFixture.CreateAsync();
        AetherIdentityUser disabled = await fixture.SeedUserAsync(
            "disabled-operator",
            ValidPassword,
            enabled: false,
            twoFactorEnabled: true);

        AetherLocalPasswordVerificationResult unknown =
            await fixture.Service.VerifyAsync(
                "missing-operator",
                ValidPassword,
                "local-correlation-unknown");
        AetherLocalPasswordVerificationResult rejectedDisabled =
            await fixture.Service.VerifyAsync(
                "disabled-operator",
                ValidPassword,
                "local-correlation-disabled");

        Assert.Equal("local-password-rejected", unknown.Code);
        Assert.Equal(
            "local-password-rejected",
            rejectedDisabled.Code);
        Assert.False(unknown.ReadyForSecondFactor);
        Assert.False(rejectedDisabled.ReadyForSecondFactor);
        Assert.Null(unknown.UserId);
        Assert.Null(rejectedDisabled.UserId);
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());

        AetherIdentityAuditRecord[] audits =
            await fixture.Database.IdentityAuditRecords
                .OrderBy(record => record.Id)
                .ToArrayAsync();
        Assert.Equal(2, audits.Length);
        Assert.Null(audits[0].SubjectUserId);
        Assert.Equal(disabled.Id, audits[1].SubjectUserId);
        Assert.DoesNotContain("missing-operator", audits[0].DetailJson);
        Assert.DoesNotContain("disabled-operator", audits[1].DetailJson);
    }

    [Fact]
    public async Task SuccessfulVerificationRehashesAndStillRequiresMfaEnrollment()
    {
        await using LocalPasswordFixture fixture =
            await LocalPasswordFixture.CreateAsync();
        AetherIdentityUser user = await fixture.SeedUserAsync(
            "rehash-operator",
            ValidPassword,
            enabled: true,
            twoFactorEnabled: false,
            passwordHashIterationCount: 100_000);
        user.AccessFailedCount = 2;
        user.LockoutEnd = fixture.Now.AddMinutes(-1);
        await fixture.Database.SaveChangesAsync();

        Assert.Equal(
            100_000,
            ReadIterationCount(user.PasswordHash!));

        AetherLocalPasswordVerificationResult result =
            await fixture.Service.VerifyAsync(
                "rehash-operator",
                ValidPassword,
                "local-correlation-rehash");

        Assert.False(result.ReadyForSecondFactor);
        Assert.Equal("local-mfa-enrollment-required", result.Code);
        Assert.Null(result.UserId);
        AetherIdentityUser persisted =
            await fixture.Database.Users.SingleAsync(
                candidate => candidate.Id == user.Id);
        Assert.Equal(0, persisted.AccessFailedCount);
        Assert.Null(persisted.LockoutEnd);
        Assert.Equal(
            210_000,
            ReadIterationCount(persisted.PasswordHash!));
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
        AetherIdentityAuditRecord audit =
            Assert.Single(await fixture.Database.IdentityAuditRecords
                .ToArrayAsync());
        Assert.Equal(AetherIdentityAuditOutcome.Rejected, audit.Outcome);
        Assert.Contains("local-mfa-not-enrolled", audit.DetailJson);
    }

    private static int ReadIterationCount(string passwordHash)
    {
        byte[] decoded = Convert.FromBase64String(passwordHash);
        Assert.True(decoded.Length >= 9);
        Assert.Equal(1, decoded[0]);
        return checked((int)BinaryPrimitives.ReadUInt32BigEndian(
            decoded.AsSpan(5, 4)));
    }

    private sealed class LocalPasswordFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory temporary;
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private LocalPasswordFixture(
            TemporaryDirectory temporary,
            ServiceProvider provider,
            AsyncServiceScope scope,
            AetherIdentityDbContext database,
            AetherLocalPasswordAuthenticationService service,
            IPasswordHasher<AetherIdentityUser> passwordHasher,
            ManualTimeProvider time)
        {
            this.temporary = temporary;
            this.provider = provider;
            this.scope = scope;
            Database = database;
            Service = service;
            PasswordHasher = passwordHasher;
            Time = time;
        }

        internal DateTimeOffset Now { get; } = DateTimeOffset.Parse(
            "2026-08-09T19:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        internal AetherIdentityDbContext Database { get; }

        internal AetherLocalPasswordAuthenticationService Service { get; }

        internal IPasswordHasher<AetherIdentityUser> PasswordHasher { get; }

        internal ManualTimeProvider Time { get; }

        internal static async Task<LocalPasswordFixture> CreateAsync()
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
                        "The local password test database could not be initialized.");
                }

                AetherAuthenticationTopology topology =
                    AetherAuthenticationConfiguration.Validate(
                        new AuthSettings { Mode = "Local" },
                        isDevelopmentEnvironment: false);
                ManualTimeProvider time = new(
                    DateTimeOffset.Parse(
                        "2026-08-09T19:00:00Z",
                        System.Globalization.CultureInfo.InvariantCulture));
                ServiceCollection services = new();
                services.AddAetherIdentityPersistence(paths);
                services.AddSingleton<TimeProvider>(time);
                services.AddAetherLocalAuthenticationFoundation(
                    topology.LocalPolicy);
                ServiceProvider provider = services.BuildServiceProvider();
                AsyncServiceScope scope = provider.CreateAsyncScope();
                AetherIdentityDbContext database =
                    scope.ServiceProvider
                        .GetRequiredService<AetherIdentityDbContext>();
                AetherLocalPasswordAuthenticationService service =
                    scope.ServiceProvider.GetRequiredService<
                        AetherLocalPasswordAuthenticationService>();
                IPasswordHasher<AetherIdentityUser> passwordHasher =
                    scope.ServiceProvider.GetRequiredService<
                        IPasswordHasher<AetherIdentityUser>>();
                return new(
                    temporary,
                    provider,
                    scope,
                    database,
                    service,
                    passwordHasher,
                    time);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        internal async Task<AetherIdentityUser> SeedUserAsync(
            string userName,
            string password,
            bool enabled,
            bool twoFactorEnabled,
            int? passwordHashIterationCount = null)
        {
            AetherIdentityUser user = new()
            {
                Id = Guid.NewGuid(),
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                DisplayName = userName,
                Enabled = enabled,
                AuthorityVersion = 1,
                EmailConfirmed = true,
                TwoFactorEnabled = twoFactorEnabled,
                LockoutEnabled = true,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };
            if (passwordHashIterationCount is int iterations)
            {
                PasswordHasherOptions options = new()
                {
                    CompatibilityMode =
                        PasswordHasherCompatibilityMode.IdentityV3,
                    IterationCount = iterations
                };
                PasswordHasher<AetherIdentityUser> oldHasher =
                    new(Options.Create(options));
                user.PasswordHash =
                    oldHasher.HashPassword(user, password);
            }
            else
            {
                user.PasswordHash =
                    PasswordHasher.HashPassword(user, password);
            }

            Database.Users.Add(user);
            await Database.SaveChangesAsync();
            return user;
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
                $"aethersdr-local-password-tests-{Guid.NewGuid():N}");
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
