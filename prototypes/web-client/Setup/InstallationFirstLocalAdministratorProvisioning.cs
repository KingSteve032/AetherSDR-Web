namespace AetherSDR.Web.Setup;

public sealed class InstallationFirstLocalAdministratorEnrollment
{
    public InstallationFirstLocalAdministratorEnrollment(
        string userName,
        string displayName,
        string? email,
        string password,
        string correlationId)
    {
        UserName = userName;
        DisplayName = displayName;
        Email = email;
        Password = password;
        CorrelationId = correlationId;
    }

    public string UserName { get; }

    public string DisplayName { get; }

    public string? Email { get; }

    public string Password { get; }

    public string CorrelationId { get; }

    public override string ToString() =>
        $"{nameof(InstallationFirstLocalAdministratorEnrollment)} " +
        "{ UserName = [redacted], DisplayName = [redacted], " +
        "Email = [redacted], Password = [redacted], " +
        $"CorrelationId = {CorrelationId} }}";
}

public sealed class InstallationFirstLocalAdministratorEnrollmentIssue
{
    public InstallationFirstLocalAdministratorEnrollmentIssue(
        Guid userId,
        DateTimeOffset accountCreatedAtUtc,
        string sharedSecretBase32,
        IReadOnlyList<string> recoveryCodes,
        bool rotated)
    {
        UserId = userId;
        AccountCreatedAtUtc = accountCreatedAtUtc;
        SharedSecretBase32 = sharedSecretBase32;
        RecoveryCodes = recoveryCodes;
        Rotated = rotated;
    }

    public Guid UserId { get; }

    public DateTimeOffset AccountCreatedAtUtc { get; }

    public string SharedSecretBase32 { get; }

    public IReadOnlyList<string> RecoveryCodes { get; }

    public bool Rotated { get; }

    public override string ToString() =>
        $"{nameof(InstallationFirstLocalAdministratorEnrollmentIssue)} " +
        $"{{ UserId = {UserId:D}, AccountCreatedAtUtc = " +
        $"{AccountCreatedAtUtc:O}, SharedSecretBase32 = [redacted], " +
        $"RecoveryCodes = [redacted], Rotated = {Rotated} }}";
}

public sealed record InstallationFirstLocalAdministratorConfirmationResult(
    bool Succeeded,
    string Code,
    Guid? UserId,
    bool MutationAttempted);

public interface IInstallationFirstLocalAdministratorProvisioner
    : IInstallationFirstAdministratorVerifier
{
    Task<InstallationFirstLocalAdministratorEnrollmentIssue> BeginAsync(
        InstallationFirstAdministratorVerificationRequest setup,
        InstallationFirstLocalAdministratorEnrollment enrollment,
        CancellationToken cancellationToken = default);

    Task<InstallationFirstLocalAdministratorConfirmationResult> ConfirmAsync(
        InstallationFirstAdministratorVerificationRequest setup,
        string? totpCode,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<bool> HasIdentityAsync(
        CancellationToken cancellationToken = default);
}

public sealed record InstallationFirstLocalAdministratorCompletionResult(
    InstallationFirstLocalAdministratorConfirmationResult Confirmation,
    InstallationSetupState? CompletedState)
{
    public bool Completed => CompletedState is not null;
}

public interface IInstallationFirstLocalAdministratorProvisioningExecutor
{
    Task<InstallationFirstLocalAdministratorEnrollmentIssue> BeginAsync(
        InstallationFirstAdministratorVerificationRequest setup,
        InstallationFirstLocalAdministratorEnrollment enrollment,
        CancellationToken cancellationToken = default);

    Task<InstallationFirstLocalAdministratorCompletionResult>
        ConfirmAndCompleteAsync(
            InstallationFirstAdministratorHandoff handoff,
            long expectedRevision,
            InstallationFirstAdministratorVerificationRequest setup,
            string? totpCode,
            string correlationId,
            CancellationToken cancellationToken = default);

    Task<bool> HasIdentityAsync(
        CancellationToken cancellationToken = default);
}
