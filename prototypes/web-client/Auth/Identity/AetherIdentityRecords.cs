using Microsoft.AspNetCore.Identity;

namespace AetherSDR.Web.Auth.Identity;

internal sealed class AetherIdentitySchemaVersion
{
    public int Id { get; set; }

    public int Version { get; set; }
}

internal sealed class AetherIdentityUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public long AuthorityVersion { get; set; } = 1;

    public DateTimeOffset? DisabledAtUtc { get; set; }
}

internal sealed class AetherExternalIdentity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string ProviderId { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public DateTimeOffset LinkedAtUtc { get; set; }

    public AetherIdentityUser User { get; set; } = null!;
}

internal enum AetherAuthenticationMethod
{
    LocalPasswordWithTotp = 1,
    LocalPasskey = 2,
    ExternalOpenIdConnect = 3,
    LocalPasswordWithRecoveryCode = 4
}

internal sealed class AetherAuthenticationSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public AetherAuthenticationMethod AuthenticationMethod { get; set; }

    public string? ProviderId { get; set; }

    public long AuthorityVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastSeenAtUtc { get; set; }

    public DateTimeOffset AbsoluteExpiresAtUtc { get; set; }

    public DateTimeOffset? ReauthenticatedAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public string? RevocationReason { get; set; }

    public AetherIdentityUser User { get; set; } = null!;
}

internal enum AetherIdentityAuditOutcome
{
    Succeeded = 1,
    Rejected = 2,
    Failed = 3
}

internal sealed class AetherIdentityAuditRecord
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public Guid? ActorUserId { get; set; }

    public Guid? SubjectUserId { get; set; }

    public string Action { get; set; } = string.Empty;

    public AetherIdentityAuditOutcome Outcome { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public string DetailJson { get; set; } = "{}";
}
