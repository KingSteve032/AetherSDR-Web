using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AetherSDR.Web.Auth;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public enum ReleaseUpdateOperatorAuthenticationFailureCode
{
    None = 0,
    AuthenticationMissing = 1,
    SubjectMissing = 2,
    AdministratorRoleMissing = 3,
    AuthenticationTimeMissing = 4,
    AuthenticationTimeInvalid = 5,
    ReauthenticationStale = 6
}

public sealed record ReleaseUpdateOperatorAuthenticationReport(
    bool Succeeded,
    ReleaseUpdateOperatorAuthenticationFailureCode FailureCode,
    string Message,
    bool Authenticated,
    bool AdministratorAuthorized,
    bool ReauthenticationCurrent,
    DateTimeOffset? ReauthenticatedAt)
{
    internal VerifiedReleaseActivationOperatorAuthenticationEvidence? Evidence
    {
        get;
        init;
    }
}

/// <summary>
/// Converts one server-authenticated principal into exact, path- and identity-
/// redacted release approval evidence. The browser cannot supply subject, role,
/// or authentication timestamps. A bounded SHA-256 subject binding is retained
/// instead of the raw account identifier.
/// </summary>
public sealed class ReleaseUpdateOperatorAuthenticationEvidenceFactory
{
    private readonly TimeProvider m_timeProvider;
    private readonly TimeSpan m_maximumAge;

    public ReleaseUpdateOperatorAuthenticationEvidenceFactory(
        IOptions<ReleaseActivationOperatorApprovalSettings> settings,
        TimeProvider? timeProvider = null)
    {
        ReleaseActivationOperatorApprovalSettings value =
            settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_maximumAge = TimeSpan.FromSeconds(value.MaximumApprovalAgeSeconds);
    }

    public ReleaseUpdateOperatorAuthenticationReport Create(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        if (principal.Identity?.IsAuthenticated != true)
        {
            return Failure(
                ReleaseUpdateOperatorAuthenticationFailureCode.AuthenticationMissing,
                "Current server authentication is required.");
        }

        string subject =
            principal.FindFirstValue("sub") ??
            principal.FindFirstValue("oid") ??
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
            string.Empty;
        string issuer = principal.FindFirstValue("iss") ??
            principal.Identity.AuthenticationType ?? "local";
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 512 ||
            string.IsNullOrWhiteSpace(issuer) || issuer.Length > 512)
        {
            return Failure(
                ReleaseUpdateOperatorAuthenticationFailureCode.SubjectMissing,
                "The authenticated administrator subject is unavailable.",
                authenticated: true);
        }

        bool administrator = principal.IsInRole(AetherRoles.Admin) ||
            principal.Claims.Any(claim =>
                string.Equals(claim.Type, "roles", StringComparison.Ordinal) &&
                string.Equals(claim.Value, AetherRoles.Admin, StringComparison.Ordinal));
        if (!administrator)
        {
            return Failure(
                ReleaseUpdateOperatorAuthenticationFailureCode
                    .AdministratorRoleMissing,
                "The authenticated identity is not authorized as an Aether administrator.",
                authenticated: true);
        }

        string authTimeText = principal.FindFirstValue("auth_time") ?? string.Empty;
        if (authTimeText.Length == 0)
        {
            return Failure(
                ReleaseUpdateOperatorAuthenticationFailureCode
                    .AuthenticationTimeMissing,
                "Fresh authentication time evidence is required for release approval.",
                authenticated: true,
                administratorAuthorized: true);
        }
        if (!long.TryParse(
                authTimeText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long authTimeSeconds))
        {
            return Failure(
                ReleaseUpdateOperatorAuthenticationFailureCode
                    .AuthenticationTimeInvalid,
                "Authentication time evidence is invalid.",
                authenticated: true,
                administratorAuthorized: true);
        }

        DateTimeOffset authTime;
        try
        {
            authTime = DateTimeOffset.FromUnixTimeSeconds(authTimeSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Failure(
                ReleaseUpdateOperatorAuthenticationFailureCode
                    .AuthenticationTimeInvalid,
                "Authentication time evidence is outside its supported range.",
                authenticated: true,
                administratorAuthorized: true);
        }
        if (authTime > now || now - authTime >= m_maximumAge)
        {
            return Failure(
                ReleaseUpdateOperatorAuthenticationFailureCode
                    .ReauthenticationStale,
                "Fresh administrator reauthentication is required before release approval.",
                authenticated: true,
                administratorAuthorized: true,
                reauthenticatedAt: authTime);
        }

        string binding = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}\n{subject}")));
        VerifiedReleaseActivationOperatorAuthenticationEvidence evidence = new(
            binding,
            Authenticated: true,
            AdministratorAuthorized: true,
            AuthenticatedAt: authTime,
            ReauthenticatedAt: authTime);
        return new ReleaseUpdateOperatorAuthenticationReport(
            true,
            ReleaseUpdateOperatorAuthenticationFailureCode.None,
            "Fresh authenticated administrator evidence was derived on the server.",
            Authenticated: true,
            AdministratorAuthorized: true,
            ReauthenticationCurrent: true,
            authTime)
        {
            Evidence = evidence
        };
    }

    internal static VerifiedReleaseActivationOperatorAuthenticationEvidence
        CreateLocalCliEvidence(TimeProvider? timeProvider = null)
    {
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        string account = Environment.UserName;
        string binding = Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes($"local-cli\n{account}\n{Environment.MachineName}")));
        return new VerifiedReleaseActivationOperatorAuthenticationEvidence(
            binding,
            Authenticated: true,
            AdministratorAuthorized: true,
            AuthenticatedAt: now,
            ReauthenticatedAt: now);
    }

    private static ReleaseUpdateOperatorAuthenticationReport Failure(
        ReleaseUpdateOperatorAuthenticationFailureCode code,
        string message,
        bool authenticated = false,
        bool administratorAuthorized = false,
        DateTimeOffset? reauthenticatedAt = null) =>
        new(
            false,
            code,
            message,
            authenticated,
            administratorAuthorized,
            ReauthenticationCurrent: false,
            reauthenticatedAt);
}
