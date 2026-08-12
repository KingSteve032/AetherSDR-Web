using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AetherSDR.Web.Auth.Identity;

internal sealed record AetherAdministratorAuthorityEvidence(
    Guid UserId,
    Guid SessionId,
    long AuthorityVersion,
    DateTimeOffset ReauthenticatedAtUtc);

/// <summary>
/// Revalidates administrator authority from the canonical durable identity
/// session. Claims alone never authorize a sensitive policy transition.
/// </summary>
internal sealed class AetherAdministratorAuthorityService(
    IServiceProvider services,
    AetherAuthenticationTopology topology,
    TimeProvider timeProvider)
{
    internal async Task<AetherAdministratorAuthorityEvidence>
        RequireFreshAsync(
            ClaimsPrincipal administrator,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        if (!AetherAuthenticationSessionService.TryReadCanonicalIdentity(
                administrator,
                out Guid userId,
                out Guid sessionId,
                out long authorityVersion) ||
            !administrator.IsInRole(AetherRoles.Admin))
        {
            throw FreshAdministratorRequired();
        }

        AetherIdentityDbContext? database =
            services.GetService<AetherIdentityDbContext>();
        if (database is null)
        {
            throw FreshAdministratorRequired();
        }

        AetherAuthenticationSession? session =
            await database.AuthenticationSessions
                .Include(candidate => candidate.User)
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.Id == sessionId &&
                        candidate.UserId == userId,
                    cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (session is null ||
            session.AuthorityVersion != authorityVersion ||
            session.User.AuthorityVersion != authorityVersion ||
            session.ReauthenticatedAtUtc is not DateTimeOffset reauthenticated ||
            reauthenticated > now ||
            now - reauthenticated >
                topology.LocalPolicy.AdministratorReauthenticationLifetime)
        {
            throw FreshAdministratorRequired();
        }

        string[] roles = await (
            from assignment in database.Set<IdentityUserRole<Guid>>()
            join role in database.Roles
                on assignment.RoleId equals role.Id
            where assignment.UserId == userId && role.Name != null
            orderby role.Name
            select role.Name!)
            .ToArrayAsync(cancellationToken);
        if (!roles.Contains(AetherRoles.Admin, StringComparer.Ordinal))
        {
            throw FreshAdministratorRequired();
        }

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

        return new(
            userId,
            sessionId,
            authorityVersion,
            reauthenticated);
    }

    private static AetherAdministratorReauthenticationRequiredException
        FreshAdministratorRequired() =>
        new();
}
