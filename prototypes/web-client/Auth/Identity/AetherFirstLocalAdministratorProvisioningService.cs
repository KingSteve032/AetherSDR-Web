using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AetherSDR.Web.Auth.Identity;

internal sealed class AetherFirstLocalAdministratorEnrollment
{
    internal AetherFirstLocalAdministratorEnrollment(
        string userName,
        string displayName,
        string? email,
        string password,
        string correlationId)
    {
        UserName = userName;
        DisplayName = displayName;
        Email = email;
        Password = password;
        CorrelationId = correlationId;
    }

    internal string UserName { get; }

    internal string DisplayName { get; }

    internal string? Email { get; }

    internal string Password { get; }

    internal string CorrelationId { get; }

    public override string ToString() =>
        $"{nameof(AetherFirstLocalAdministratorEnrollment)} " +
        $"{{ UserName = [redacted], DisplayName = [redacted], " +
        $"Email = [redacted], Password = [redacted], " +
        $"CorrelationId = {CorrelationId} }}";
}

internal sealed class AetherFirstLocalAdministratorEnrollmentIssue
{
    internal AetherFirstLocalAdministratorEnrollmentIssue(
        Guid userId,
        DateTimeOffset accountCreatedAtUtc,
        string sharedSecretBase32,
        IReadOnlyList<string> recoveryCodes,
        bool rotated)
    {
        UserId = userId;
        AccountCreatedAtUtc = accountCreatedAtUtc;
        SharedSecretBase32 = sharedSecretBase32;
        RecoveryCodes = recoveryCodes;
        Rotated = rotated;
    }

    internal Guid UserId { get; }

    internal DateTimeOffset AccountCreatedAtUtc { get; }

    internal string SharedSecretBase32 { get; }

    internal IReadOnlyList<string> RecoveryCodes { get; }

    internal bool Rotated { get; }

    public override string ToString() =>
        $"{nameof(AetherFirstLocalAdministratorEnrollmentIssue)} " +
        $"{{ UserId = {UserId:D}, AccountCreatedAtUtc = " +
        $"{AccountCreatedAtUtc:O}, SharedSecretBase32 = [redacted], " +
        $"RecoveryCodes = [redacted], Rotated = {Rotated} }}";
}

internal sealed record AetherFirstLocalAdministratorConfirmationResult(
    bool Succeeded,
    string Code,
    Guid? UserId,
    bool MutationAttempted);

internal sealed class AetherFirstLocalAdministratorProvisioningLock
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
}

