using System.Buffers.Binary;
using System.Globalization;
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

public sealed class AetherFirstLocalAdministratorProvisioningServiceTests
{
    private const string ValidPassword =
        "Correct-Horse-Battery-Staple-42";
    private const string RotatedPassword =
        "Another-Correct-Horse-Battery-Staple-84";

    [Fact]
    public async Task BeginCreatesOnlyDisabledSetupBoundAuthorityAndRedactsSecrets()
    {
        await using ProvisioningFixture fixture =
            await ProvisioningFixture.CreateAsync();
        InstallationFirstLocalAdministratorEnrollment enrollment =
            CreateEnrollment();

        InstallationFirstLocalAdministratorEnrollmentIssue issue =
            await fixture.Service.BeginAsync(fixture.Setup, enrollment);

        AetherIdentityUser user =
            Assert.Single(await fixture.Database.Users.ToArrayAsync());
        Assert.Equal(issue.UserId, user.Id);
        Assert.False(user.Enabled);
        Assert.True(user.LockoutEnabled);
        Assert.False(user.TwoFactorEnabled);
        Assert.NotNull(user.DisabledAtUtc);
        Assert.Equal(1, user.AuthorityVersion);
        Assert.NotEqual(ValidPassword, user.PasswordHash);
        Assert.Equal(
            PasswordVerificationResult.Success,
            fixture.PasswordHasher.VerifyHashedPassword(
                user,
                Assert.IsType<string>(user.PasswordHash),
                ValidPassword));
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());

        string[] roles = await fixture.ReadRolesAsync(user.Id);
        Assert.Equal(
            [AetherRoles.Admin, AetherRoles.Observe],
            roles.Order(StringComparer.Ordinal).ToArray());

