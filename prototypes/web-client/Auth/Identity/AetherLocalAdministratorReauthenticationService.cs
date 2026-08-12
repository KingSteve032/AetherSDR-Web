using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AetherSDR.Web.Auth.Identity;

internal sealed record AetherLocalAdministratorReauthenticationChallenge(
    bool ReadyForSecondFactor,
    string Code,
    string? ChallengeToken);

internal sealed record AetherAdministratorReauthenticationResult(
    bool Succeeded,
    string Code,
    ClaimsPrincipal? Principal,
    Guid? SessionId,
    DateTimeOffset? AbsoluteExpiresAtUtc);

internal sealed class AetherLocalAdministratorReauthenticationService(
    AetherIdentityDbContext database,
    AetherAuthenticationSessionService sessions,
    AetherLocalPasswordAuthenticationService passwords,
    AetherLocalMfaAuthenticationService mfa,
    AetherAuthenticationTopology topology,
    TimeProvider timeProvider)
{
    private const string AuditAction =
        "authentication.administrator.reauthenticated";

    internal async Task<AetherLocalAdministratorReauthenticationChallenge>
        BeginAsync(
            ClaimsPrincipal administrator,
            string? password,
            string correlationId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        CurrentAdministrator? current = await ReadCurrentAdministratorAsync(
            administrator,
            cancellationToken);
        if (current is null)
        {
            return RejectedChallenge();
        }

        AetherIdentityUser? user = await database.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == current.UserId,
            cancellationToken);
        if (user?.UserName is null ||
            string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            return RejectedChallenge();
        }

        AetherLocalPasswordVerificationResult result =
            await passwords.VerifyAsync(
                user.UserName,
                password,
                correlationId,
                cancellationToken);
        if (!result.ReadyForSecondFactor ||
            string.IsNullOrEmpty(result.ChallengeToken))
        {
            return RejectedChallenge();
        }
        return new(
            ReadyForSecondFactor: true,
            Code: "administrator-second-factor-required",
            result.ChallengeToken);
    }

    internal async Task<AetherAdministratorReauthenticationResult>
        CompleteAsync(
            ClaimsPrincipal administrator,
            string? challengeToken,
            string? verificationCode,
            string correlationId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        CurrentAdministrator? current = await ReadCurrentAdministratorAsync(
            administrator,
            cancellationToken);
        if (current is null)
        {
            return Rejected();
        }

        AetherLocalMfaAuthenticationResult result =
            await mfa.AuthenticateAsync(
                challengeToken,
                verificationCode,
                correlationId,
                topology.SessionAbsoluteLifetime,
                cancellationToken);
        if (!result.Succeeded ||
            result.Principal is null ||
            result.SessionId is null ||
            result.AbsoluteExpiresAtUtc is null ||
            !AetherAuthenticationSessionService.TryReadCanonicalIdentity(
                result.Principal,
                out Guid reauthenticatedUserId,
                out Guid reauthenticatedSessionId,
                out _) ||
            reauthenticatedUserId != current.UserId ||
            reauthenticatedSessionId != result.SessionId ||
            !result.Principal.IsInRole(AetherRoles.Admin))
        {
            if (result.Principal is not null)
            {
                _ = await sessions.RevokeAsync(
                    result.Principal,
                    "administrator-reauthentication-binding-rejected",
                    cancellationToken);
            }
            return Rejected();
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        database.IdentityAuditRecords.Add(
            new()
            {
                OccurredAtUtc = now,
                ActorUserId = reauthenticatedUserId,
                SubjectUserId = reauthenticatedUserId,
                Action = AuditAction,
                Outcome = AetherIdentityAuditOutcome.Succeeded,
                CorrelationId = correlationId,
                DetailJson = JsonSerializer.Serialize(
                    new
                    {
                        code = "administrator-reauthenticated",
                        priorSessionId = current.SessionId,
                        sessionId = result.SessionId
                    })
            });
        await database.SaveChangesAsync(cancellationToken);
        return new(
            Succeeded: true,
            Code: "administrator-reauthenticated",
            result.Principal,
            result.SessionId,
            result.AbsoluteExpiresAtUtc);
    }

    private async Task<CurrentAdministrator?> ReadCurrentAdministratorAsync(
        ClaimsPrincipal administrator,
        CancellationToken cancellationToken)
    {
        AetherAuthenticationSessionValidationResult validation =
            await sessions.ValidateAsync(
                administrator,
                cancellationToken);
        if (!validation.Succeeded ||
            validation.Principal is null ||
            !validation.Principal.IsInRole(AetherRoles.Admin) ||
            !AetherAuthenticationSessionService.TryReadCanonicalIdentity(
                validation.Principal,
                out Guid userId,
                out Guid sessionId,
                out _) ||
            validation.SessionId != sessionId)
        {
            return null;
        }
        return new(userId, sessionId);
    }

    private static AetherLocalAdministratorReauthenticationChallenge
        RejectedChallenge() =>
        new(
            ReadyForSecondFactor: false,
            Code: "administrator-reauthentication-rejected",
            ChallengeToken: null);

    private static AetherAdministratorReauthenticationResult Rejected() =>
        new(
            Succeeded: false,
            Code: "administrator-reauthentication-rejected",
            Principal: null,
            SessionId: null,
            AbsoluteExpiresAtUtc: null);

    private sealed record CurrentAdministrator(
        Guid UserId,
        Guid SessionId);
}
