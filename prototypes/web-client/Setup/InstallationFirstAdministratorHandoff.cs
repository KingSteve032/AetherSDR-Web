using AetherSDR.Web.Auth;

namespace AetherSDR.Web.Setup;

public sealed record InstallationFirstAdministratorVerificationRequest(
    int SetupSchemaVersion,
    long SetupRevision,
    DateTimeOffset SetupCreatedAt,
    InstallationTopologyKind Topology,
    string CanonicalPublicUrl);

public sealed record InstallationFirstAdministratorEvidence(
    int SetupSchemaVersion,
    long SetupRevision,
    DateTimeOffset SetupCreatedAt,
    InstallationTopologyKind Topology,
    string CanonicalPublicUrl,
    string SubjectId,
    DateTimeOffset AccountCreatedAt,
    bool IsEnabled,
    IReadOnlyList<string> Roles);

public interface IInstallationFirstAdministratorVerifier
{
    Task<InstallationFirstAdministratorEvidence> VerifyAsync(
        InstallationFirstAdministratorVerificationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class InstallationFirstAdministratorHandoff
{
    private const int MaximumSubjectIdLength = 256;

    private readonly InstallationSetupStore m_store;
    private readonly InstallationSetupPreflight m_preflight;
    private readonly TimeProvider m_timeProvider;

    public InstallationFirstAdministratorHandoff(
        InstallationSetupStore store,
        TimeProvider? timeProvider = null)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_preflight = new InstallationSetupPreflight(m_store, m_timeProvider);
    }

    public async Task<InstallationSetupState> CompleteAsync(
        long expectedRevision,
        IInstallationFirstAdministratorVerifier verifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);

        InstallationSetupState state =
            await m_store.LoadAsync(cancellationToken);
        if (state.Revision != expectedRevision)
        {
            throw new InstallationSetupConcurrencyException(
                expectedRevision,
                state.Revision);
        }
        RequireReadyForAdministrator(state);

        InstallationSetupPreflightReport preflight =
            await m_preflight.CreateAsync(cancellationToken);
        if (preflight.StateRevision != expectedRevision)
        {
            throw new InstallationSetupConcurrencyException(
                expectedRevision,
                preflight.StateRevision);
        }

        InstallationFirstAdministratorVerificationRequest request =
            CreateRequest(state);
        InstallationFirstAdministratorEvidence evidence =
            await verifier.VerifyAsync(request, cancellationToken) ??
            throw new InvalidOperationException(
                "The first-administrator verifier returned no evidence.");
        DateTimeOffset verifiedAt = m_timeProvider.GetUtcNow();
        ValidateEvidence(request, evidence, verifiedAt);

        return await m_store.UpdateAsync(
            expectedRevision,
            current =>
            {
                RequireReadyForAdministrator(current);
                InstallationFirstAdministratorVerificationRequest currentRequest =
                    CreateRequest(current);
                if (currentRequest != request)
                {
                    throw new InvalidOperationException(
                        "Installation setup identity changed during administrator verification.");
                }

                DateTimeOffset claimedAt = current.Lock.ClaimedAt ??
                    throw new InvalidOperationException(
                        "Installation setup completion requires a claim timestamp.");
                DateTimeOffset completedAt = m_timeProvider.GetUtcNow();
                if (completedAt < claimedAt)
                {
                    completedAt = claimedAt;
                }

                return current with
                {
                    LastCompletedStep = InstallationSetupStep.Administrator,
                    Lock = new InstallationSetupLock
                    {
                        Mode = InstallationSetupLockMode.Complete,
                        ClaimedAt = claimedAt,
                        CompletedAt = completedAt
                    }
                };
            },
            cancellationToken);
    }

    private static InstallationFirstAdministratorVerificationRequest CreateRequest(
        InstallationSetupState state)
    {
        InstallationTopologyKind topology = state.Topology ??
            throw new InvalidOperationException(
                "Installation setup completion requires a topology.");
        CanonicalPublicUrl publicUrl =
            CanonicalPublicUrl.Parse(state.CanonicalPublicUrl);
        return new InstallationFirstAdministratorVerificationRequest(
            state.SchemaVersion,
            state.Revision,
            state.CreatedAt,
            topology,
            publicUrl.Value);
    }

    private static void RequireReadyForAdministrator(
        InstallationSetupState state)
    {
        InstallationSetupStateValidator.Validate(state);
        if (state.Lock.Mode != InstallationSetupLockMode.Claimed)
        {
            throw new InvalidOperationException(
                "Installation setup completion requires a claimed first-run lock.");
        }
        if (state.LastCompletedStep < InstallationSetupStep.TransmitSupport)
        {
            throw new InvalidOperationException(
                "Installation setup completion requires all setup choices and preflight readiness.");
        }
    }

    private static void ValidateEvidence(
        InstallationFirstAdministratorVerificationRequest request,
        InstallationFirstAdministratorEvidence evidence,
        DateTimeOffset verifiedAt)
    {
        if (evidence.SetupSchemaVersion != request.SetupSchemaVersion ||
            evidence.SetupRevision != request.SetupRevision ||
            evidence.SetupCreatedAt != request.SetupCreatedAt ||
            evidence.Topology != request.Topology ||
            !string.Equals(
                evidence.CanonicalPublicUrl,
                request.CanonicalPublicUrl,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "First-administrator evidence does not match the exact installation setup identity.");
        }

        string subjectId = evidence.SubjectId?.Trim() ?? string.Empty;
        if (subjectId.Length is < 1 or > MaximumSubjectIdLength ||
            !string.Equals(subjectId, evidence.SubjectId, StringComparison.Ordinal) ||
            subjectId.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "First-administrator evidence requires one bounded canonical subject identity.");
        }
        if (!evidence.IsEnabled)
        {
            throw new InvalidOperationException(
                "The first administrator must be enabled before setup can complete.");
        }
        if (evidence.AccountCreatedAt < request.SetupCreatedAt ||
            evidence.AccountCreatedAt > verifiedAt)
        {
            throw new InvalidOperationException(
                "First-administrator evidence contains an invalid account creation timestamp.");
        }

        IReadOnlyList<string> roles = evidence.Roles ??
            throw new InvalidOperationException(
                "First-administrator evidence requires explicit roles.");
        HashSet<string> distinctRoles = new(StringComparer.Ordinal);
        foreach (string role in roles)
        {
            if (string.IsNullOrWhiteSpace(role) ||
                !AetherRoles.All.Contains(role, StringComparer.Ordinal) ||
                !distinctRoles.Add(role))
            {
                throw new InvalidOperationException(
                    "First-administrator evidence contains an unknown or duplicate role.");
            }
        }
        if (!distinctRoles.Contains(AetherRoles.Admin))
        {
            throw new InvalidOperationException(
                "The first administrator must hold the Aether.Admin role.");
        }
    }
}