        IdentityUserToken<Guid>[] localTokens =
            await fixture.Database.Set<IdentityUserToken<Guid>>()
                .Where(token =>
                    token.LoginProvider ==
                        AetherLocalMfaCredentialProtector.LoginProvider)
                .ToArrayAsync();
        Assert.Single(
            localTokens,
            token =>
                token.Name ==
                    AetherLocalMfaCredentialProtector.TotpSecretName);
        Assert.Equal(
            10,
            localTokens.Count(token =>
                token.Name.StartsWith(
                    AetherLocalMfaCredentialProtector
                        .RecoveryCodeNamePrefix,
                    StringComparison.Ordinal)));
        foreach (IdentityUserToken<Guid> token in localTokens)
        {
            Assert.DoesNotContain(
                issue.SharedSecretBase32,
                token.Value ?? string.Empty,
                StringComparison.Ordinal);
            foreach (string recoveryCode in issue.RecoveryCodes)
            {
                Assert.DoesNotContain(
                    recoveryCode,
                    token.Name,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    recoveryCode,
                    token.Value ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Equal(10, issue.RecoveryCodes.Count);
        Assert.Equal(32, issue.SharedSecretBase32.Length);
        Assert.False(issue.Rotated);
        Assert.Equal(fixture.Time.GetUtcNow(), issue.AccountCreatedAtUtc);

        string enrollmentText = enrollment.ToString();
        string issueText = issue.ToString();
        Assert.DoesNotContain(ValidPassword, enrollmentText);
        Assert.DoesNotContain("first-admin", enrollmentText);
        Assert.DoesNotContain("admin@example.test", enrollmentText);
        Assert.DoesNotContain(issue.SharedSecretBase32, issueText);
        foreach (string recoveryCode in issue.RecoveryCodes)
        {
            Assert.DoesNotContain(recoveryCode, issueText);
        }

        AetherIdentityAuditRecord audit =
            Assert.Single(
                await fixture.Database.IdentityAuditRecords.ToArrayAsync());
        Assert.Equal(
            "identity.first-administrator.enrollment",
            audit.Action);
        Assert.DoesNotContain(ValidPassword, audit.DetailJson);
        Assert.DoesNotContain("first-admin", audit.DetailJson);
        Assert.DoesNotContain("admin@example.test", audit.DetailJson);
        Assert.DoesNotContain(issue.SharedSecretBase32, audit.DetailJson);
        foreach (string recoveryCode in issue.RecoveryCodes)
        {
            Assert.DoesNotContain(recoveryCode, audit.DetailJson);
        }
    }

    [Fact]
    public async Task ExecutorUsesFreshScopesAndReportsDurableIdentityPresence()
    {
        await using ProvisioningFixture fixture =
            await ProvisioningFixture.CreateAsync();

        Assert.False(await fixture.Executor.HasIdentityAsync());

        InstallationFirstLocalAdministratorEnrollmentIssue issue =
            await fixture.Executor.BeginAsync(
                fixture.Setup,
                CreateEnrollment(correlationId: "executor-first-admin"));

        Assert.NotEqual(Guid.Empty, issue.UserId);
        Assert.True(await fixture.Executor.HasIdentityAsync());
    }

    [Fact]
    public async Task TotpConfirmationEnablesCanonicalAdminWithoutCreatingSession()
    {
        await using ProvisioningFixture fixture =
            await ProvisioningFixture.CreateAsync();
        InstallationFirstLocalAdministratorEnrollmentIssue issue =
            await fixture.Service.BeginAsync(
                fixture.Setup,
                CreateEnrollment());

        InstallationFirstLocalAdministratorConfirmationResult confirmed =
            await fixture.Service.ConfirmAsync(
                fixture.Setup,
                CurrentTotp(
                    issue.SharedSecretBase32,
                    fixture.Time.GetUtcNow()),
                "confirm-first-admin");

        Assert.True(confirmed.Succeeded);
        Assert.Equal("first-local-administrator-confirmed", confirmed.Code);
        Assert.Equal(issue.UserId, confirmed.UserId);
        Assert.True(confirmed.MutationAttempted);

        AetherIdentityUser user =
            await fixture.Database.Users.SingleAsync();
        Assert.True(user.Enabled);
        Assert.True(user.TwoFactorEnabled);
        Assert.Null(user.DisabledAtUtc);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Null(user.LockoutEnd);
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
        Assert.NotNull(
            await fixture.Database.Set<IdentityUserToken<Guid>>()
                .SingleOrDefaultAsync(token =>
                    token.UserId == user.Id &&
                    token.Name ==
                        AetherLocalMfaCredentialProtector
                            .TotpLastAcceptedStepName));

        InstallationFirstAdministratorEvidence evidence =
            await fixture.Service.VerifyAsync(fixture.Setup);
        Assert.Equal(fixture.Setup.SetupSchemaVersion, evidence.SetupSchemaVersion);
        Assert.Equal(fixture.Setup.SetupRevision, evidence.SetupRevision);
        Assert.Equal(fixture.Setup.SetupCreatedAt, evidence.SetupCreatedAt);
        Assert.Equal(fixture.Setup.Topology, evidence.Topology);
        Assert.Equal(
            fixture.Setup.CanonicalPublicUrl,
            evidence.CanonicalPublicUrl);
        Assert.Equal($"local:{user.Id:D}", evidence.SubjectId);
        Assert.Equal(issue.AccountCreatedAtUtc, evidence.AccountCreatedAt);
        Assert.True(evidence.IsEnabled);
        Assert.Equal(
            [AetherRoles.Admin, AetherRoles.Observe],
            evidence.Roles.Order(StringComparer.Ordinal).ToArray());

        InstallationFirstLocalAdministratorConfirmationResult retry =
            await fixture.Service.ConfirmAsync(
                fixture.Setup,
                "not-a-code",
                "confirm-first-admin-retry");
        Assert.True(retry.Succeeded);
        Assert.False(retry.MutationAttempted);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.BeginAsync(
                fixture.Setup,
                CreateEnrollment(
                    password: RotatedPassword,
                    correlationId: "reissue-after-confirmation")));

        Guid transmitRoleId = await fixture.Database.Roles
            .Where(role => role.Name == AetherRoles.Transmit)
            .Select(role => role.Id)
            .SingleAsync();
        fixture.Database.Set<IdentityUserRole<Guid>>().Add(
            new()
            {
                UserId = user.Id,
                RoleId = transmitRoleId
            });
        await fixture.Database.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.VerifyAsync(fixture.Setup));
        InstallationFirstLocalAdministratorConfirmationResult tampered =
            await fixture.Service.ConfirmAsync(
                fixture.Setup,
                "not-a-code",
                "tampered-confirmation-retry");
        Assert.False(tampered.Succeeded);
    }

