using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AetherSDR.Web.Auth.Identity;

internal sealed record AetherAuthenticationSessionValidationResult(
    bool Succeeded,
    string Code,
    ClaimsPrincipal? Principal,
    Guid? SessionId);

internal sealed record AetherAuthenticationSessionRevocationResult(
    bool Succeeded,
    string Code,
    Guid? SessionId,
    bool MutationAttempted);

internal sealed class AetherAuthenticationSessionService(
    AetherIdentityDbContext database,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan LastSeenWriteInterval =
        TimeSpan.FromMinutes(5);

    internal async Task<AetherAuthenticationSessionValidationResult>
        ValidateAsync(
            ClaimsPrincipal cookiePrincipal,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cookiePrincipal);
        if (!TryReadCanonicalIdentity(
                cookiePrincipal,
                out Guid userId,
                out Guid sessionId,
                out long authorityVersion))
        {
            return ValidationRejected("canonical-session-claims-invalid");
        }

        AetherAuthenticationSession? session =
            await database.AuthenticationSessions
                .Include(candidate => candidate.User)
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.Id == sessionId &&
                        candidate.UserId == userId,
                    cancellationToken);
        if (session is null)
        {
            return ValidationRejected("canonical-session-not-found");
        }
        if (session.AuthorityVersion != authorityVersion ||
            session.User.AuthorityVersion != authorityVersion)
        {
            return ValidationRejected("canonical-session-authority-stale");
        }

        string[] roles = await (
            from userRole in database.Set<IdentityUserRole<Guid>>()
            join role in database.Roles
                on userRole.RoleId equals role.Id
            where userRole.UserId == userId && role.Name != null
            orderby role.Name
            select role.Name!)
            .ToArrayAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();
        ClaimsPrincipal canonicalPrincipal;
        try
        {
            canonicalPrincipal = AetherCanonicalPrincipalFactory.Create(
                session.User,
                session,
                roles,
                now);
        }
        catch (InvalidOperationException)
        {
            return ValidationRejected("canonical-session-inactive");
        }

        if (now - session.LastSeenAtUtc >= LastSeenWriteInterval)
        {
            session.LastSeenAtUtc = now;
            await database.SaveChangesAsync(cancellationToken);
        }

        return new(
            Succeeded: true,
            Code: "canonical-session-current",
            canonicalPrincipal,
            session.Id);
    }

    internal async Task<AetherAuthenticationSessionRevocationResult>
        RevokeAsync(
            ClaimsPrincipal cookiePrincipal,
            string reason,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cookiePrincipal);
        ValidateRevocationReason(reason);
        if (!TryReadCanonicalIdentity(
                cookiePrincipal,
                out Guid userId,
                out Guid sessionId,
                out long authorityVersion))
        {
            return RevocationRejected(
                "canonical-session-claims-invalid",
                sessionId: null);
        }

        AetherAuthenticationSession? session =
            await database.AuthenticationSessions
                .Include(candidate => candidate.User)
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.Id == sessionId &&
                        candidate.UserId == userId,
                    cancellationToken);
        if (session is null)
        {
            return RevocationRejected(
                "canonical-session-not-found",
                sessionId);
        }
        if (session.AuthorityVersion != authorityVersion ||
            session.User.AuthorityVersion != authorityVersion)
        {
            return RevocationRejected(
                "canonical-session-authority-stale",
                sessionId);
        }
        if (session.RevokedAtUtc is not null)
        {
            return new(
                Succeeded: true,
                Code: "canonical-session-already-revoked",
                sessionId,
                MutationAttempted: false);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        session.RevokedAtUtc = now;
        session.RevocationReason = reason;
        database.IdentityAuditRecords.Add(new AetherIdentityAuditRecord
        {
            OccurredAtUtc = now,
            ActorUserId = userId,
            SubjectUserId = userId,
            Action = "authentication.session.revoked",
            Outcome = AetherIdentityAuditOutcome.Succeeded,
            CorrelationId = sessionId.ToString(
                "D",
                CultureInfo.InvariantCulture),
            DetailJson = JsonSerializer.Serialize(
                new
                {
                    code = "canonical-session-revoked",
                    sessionId,
                    reason
                })
        });
        await database.SaveChangesAsync(cancellationToken);
        return new(
            Succeeded: true,
            Code: "canonical-session-revoked",
            sessionId,
            MutationAttempted: true);
    }

    internal static bool TryReadCanonicalIdentity(
        ClaimsPrincipal principal,
        out Guid userId,
        out Guid sessionId,
        out long authorityVersion)
    {
        userId = Guid.Empty;
        sessionId = Guid.Empty;
        authorityVersion = 0;
        ClaimsIdentity[] identities = principal.Identities.ToArray();
        if (identities.Length != 1 ||
            !string.Equals(
                identities[0].AuthenticationType,
                AetherCanonicalPrincipalFactory.AuthenticationType,
                StringComparison.Ordinal))
        {
            return false;
        }

        ClaimsIdentity identity = identities[0];
        string? userIdValue = SingleClaim(
            identity,
            ClaimTypes.NameIdentifier);
        string? sessionIdValue = SingleClaim(
            identity,
            AetherIdentityClaimTypes.SessionId);
        string? authorityVersionValue = SingleClaim(
            identity,
            AetherIdentityClaimTypes.AuthorityVersion);
        return Guid.TryParseExact(userIdValue, "D", out userId) &&
               userId != Guid.Empty &&
               Guid.TryParseExact(sessionIdValue, "D", out sessionId) &&
               sessionId != Guid.Empty &&
               long.TryParse(
                   authorityVersionValue,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out authorityVersion) &&
               authorityVersion > 0;
    }

    private static string? SingleClaim(
        ClaimsIdentity identity,
        string claimType)
    {
        Claim[] claims = identity.FindAll(claimType).ToArray();
        return claims.Length == 1 &&
               !string.IsNullOrWhiteSpace(claims[0].Value) &&
               string.Equals(
                   claims[0].Value,
                   claims[0].Value.Trim(),
                   StringComparison.Ordinal) &&
               !claims[0].Value.Any(char.IsControl)
            ? claims[0].Value
            : null;
    }

    private static void ValidateRevocationReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The session revocation reason must be an exact value of at " +
                "most 200 characters.",
                nameof(value));
        }
    }

    private static AetherAuthenticationSessionValidationResult
        ValidationRejected(string code) =>
        new(
            Succeeded: false,
            code,
            Principal: null,
            SessionId: null);

    private static AetherAuthenticationSessionRevocationResult
        RevocationRejected(string code, Guid? sessionId) =>
        new(
            Succeeded: false,
            code,
            sessionId,
            MutationAttempted: false);
}
