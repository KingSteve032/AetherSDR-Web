using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AetherSDR.Web.Auth.Identity;

internal sealed record AetherExternalIdentityLinkAuthorization(
    Guid ActorUserId,
    Guid ActorSessionId,
    Guid TargetUserId,
    string ProviderId);

internal sealed record AetherExternalIdentityMutationResult(
    bool Succeeded,
    string Code,
    Guid UserId,
    string ProviderId,
    long AuthorityVersion,
    int RevokedSessionCount,
    bool MutationAttempted);

internal sealed class AetherExternalIdentityAdministrationService(
    AetherIdentityDbContext database,
    AetherAuthenticationTopology topology,
    AetherLocalMfaCredentialProtector credentialProtector,
    AetherIdentityAdministrationLock administrationLock,
    TimeProvider timeProvider)
{
    private const string LinkAction = "identity.external-identity.linked";
    private const string UnlinkAction = "identity.external-identity.unlinked";

    internal async Task<AetherExternalIdentityLinkAuthorization>
        AuthorizeLinkAsync(
            ClaimsPrincipal administrator,
            Guid targetUserId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ValidateIdentifier(targetUserId, nameof(targetUserId));
        AetherExternalProviderDescriptor provider =
            topology.ExternalProvider ??
            throw new InvalidOperationException(
                "External identity linking requires a configured provider.");
        DateTimeOffset now = timeProvider.GetUtcNow();

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            FreshAdministrator authority = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            _ = await RequireUserAsync(targetUserId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                authority.UserId,
                authority.SessionId,
                targetUserId,
                provider.ProviderId);
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    internal async Task<AetherExternalIdentityMutationResult> LinkAsync(
        ClaimsPrincipal administrator,
        Guid expectedActorUserId,
        Guid expectedActorSessionId,
        Guid targetUserId,
        AetherExternalProviderDescriptor provider,
        ClaimsPrincipal externalPrincipal,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(externalPrincipal);
        ValidateIdentifier(expectedActorUserId, nameof(expectedActorUserId));
        ValidateIdentifier(expectedActorSessionId, nameof(expectedActorSessionId));
        ValidateIdentifier(targetUserId, nameof(targetUserId));
        ValidateCorrelationId(correlationId);
        RequireConfiguredProvider(provider);

        AetherExternalIdentityEvidence evidence =
            AetherExternalIdentityEvidenceReader.Read(
                provider,
                externalPrincipal);
        DateTimeOffset now = timeProvider.GetUtcNow();
        string subjectBinding = ComputeSubjectBinding(evidence.Key);

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            FreshAdministrator authority = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            if (authority.UserId != expectedActorUserId ||
                authority.SessionId != expectedActorSessionId)
            {
                throw new AetherAdministratorReauthenticationRequiredException();
            }

            AetherIdentityUser user = await RequireUserAsync(
                targetUserId,
                cancellationToken);
            if (evidence.AuthenticatedAtUtc is not DateTimeOffset authenticatedAt ||
                authenticatedAt > now ||
                now - authenticatedAt >
                    topology.LocalPolicy
                        .AdministratorReauthenticationLifetime)
            {
                AddAudit(
                    authority.UserId,
                    targetUserId,
                    LinkAction,
                    correlationId,
                    AetherIdentityAuditOutcome.Rejected,
                    now,
                    new
                    {
                        code = "external-identity-link-not-fresh",
                        userId = targetUserId,
                        providerId = provider.ProviderId,
                        subjectBinding
                    });
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(
                    Succeeded: false,
                    Code: "external-identity-link-rejected",
                    targetUserId,
                    provider.ProviderId,
                    user.AuthorityVersion,
                    RevokedSessionCount: 0,
                    MutationAttempted: false);
            }

            AetherExternalIdentity? existingBySubject =
                await database.ExternalIdentities.SingleOrDefaultAsync(
                    identity =>
                        identity.ProviderId == evidence.Key.ProviderId &&
                        identity.Issuer == evidence.Key.Issuer &&
                        identity.Subject == evidence.Key.Subject,
                    cancellationToken);
            AetherExternalIdentity? existingForUser =
                await database.ExternalIdentities.SingleOrDefaultAsync(
                    identity =>
                        identity.UserId == targetUserId &&
                        identity.ProviderId == provider.ProviderId,
                    cancellationToken);
            if (existingBySubject is not null &&
                existingBySubject.UserId == targetUserId &&
                existingForUser?.Id == existingBySubject.Id)
            {
                AddAudit(
                    authority.UserId,
                    targetUserId,
                    LinkAction,
                    correlationId,
                    AetherIdentityAuditOutcome.Succeeded,
                    now,
                    new
                    {
                        code = "external-identity-link-converged",
                        userId = targetUserId,
                        providerId = provider.ProviderId,
                        subjectBinding,
                        authorityVersion = user.AuthorityVersion
                    });
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(
                    Succeeded: true,
                    Code: "external-identity-link-converged",
                    targetUserId,
                    provider.ProviderId,
                    user.AuthorityVersion,
                    RevokedSessionCount: 0,
                    MutationAttempted: false);
            }

            if (existingBySubject is not null || existingForUser is not null)
            {
                AddAudit(
                    authority.UserId,
                    targetUserId,
                    LinkAction,
                    correlationId,
                    AetherIdentityAuditOutcome.Rejected,
                    now,
                    new
                    {
                        code = "external-identity-link-conflict",
                        userId = targetUserId,
                        providerId = provider.ProviderId,
                        subjectBinding
                    });
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(
                    Succeeded: false,
                    Code: "external-identity-link-rejected",
                    targetUserId,
                    provider.ProviderId,
                    user.AuthorityVersion,
                    RevokedSessionCount: 0,
                    MutationAttempted: false);
            }

            database.ExternalIdentities.Add(
                new()
                {
                    Id = Guid.NewGuid(),
                    UserId = targetUserId,
                    User = user,
                    ProviderId = provider.ProviderId,
                    Issuer = evidence.Key.Issuer,
                    Subject = evidence.Key.Subject,
                    LinkedAtUtc = now
                });
            RotateAuthority(user);
            int revokedSessionCount = await RevokeActiveSessionsAsync(
                targetUserId,
                now,
                "administrator-external-identity-link",
                cancellationToken);
            AddAudit(
                authority.UserId,
                targetUserId,
                LinkAction,
                correlationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                new
                {
                    code = "external-identity-linked",
                    userId = targetUserId,
                    providerId = provider.ProviderId,
                    subjectBinding,
                    authorityVersion = user.AuthorityVersion,
                    revokedSessionCount
                });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                Succeeded: true,
                Code: "external-identity-linked",
                targetUserId,
                provider.ProviderId,
                user.AuthorityVersion,
                revokedSessionCount,
                MutationAttempted: true);
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    internal async Task<AetherExternalIdentityMutationResult> UnlinkAsync(
        ClaimsPrincipal administrator,
        Guid targetUserId,
        string providerId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        ValidateIdentifier(targetUserId, nameof(targetUserId));
        ValidateProviderId(providerId);
        ValidateCorrelationId(correlationId);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await administrationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            FreshAdministrator authority = await RequireFreshAdministratorAsync(
                administrator,
                now,
                cancellationToken);
            AetherIdentityUser user = await RequireUserAsync(
                targetUserId,
                cancellationToken);
            AetherExternalIdentity? link =
                await database.ExternalIdentities.SingleOrDefaultAsync(
                    identity =>
                        identity.UserId == targetUserId &&
                        identity.ProviderId == providerId,
                    cancellationToken);
            if (link is null)
            {
                AddAudit(
                    authority.UserId,
                    targetUserId,
                    UnlinkAction,
                    correlationId,
                    AetherIdentityAuditOutcome.Succeeded,
                    now,
                    new
                    {
                        code = "external-identity-unlink-converged",
                        userId = targetUserId,
                        providerId,
                        authorityVersion = user.AuthorityVersion
                    });
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(
                    Succeeded: true,
                    Code: "external-identity-unlink-converged",
                    targetUserId,
                    providerId,
                    user.AuthorityVersion,
                    RevokedSessionCount: 0,
                    MutationAttempted: false);
            }

            bool hasUsableLocalMethod = await HasUsableLocalMethodAsync(
                user,
                cancellationToken);
            string? configuredProviderId =
                topology.ExternalProvider?.ProviderId;
            bool hasOtherUsableExternalMethod =
                configuredProviderId is not null &&
                await database.ExternalIdentities.AnyAsync(
                    identity =>
                        identity.UserId == targetUserId &&
                        identity.Id != link.Id &&
                        identity.ProviderId == configuredProviderId,
                    cancellationToken);
            if (!hasUsableLocalMethod && !hasOtherUsableExternalMethod)
            {
                AddAudit(
                    authority.UserId,
                    targetUserId,
                    UnlinkAction,
                    correlationId,
                    AetherIdentityAuditOutcome.Rejected,
                    now,
                    new
                    {
                        code =
                            "external-identity-last-sign-in-method-protected",
                        userId = targetUserId,
                        providerId
                    });
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(
                    Succeeded: false,
                    Code:
                        "external-identity-last-sign-in-method-protected",
                    targetUserId,
                    providerId,
                    user.AuthorityVersion,
                    RevokedSessionCount: 0,
                    MutationAttempted: false);
            }

            string subjectBinding = ComputeSubjectBinding(
                new(
                    link.ProviderId,
                    link.Issuer,
                    link.Subject));
            database.ExternalIdentities.Remove(link);
            RotateAuthority(user);
            int revokedSessionCount = await RevokeActiveSessionsAsync(
                targetUserId,
                now,
                "administrator-external-identity-unlink",
                cancellationToken);
            AddAudit(
                authority.UserId,
                targetUserId,
                UnlinkAction,
                correlationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                new
                {
                    code = "external-identity-unlinked",
                    userId = targetUserId,
                    providerId,
                    subjectBinding,
                    authorityVersion = user.AuthorityVersion,
                    revokedSessionCount
                });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                Succeeded: true,
                Code: "external-identity-unlinked",
                targetUserId,
                providerId,
                user.AuthorityVersion,
                revokedSessionCount,
                MutationAttempted: true);
        }
        finally
        {
            administrationLock.Gate.Release();
        }
    }

    private async Task<FreshAdministrator> RequireFreshAdministratorAsync(
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
            throw new AetherAdministratorReauthenticationRequiredException();
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
                topology.LocalPolicy.AdministratorReauthenticationLifetime)
        {
            throw new AetherAdministratorReauthenticationRequiredException();
        }

        string[] roles = await (
            from assignment in database.Set<IdentityUserRole<Guid>>()
            join role in database.Roles
                on assignment.RoleId equals role.Id
            where assignment.UserId == actorUserId && role.Name != null
            orderby role.Name
            select role.Name!)
            .ToArrayAsync(cancellationToken);
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
            throw new AetherAdministratorReauthenticationRequiredException();
        }
        if (!roles.Contains(AetherRoles.Admin, StringComparer.Ordinal))
        {
            throw new AetherAdministratorReauthenticationRequiredException();
        }
        return new(actorUserId, actorSessionId);
    }

    private async Task<bool> HasUsableLocalMethodAsync(
        AetherIdentityUser user,
        CancellationToken cancellationToken)
    {
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

    private async Task<AetherIdentityUser> RequireUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await database.Users.SingleOrDefaultAsync(
                candidate => candidate.Id == userId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "The target identity does not exist.");

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

    private void RequireConfiguredProvider(
        AetherExternalProviderDescriptor provider)
    {
        if (topology.ExternalProvider != provider)
        {
            throw new InvalidOperationException(
                "The external identity provider is not the configured provider.");
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

    private static string ComputeSubjectBinding(
        AetherExternalIdentityKey key)
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            key.ProviderId + "\n" +
            key.Issuer + "\n" +
            key.Subject);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void ValidateCorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 100 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "The identity correlation identifier is invalid.");
        }
    }

    private static void ValidateProviderId(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > 100 ||
            value[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9') &&
                character is not ('.' or '_' or '-')))
        {
            throw new InvalidOperationException(
                "The external identity provider identifier is invalid.");
        }
    }

    private static void ValidateIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "The identity identifier cannot be empty.",
                parameterName);
        }
    }

    private sealed record FreshAdministrator(
        Guid UserId,
        Guid SessionId);
}