    [Fact]
    public async Task PendingEnrollmentCanRotateButOldCredentialsCannotConfirm()
    {
        await using ProvisioningFixture fixture =
            await ProvisioningFixture.CreateAsync();
        InstallationFirstLocalAdministratorEnrollmentIssue first =
            await fixture.Service.BeginAsync(
                fixture.Setup,
                CreateEnrollment());
        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        InstallationFirstLocalAdministratorEnrollmentIssue rotated =
            await fixture.Service.BeginAsync(
                fixture.Setup,
                CreateEnrollment(
                    password: RotatedPassword,
                    correlationId: "rotate-first-admin"));

        Assert.True(rotated.Rotated);
        Assert.Equal(first.UserId, rotated.UserId);
        Assert.Equal(first.AccountCreatedAtUtc, rotated.AccountCreatedAtUtc);
        Assert.NotEqual(
            first.SharedSecretBase32,
            rotated.SharedSecretBase32);
        Assert.Empty(first.RecoveryCodes.Intersect(rotated.RecoveryCodes));

        AetherIdentityUser user =
            await fixture.Database.Users.SingleAsync();
        Assert.Equal(
            PasswordVerificationResult.Failed,
            fixture.PasswordHasher.VerifyHashedPassword(
                user,
                Assert.IsType<string>(user.PasswordHash),
                ValidPassword));
        Assert.Equal(
            PasswordVerificationResult.Success,
            fixture.PasswordHasher.VerifyHashedPassword(
                user,
                user.PasswordHash,
                RotatedPassword));

        InstallationFirstLocalAdministratorConfirmationResult oldRejected =
            await fixture.Service.ConfirmAsync(
                fixture.Setup,
                CurrentTotp(
                    first.SharedSecretBase32,
                    fixture.Time.GetUtcNow()),
                "old-enrollment-rejected");
        Assert.False(oldRejected.Succeeded);
        Assert.Null(oldRejected.UserId);

        InstallationFirstLocalAdministratorConfirmationResult confirmed =
            await fixture.Service.ConfirmAsync(
                fixture.Setup,
                CurrentTotp(
                    rotated.SharedSecretBase32,
                    fixture.Time.GetUtcNow()),
                "rotated-enrollment-confirmed");
        Assert.True(confirmed.Succeeded);
        Assert.True(
            (await fixture.Database.Users.SingleAsync()).Enabled);
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
    }

    [Fact]
    public async Task FailedConfirmationLocksPendingIdentityUntilSetupRotatesIt()
    {
        await using ProvisioningFixture fixture =
            await ProvisioningFixture.CreateAsync();
        InstallationFirstLocalAdministratorEnrollmentIssue first =
            await fixture.Service.BeginAsync(
                fixture.Setup,
                CreateEnrollment());

        for (int attempt = 0;
            attempt < fixture.Policy.MaximumFailedAttempts;
            attempt++)
        {
            InstallationFirstLocalAdministratorConfirmationResult rejected =
                await fixture.Service.ConfirmAsync(
                    fixture.Setup,
                    "invalid",
                    $"invalid-confirmation-{attempt}");
            Assert.False(rejected.Succeeded);
            Assert.Equal(
                "first-local-administrator-confirmation-rejected",
                rejected.Code);
            Assert.False(rejected.MutationAttempted);
        }

        AetherIdentityUser locked =
            await fixture.Database.Users.SingleAsync();
        Assert.False(locked.Enabled);
        Assert.Equal(0, locked.AccessFailedCount);
        Assert.Equal(
            fixture.Time.GetUtcNow().Add(fixture.Policy.LockoutDuration),
            locked.LockoutEnd);

        InstallationFirstLocalAdministratorConfirmationResult whileLocked =
            await fixture.Service.ConfirmAsync(
                fixture.Setup,
                CurrentTotp(
                    first.SharedSecretBase32,
                    fixture.Time.GetUtcNow()),
                "correct-code-while-locked");
        Assert.False(whileLocked.Succeeded);
        Assert.False(
            (await fixture.Database.Users.SingleAsync()).Enabled);

        InstallationFirstLocalAdministratorEnrollmentIssue rotated =
            await fixture.Service.BeginAsync(
                fixture.Setup,
                CreateEnrollment(
                    password: RotatedPassword,
                    correlationId: "rotate-locked-enrollment"));
        AetherIdentityUser reset =
            await fixture.Database.Users.SingleAsync();
        Assert.Null(reset.LockoutEnd);
        Assert.Equal(0, reset.AccessFailedCount);

        InstallationFirstLocalAdministratorConfirmationResult confirmed =
            await fixture.Service.ConfirmAsync(
                fixture.Setup,
                CurrentTotp(
                    rotated.SharedSecretBase32,
                    fixture.Time.GetUtcNow()),
                "confirm-after-setup-rotation");
        Assert.True(confirmed.Succeeded);
    }

    [Fact]
    public async Task ExactSetupIdentityIsRequiredForEveryProvisioningPhase()
    {
        await using ProvisioningFixture fixture =
            await ProvisioningFixture.CreateAsync();
        InstallationFirstLocalAdministratorEnrollmentIssue issue =
            await fixture.Service.BeginAsync(
                fixture.Setup,
                CreateEnrollment());
        InstallationFirstAdministratorVerificationRequest different =
            fixture.Setup with
            {
                SetupRevision = fixture.Setup.SetupRevision + 1
            };

        InstallationFirstLocalAdministratorConfirmationResult rejected =
            await fixture.Service.ConfirmAsync(
                different,
                CurrentTotp(
                    issue.SharedSecretBase32,
                    fixture.Time.GetUtcNow()),
                "wrong-setup-confirmation");
        Assert.False(rejected.Succeeded);
        Assert.False(
            (await fixture.Database.Users.SingleAsync()).Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.VerifyAsync(different));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.BeginAsync(
                different,
                CreateEnrollment(
                    password: RotatedPassword,
                    correlationId: "wrong-setup-begin")));

