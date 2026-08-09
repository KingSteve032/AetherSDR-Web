using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AetherSDR.Web.Auth.Identity;

internal sealed record AetherLocalPasswordVerificationResult(
    bool ReadyForSecondFactor,
    string Code,
    string? ChallengeToken);

internal sealed class AetherLocalPasswordAuthenticationService(
    AetherIdentityDbContext database,
    IPasswordHasher<AetherIdentityUser> passwordHasher,
    ILookupNormalizer normalizer,
    AetherLocalPasswordTimingDefense timingDefense,
    AetherLocalMfaChallengeStore challenges,
    AetherLocalAuthenticationPolicy policy,
    TimeProvider timeProvider)
{
    private const string AuditAction = "authentication.local.password";

    internal async Task<AetherLocalPasswordVerificationResult> VerifyAsync(
        string? userName,
        string? password,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ValidateCorrelationId(correlationId);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await VerifyOnceAsync(
                    userName,
                    password,
                    correlationId,
                    cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.ChangeTracker.Clear();
            }
        }
    }

    private async Task<AetherLocalPasswordVerificationResult> VerifyOnceAsync(
        string? userName,
        string? password,
        string correlationId,
        CancellationToken cancellationToken)
    {
        bool boundedPassword =
            password is not null &&
            password.Length is > 0 &&
            password.Length <= policy.MaximumPasswordLength;
        string? normalizedUserName = NormalizeUserName(userName);
        AetherIdentityUser? user = normalizedUserName is null
            ? null
            : await database.Users.SingleOrDefaultAsync(
                candidate =>
                    candidate.NormalizedUserName == normalizedUserName,
                cancellationToken);

        if (user is null || !boundedPassword)
        {
            if (boundedPassword)
            {
                _ = passwordHasher.VerifyHashedPassword(
                    timingDefense.User,
                    timingDefense.PasswordHash,
                    password!);
            }
            AddAudit(
                subjectUserId: null,
                correlationId,
                AetherIdentityAuditOutcome.Rejected,
                "local-password-rejected",
                failedAttempts: null,
                lockoutEndUtc: null);
            await database.SaveChangesAsync(cancellationToken);
            return Rejected();
        }

        PasswordVerificationResult verification =
            VerifyStoredPassword(user, password!);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!user.Enabled)
        {
            AddAudit(
                user.Id,
                correlationId,
                AetherIdentityAuditOutcome.Rejected,
                "local-password-user-disabled",
                user.AccessFailedCount,
                user.LockoutEnd);
            await database.SaveChangesAsync(cancellationToken);
            return Rejected();
        }
        if (!user.LockoutEnabled)
        {
            AddAudit(
                user.Id,
                correlationId,
                AetherIdentityAuditOutcome.Rejected,
                "local-password-lockout-disabled",
                user.AccessFailedCount,
                user.LockoutEnd);
            await database.SaveChangesAsync(cancellationToken);
            return Rejected();
        }
        if (user.LockoutEnd is DateTimeOffset lockoutEnd &&
            lockoutEnd > now)
        {
            AddAudit(
                user.Id,
                correlationId,
                AetherIdentityAuditOutcome.Rejected,
                "local-password-locked",
                user.AccessFailedCount,
                lockoutEnd);
            await database.SaveChangesAsync(cancellationToken);
            return Rejected();
        }

        if (verification == PasswordVerificationResult.Failed)
        {
            if (user.AccessFailedCount < 0 ||
                user.AccessFailedCount >= policy.MaximumFailedAttempts)
            {
                AddAudit(
                    user.Id,
                    correlationId,
                    AetherIdentityAuditOutcome.Failed,
                    "local-password-state-invalid",
                    user.AccessFailedCount,
                    user.LockoutEnd);
                await database.SaveChangesAsync(cancellationToken);
                return Rejected();
            }

            int failedAttempts = user.AccessFailedCount + 1;
            string auditCode = "local-password-rejected";
            if (failedAttempts >= policy.MaximumFailedAttempts)
            {
                user.AccessFailedCount = 0;
                user.LockoutEnd = now.Add(policy.LockoutDuration);
                auditCode = "local-password-lockout-started";
            }
            else
            {
                user.AccessFailedCount = failedAttempts;
                user.LockoutEnd = null;
            }
            MarkUserMutation(user);
            AddAudit(
                user.Id,
                correlationId,
                AetherIdentityAuditOutcome.Rejected,
                auditCode,
                failedAttempts,
                user.LockoutEnd);
            await database.SaveChangesAsync(cancellationToken);
            return Rejected();
        }

        if (verification ==
            PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash =
                passwordHasher.HashPassword(user, password!);
            MarkUserMutation(user);
        }

        if (!user.TwoFactorEnabled)
        {
            AddAudit(
                user.Id,
                correlationId,
                AetherIdentityAuditOutcome.Rejected,
                "local-mfa-not-enrolled",
                user.AccessFailedCount,
                user.LockoutEnd);
            await database.SaveChangesAsync(cancellationToken);
            return new(
                ReadyForSecondFactor: false,
                Code: "local-mfa-enrollment-required",
                ChallengeToken: null);
        }

        AetherLocalMfaChallengeIssue challenge = challenges.Issue(user);
        if (!challenge.Succeeded || challenge.Token is null)
        {
            AddAudit(
                user.Id,
                correlationId,
                AetherIdentityAuditOutcome.Failed,
                "local-mfa-challenge-unavailable",
                user.AccessFailedCount,
                user.LockoutEnd);
            await database.SaveChangesAsync(cancellationToken);
            return Rejected();
        }

        AddAudit(
            user.Id,
            correlationId,
            AetherIdentityAuditOutcome.Succeeded,
            "local-password-verified",
            user.AccessFailedCount,
            user.LockoutEnd);
        await database.SaveChangesAsync(cancellationToken);
        return new(
            ReadyForSecondFactor: true,
            Code: "local-second-factor-required",
            challenge.Token);
    }

    private PasswordVerificationResult VerifyStoredPassword(
        AetherIdentityUser user,
        string password)
    {
        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            _ = passwordHasher.VerifyHashedPassword(
                timingDefense.User,
                timingDefense.PasswordHash,
                password);
            return PasswordVerificationResult.Failed;
        }

        try
        {
            return passwordHasher.VerifyHashedPassword(
                user,
                user.PasswordHash,
                password);
        }
        catch (FormatException)
        {
            _ = passwordHasher.VerifyHashedPassword(
                timingDefense.User,
                timingDefense.PasswordHash,
                password);
            return PasswordVerificationResult.Failed;
        }
    }

    private string? NormalizeUserName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 100 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            return null;
        }

        string? normalized = normalizer.NormalizeName(value);
        return string.IsNullOrWhiteSpace(normalized) ||
               normalized.Length > 100 ||
               normalized.Any(char.IsControl)
            ? null
            : normalized;
    }

    private void AddAudit(
        Guid? subjectUserId,
        string correlationId,
        AetherIdentityAuditOutcome outcome,
        string code,
        int? failedAttempts,
        DateTimeOffset? lockoutEndUtc)
    {
        database.IdentityAuditRecords.Add(new AetherIdentityAuditRecord
        {
            OccurredAtUtc = timeProvider.GetUtcNow(),
            ActorUserId = null,
            SubjectUserId = subjectUserId,
            Action = AuditAction,
            Outcome = outcome,
            CorrelationId = correlationId,
            DetailJson = JsonSerializer.Serialize(
                new
                {
                    code,
                    failedAttempts,
                    lockoutEndUtc
                })
        });
    }

    private static void MarkUserMutation(AetherIdentityUser user)
    {
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }

    private static AetherLocalPasswordVerificationResult Rejected() =>
        new(
            ReadyForSecondFactor: false,
            Code: "local-password-rejected",
            ChallengeToken: null);

    private static void ValidateCorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 100 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The local authentication correlation identifier must be an " +
                "exact value of at most 100 characters.",
                nameof(value));
        }
    }
}
