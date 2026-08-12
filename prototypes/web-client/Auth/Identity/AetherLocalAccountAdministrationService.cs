using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AetherSDR.Web.Auth.Identity;

internal sealed class AetherIdentityAdministrationLock
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
}

internal sealed class AetherLocalAccountEnrollmentRequest
{
    internal AetherLocalAccountEnrollmentRequest(
        string userName,
        string displayName,
        string? email,
        string password,
        IReadOnlyCollection<string> roles,
        string correlationId)
    {
        UserName = userName;
        DisplayName = displayName;
        Email = email;
        Password = password;
        Roles = roles;
        CorrelationId = correlationId;
    }

    internal string UserName { get; }

    internal string DisplayName { get; }

    internal string? Email { get; }

    internal string Password { get; }

    internal IReadOnlyCollection<string> Roles { get; }

    internal string CorrelationId { get; }

    public override string ToString() =>
        $"{nameof(AetherLocalAccountEnrollmentRequest)} " +
        "{ UserName = [redacted], DisplayName = [redacted], " +
        "Email = [redacted], Password = [redacted], " +
        $"Roles = [redacted], CorrelationId = {CorrelationId} }}";
}

internal sealed class AetherLocalAccountEnrollmentIssue
{
    internal AetherLocalAccountEnrollmentIssue(
        Guid userId,
        Guid enrollmentId,
        string sharedSecretBase32,
        IReadOnlyList<string> recoveryCodes)
    {
        UserId = userId;
        EnrollmentId = enrollmentId;
        SharedSecretBase32 = sharedSecretBase32;
        RecoveryCodes = recoveryCodes;
    }

    internal Guid UserId { get; }

    internal Guid EnrollmentId { get; }

    internal string SharedSecretBase32 { get; }

    internal IReadOnlyList<string> RecoveryCodes { get; }

    public override string ToString() =>
        $"{nameof(AetherLocalAccountEnrollmentIssue)} " +
        $"{{ UserId = {UserId:D}, EnrollmentId = {EnrollmentId:D}, " +
        "SharedSecretBase32 = [redacted], RecoveryCodes = [redacted] }";
}

internal sealed class AetherIdentityAccountProvisioningRequest
{
    internal AetherIdentityAccountProvisioningRequest(
        string userName,
        string displayName,
        string? email,
        IReadOnlyCollection<string> roles,
        string correlationId)
    {
        UserName = userName;
        DisplayName = displayName;
        Email = email;
        Roles = roles;
        CorrelationId = correlationId;
    }

    internal string UserName { get; }

    internal string DisplayName { get; }

    internal string? Email { get; }

    internal IReadOnlyCollection<string> Roles { get; }

    internal string CorrelationId { get; }

    public override string ToString() =>
        $"{nameof(AetherIdentityAccountProvisioningRequest)} " +
        "{ UserName = [redacted], DisplayName = [redacted], " +
        "Email = [redacted], Roles = [redacted], " +
        $"CorrelationId = {CorrelationId} }}";
}

internal sealed class AetherLocalAccountPasswordResetRequest
{
    internal AetherLocalAccountPasswordResetRequest(
        Guid userId,
        string password,
        string correlationId)
    {
        UserId = userId;
        Password = password;
        CorrelationId = correlationId;
    }

    internal Guid UserId { get; }

    internal string Password { get; }

    internal string CorrelationId { get; }

    public override string ToString() =>
        $"{nameof(AetherLocalAccountPasswordResetRequest)} " +
        $"{{ UserId = {UserId:D}, Password = [redacted], " +
        $"CorrelationId = {CorrelationId} }}";
}

internal sealed record AetherIdentityAccountMutationResult(
    bool Succeeded,
    string Code,
    Guid UserId,
    long AuthorityVersion,
    int RevokedSessionCount,
    bool MutationAttempted);

internal sealed record AetherIdentityAccountSummary(
    Guid UserId,
    string UserName,
    string DisplayName,
    string? Email,
    bool Enabled,
    bool TwoFactorEnabled,
    bool HasLocalPassword,
    long AuthorityVersion,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> ExternalProviderIds);