        InstallationFirstLocalAdministratorConfirmationResult confirmed =
            await fixture.Service.ConfirmAsync(
                fixture.Setup,
                CurrentTotp(
                    issue.SharedSecretBase32,
                    fixture.Time.GetUtcNow()),
                "exact-setup-confirmation");
        Assert.True(confirmed.Succeeded);
    }

    [Fact]
    public async Task InvalidOrMissingPasswordCannotCreateDefaultAuthority()
    {
        await using ProvisioningFixture fixture =
            await ProvisioningFixture.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.BeginAsync(
                fixture.Setup,
                CreateEnrollment(
                    password: string.Empty,
                    correlationId: "empty-password")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.BeginAsync(
                fixture.Setup,
                CreateEnrollment(
                    password: null!,
                    correlationId: "null-password")));

        Assert.Empty(await fixture.Database.Users.ToArrayAsync());
        Assert.Empty(
            await fixture.Database.IdentityAuditRecords.ToArrayAsync());
        Assert.Empty(
            await fixture.Database.AuthenticationSessions.ToArrayAsync());
    }

    private static InstallationFirstLocalAdministratorEnrollment CreateEnrollment(
        string password = ValidPassword,
        string correlationId = "enrollment-001") =>
        new(
            "first-admin",
            "First Administrator",
            "admin@example.test",
            password,
            correlationId);

    private static string CurrentTotp(
        string sharedSecretBase32,
        DateTimeOffset now)
    {
        byte[] secret = DecodeBase32(sharedSecretBase32);
        byte[] movingFactor = new byte[sizeof(long)];
        byte[] hash = Array.Empty<byte>();
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
        List<byte> bytes = new();
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

    private sealed class ProvisioningFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory temporary;
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private ProvisioningFixture(
            TemporaryDirectory temporary,
            ServiceProvider provider,
            AsyncServiceScope scope,
            AetherIdentityDbContext database,
            AetherFirstLocalAdministratorProvisioningService service,
            IInstallationFirstLocalAdministratorProvisioningExecutor executor,
            IPasswordHasher<AetherIdentityUser> passwordHasher,
            AetherLocalAuthenticationPolicy policy,
            ManualTimeProvider time)
        {
            this.temporary = temporary;
            this.provider = provider;
            this.scope = scope;
            Database = database;
            Service = service;
            Executor = executor;
            PasswordHasher = passwordHasher;
            Policy = policy;
            Time = time;
            Setup = new(
                InstallationSetupState.CurrentSchemaVersion,
                SetupRevision: 9,
                SetupCreatedAt: time.GetUtcNow().AddHours(-1),
                InstallationTopologyKind.PersonalSingleStation,
                "https://aethersdr.example.test");
        }

        internal AetherIdentityDbContext Database { get; }

        internal AetherFirstLocalAdministratorProvisioningService Service
        {
            get;
        }

        internal IInstallationFirstLocalAdministratorProvisioningExecutor Executor
        {
            get;
        }

        internal IPasswordHasher<AetherIdentityUser> PasswordHasher { get; }

        internal AetherLocalAuthenticationPolicy Policy { get; }

        internal ManualTimeProvider Time { get; }

        internal InstallationFirstAdministratorVerificationRequest Setup
        {
            get;
        }

        internal static async Task<ProvisioningFixture> CreateAsync()
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
                        "The provisioning test database could not be initialized.");
                }

                AetherAuthenticationTopology topology =
                    AetherAuthenticationConfiguration.Validate(
                        new AuthSettings { Mode = "Local" },
                        isDevelopmentEnvironment: false);
                ManualTimeProvider time = new(
                    DateTimeOffset.Parse(
                        "2026-08-09T21:00:00Z",
                        CultureInfo.InvariantCulture));
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
                        AetherFirstLocalAdministratorProvisioningService>(),
                    provider.GetRequiredService<
                        IInstallationFirstLocalAdministratorProvisioningExecutor>(),
                    scoped.GetRequiredService<
                        IPasswordHasher<AetherIdentityUser>>(),
                    topology.LocalPolicy,
                    time);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        internal async Task<string[]> ReadRolesAsync(Guid userId) =>
            await (
                from userRole in
                    Database.Set<IdentityUserRole<Guid>>()
                join role in Database.Roles
                    on userRole.RoleId equals role.Id
                where userRole.UserId == userId && role.Name != null
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
                $"aethersdr-first-admin-tests-{Guid.NewGuid():N}");
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