internal sealed class AetherFirstLocalAdministratorProvisioningService(
    AetherIdentityDbContext database,
    IPasswordHasher<AetherIdentityUser> passwordHasher,
    ILookupNormalizer normalizer,
    AetherLocalMfaCredentialProtector credentialProtector,
    AetherLocalAuthenticationPolicy policy,
    AetherFirstLocalAdministratorProvisioningLock provisioningLock,
    TimeProvider timeProvider)
    : IInstallationFirstAdministratorVerifier
{
    private const string SetupLoginProvider = "Aether.Setup";
    private const string SetupBindingName = "FirstAdministratorBinding.v1";
    private const string AuditAction =
        "identity.first-administrator.enrollment";
    private const int RecoveryCodeCount = 10;

    internal async Task<AetherFirstLocalAdministratorEnrollmentIssue>
        BeginAsync(
            InstallationFirstAdministratorVerificationRequest setup,
            AetherFirstLocalAdministratorEnrollment enrollment,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(enrollment);
        ValidatedSetup validatedSetup = ValidateSetup(setup);
        ValidatedEnrollment validatedEnrollment =
            ValidateEnrollment(enrollment);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (now < setup.SetupCreatedAt)
        {
            throw new InvalidOperationException(
                "First-administrator enrollment time precedes setup creation.");
        }

        await provisioningLock.Gate.WaitAsync(cancellationToken);
        try
        {
            Guid userId = DeriveUserId(validatedSetup.Binding);
            AetherIdentityUser? user = await database.Users
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == userId,
                    cancellationToken);
            bool rotated = user is not null;
            DateTimeOffset accountCreatedAt;
            IdentityUserToken<Guid>? setupBinding = null;
            if (user is null)
            {
                if (await database.Users.AnyAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        "A first local administrator cannot be provisioned " +
                        "into a non-empty identity store.");
                }

                accountCreatedAt = now;
                user = new AetherIdentityUser
                {
                    Id = userId,
                    AuthorityVersion = 1,
                    Enabled = false,
                    DisabledAtUtc = now,
                    TwoFactorEnabled = false,
                    LockoutEnabled = true,
                    EmailConfirmed = false
                };
                database.Users.Add(user);
            }
            else
            {
                setupBinding = await FindSetupBindingAsync(
                    userId,
                    cancellationToken);
                accountCreatedAt = ValidateExistingPendingUser(
                    user,
                    setupBinding,
                    validatedSetup.Binding);
                if (await database.Users.CountAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException(
                        "First-administrator enrollment requires one exact " +
                        "pending setup-bound identity.");
                }
            }

            user.UserName = validatedEnrollment.UserName;
            user.NormalizedUserName = validatedEnrollment.NormalizedUserName;
            user.DisplayName = validatedEnrollment.DisplayName;
            user.Email = validatedEnrollment.Email;
            user.NormalizedEmail = validatedEnrollment.NormalizedEmail;
            user.PasswordHash = passwordHasher.HashPassword(
                user,
                enrollment.Password);
            user.Enabled = false;
            user.DisabledAtUtc = now;
            user.TwoFactorEnabled = false;
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.ConcurrencyStamp = Guid.NewGuid().ToString("N");

            await ReplaceRolesAsync(userId, cancellationToken);
            await RemoveLocalMfaTokensAsync(userId, cancellationToken);

            AetherLocalTotpEnrollmentCredential totp =
                credentialProtector.GenerateTotpEnrollmentCredential(userId);
            database.Set<IdentityUserToken<Guid>>().Add(totp.Token);
            List<string> recoveryCodes = new(RecoveryCodeCount);
            for (int index = 0; index < RecoveryCodeCount; index++)
            {
                AetherLocalRecoveryCredential recovery =
                    AetherLocalMfaCredentialProtector
                        .GenerateRecoveryCredential(userId);
                recoveryCodes.Add(recovery.Code);
                database.Set<IdentityUserToken<Guid>>().Add(
                    recovery.Token);
            }

            string bindingValue = CreateBindingValue(
                validatedSetup.Binding,
                accountCreatedAt);
            if (setupBinding is null)
            {
                database.Set<IdentityUserToken<Guid>>().Add(
                    new()
                    {
                        UserId = userId,
                        LoginProvider = SetupLoginProvider,
                        Name = SetupBindingName,
                        Value = bindingValue
                    });
            }
            else
            {
                setupBinding.Value = bindingValue;
            }

            AddAudit(
                userId,
                enrollment.CorrelationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                rotated
                    ? "first-local-administrator-enrollment-rotated"
                    : "first-local-administrator-enrollment-started",
                validatedSetup.Binding,
                mutationAttempted: true);
            await database.SaveChangesAsync(cancellationToken);

            return new(
                userId,
                accountCreatedAt,
                totp.SharedSecretBase32,
                recoveryCodes.AsReadOnly(),
                rotated);
        }
        finally
        {
            provisioningLock.Gate.Release();
        }
    }

    internal async Task<AetherFirstLocalAdministratorConfirmationResult>
        ConfirmAsync(
            InstallationFirstAdministratorVerificationRequest setup,
            string? totpCode,
            string correlationId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ValidatedSetup validatedSetup = ValidateSetup(setup);
        ValidateCorrelationId(correlationId);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await provisioningLock.Gate.WaitAsync(cancellationToken);
        try
        {
            Guid userId = DeriveUserId(validatedSetup.Binding);
            AetherIdentityUser? user = await database.Users
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == userId,
                    cancellationToken);
            IdentityUserToken<Guid>? setupBinding =
                user is null
                    ? null
                    : await FindSetupBindingAsync(
                        userId,
                        cancellationToken);
            if (user is null ||
                !BindingMatches(
                    setupBinding,
                    validatedSetup.Binding,
                    out _) ||
                await database.Users.CountAsync(cancellationToken) != 1)
            {
                return await RejectConfirmationAsync(
                    subjectUserId: null,
                    correlationId,
                    "first-local-administrator-binding-rejected",
                    validatedSetup.Binding,
                    now,
                    cancellationToken);
            }

            if (user.Enabled && user.TwoFactorEnabled)
            {
                if (!await IsCanonicalConfirmedAuthorityAsync(
                        user,
                        cancellationToken))
                {
                    return await RejectConfirmationAsync(
                        user.Id,
                        correlationId,
                        "first-local-administrator-state-rejected",
                        validatedSetup.Binding,
                        now,
                        cancellationToken);
                }

                AddAudit(
                    user.Id,
                    correlationId,
                    AetherIdentityAuditOutcome.Succeeded,
                    now,
                    "first-local-administrator-already-confirmed",
                    validatedSetup.Binding,
                    mutationAttempted: false);
                await database.SaveChangesAsync(cancellationToken);
                return new(
                    Succeeded: true,
                    Code: "first-local-administrator-confirmed",
                    user.Id,
                    MutationAttempted: false);
            }
            if (user.Enabled ||
                user.TwoFactorEnabled ||
                user.DisabledAtUtc is null ||
                !user.LockoutEnabled ||
                string.IsNullOrWhiteSpace(user.PasswordHash) ||
                user.LockoutEnd is DateTimeOffset lockoutEnd &&
                lockoutEnd > now)
            {
                return await RejectConfirmationAsync(
                    user.Id,
                    correlationId,
                    "first-local-administrator-state-rejected",
                    validatedSetup.Binding,
                    now,
                    cancellationToken);
            }

            IdentityUserToken<Guid>? secretToken =
                await FindLocalMfaTokenAsync(
                    user.Id,
                    AetherLocalMfaCredentialProtector.TotpSecretName,
                    cancellationToken);
            bool accepted = credentialProtector.TryUnprotectTotpSecret(
                secretToken,
                out byte[] secret);
            long acceptedStep = -1;
            try
            {
                accepted = accepted &&
                    AetherTotp.TryVerify(
                        secret,
                        totpCode,
                        now,
                        out acceptedStep);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
            if (!accepted)
            {
                return await RegisterConfirmationFailureAsync(
                    user,
                    correlationId,
                    validatedSetup.Binding,
                    now,
                    cancellationToken);
            }

            IdentityUserToken<Guid>? replayState =
                await FindLocalMfaTokenAsync(
                    user.Id,
                    AetherLocalMfaCredentialProtector
                        .TotpLastAcceptedStepName,
                    cancellationToken);
            if (replayState is null)
            {
                database.Set<IdentityUserToken<Guid>>().Add(
                    new()
                    {
                        UserId = user.Id,
                        LoginProvider =
                            AetherLocalMfaCredentialProtector.LoginProvider,
                        Name = AetherLocalMfaCredentialProtector
                            .TotpLastAcceptedStepName,
                        Value = acceptedStep.ToString(
                            CultureInfo.InvariantCulture)
                    });
            }
            else
            {
                replayState.Value = acceptedStep.ToString(
                    CultureInfo.InvariantCulture);
            }

            user.Enabled = true;
            user.DisabledAtUtc = null;
            user.TwoFactorEnabled = true;
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            AddAudit(
                user.Id,
                correlationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                "first-local-administrator-confirmed",
                validatedSetup.Binding,
                mutationAttempted: true);
            await database.SaveChangesAsync(cancellationToken);
            return new(
                Succeeded: true,
                Code: "first-local-administrator-confirmed",
                user.Id,
                MutationAttempted: true);
        }
        finally
        {
            provisioningLock.Gate.Release();
        }
    }

    public async Task<InstallationFirstAdministratorEvidence> VerifyAsync(
        InstallationFirstAdministratorVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatedSetup setup = ValidateSetup(request);

        await provisioningLock.Gate.WaitAsync(cancellationToken);
        try
        {
            Guid userId = DeriveUserId(setup.Binding);
            AetherIdentityUser user = await database.Users
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == userId,
                    cancellationToken) ??
                throw new InvalidOperationException(
                    "The setup-bound first local administrator was not found.");
            IdentityUserToken<Guid>? binding =
                await FindSetupBindingAsync(userId, cancellationToken);
            if (!BindingMatches(
                    binding,
                    setup.Binding,
                    out DateTimeOffset accountCreatedAt) ||
                await database.Users.CountAsync(cancellationToken) != 1 ||
                !await IsCanonicalConfirmedAuthorityAsync(
                    user,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "The first local administrator is not confirmed canonical authority.");
            }

            string[] roles = await ReadRolesAsync(userId, cancellationToken);

            return new(
                request.SetupSchemaVersion,
                request.SetupRevision,
                request.SetupCreatedAt,
                request.Topology,
                request.CanonicalPublicUrl,
                $"local:{user.Id:D}",
                accountCreatedAt,
                IsEnabled: true,
                roles);
        }
        finally
        {
            provisioningLock.Gate.Release();
        }
    }

    private async Task ReplaceRolesAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        IdentityUserRole<Guid>[] existing =
            await database.Set<IdentityUserRole<Guid>>()
                .Where(userRole => userRole.UserId == userId)
                .ToArrayAsync(cancellationToken);
        database.Set<IdentityUserRole<Guid>>().RemoveRange(existing);

        string[] requiredRoles = [AetherRoles.Admin, AetherRoles.Observe];
        foreach (string roleName in requiredRoles)
        {
            Guid roleId = await database.Roles
                .Where(role => role.Name == roleName)
                .Select(role => role.Id)
                .SingleAsync(cancellationToken);
            database.Set<IdentityUserRole<Guid>>().Add(
                new()
                {
                    UserId = userId,
                    RoleId = roleId
                });
        }
    }

    private async Task<string[]> ReadRolesAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await (
            from userRole in database.Set<IdentityUserRole<Guid>>()
            join role in database.Roles
                on userRole.RoleId equals role.Id
            where userRole.UserId == userId && role.Name != null
            orderby role.Name
            select role.Name!)
            .ToArrayAsync(cancellationToken);

    private async Task<bool> IsCanonicalConfirmedAuthorityAsync(
        AetherIdentityUser user,
        CancellationToken cancellationToken)
    {
        if (!user.Enabled ||
            !user.TwoFactorEnabled ||
            user.DisabledAtUtc is not null ||
            string.IsNullOrWhiteSpace(user.PasswordHash) ||
            user.AuthorityVersion != 1 ||
            !user.LockoutEnabled ||
            user.LockoutEnd is not null ||
            user.AccessFailedCount != 0)
        {
            return false;
        }

        string[] roles = await ReadRolesAsync(
            user.Id,
            cancellationToken);
        if (roles.Length != 2 ||
            !roles.Contains(AetherRoles.Admin, StringComparer.Ordinal) ||
            !roles.Contains(AetherRoles.Observe, StringComparer.Ordinal))
        {
            return false;
        }

        IdentityUserToken<Guid>? secretToken =
            await FindLocalMfaTokenAsync(
                user.Id,
                AetherLocalMfaCredentialProtector.TotpSecretName,
                cancellationToken);
        bool validSecret = credentialProtector.TryUnprotectTotpSecret(
            secretToken,
            out byte[] secret);
        CryptographicOperations.ZeroMemory(secret);
        if (!validSecret)
        {
            return false;
        }

        IdentityUserToken<Guid>? replayState =
            await FindLocalMfaTokenAsync(
                user.Id,
                AetherLocalMfaCredentialProtector
                    .TotpLastAcceptedStepName,
                cancellationToken);
        if (replayState?.Value is null ||
            !long.TryParse(
                replayState.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long acceptedStep) ||
            acceptedStep < 0)
        {
            return false;
        }

        int recoveryCodes =
            await database.Set<IdentityUserToken<Guid>>()
                .CountAsync(
                    token =>
                        token.UserId == user.Id &&
                        token.LoginProvider ==
                            AetherLocalMfaCredentialProtector.LoginProvider &&
                        token.Name.StartsWith(
                            AetherLocalMfaCredentialProtector
                                .RecoveryCodeNamePrefix) &&
                        token.Value == "active",
                    cancellationToken);
        return recoveryCodes == RecoveryCodeCount;
    }

    private async Task RemoveLocalMfaTokensAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        IdentityUserToken<Guid>[] tokens =
            await database.Set<IdentityUserToken<Guid>>()
                .Where(token =>
                    token.UserId == userId &&
                    token.LoginProvider ==
                        AetherLocalMfaCredentialProtector.LoginProvider)
                .ToArrayAsync(cancellationToken);
        database.Set<IdentityUserToken<Guid>>().RemoveRange(tokens);
    }

    private async Task<IdentityUserToken<Guid>?> FindSetupBindingAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await database.Set<IdentityUserToken<Guid>>()
            .SingleOrDefaultAsync(
                token =>
                    token.UserId == userId &&
                    token.LoginProvider == SetupLoginProvider &&
                    token.Name == SetupBindingName,
                cancellationToken);

    private async Task<IdentityUserToken<Guid>?> FindLocalMfaTokenAsync(
        Guid userId,
        string name,
        CancellationToken cancellationToken) =>
        await database.Set<IdentityUserToken<Guid>>()
            .SingleOrDefaultAsync(
                token =>
                    token.UserId == userId &&
                    token.LoginProvider ==
                        AetherLocalMfaCredentialProtector.LoginProvider &&
                    token.Name == name,
                cancellationToken);

    private async Task<AetherFirstLocalAdministratorConfirmationResult>
        RegisterConfirmationFailureAsync(
            AetherIdentityUser user,
            string correlationId,
            string setupBinding,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        if (user.AccessFailedCount < 0 ||
            user.AccessFailedCount >= policy.MaximumFailedAttempts)
        {
            return await RejectConfirmationAsync(
                user.Id,
                correlationId,
                "first-local-administrator-state-invalid",
                setupBinding,
                now,
                cancellationToken);
        }

        int failedAttempts = user.AccessFailedCount + 1;
        string code =
            "first-local-administrator-confirmation-rejected";
        if (failedAttempts >= policy.MaximumFailedAttempts)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = now.Add(policy.LockoutDuration);
            code = "first-local-administrator-confirmation-locked";
        }
        else
        {
            user.AccessFailedCount = failedAttempts;
            user.LockoutEnd = null;
        }
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        AddAudit(
            user.Id,
            correlationId,
            AetherIdentityAuditOutcome.Rejected,
            now,
            code,
            setupBinding,
            mutationAttempted: true);
        await database.SaveChangesAsync(cancellationToken);
        return RejectedConfirmation();
    }

    private async Task<AetherFirstLocalAdministratorConfirmationResult>
        RejectConfirmationAsync(
            Guid? subjectUserId,
            string correlationId,
            string code,
            string setupBinding,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        AddAudit(
            subjectUserId,
            correlationId,
            AetherIdentityAuditOutcome.Rejected,
            now,
            code,
            setupBinding,
            mutationAttempted: false);
        await database.SaveChangesAsync(cancellationToken);
        return RejectedConfirmation();
    }

    private void AddAudit(
        Guid? subjectUserId,
        string correlationId,
        AetherIdentityAuditOutcome outcome,
        DateTimeOffset occurredAt,
        string code,
        string setupBinding,
        bool mutationAttempted)
    {
        database.IdentityAuditRecords.Add(new AetherIdentityAuditRecord
        {
            OccurredAtUtc = occurredAt,
            ActorUserId = null,
            SubjectUserId = subjectUserId,
            Action = AuditAction,
            Outcome = outcome,
            CorrelationId = correlationId,
            DetailJson = JsonSerializer.Serialize(
                new
                {
                    code,
                    setupBinding,
                    mutationAttempted
                })
        });
    }

    private DateTimeOffset ValidateExistingPendingUser(
        AetherIdentityUser user,
        IdentityUserToken<Guid>? setupBinding,
        string expectedBinding)
    {
        if (!BindingMatches(
                setupBinding,
                expectedBinding,
                out DateTimeOffset accountCreatedAt) ||
            user.Enabled ||
            user.TwoFactorEnabled ||
            user.DisabledAtUtc is null ||
            user.AuthorityVersion != 1)
        {
            throw new InvalidOperationException(
                "An existing first-administrator identity cannot be rotated.");
        }
        return accountCreatedAt;
    }

    private ValidatedEnrollment ValidateEnrollment(
        AetherFirstLocalAdministratorEnrollment enrollment)
    {
        string userName = ValidateExactText(
            enrollment.UserName,
            1,
            100,
            "user name");
        string? normalizedUserName = normalizer.NormalizeName(userName);
        if (string.IsNullOrWhiteSpace(normalizedUserName) ||
            normalizedUserName.Length > 100 ||
            normalizedUserName.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "The first-administrator user name cannot be normalized.");
        }
        string displayName = ValidateExactText(
            enrollment.DisplayName,
            1,
            200,
            "display name");
        string? email = null;
        string? normalizedEmail = null;
        if (enrollment.Email is not null)
        {
            email = ValidateExactText(
                enrollment.Email,
                3,
                320,
                "email address");
            normalizedEmail = normalizer.NormalizeEmail(email);
            if (string.IsNullOrWhiteSpace(normalizedEmail) ||
                normalizedEmail.Length > 320 ||
                normalizedEmail.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    "The first-administrator email cannot be normalized.");
            }
        }
        if (enrollment.Password is null ||
            enrollment.Password.Length <
                policy.MinimumPasswordLength ||
            enrollment.Password.Length >
                policy.MaximumPasswordLength)
        {
            throw new InvalidOperationException(
                "The first-administrator password does not satisfy the " +
                "configured bounded length policy.");
        }
        ValidateCorrelationId(enrollment.CorrelationId);
        return new(
            userName,
            normalizedUserName,
            displayName,
            email,
            normalizedEmail);
    }

    private static ValidatedSetup ValidateSetup(
        InstallationFirstAdministratorVerificationRequest setup)
    {
        if (setup.SetupSchemaVersion !=
                InstallationSetupState.CurrentSchemaVersion ||
            setup.SetupRevision < 0 ||
            setup.SetupCreatedAt == default ||
            !Enum.IsDefined(setup.Topology))
        {
            throw new InvalidOperationException(
                "First-administrator provisioning requires valid setup identity.");
        }
        _ = InstallationTopologyProfile.For(setup.Topology);
        CanonicalPublicUrl publicUrl =
            CanonicalPublicUrl.Parse(setup.CanonicalPublicUrl);
        if (!string.Equals(
                publicUrl.Value,
                setup.CanonicalPublicUrl,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "First-administrator provisioning requires a canonical public URL.");
        }

        string payload = string.Join(
            '\n',
            "aethersdr-first-local-administrator-v1",
            setup.SetupSchemaVersion.ToString(CultureInfo.InvariantCulture),
            setup.SetupRevision.ToString(CultureInfo.InvariantCulture),
            setup.SetupCreatedAt.ToString(
                "O",
                CultureInfo.InvariantCulture),
            ((int)setup.Topology).ToString(CultureInfo.InvariantCulture),
            setup.CanonicalPublicUrl);
        return new(
            Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
    }

    private static string CreateBindingValue(
        string binding,
        DateTimeOffset accountCreatedAt) =>
        string.Join(
            '|',
            binding,
            accountCreatedAt.ToString("O", CultureInfo.InvariantCulture));

    private static bool BindingMatches(
        IdentityUserToken<Guid>? token,
        string expectedBinding,
        out DateTimeOffset accountCreatedAt)
    {
        accountCreatedAt = default;
        if (token is null ||
            !string.Equals(
                token.LoginProvider,
                SetupLoginProvider,
                StringComparison.Ordinal) ||
            !string.Equals(
                token.Name,
                SetupBindingName,
                StringComparison.Ordinal) ||
            token.Value is null)
        {
            return false;
        }

        string[] values = token.Value.Split('|');
        if (values.Length != 2 ||
            values[0].Length != 64 ||
            expectedBinding.Length != 64)
        {
            return false;
        }

        byte[] storedBinding = Encoding.ASCII.GetBytes(values[0]);
        byte[] candidateBinding = Encoding.ASCII.GetBytes(expectedBinding);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    storedBinding,
                    candidateBinding) ||
                !DateTimeOffset.TryParseExact(
                    values[1],
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out accountCreatedAt))
            {
                accountCreatedAt = default;
                return false;
            }
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(storedBinding);
            CryptographicOperations.ZeroMemory(candidateBinding);
        }
    }

    private static Guid DeriveUserId(string binding)
    {
        byte[] digest = SHA256.HashData(
            Encoding.ASCII.GetBytes(
                $"aethersdr-first-local-administrator-user-v1\n{binding}"));
        try
        {
            Guid userId = new(digest.AsSpan(0, 16));
            if (userId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "The setup-bound administrator identity is invalid.");
            }
            return userId;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static string ValidateExactText(
        string? value,
        int minimumLength,
        int maximumLength,
        string field)
    {
        string candidate = value ?? string.Empty;
        if (candidate.Length < minimumLength ||
            candidate.Length > maximumLength ||
            !string.Equals(
                candidate,
                candidate.Trim(),
                StringComparison.Ordinal) ||
            candidate.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"The first-administrator {field} is not a bounded exact value.");
        }
        return candidate;
    }

    private static void ValidateCorrelationId(string value)
    {
        _ = ValidateExactText(
            value,
            1,
            100,
            "correlation identifier");
    }

    private static AetherFirstLocalAdministratorConfirmationResult
        RejectedConfirmation() =>
        new(
            Succeeded: false,
            Code: "first-local-administrator-confirmation-rejected",
            UserId: null,
            MutationAttempted: false);

    private sealed record ValidatedSetup(string Binding);

    private sealed record ValidatedEnrollment(
        string UserName,
        string NormalizedUserName,
        string DisplayName,
        string? Email,
        string? NormalizedEmail);
}