internal sealed record AetherIdentityAccountPage(
    int Offset,
    int Limit,
    int TotalCount,
    IReadOnlyList<AetherIdentityAccountSummary> Accounts);

internal sealed class AetherAdministratorReauthenticationRequiredException()
    : InvalidOperationException(
        "Current canonical administrator authority with fresh durable " +
        "reauthentication is required.");

internal sealed class AetherLocalAccountAdministrationService(
    AetherIdentityDbContext database,
    IPasswordHasher<AetherIdentityUser> passwordHasher,
    ILookupNormalizer normalizer,
    AetherLocalMfaCredentialProtector credentialProtector,
    AetherLocalAuthenticationPolicy policy,
    AetherAuthenticationTopology topology,
    AetherIdentityAdministrationLock administrationLock,
    TimeProvider timeProvider)
{
    private const string AdministrationLoginProvider = "Aether.Administration";
    private const string PendingEnrollmentName = "PendingLocalEnrollment.v1";
    private const int RecoveryCodeCount = 10;
    private const int MaximumAccountPageSize = 200;

    internal async Task<AetherIdentityAccountPage> ListAsync(
        ClaimsPrincipal administrator,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        if (offset < 0 || limit is < 1 or > MaximumAccountPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "Identity account pages require a non-negative offset and " +
                $"a limit between 1 and {MaximumAccountPageSize}.");
        }
        DateTimeOffset now = timeProvider.GetUtcNow();

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            _ = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            int totalCount = await database.Users.CountAsync(cancellationToken);
            AetherIdentityUser[] users = await database.Users
                .OrderBy(user => user.NormalizedUserName)
                .ThenBy(user => user.Id)
                .Skip(offset)
                .Take(limit)
                .ToArrayAsync(cancellationToken);
            Guid[] userIds = users.Select(user => user.Id).ToArray();
            IdentityUserRole<Guid>[] assignments = await database
                .Set<IdentityUserRole<Guid>>()
                .Where(assignment => userIds.Contains(assignment.UserId))
                .ToArrayAsync(cancellationToken);
            Dictionary<Guid, string> roleNames = await database.Roles
                .Where(role => role.Name != null)
                .ToDictionaryAsync(
                    role => role.Id,
                    role => role.Name!,
                    cancellationToken);
            AetherExternalIdentity[] externalIdentities =
                await database.ExternalIdentities
                    .Where(identity => userIds.Contains(identity.UserId))
                    .ToArrayAsync(cancellationToken);
            List<AetherIdentityAccountSummary> accounts =
                new(users.Length);
            foreach (AetherIdentityUser user in users)
            {
                string[] roles = assignments
                    .Where(assignment => assignment.UserId == user.Id)
                    .Select(assignment => roleNames.GetValueOrDefault(
                        assignment.RoleId))
                    .Where(role => role is not null)
                    .Select(role => role!)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                string[] providerIds = externalIdentities
                    .Where(identity => identity.UserId == user.Id)
                    .Select(identity => identity.ProviderId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                accounts.Add(
                    new(
                        user.Id,
                        user.UserName ?? string.Empty,
                        user.DisplayName,
                        user.Email,
                        user.Enabled,
                        user.TwoFactorEnabled,
                        !string.IsNullOrWhiteSpace(user.PasswordHash),
                        user.AuthorityVersion,
                        roles,
                        providerIds));
            }
            await transaction.CommitAsync(cancellationToken);
            return new(
                offset,
                limit,
                totalCount,
                accounts.AsReadOnly());
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    internal async Task<AetherIdentityAccountMutationResult>
        ProvisionExternalAccountAsync(
            ClaimsPrincipal administrator,
            AetherIdentityAccountProvisioningRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ArgumentNullException.ThrowIfNull(request);
        if (topology.ExternalProvider is null)
        {
            throw new InvalidOperationException(
                "External account provisioning requires a configured provider.");
        }
        ValidatedIdentity identity = ValidateIdentity(
            request.UserName,
            request.DisplayName,
            request.Email,
            request.Roles,
            request.CorrelationId);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            Guid actorUserId = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            if (await database.Users.AnyAsync(
                    candidate =>
                        candidate.NormalizedUserName ==
                            identity.NormalizedUserName,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "The requested identity account already exists.");
            }

            Guid userId = Guid.NewGuid();
            AetherIdentityUser user = new()
            {
                Id = userId,
                UserName = identity.UserName,
                NormalizedUserName = identity.NormalizedUserName,
                DisplayName = identity.DisplayName,
                Email = identity.Email,
                NormalizedEmail = identity.NormalizedEmail,
                PasswordHash = null,
                Enabled = false,
                DisabledAtUtc = now,
                AuthorityVersion = 1,
                TwoFactorEnabled = false,
                LockoutEnabled = true,
                EmailConfirmed = false,
                AccessFailedCount = 0,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };
            database.Users.Add(user);
            await AddRolesAsync(userId, identity.Roles, cancellationToken);
            AddAudit(
                actorUserId,
                userId,
                "identity.external-account.provisioned",
                request.CorrelationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                new
                {
                    code = "external-account-provisioned",
                    userId,
                    roles = identity.Roles,
                    enabled = false
                });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                Succeeded: true,
                Code: "external-account-provisioned",
                userId,
                user.AuthorityVersion,
                RevokedSessionCount: 0,
                MutationAttempted: true);
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    internal async Task<AetherLocalAccountEnrollmentIssue> BeginEnrollmentAsync(
        ClaimsPrincipal administrator,
        AetherLocalAccountEnrollmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ArgumentNullException.ThrowIfNull(request);
        ValidatedEnrollment enrollment = ValidateEnrollment(request);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            Guid actorUserId = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            if (await database.Users.AnyAsync(
                    candidate =>
                        candidate.NormalizedUserName ==
                            enrollment.NormalizedUserName,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "The requested local account identity already exists.");
            }

            Guid userId = Guid.NewGuid();
            Guid enrollmentId = Guid.NewGuid();
            AetherIdentityUser user = new()
            {
                Id = userId,
                UserName = enrollment.UserName,
                NormalizedUserName = enrollment.NormalizedUserName,
                DisplayName = enrollment.DisplayName,
                Email = enrollment.Email,
                NormalizedEmail = enrollment.NormalizedEmail,
                PasswordHash = null,
                Enabled = false,
                DisabledAtUtc = now,
                AuthorityVersion = 1,
                TwoFactorEnabled = false,
                LockoutEnabled = true,
                EmailConfirmed = false,
                AccessFailedCount = 0,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };
            user.PasswordHash = passwordHasher.HashPassword(
                user,
                request.Password);
            database.Users.Add(user);
            await AddRolesAsync(userId, enrollment.Roles, cancellationToken);

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
                database.Set<IdentityUserToken<Guid>>().Add(recovery.Token);
            }
            database.Set<IdentityUserToken<Guid>>().Add(
                new()
                {
                    UserId = userId,
                    LoginProvider = AdministrationLoginProvider,
                    Name = PendingEnrollmentName,
                    Value = ComputeEnrollmentBinding(enrollmentId)
                });
            AddAudit(
                actorUserId,
                userId,
                "identity.local-account.enrollment-begun",
                request.CorrelationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                new
                {
                    code = "local-account-enrollment-begun",
                    userId,
                    roles = enrollment.Roles
                });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                userId,
                enrollmentId,
                totp.SharedSecretBase32,
                recoveryCodes.AsReadOnly());
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    internal async Task<AetherIdentityAccountMutationResult>
        ConfirmEnrollmentAsync(
            ClaimsPrincipal administrator,
            Guid userId,
            Guid enrollmentId,
            string? totpCode,
            string correlationId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ValidateIdentifier(userId, nameof(userId));
        ValidateIdentifier(enrollmentId, nameof(enrollmentId));
        ValidateCorrelationId(correlationId);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            Guid actorUserId = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            AetherIdentityUser? user = await database.Users
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == userId,
                    cancellationToken);
            IdentityUserToken<Guid>? pending = await FindTokenAsync(
                userId,
                AdministrationLoginProvider,
                PendingEnrollmentName,
                cancellationToken);
            IdentityUserToken<Guid>? secret = await FindTokenAsync(
                userId,
                AetherLocalMfaCredentialProtector.LoginProvider,
                AetherLocalMfaCredentialProtector.TotpSecretName,
                cancellationToken);
            string expectedBinding = ComputeEnrollmentBinding(enrollmentId);
            bool validPending =
                user is not null &&
                !user.Enabled &&
                !user.TwoFactorEnabled &&
                user.DisabledAtUtc is not null &&
                user.AuthorityVersion == 1 &&
                pending?.Value is string storedBinding &&
                FixedTimeEquals(storedBinding, expectedBinding);
            byte[] sharedSecret = [];
            bool validSecret =
                validPending &&
                credentialProtector.TryUnprotectTotpSecret(
                    secret,
                    out sharedSecret);
            long matchingStep = 0;
            bool accepted = false;
            try
            {
                accepted =
                    validSecret &&
                    AetherTotp.TryVerify(
                        sharedSecret,
                        totpCode,
                        now,
                        out matchingStep);
            }
            finally
            {
                if (sharedSecret is not null)
                {
                    CryptographicOperations.ZeroMemory(sharedSecret);
                }
            }

            if (!accepted)
            {
                AddAudit(
                    actorUserId,
                    userId,
                    "identity.local-account.enrollment-confirmed",
                    correlationId,
                    AetherIdentityAuditOutcome.Rejected,
                    now,
                    new
                    {
                        code = "local-account-enrollment-confirmation-rejected",
                        userId
                    });
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(
                    Succeeded: false,
                    Code: "local-account-enrollment-confirmation-rejected",
                    userId,
                    AuthorityVersion: user?.AuthorityVersion ?? 0,
                    RevokedSessionCount: 0,
                    MutationAttempted: false);
            }

            user!.Enabled = true;
            user.DisabledAtUtc = null;
            user.TwoFactorEnabled = true;
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            database.Set<IdentityUserToken<Guid>>().Remove(pending!);
            database.Set<IdentityUserToken<Guid>>().Add(
                new()
                {
                    UserId = userId,
                    LoginProvider =
                        AetherLocalMfaCredentialProtector.LoginProvider,
                    Name =
                        AetherLocalMfaCredentialProtector
                            .TotpLastAcceptedStepName,
                    Value = matchingStep.ToString(
                        CultureInfo.InvariantCulture)
                });
            AddAudit(
                actorUserId,
                userId,
                "identity.local-account.enrollment-confirmed",
                correlationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                new
                {
                    code = "local-account-enrollment-confirmed",
                    userId
                });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                Succeeded: true,
                Code: "local-account-enrollment-confirmed",
                userId,
                user.AuthorityVersion,
                RevokedSessionCount: 0,
                MutationAttempted: true);
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    internal async Task<AetherIdentityAccountMutationResult> ResetPasswordAsync(
        ClaimsPrincipal administrator,
        AetherLocalAccountPasswordResetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifier(request.UserId, nameof(request.UserId));
        ValidatePassword(request.Password);
        ValidateCorrelationId(request.CorrelationId);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            Guid actorUserId = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            AetherIdentityUser user = await RequireLocalUserAsync(
                request.UserId,
                cancellationToken);
            RotateAuthority(user);
            user.PasswordHash = passwordHasher.HashPassword(
                user,
                request.Password);
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            int revokedSessionCount = await RevokeActiveSessionsAsync(
                user.Id,
                now,
                "administrator-password-reset",
                cancellationToken);
            AddAudit(
                actorUserId,
                user.Id,
                "identity.local-account.password-reset",
                request.CorrelationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                new
                {
                    code = "local-account-password-reset",
                    userId = user.Id,
                    authorityVersion = user.AuthorityVersion,
                    revokedSessionCount
                });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                Succeeded: true,
                Code: "local-account-password-reset",
                user.Id,
                user.AuthorityVersion,
                revokedSessionCount,
                MutationAttempted: true);
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    internal async Task<AetherIdentityAccountMutationResult> ReplaceRolesAsync(
        ClaimsPrincipal administrator,
        Guid userId,
        IReadOnlyCollection<string> roles,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ValidateIdentifier(userId, nameof(userId));
        string[] validatedRoles = ValidateRoles(roles);
        ValidateCorrelationId(correlationId);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            Guid actorUserId = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            AetherIdentityUser user = await database.Users.SingleOrDefaultAsync(
                    candidate => candidate.Id == userId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "The target identity does not exist.");
            string[] currentRoles = await ReadRolesAsync(
                userId,
                cancellationToken);
            if (currentRoles.SequenceEqual(
                    validatedRoles,
                    StringComparer.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                return new(
                    Succeeded: true,
                    Code: "identity-account-roles-converged",
                    userId,
                    user.AuthorityVersion,
                    RevokedSessionCount: 0,
                    MutationAttempted: false);
            }

            if (user.Enabled &&
                currentRoles.Contains(
                    AetherRoles.Admin,
                    StringComparer.Ordinal) &&
                !validatedRoles.Contains(
                    AetherRoles.Admin,
                    StringComparer.Ordinal) &&
                await CountEnabledAdministratorsAsync(cancellationToken) <= 1)
            {
                throw new InvalidOperationException(
                    "The final enabled administrator role cannot be removed.");
            }

            IdentityUserRole<Guid>[] assignments = await database
                .Set<IdentityUserRole<Guid>>()
                .Where(candidate => candidate.UserId == userId)
                .ToArrayAsync(cancellationToken);
            database.Set<IdentityUserRole<Guid>>().RemoveRange(assignments);
            await AddRolesAsync(userId, validatedRoles, cancellationToken);
            RotateAuthority(user);
            int revokedSessionCount = await RevokeActiveSessionsAsync(
                userId,
                now,
                "administrator-role-change",
                cancellationToken);
            AddAudit(
                actorUserId,
                userId,
                "identity.account.roles-replaced",
                correlationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                new
                {
                    code = "identity-account-roles-replaced",
                    userId,
                    roles = validatedRoles,
                    authorityVersion = user.AuthorityVersion,
                    revokedSessionCount
                });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                Succeeded: true,
                Code: "identity-account-roles-replaced",
                userId,
                user.AuthorityVersion,
                revokedSessionCount,
                MutationAttempted: true);
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    internal async Task<AetherIdentityAccountMutationResult> SetEnabledAsync(
        ClaimsPrincipal administrator,
        Guid userId,
        bool enabled,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ValidateIdentifier(userId, nameof(userId));
        ValidateCorrelationId(correlationId);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            Guid actorUserId = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            AetherIdentityUser user = await database.Users.SingleOrDefaultAsync(
                    candidate => candidate.Id == userId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "The target identity does not exist.");
            if (user.Enabled == enabled)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(
                    Succeeded: true,
                    Code: enabled
                        ? "identity-account-enabled-converged"
                        : "identity-account-disabled-converged",
                    userId,
                    user.AuthorityVersion,
                    RevokedSessionCount: 0,
                    MutationAttempted: false);
            }

            string[] roles = await ReadRolesAsync(userId, cancellationToken);
            if (!enabled &&
                roles.Contains(AetherRoles.Admin, StringComparer.Ordinal) &&
                await CountEnabledAdministratorsAsync(cancellationToken) <= 1)
            {
                throw new InvalidOperationException(
                    "The final enabled administrator cannot be disabled.");
            }
            if (enabled &&
                !await HasUsableSignInMethodAsync(user, cancellationToken))
            {
                throw new InvalidOperationException(
                    "The identity cannot be enabled without a usable sign-in method.");
            }

            RotateAuthority(user);
            user.Enabled = enabled;
            user.DisabledAtUtc = enabled ? null : now;
            if (enabled)
            {
                user.AccessFailedCount = 0;
                user.LockoutEnd = null;
            }
            int revokedSessionCount = await RevokeActiveSessionsAsync(
                userId,
                now,
                enabled
                    ? "administrator-account-enabled"
                    : "administrator-account-disabled",
                cancellationToken);
            string code = enabled
                ? "identity-account-enabled"
                : "identity-account-disabled";
            AddAudit(
                actorUserId,
                userId,
                enabled
                    ? "identity.account.enabled"
                    : "identity.account.disabled",
                correlationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                new
                {
                    code,
                    userId,
                    authorityVersion = user.AuthorityVersion,
                    revokedSessionCount
                });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                Succeeded: true,
                Code: code,
                userId,
                user.AuthorityVersion,
                revokedSessionCount,
                MutationAttempted: true);
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    internal async Task<AetherIdentityAccountMutationResult> RevokeSessionsAsync(
        ClaimsPrincipal administrator,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ValidateIdentifier(userId, nameof(userId));
        ValidateCorrelationId(correlationId);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            Guid actorUserId = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            AetherIdentityUser user = await database.Users.SingleOrDefaultAsync(
                    candidate => candidate.Id == userId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "The target identity does not exist.");
            RotateAuthority(user);
            int revokedSessionCount = await RevokeActiveSessionsAsync(
                userId,
                now,
                "administrator-session-revocation",
                cancellationToken);
            AddAudit(
                actorUserId,
                userId,
                "identity.account.sessions-revoked",
                correlationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                new
                {
                    code = "identity-account-sessions-revoked",
                    userId,
                    authorityVersion = user.AuthorityVersion,
                    revokedSessionCount
                });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                Succeeded: true,
                Code: "identity-account-sessions-revoked",
                userId,
                user.AuthorityVersion,
                revokedSessionCount,
                MutationAttempted: true);
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    private async Task<Guid> RequireFreshAdministratorAsync(
        ClaimsPrincipal administrator,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!AetherAuthenticationSessionService.TryReadCanonicalIdentity(
                administrator,
                out Guid actorUserId,
                out Guid actorSessionId,
                out long authorityVersion) ||
            !administrator.IsInRole(AetherRoles.Admin))
        {
            throw FreshAdministratorRequired();
        }

        AetherAuthenticationSession? session =
            await database.AuthenticationSessions
                .Include(candidate => candidate.User)
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.Id == actorSessionId &&
                        candidate.UserId == actorUserId,
                    cancellationToken);
        if (session is null ||
            session.AuthorityVersion != authorityVersion ||
            session.User.AuthorityVersion != authorityVersion ||
            session.ReauthenticatedAtUtc is not DateTimeOffset reauthenticated ||
            reauthenticated > now ||
            now - reauthenticated >
                policy.AdministratorReauthenticationLifetime)
        {
            throw FreshAdministratorRequired();
        }

        string[] roles = await ReadRolesAsync(
            actorUserId,
            cancellationToken);
        try
        {
            _ = AetherCanonicalPrincipalFactory.Create(
                session.User,
                session,
                roles,
                now);
        }
        catch (InvalidOperationException)
        {
            throw FreshAdministratorRequired();
        }
        if (!roles.Contains(AetherRoles.Admin, StringComparer.Ordinal))
        {
            throw FreshAdministratorRequired();
        }
        return actorUserId;
    }

    private async Task<bool> HasUsableSignInMethodAsync(
        AetherIdentityUser user,
        CancellationToken cancellationToken)
    {
        if (topology.ExternalProvider is AetherExternalProviderDescriptor provider &&
            await database.ExternalIdentities.AnyAsync(
                identity =>
                    identity.UserId == user.Id &&
                    identity.ProviderId == provider.ProviderId,
                cancellationToken))
        {
            return true;
        }
        if (!topology.LocalAccountsEnabled ||
            string.IsNullOrWhiteSpace(user.PasswordHash) ||
            !user.TwoFactorEnabled ||
            !user.LockoutEnabled)
        {
            return false;
        }

        IdentityUserToken<Guid>[] tokens =
            await database.Set<IdentityUserToken<Guid>>()
                .Where(token =>
                    token.UserId == user.Id &&
                    token.LoginProvider ==
                        AetherLocalMfaCredentialProtector.LoginProvider &&
                    (token.Name ==
                        AetherLocalMfaCredentialProtector.TotpSecretName ||
                     token.Name.StartsWith(
                         AetherLocalMfaCredentialProtector
                             .RecoveryCodeNamePrefix)))
                .ToArrayAsync(cancellationToken);
        foreach (IdentityUserToken<Guid> token in tokens)
        {
            if (string.Equals(
                    token.Name,
                    AetherLocalMfaCredentialProtector.TotpSecretName,
                    StringComparison.Ordinal) &&
                credentialProtector.TryUnprotectTotpSecret(
                    token,
                    out byte[] secret))
            {
                CryptographicOperations.ZeroMemory(secret);
                return true;
            }
            if (IsActiveRecoveryCredential(token))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsActiveRecoveryCredential(
        IdentityUserToken<Guid> token)
    {
        const int bindingLength = 64;
        string prefix =
            AetherLocalMfaCredentialProtector.RecoveryCodeNamePrefix;
        if (!token.Name.StartsWith(prefix, StringComparison.Ordinal) ||
            token.Name.Length != prefix.Length + bindingLength ||
            !string.Equals(token.Value, "active", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (char character in token.Name.AsSpan(prefix.Length))
        {
            if (character is not (>= 'a' and <= 'f' or >= '0' and <= '9'))
            {
                return false;
            }
        }
        return true;
    }

    private async Task<AetherIdentityUser> RequireLocalUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        AetherIdentityUser user = await database.Users.SingleOrDefaultAsync(
                candidate => candidate.Id == userId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "The target identity does not exist.");
        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            throw new InvalidOperationException(
                "The target identity has no local password credential.");
        }
        return user;
    }

    private async Task AddRolesAsync(
        Guid userId,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
    {
        Dictionary<string, Guid> roleIds = await database.Roles
            .Where(role => role.Name != null && roles.Contains(role.Name))
            .ToDictionaryAsync(
                role => role.Name!,
                role => role.Id,
                StringComparer.Ordinal,
                cancellationToken);
        if (roleIds.Count != roles.Count)
        {
            throw new InvalidOperationException(
                "The canonical identity roles are unavailable.");
        }
        foreach (string role in roles)
        {
            database.Set<IdentityUserRole<Guid>>().Add(
                new()
                {
                    UserId = userId,
                    RoleId = roleIds[role]
                });
        }
    }

    private async Task<string[]> ReadRolesAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await (
            from assignment in database.Set<IdentityUserRole<Guid>>()
            join role in database.Roles
                on assignment.RoleId equals role.Id
            where assignment.UserId == userId && role.Name != null
            orderby role.Name
            select role.Name!)
            .ToArrayAsync(cancellationToken);

    private async Task<int> CountEnabledAdministratorsAsync(
        CancellationToken cancellationToken) =>
        await (
            from assignment in database.Set<IdentityUserRole<Guid>>()
            join role in database.Roles
                on assignment.RoleId equals role.Id
            join user in database.Users
                on assignment.UserId equals user.Id
            where role.Name == AetherRoles.Admin && user.Enabled
            select user.Id)
            .Distinct()
            .CountAsync(cancellationToken);

    private async Task<int> RevokeActiveSessionsAsync(
        Guid userId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        AetherAuthenticationSession[] sessions =
            (await database.AuthenticationSessions
                .Where(session =>
                    session.UserId == userId &&
                    session.RevokedAtUtc == null)
                .ToArrayAsync(cancellationToken))
            .Where(session => session.AbsoluteExpiresAtUtc > now)
            .ToArray();
        foreach (AetherAuthenticationSession session in sessions)
        {
            session.RevokedAtUtc = now;
            session.RevocationReason = reason;
        }
        return sessions.Length;
    }

    private static void RotateAuthority(AetherIdentityUser user)
    {
        if (user.AuthorityVersion is <= 0 or long.MaxValue)
        {
            throw new InvalidOperationException(
                "The target identity authority version cannot be rotated.");
        }
        user.AuthorityVersion++;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }

    private async Task<IdentityUserToken<Guid>?> FindTokenAsync(
        Guid userId,
        string loginProvider,
        string name,
        CancellationToken cancellationToken) =>
        await database.Set<IdentityUserToken<Guid>>().SingleOrDefaultAsync(
            candidate =>
                candidate.UserId == userId &&
                candidate.LoginProvider == loginProvider &&
                candidate.Name == name,
            cancellationToken);

    private ValidatedEnrollment ValidateEnrollment(
        AetherLocalAccountEnrollmentRequest request)
    {
        ValidatedIdentity identity = ValidateIdentity(
            request.UserName,
            request.DisplayName,
            request.Email,
            request.Roles,
            request.CorrelationId);
        ValidatePassword(request.Password);
        return new(
            identity.UserName,
            identity.NormalizedUserName,
            identity.DisplayName,
            identity.Email,
            identity.NormalizedEmail,
            identity.Roles);
    }

    private ValidatedIdentity ValidateIdentity(
        string? requestedUserName,
        string? requestedDisplayName,
        string? requestedEmail,
        IReadOnlyCollection<string>? requestedRoles,
        string correlationId)
    {
        string userName = ValidateExactText(
            requestedUserName,
            1,
            100,
            "user name");
        string? normalizedUserName = normalizer.NormalizeName(userName);
        if (string.IsNullOrWhiteSpace(normalizedUserName) ||
            normalizedUserName.Length > 100 ||
            normalizedUserName.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "The identity account user name cannot be normalized.");
        }
        string displayName = ValidateExactText(
            requestedDisplayName,
            1,
            200,
            "display name");
        string? email = null;
        string? normalizedEmail = null;
        if (requestedEmail is not null)
        {
            email = ValidateExactText(
                requestedEmail,
                3,
                320,
                "email address");
            normalizedEmail = normalizer.NormalizeEmail(email);
            if (string.IsNullOrWhiteSpace(normalizedEmail) ||
                normalizedEmail.Length > 320 ||
                normalizedEmail.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    "The identity account email cannot be normalized.");
            }
        }
        string[] roles = ValidateRoles(requestedRoles);
        ValidateCorrelationId(correlationId);
        return new(
            userName,
            normalizedUserName,
            displayName,
            email,
            normalizedEmail,
            roles);
    }

    private void ValidatePassword(string? password)
    {
        if (password is null ||
            password.Length < policy.MinimumPasswordLength ||
            password.Length > policy.MaximumPasswordLength)
        {
            throw new InvalidOperationException(
                "The local account password does not satisfy the configured " +
                "bounded length policy.");
        }
    }

    private static string[] ValidateRoles(
        IReadOnlyCollection<string>? roles)
    {
        if (roles is null ||
            roles.Count == 0 ||
            roles.Count > AetherRoles.All.Length ||
            roles.Any(string.IsNullOrWhiteSpace) ||
            roles.Distinct(StringComparer.Ordinal).Count() != roles.Count ||
            roles.Any(role =>
                !AetherRoles.All.Contains(role, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                "Identity account roles must be a non-empty exact subset of " +
                "the canonical roles.");
        }
        return AetherRoles.All
            .Where(role => roles.Contains(role, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
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
                $"The identity account {field} is not a bounded exact value.");
        }
        return candidate;
    }

    private static void ValidateCorrelationId(string value) =>
        _ = ValidateExactText(
            value,
            1,
            100,
            "correlation identifier");

    private static void ValidateIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "The identity identifier cannot be empty.",
                parameterName);
        }
    }

    private static string ComputeEnrollmentBinding(Guid enrollmentId) =>
        Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.ASCII.GetBytes(
                    $"aethersdr-local-account-enrollment-v1\n{enrollmentId:D}")));

    private static bool FixedTimeEquals(string stored, string expected)
    {
        if (stored.Length != expected.Length)
        {
            return false;
        }
        byte[] storedBytes = Encoding.ASCII.GetBytes(stored);
        byte[] expectedBytes = Encoding.ASCII.GetBytes(expected);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                storedBytes,
                expectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(storedBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private void AddAudit(
        Guid actorUserId,
        Guid subjectUserId,
        string action,
        string correlationId,
        AetherIdentityAuditOutcome outcome,
        DateTimeOffset now,
        object detail)
    {
        database.IdentityAuditRecords.Add(
            new()
            {
                OccurredAtUtc = now,
                ActorUserId = actorUserId,
                SubjectUserId = subjectUserId,
                Action = action,
                Outcome = outcome,
                CorrelationId = correlationId,
                DetailJson = JsonSerializer.Serialize(detail)
            });
    }

    private static InvalidOperationException FreshAdministratorRequired() =>
        new AetherAdministratorReauthenticationRequiredException();

    private sealed record ValidatedIdentity(
        string UserName,
        string NormalizedUserName,
        string DisplayName,
        string? Email,
        string? NormalizedEmail,
        string[] Roles);

    private sealed record ValidatedEnrollment(
        string UserName,
        string NormalizedUserName,
        string DisplayName,
        string? Email,
        string? NormalizedEmail,
        string[] Roles);
}
