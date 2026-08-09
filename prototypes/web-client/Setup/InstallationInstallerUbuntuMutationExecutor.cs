namespace AetherSDR.Web.Setup;

public enum InstallationInstallerUbuntuStepOutcome
{
    Converged = 1,
    Applied = 2,
    Rejected = 3,
    Unknown = 4
}

public sealed record InstallationInstallerUbuntuStepResult(
    InstallationInstallerUbuntuStepOutcome Outcome,
    string Code,
    string Summary)
{
    public static InstallationInstallerUbuntuStepResult Converged(
        string code = "step-converged",
        string summary = "The fixed installer step is already converged.") =>
        Create(InstallationInstallerUbuntuStepOutcome.Converged, code, summary);

    public static InstallationInstallerUbuntuStepResult Applied(
        string code = "step-applied",
        string summary = "The fixed installer step was applied.") =>
        Create(InstallationInstallerUbuntuStepOutcome.Applied, code, summary);

    public static InstallationInstallerUbuntuStepResult Rejected(
        string code,
        string summary) =>
        Create(InstallationInstallerUbuntuStepOutcome.Rejected, code, summary);

    public static InstallationInstallerUbuntuStepResult Unknown(
        string code,
        string summary) =>
        Create(InstallationInstallerUbuntuStepOutcome.Unknown, code, summary);

    private static InstallationInstallerUbuntuStepResult Create(
        InstallationInstallerUbuntuStepOutcome outcome,
        string code,
        string summary) =>
        new(
            outcome,
            InstallationInstallerHostResultText.ValidateCode(code),
            InstallationInstallerHostResultText.ValidateSummary(summary));
}

public interface IInstallationInstallerUbuntuMutationPrimitives
{
    Task<InstallationInstallerUbuntuStepResult> PrepareAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<InstallationInstallerUbuntuStepResult> ExecuteAsync(
        InstallationInstallerUbuntuMutationRequest request,
        InstallationInstallerPlanAction action,
        CancellationToken cancellationToken = default);
}

public interface IInstallationInstallerUbuntuPlanInspector
{
    Task<InstallationInstallerHostInspectionResult> InspectPlanAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IInstallationInstallerUbuntuMutationRollback
{
    Task<InstallationInstallerUbuntuStepResult> RollbackAsync(
        InstallationInstallerUbuntuMutationRequest request,
        IReadOnlyList<InstallationInstallerPlanAction> rollbackCandidates,
        CancellationToken cancellationToken = default);
}

public sealed class InstallationInstallerUbuntuMutationExecutor
{
    private readonly IInstallationInstallerUbuntuMutationPrimitives m_primitives;

    public InstallationInstallerUbuntuMutationExecutor(
        IInstallationInstallerUbuntuMutationPrimitives primitives)
    {
        m_primitives = primitives ??
            throw new ArgumentNullException(nameof(primitives));
    }

    public Task<InstallationInstallerHostInspectionResult> InspectAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        _ = InstallationInstallerUbuntuPrimitivePlanner.Compose(request);
        return m_primitives is IInstallationInstallerUbuntuPlanInspector inspector
            ? inspector.InspectPlanAsync(request, cancellationToken)
            : Task.FromResult(
                InstallationInstallerHostInspectionResult.Unknown(
                    "ubuntu-inspection-unregistered",
                    "Exact Ubuntu installer plan inspection is not registered."));
    }

    public async Task<InstallationInstallerHostMutationResult> ExecuteAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        _ = InstallationInstallerUbuntuPrimitivePlanner.Compose(request);

