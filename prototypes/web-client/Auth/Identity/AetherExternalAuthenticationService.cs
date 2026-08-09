using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AetherSDR.Web.Auth.Identity;

internal sealed record AetherExternalAuthenticationResult(
    bool Succeeded,
    string Code,
    ClaimsPrincipal? Principal,
    Guid? SessionId);

internal sealed class AetherExternalAuthenticationService(
    AetherIdentityDbContext database,
    TimeProvider timeProvider)
{
    private const string AuditAction = "authentication.external";

    internal async Task<AetherExternalAuthenticationResult> AuthenticateAsync(
        AetherExternalProviderDescriptor provider,
        ClaimsPrincipal externalPrincipal,
        string correlationId,
        TimeSpan absoluteSessionLifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(externalPrincipal);
        ValidateCorrelationId(correlationId);
        if (absoluteSessionLifetime < TimeSpan.FromMinutes(5) ||
            absoluteSessionLifetime > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(absoluteSessionLifetime),
                "The external session lifetime must be between 5 minutes and " +
                "24 hours.");
        }

        AetherExternalIdentityEvidence evidence;
        try
        {
            evidence = AetherExternalIdentityEvidenceReader.Read(
                provider,
                externalPrincipal);
        }
        catch (InvalidOperationException)
        {
            return await RejectAsync(
                provider.ProviderId,
                subjectBinding: null,
                subjectUserId: null,
                correlationId,
                "external-identity-evidence-invalid",
                cancellationToken);
        }

        string subjectBinding = ComputeSubjectBinding(evidence.Key);
        AetherExternalIdentity? link = await database.ExternalIdentities
            .Include(identity => identity.User)
            .SingleOrDefaultAsync(
                identity =>
                    identity.ProviderId == evidence.Key.ProviderId &&
                    identity.Issuer == evidence.Key.Issuer &&
                    identity.Subject == evidence.Key.Subject,
                cancellationToken);
        if (link is null)
        {
            return await RejectAsync(
                provider.ProviderId,
                subjectBinding,
                subjectUserId: null,
                correlationId,
                "external-identity-unlinked",
                cancellationToken);
        }
        if (!link.User.Enabled)
        {
            return await RejectAsync(
                provider.ProviderId,
                subjectBinding,
                link.UserId,
                correlationId,
                "external-identity-user-disabled",
                cancellationToken);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset reauthenticatedAt =
            evidence.AuthenticatedAtUtc ?? now;
        if (reauthenticatedAt > now)
        {
            return await RejectAsync(
                provider.ProviderId,
                subjectBinding,
                link.UserId,
                correlationId,
                "external-authentication-time-invalid",
                cancellationToken);
        }

        string[] roles = await (
            from userRole in database.Set<IdentityUserRole<Guid>>()
            join role in database.Roles
                on userRole.RoleId equals role.Id
            where userRole.UserId == link.UserId && role.Name != null
            orderby role.Name
            select role.Name!)
            .ToArrayAsync(cancellationToken);

        AetherAuthenticationSession session = new()
        {
            Id = Guid.NewGuid(),
            UserId = link.UserId,
            User = link.User,
            AuthenticationMethod =
                AetherAuthenticationMethod.ExternalOpenIdConnect,
            ProviderId = provider.ProviderId,
            AuthorityVersion = link.User.AuthorityVersion,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            AbsoluteExpiresAtUtc = now.Add(absoluteSessionLifetime),
            ReauthenticatedAtUtc = reauthenticatedAt
        };
        ClaimsPrincipal principal = AetherCanonicalPrincipalFactory.Create(
            link.User,
            session,
            roles,
            now);

        database.AuthenticationSessions.Add(session);
        database.IdentityAuditRecords.Add(
            Audit(
                actorUserId: link.UserId,
                subjectUserId: link.UserId,
                correlationId,
                AetherIdentityAuditOutcome.Succeeded,
                now,
                "external-identity-authenticated",
                provider.ProviderId,
                subjectBinding,
                session.Id));
        await database.SaveChangesAsync(cancellationToken);

        return new(
            Succeeded: true,
            Code: "external-identity-authenticated",
            principal,
            session.Id);
    }

    private async Task<AetherExternalAuthenticationResult> RejectAsync(
        string providerId,
        string? subjectBinding,
        Guid? subjectUserId,
        string correlationId,
        string code,
        CancellationToken cancellationToken)
    {
        database.IdentityAuditRecords.Add(
            Audit(
                actorUserId: null,
                subjectUserId,
                correlationId,
                AetherIdentityAuditOutcome.Rejected,
                timeProvider.GetUtcNow(),
                code,
                providerId,
                subjectBinding,
                sessionId: null));
        await database.SaveChangesAsync(cancellationToken);
        return new(
            Succeeded: false,
            code,
            Principal: null,
            SessionId: null);
    }

    private static AetherIdentityAuditRecord Audit(
        Guid? actorUserId,
        Guid? subjectUserId,
        string correlationId,
        AetherIdentityAuditOutcome outcome,
        DateTimeOffset occurredAt,
        string code,
        string providerId,
        string? subjectBinding,
        Guid? sessionId) =>
        new()
        {
            OccurredAtUtc = occurredAt,
            ActorUserId = actorUserId,
            SubjectUserId = subjectUserId,
            Action = AuditAction,
            Outcome = outcome,
            CorrelationId = correlationId,
            DetailJson = JsonSerializer.Serialize(
                new
                {
                    code,
                    providerId,
                    subjectBinding,
                    sessionId
                })
        };

    private static string ComputeSubjectBinding(
        AetherExternalIdentityKey key)
    {
        string payload = string.Join(
            '\n',
            "aethersdr-external-subject-v1",
            key.ProviderId,
            key.Issuer,
            key.Subject);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static void ValidateCorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 100 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The authentication correlation identifier must be an exact " +
                "value of at most 100 characters.",
                nameof(value));
        }
    }
}
