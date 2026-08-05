using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public sealed class ReleaseActivationOperatorApprovalSettings
{
    public const string SectionName = "ReleaseActivationOperatorApproval";
    public const int DefaultMaximumApprovalAgeSeconds = 300;
    public const int MinimumApprovalAgeSeconds = 30;
    public const int MaximumAllowedApprovalAgeSeconds = 600;

    public bool AuthorityEnabled { get; init; }
    public int MaximumApprovalAgeSeconds { get; init; } =
        DefaultMaximumApprovalAgeSeconds;
}

public enum VerifiedReleaseActivationOperatorApprovalFailureCode
{
    None = 0,
    AuthorityDisabled = 1,
    ActivationPlanNotEligible = 2,
    ActivationPlanUnavailable = 3,
    ActivationPlanMismatch = 4,
    AuthenticationEvidenceInvalid = 5,
    AuthenticationNotCurrent = 6,
    AdministratorAuthorizationMissing = 7,
    ReauthenticationRequired = 8,
    ApprovalAlreadyActive = 9,
    ApprovalIdentityInvalid = 10
}

public sealed record VerifiedReleaseActivationOperatorApprovalReport(
    bool Succeeded,
    VerifiedReleaseActivationOperatorApprovalFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    bool AuthorityEnabled,
    int MaximumApprovalAgeSeconds,
    bool ExactPlanBound,
    bool AuthenticationCurrent,
    bool AdministratorAuthorized,
    bool ReauthenticationCurrent,
    bool ApprovalFresh,
    bool ApprovalStored,
    bool CurrentPointerChanged,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationOperatorApproval? Approval { get; init; }

    internal static VerifiedReleaseActivationOperatorApprovalReport Failure(
        VerifiedReleaseActivationOperatorApprovalFailureCode failureCode,
        string message,
        ReleaseActivationOperatorApprovalSettings settings,
        VerifiedReleaseActivationPlanCompositionResult? planResult = null,
        bool exactPlanBound = false,
        bool authenticationCurrent = false,
        bool administratorAuthorized = false,
        bool reauthenticationCurrent = false) =>
        new(
            false,
            failureCode,
            message,
            planResult?.SetupRevision,
            planResult?.InstalledReleaseIdentity ?? string.Empty,
            planResult?.TargetReleaseIdentity ?? string.Empty,
            settings.AuthorityEnabled,
            settings.MaximumApprovalAgeSeconds,
            exactPlanBound,
            authenticationCurrent,
            administratorAuthorized,
            reauthenticationCurrent,
            ApprovalFresh: false,
            ApprovalStored: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationOperatorApprovalReport Success(
        ReleaseActivationOperatorApprovalSettings settings,
        VerifiedReleaseActivationPlanCompositionResult planResult,
        VerifiedReleaseActivationOperatorApproval approval) =>
        new(
            true,
            VerifiedReleaseActivationOperatorApprovalFailureCode.None,
            "Exact release activation operator approval was retained without changing current or authorizing activation.",
            approval.Plan.SetupRevision,
            approval.Plan.InstalledReleaseIdentity,
            approval.Plan.TargetReleaseIdentity,
            settings.AuthorityEnabled,
            settings.MaximumApprovalAgeSeconds,
            ExactPlanBound: true,
            AuthenticationCurrent: true,
            AdministratorAuthorized: true,
            ReauthenticationCurrent: true,
            ApprovalFresh: true,
            ApprovalStored: true,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            Approval = approval
        };
}

public sealed record VerifiedReleaseActivationOperatorApprovalDiagnostics(
    bool Registered,
    bool AuthorityEnabled,
    int MaximumApprovalAgeSeconds,
    bool ExactPlanBindingRegistered,
    bool AuthenticationEvidenceRequired,
    bool AdministratorAuthorizationRequired,
    bool ReauthenticationRequired,
    bool BoundedApprovalLifetimeRegistered,
    bool SingleActiveApprovalRegistered,
    bool RevocationRegistered,
    bool ActiveApproval,
    bool ApprovalAvailable,
    long AttemptCount,
    long AcceptedCount,
    long RejectedCount,
    long RevokedCount,
    string LastOutcome,
    DateTimeOffset? LastObservedAt,
    bool FileWriteRegistered,
    bool CurrentPointerMutationRegistered,
    bool ActivationExecutionRegistered,
    bool ActivationAuthorityRegistered,
    bool TxLeaseMutationRegistered,
    bool RadioCommandRegistered,
    bool WatchdogMutationRegistered,
    bool BackupExecutionRegistered,
    bool MigrationExecutionRegistered,
    bool ServiceControlRegistered,
    bool HealthProbeCallerRegistered,
    bool RollbackExecutionRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool HttpCallerRegistered,
    bool WebSocketCallerRegistered,
    bool HostedServiceCallerRegistered,
    bool TimerCallerRegistered,
    bool AetherRemoteCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed record VerifiedReleaseActivationOperatorAuthenticationEvidence(
    string SubjectBinding,
    bool Authenticated,
    bool AdministratorAuthorized,
    DateTimeOffset AuthenticatedAt,
    DateTimeOffset ReauthenticatedAt);

internal sealed record VerifiedReleaseActivationOperatorApprovalObservation(
    bool OperatorApproved,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? ExpiresAt,
    bool Revoked);

internal sealed class VerifiedReleaseActivationOperatorApproval
{
    internal VerifiedReleaseActivationOperatorApproval(
        VerifiedReleaseActivationPlan plan,
        string approvalIdentity,
        string subjectBinding,
        DateTimeOffset approvedAt,
        DateTimeOffset expiresAt)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (!IsCanonicalApprovalIdentity(approvalIdentity))
        {
            throw new ArgumentException(
                "Approval identity must be 32 lowercase hexadecimal characters.",
                nameof(approvalIdentity));
        }
        if (!IsCanonicalSubjectBinding(subjectBinding))
        {
            throw new ArgumentException(
                "Subject binding is not canonical.",
                nameof(subjectBinding));
        }
        if (approvedAt == default || expiresAt <= approvedAt)
        {
            throw new ArgumentException(
                "Approval timestamps are invalid.",
                nameof(expiresAt));
        }
        ApprovalIdentity = approvalIdentity;
        SubjectBinding = subjectBinding;
        ApprovedAt = approvedAt;
        ExpiresAt = expiresAt;
    }

    internal VerifiedReleaseActivationPlan Plan { get; }
    internal string ApprovalIdentity { get; }
    internal string SubjectBinding { get; }
    internal DateTimeOffset ApprovedAt { get; }
    internal DateTimeOffset ExpiresAt { get; }
    internal bool Revoked { get; private set; }

    internal void Revoke() => Revoked = true;

    internal static bool IsCanonicalApprovalIdentity(string value) =>
        value is { Length: 32 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsCanonicalSubjectBinding(string value) =>
        value is { Length: > 0 and <= 128 } &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);
}

/// <summary>
/// Disabled-by-default, callerless authority for one exact verified release
/// activation plan. It accepts only current authenticated administrator evidence
/// with bounded reauthentication freshness and retains at most one expiring
/// reference-bound approval. It owns no authentication flow, route, activation
/// transaction, filesystem write, service control, radio/watchdog command, lease,
/// keying, or transmit authority.
/// </summary>
public sealed class VerifiedReleaseActivationOperatorApprovalAuthority
{
    private readonly object m_gate = new();
    private readonly ReleaseActivationOperatorApprovalSettings m_settings;
    private readonly TimeProvider m_timeProvider;
    private readonly Func<string> m_approvalIdentityFactory;
    private VerifiedReleaseActivationOperatorApproval? m_activeApproval;
    private long m_attemptCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private long m_revokedCount;
    private string m_lastOutcome = "none";
    private DateTimeOffset? m_lastObservedAt;

    public VerifiedReleaseActivationOperatorApprovalAuthority(
        IOptions<ReleaseActivationOperatorApprovalSettings> settings)
        : this(
            settings?.Value ?? throw new ArgumentNullException(nameof(settings)),
            TimeProvider.System)
    {
    }

    internal VerifiedReleaseActivationOperatorApprovalAuthority(
        ReleaseActivationOperatorApprovalSettings settings,
        TimeProvider timeProvider,
        Func<string>? approvalIdentityFactory = null)
    {
        m_settings = ValidateSettings(settings);
        m_timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        m_approvalIdentityFactory = approvalIdentityFactory ??
            (() => Guid.NewGuid().ToString("N"));
    }

    public VerifiedReleaseActivationOperatorApprovalDiagnostics Snapshot
    {
        get
        {
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            lock (m_gate)
            {
                bool active = IsActive(m_activeApproval, now);
                return new VerifiedReleaseActivationOperatorApprovalDiagnostics(
                    Registered: true,
                    m_settings.AuthorityEnabled,
                    m_settings.MaximumApprovalAgeSeconds,
                    ExactPlanBindingRegistered: true,
                    AuthenticationEvidenceRequired: true,
                    AdministratorAuthorizationRequired: true,
                    ReauthenticationRequired: true,
                    BoundedApprovalLifetimeRegistered: true,
                    SingleActiveApprovalRegistered: true,
                    RevocationRegistered: true,
                    ActiveApproval: active,
                    ApprovalAvailable: m_settings.AuthorityEnabled && active,
                    m_attemptCount,
                    m_acceptedCount,
                    m_rejectedCount,
                    m_revokedCount,
                    m_lastOutcome,
                    m_lastObservedAt,
                    FileWriteRegistered: false,
                    CurrentPointerMutationRegistered: false,
                    ActivationExecutionRegistered: false,
                    ActivationAuthorityRegistered: false,
                    TxLeaseMutationRegistered: false,
                    RadioCommandRegistered: false,
                    WatchdogMutationRegistered: false,
                    BackupExecutionRegistered: false,
                    MigrationExecutionRegistered: false,
                    ServiceControlRegistered: false,
                    HealthProbeCallerRegistered: false,
                    RollbackExecutionRegistered: false,
                    CliCallerRegistered: false,
                    AdminCallerRegistered: false,
                    BrowserCallerRegistered: false,
                    HttpCallerRegistered: false,
                    WebSocketCallerRegistered: false,
                    HostedServiceCallerRegistered: false,
                    TimerCallerRegistered: false,
                    AetherRemoteCallerRegistered: false,
                    CommandCallerRegistered: false,
                    LeaseCallerRegistered: false,
                    TxCallerRegistered: false);
            }
        }
    }

    internal VerifiedReleaseActivationOperatorApprovalReport Approve(
        VerifiedReleaseActivationPlanCompositionResult planResult,
        VerifiedReleaseActivationOperatorAuthenticationEvidence authentication)
    {
        ArgumentNullException.ThrowIfNull(planResult);
        ArgumentNullException.ThrowIfNull(authentication);

        DateTimeOffset now = m_timeProvider.GetUtcNow();
        lock (m_gate)
        {
            m_attemptCount++;
            m_lastObservedAt = now;

            if (!m_settings.AuthorityEnabled)
            {
                return Reject(
                    VerifiedReleaseActivationOperatorApprovalFailureCode
                        .AuthorityDisabled,
                    "Release activation operator approval authority is disabled.",
                    planResult);
            }
            if (!IsEligiblePlanResult(planResult))
            {
                return Reject(
                    VerifiedReleaseActivationOperatorApprovalFailureCode
                        .ActivationPlanNotEligible,
                    "A successful non-mutating verified activation plan is required.",
                    planResult);
            }
            VerifiedReleaseActivationPlan? plan = planResult.Plan;
            if (plan is null)
            {
                return Reject(
                    VerifiedReleaseActivationOperatorApprovalFailureCode
                        .ActivationPlanUnavailable,
                    "The successful activation plan does not retain its internal verified plan.",
                    planResult);
            }
            if (!MatchesPlanResult(planResult, plan))
            {
                return Reject(
                    VerifiedReleaseActivationOperatorApprovalFailureCode
                        .ActivationPlanMismatch,
                    "Activation plan metadata does not match its exact retained plan.",
                    planResult);
            }
            if (!VerifiedReleaseActivationOperatorApproval
                .IsCanonicalSubjectBinding(authentication.SubjectBinding) ||
                authentication.AuthenticatedAt == default ||
                authentication.ReauthenticatedAt == default ||
                authentication.AuthenticatedAt >
                    authentication.ReauthenticatedAt ||
                authentication.ReauthenticatedAt > now)
            {
                return Reject(
                    VerifiedReleaseActivationOperatorApprovalFailureCode
                        .AuthenticationEvidenceInvalid,
                    "Operator authentication evidence is malformed.",
                    planResult,
                    exactPlanBound: true);
            }
            if (!authentication.Authenticated)
            {
                return Reject(
                    VerifiedReleaseActivationOperatorApprovalFailureCode
                        .AuthenticationNotCurrent,
                    "Current authentication is required for release activation approval.",
                    planResult,
                    exactPlanBound: true);
            }
            if (!authentication.AdministratorAuthorized)
            {
                return Reject(
                    VerifiedReleaseActivationOperatorApprovalFailureCode
                        .AdministratorAuthorizationMissing,
                    "Current administrator authorization is required for release activation approval.",
                    planResult,
                    exactPlanBound: true,
                    authenticationCurrent: true);
            }

            TimeSpan maximumAge = TimeSpan.FromSeconds(
                m_settings.MaximumApprovalAgeSeconds);
            DateTimeOffset expiresAt = authentication.ReauthenticatedAt + maximumAge;
            if (expiresAt <= now)
            {
                return Reject(
                    VerifiedReleaseActivationOperatorApprovalFailureCode
                        .ReauthenticationRequired,
                    "Fresh administrator reauthentication is required for release activation approval.",
                    planResult,
                    exactPlanBound: true,
                    authenticationCurrent: true,
                    administratorAuthorized: true);
            }
            if (IsActive(m_activeApproval, now))
            {
                return Reject(
                    VerifiedReleaseActivationOperatorApprovalFailureCode
                        .ApprovalAlreadyActive,
                    "An exact release activation operator approval is already active.",
                    planResult,
                    exactPlanBound: true,
                    authenticationCurrent: true,
                    administratorAuthorized: true,
                    reauthenticationCurrent: true);
            }

            string approvalIdentity = m_approvalIdentityFactory();
            if (!VerifiedReleaseActivationOperatorApproval
                .IsCanonicalApprovalIdentity(approvalIdentity))
            {
                return Reject(
                    VerifiedReleaseActivationOperatorApprovalFailureCode
                        .ApprovalIdentityInvalid,
                    "The approval identity source returned an invalid value.",
                    planResult,
                    exactPlanBound: true,
                    authenticationCurrent: true,
                    administratorAuthorized: true,
                    reauthenticationCurrent: true);
            }

            VerifiedReleaseActivationOperatorApproval approval = new(
                plan,
                approvalIdentity,
                authentication.SubjectBinding,
                now,
                expiresAt);
            m_activeApproval = approval;
            m_acceptedCount++;
            m_lastOutcome = "accepted";
            return VerifiedReleaseActivationOperatorApprovalReport.Success(
                m_settings,
                planResult,
                approval);
        }
    }

    internal VerifiedReleaseActivationOperatorApprovalObservation Observe(
        VerifiedReleaseActivationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        lock (m_gate)
        {
            VerifiedReleaseActivationOperatorApproval? approval = m_activeApproval;
            if (!m_settings.AuthorityEnabled ||
                !IsActive(approval, now) ||
                !ReferenceEquals(approval!.Plan, plan))
            {
                return new VerifiedReleaseActivationOperatorApprovalObservation(
                    OperatorApproved: false,
                    ApprovedAt: null,
                    ExpiresAt: null,
                    Revoked: approval?.Revoked ?? false);
            }
            return new VerifiedReleaseActivationOperatorApprovalObservation(
                OperatorApproved: true,
                approval.ApprovedAt,
                approval.ExpiresAt,
                Revoked: false);
        }
    }

    internal bool Revoke(VerifiedReleaseActivationOperatorApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        lock (m_gate)
        {
            if (!ReferenceEquals(m_activeApproval, approval) || approval.Revoked)
            {
                return false;
            }
            approval.Revoke();
            m_revokedCount++;
            m_lastOutcome = "revoked";
            m_lastObservedAt = now;
            return true;
        }
    }

    private VerifiedReleaseActivationOperatorApprovalReport Reject(
        VerifiedReleaseActivationOperatorApprovalFailureCode failureCode,
        string message,
        VerifiedReleaseActivationPlanCompositionResult planResult,
        bool exactPlanBound = false,
        bool authenticationCurrent = false,
        bool administratorAuthorized = false,
        bool reauthenticationCurrent = false)
    {
        m_rejectedCount++;
        m_lastOutcome = failureCode.ToString();
        return VerifiedReleaseActivationOperatorApprovalReport.Failure(
            failureCode,
            message,
            m_settings,
            planResult,
            exactPlanBound,
            authenticationCurrent,
            administratorAuthorized,
            reauthenticationCurrent);
    }

    private static bool IsActive(
        VerifiedReleaseActivationOperatorApproval? approval,
        DateTimeOffset now) =>
        approval is not null && !approval.Revoked && approval.ExpiresAt > now;

    private static ReleaseActivationOperatorApprovalSettings ValidateSettings(
        ReleaseActivationOperatorApprovalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaximumApprovalAgeSeconds <
                ReleaseActivationOperatorApprovalSettings
                    .MinimumApprovalAgeSeconds ||
            settings.MaximumApprovalAgeSeconds >
                ReleaseActivationOperatorApprovalSettings
                    .MaximumAllowedApprovalAgeSeconds)
        {
            throw new InvalidOperationException(
                "Release activation operator approval age is outside the bounded range.");
        }
        return new ReleaseActivationOperatorApprovalSettings
        {
            AuthorityEnabled = settings.AuthorityEnabled,
            MaximumApprovalAgeSeconds = settings.MaximumApprovalAgeSeconds
        };
    }

    private static bool IsEligiblePlanResult(
        VerifiedReleaseActivationPlanCompositionResult result) =>
        result.Succeeded &&
        result.FailureCode == VerifiedReleaseActivationPlanFailureCode.None &&
        result.SetupRevision is > 0 &&
        result.PackageCount == 4 &&
        result.PublishedBytes > 0 &&
        result.TxLeaseAdmissionClosureRequired &&
        result.RadioAuthoritativeIdleRequired &&
        result.WatchdogsDisarmedRequired &&
        result.ConfigurationBackupRequired &&
        result.AtomicCurrentPointerSwitchRequired &&
        result.ServiceHealthVerificationRequired &&
        result.AutomaticRollbackRequired &&
        result.OperatorApprovalRequired &&
        !result.CurrentPointerMutationPerformed &&
        !result.ActivationPerformed;

    private static bool MatchesPlanResult(
        VerifiedReleaseActivationPlanCompositionResult result,
        VerifiedReleaseActivationPlan plan) =>
        result.SetupRevision == plan.SetupRevision &&
        string.Equals(
            result.InstalledReleaseIdentity,
            plan.InstalledReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            result.TargetReleaseIdentity,
            plan.TargetReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            result.TargetVersion,
            plan.TargetVersion,
            StringComparison.Ordinal) &&
        result.Architecture == plan.Architecture &&
        result.PackageCount == plan.Packages.Count &&
        result.TargetConfigurationSchemaVersion ==
            plan.TargetConfigurationSchemaVersion &&
        result.MigrationKind == plan.MigrationKind &&
        result.MigrationRequired == plan.MigrationRequired &&
        result.RestartServiceCount == plan.RestartServiceCount &&
        result.HostRestartRequired == plan.RestartHost &&
        plan.TxLeaseAdmissionClosureRequired &&
        plan.RadioAuthoritativeIdleRequired &&
        plan.WatchdogsDisarmedRequired &&
        plan.ConfigurationBackupRequired &&
        plan.AtomicCurrentPointerSwitchRequired &&
        plan.ServiceHealthVerificationRequired &&
        plan.AutomaticRollbackRequired &&
        plan.OperatorApprovalRequired;
}