        InstallationInstallerUbuntuStepResult prepared;
        try
        {
            prepared = await m_primitives.PrepareAsync(
                request,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return InstallationInstallerHostMutationResult.Unknown(
                "ubuntu-prepare-unknown",
                "The fixed Ubuntu mutation preflight outcome is unknown.");
        }

        InstallationInstallerHostMutationResult? preparationFailure =
            TranslateFailure(prepared, mutationMayHaveOccurred: false);
        if (preparationFailure is not null)
        {
            return preparationFailure;
        }

        bool changed = false;
        List<InstallationInstallerPlanAction> appliedActions = [];
        foreach (InstallationInstallerPlanAction action in request.Actions)
        {
            InstallationInstallerUbuntuStepResult step;
            try
            {
                step = await m_primitives.ExecuteAsync(
                    request,
                    action,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                InstallationInstallerHostMutationResult? rollbackFailure =
                    await RollbackAfterCancellationAsync(
                        request,
                        action,
                        appliedActions);
                if (rollbackFailure is not null)
                {
                    return rollbackFailure;
                }
                throw;
            }
            catch
            {
                return await HandleFailureAsync(
                    request,
                    action,
                    InstallationInstallerUbuntuStepResult.Unknown(
                        "ubuntu-step-unknown",
                        "A fixed Ubuntu mutation step outcome is unknown."),
                    appliedActions,
                    includeUnknownActivation: true);
            }

            if (step.Outcome == InstallationInstallerUbuntuStepOutcome.Applied)
            {
                changed = true;
                appliedActions.Add(action);
                continue;
            }
            if (step.Outcome == InstallationInstallerUbuntuStepOutcome.Converged)
            {
                continue;
            }

            return await HandleFailureAsync(
                request,
                action,
                step,
                appliedActions,
                includeUnknownActivation:
                    step.Outcome ==
                    InstallationInstallerUbuntuStepOutcome.Unknown);
        }

        return InstallationInstallerHostMutationResult.Applied(
            changed ? "ubuntu-transaction-applied" : "ubuntu-transaction-converged",
            changed
                ? "The fixed Ubuntu installer transaction completed."
                : "The fixed Ubuntu installer transaction was already converged.");
    }

    private async Task<InstallationInstallerHostMutationResult>
        HandleFailureAsync(
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerPlanAction failedAction,
            InstallationInstallerUbuntuStepResult failure,
            IReadOnlyList<InstallationInstallerPlanAction> appliedActions,
            bool includeUnknownActivation)
    {
        List<InstallationInstallerPlanAction> rollbackCandidates =
            appliedActions.Where(IsActivation).ToList();
        if (includeUnknownActivation &&
            !request.Repair &&
            IsActivation(failedAction))
        {
            rollbackCandidates.Add(failedAction);
        }

        if (rollbackCandidates.Count == 0 ||
            m_primitives is not
                IInstallationInstallerUbuntuMutationRollback rollback)
        {
            return TranslateFailure(
                    failure,
                    mutationMayHaveOccurred: appliedActions.Count > 0) ??
                throw new InvalidOperationException(
                    "A failure result was expected from the Ubuntu step.");
        }

        InstallationInstallerUbuntuStepResult rollbackResult;
        try
        {
            rollbackResult = await rollback.RollbackAsync(
                request,
                rollbackCandidates,
                CancellationToken.None);
        }
        catch
        {
            return RollbackUnknown();
        }
        if (rollbackResult.Outcome is not
            (InstallationInstallerUbuntuStepOutcome.Applied or
             InstallationInstallerUbuntuStepOutcome.Converged))
        {
            return RollbackUnknown();
        }

        bool failedStepWasReadOnly =
            failedAction.Kind is
                InstallationInstallerActionKind.VerifyHealth or
                InstallationInstallerActionKind.VerifyTls;
        if (failure.Outcome == InstallationInstallerUbuntuStepOutcome.Rejected ||
            failedStepWasReadOnly)
        {
            return InstallationInstallerHostMutationResult.Rejected(
                "ubuntu-transaction-rolled-back",
                "Initial runtime activation was rolled back; the immutable release and installer evidence were retained for repair.");
        }

        return InstallationInstallerHostMutationResult.Unknown(
            failure.Code,
            "Initial runtime activation was rolled back, but the failed mutation step still requires reconciliation.");
    }

    private async Task<InstallationInstallerHostMutationResult?>
        RollbackAfterCancellationAsync(
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerPlanAction cancelledAction,
            IReadOnlyList<InstallationInstallerPlanAction> appliedActions)
    {
        List<InstallationInstallerPlanAction> rollbackCandidates =
            appliedActions.Where(IsActivation).ToList();
        if (!request.Repair && IsActivation(cancelledAction))
        {
            rollbackCandidates.Add(cancelledAction);
        }
        if (rollbackCandidates.Count == 0 ||
            m_primitives is not
                IInstallationInstallerUbuntuMutationRollback rollback)
        {
            return null;
        }

        try
        {
            InstallationInstallerUbuntuStepResult result =
                await rollback.RollbackAsync(
                    request,
                    rollbackCandidates,
                    CancellationToken.None);
            return result.Outcome is
                InstallationInstallerUbuntuStepOutcome.Applied or
                InstallationInstallerUbuntuStepOutcome.Converged
                ? null
                : RollbackUnknown();
        }
        catch
        {
            return RollbackUnknown();
        }
    }

    private static bool IsActivation(
        InstallationInstallerPlanAction action) =>
        action.Kind is
            InstallationInstallerActionKind.ActivateInitialRelease or
            InstallationInstallerActionKind.ActivateSystemdUnit;

    private static InstallationInstallerHostMutationResult RollbackUnknown() =>
        InstallationInstallerHostMutationResult.Unknown(
            "ubuntu-rollback-reconciliation-required",
            "The bounded initial activation rollback did not reach an exact safe postcondition.");

    private static InstallationInstallerHostMutationResult? TranslateFailure(
        InstallationInstallerUbuntuStepResult step,
        bool mutationMayHaveOccurred)
    {
        return step.Outcome switch
        {
            InstallationInstallerUbuntuStepOutcome.Converged => null,
            InstallationInstallerUbuntuStepOutcome.Applied => null,
            InstallationInstallerUbuntuStepOutcome.Rejected
                when !mutationMayHaveOccurred =>
                InstallationInstallerHostMutationResult.Rejected(
                    step.Code,
                    step.Summary),
            InstallationInstallerUbuntuStepOutcome.Rejected =>
                InstallationInstallerHostMutationResult.Unknown(
                    "ubuntu-partial-mutation",
                    "A fixed Ubuntu step was rejected after host mutation began."),
            InstallationInstallerUbuntuStepOutcome.Unknown =>
                InstallationInstallerHostMutationResult.Unknown(
                    step.Code,
                    step.Summary),
            _ => throw new InvalidOperationException(
                "The Ubuntu mutation primitive returned an unsupported outcome.")
        };
    }

    private static void ValidateRequest(
        InstallationInstallerUbuntuMutationRequest request)
    {
        if (request.PlanId.Length != 64 ||
            request.PlanId.Any(character =>
                !char.IsAsciiHexDigit(character)) ||
            request.SetupRevision < 0 ||
            string.IsNullOrWhiteSpace(request.ReleaseIdentity) ||
            !Enum.IsDefined(request.Architecture) ||
            !Path.IsPathFullyQualified(request.ImmutableStagingPath) ||
            !Path.IsPathFullyQualified(request.TargetReleasePath) ||
            !Path.IsPathFullyQualified(request.InstallerAssetRoot) ||
            request.Actions.Count == 0)
        {
            throw new InvalidOperationException(
                "The Ubuntu mutation request is invalid.");
        }

        int releaseActions = 0;
        for (int index = 0; index < request.Actions.Count; index++)
        {
            InstallationInstallerPlanAction action = request.Actions[index];
            if (action.Order != index + 1 ||
                !Enum.IsDefined(action.Kind) ||
                string.IsNullOrWhiteSpace(action.Target) ||
                action.Target.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    "The Ubuntu mutation action inventory is invalid.");
            }
            if (action.Kind ==
                InstallationInstallerActionKind.InstallVerifiedRelease)
            {
                releaseActions++;
            }
        }
        if (releaseActions != 1)
        {
            throw new InvalidOperationException(
                "The Ubuntu mutation request requires one verified-release step.");
        }
    }
}
