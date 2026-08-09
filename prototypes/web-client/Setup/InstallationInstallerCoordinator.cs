namespace AetherSDR.Web.Setup;

public sealed class InstallationInstallerExecutionSettings
{
    public const string SectionName = "InstallationInstaller";

    public bool Enabled { get; init; }
}

public enum InstallationInstallerOperationKind
{
    Validate = 1,
    Apply = 2,
    Repair = 3
}

public enum InstallationInstallerOperationOutcome
{
    Converged = 1,
    DriftDetected = 2,
    Applied = 3,
    Repaired = 4,
    Disabled = 5,
    Rejected = 6,
    ReconciliationRequired = 7
}

public enum InstallationInstallerHostInspectionOutcome
{
    Converged = 1,
    Drift = 2,
    Unknown = 3
}

public enum InstallationInstallerHostMutationOutcome
{
    Applied = 1,
    Rejected = 2,
    Unknown = 3
}

public sealed record InstallationInstallerHostInspectionResult
{
    private InstallationInstallerHostInspectionResult(
        InstallationInstallerHostInspectionOutcome outcome,
        string code,
        string summary)
    {
        Outcome = outcome;
        Code = InstallationInstallerHostResultText.ValidateCode(code);
        Summary = InstallationInstallerHostResultText.ValidateSummary(summary);
    }

    public InstallationInstallerHostInspectionOutcome Outcome { get; }

    public string Code { get; }

    public string Summary { get; }

    public static InstallationInstallerHostInspectionResult Converged(
        string code = "converged",
        string summary = "The host matches the exact installer plan.") =>
        new(InstallationInstallerHostInspectionOutcome.Converged, code, summary);

    public static InstallationInstallerHostInspectionResult Drift(
        string code,
        string summary) =>
        new(InstallationInstallerHostInspectionOutcome.Drift, code, summary);

    public static InstallationInstallerHostInspectionResult Unknown(
        string code,
        string summary) =>
        new(InstallationInstallerHostInspectionOutcome.Unknown, code, summary);
}

public sealed record InstallationInstallerHostMutationResult
{
    private InstallationInstallerHostMutationResult(
        InstallationInstallerHostMutationOutcome outcome,
        string code,
        string summary)
    {
        Outcome = outcome;
        Code = InstallationInstallerHostResultText.ValidateCode(code);
        Summary = InstallationInstallerHostResultText.ValidateSummary(summary);
    }

    public InstallationInstallerHostMutationOutcome Outcome { get; }

    public string Code { get; }

    public string Summary { get; }

    public static InstallationInstallerHostMutationResult Applied(
        string code = "applied",
        string summary = "The host transaction was accepted.") =>
        new(InstallationInstallerHostMutationOutcome.Applied, code, summary);

    public static InstallationInstallerHostMutationResult Rejected(
        string code,
        string summary) =>
        new(InstallationInstallerHostMutationOutcome.Rejected, code, summary);

    public static InstallationInstallerHostMutationResult Unknown(
        string code,
        string summary) =>
        new(InstallationInstallerHostMutationOutcome.Unknown, code, summary);
}

public interface IInstallationInstallerHostTransaction
{
    Task<InstallationInstallerHostInspectionResult> InspectAsync(
        InstallationInstallerPlanReport plan,
        CancellationToken cancellationToken = default);

    Task<InstallationInstallerHostMutationResult> ApplyAsync(
        InstallationInstallerPlanReport plan,
        CancellationToken cancellationToken = default);

    Task<InstallationInstallerHostMutationResult> RepairAsync(
        InstallationInstallerPlanReport plan,
        CancellationToken cancellationToken = default);
}

public sealed record InstallationInstallerOperationResult(
    InstallationInstallerOperationKind Operation,
    InstallationInstallerOperationOutcome Outcome,
    string PlanId,
    bool MutationAttempted,
    int InspectionCount,
    string Code,
    string Summary);

public sealed class InstallationInstallerCoordinator : IDisposable
{
    private readonly InstallationSetupStore m_store;
    private readonly IInstallationInstallerHostTransaction m_host;
    private readonly InstallationInstallerExecutionSettings m_settings;
    private readonly SemaphoreSlim m_operationGate = new(1, 1);
    private bool m_disposed;

    public InstallationInstallerCoordinator(
        InstallationSetupStore store,
        IInstallationInstallerHostTransaction host,
        InstallationInstallerExecutionSettings? settings = null)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_host = host ?? throw new ArgumentNullException(nameof(host));
        m_settings = settings ?? new InstallationInstallerExecutionSettings();
    }

    public async Task<InstallationInstallerPlanReport> PlanAsync(
        InstallationInstallerSelection selection,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        InstallationSetupState state =
            await m_store.LoadAsync(cancellationToken);
        return InstallationInstallerPlanComposer.Compose(state, selection);
    }

    public async Task<InstallationInstallerOperationResult> ValidateAsync(
        InstallationInstallerPlanReport report,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        InstallationInstallerPlan plan =
            InstallationInstallerPlanComposer.RequireExactPlan(report);
        await m_operationGate.WaitAsync(cancellationToken);
        try
        {
            await RequireCurrentStateAsync(plan, cancellationToken);
            InstallationInstallerHostInspectionResult inspection =
                await m_host.InspectAsync(report, cancellationToken);
            return inspection.Outcome switch
            {
                InstallationInstallerHostInspectionOutcome.Converged =>
                    Result(
                        InstallationInstallerOperationKind.Validate,
                        InstallationInstallerOperationOutcome.Converged,
                        plan,
                        mutationAttempted: false,
                        inspectionCount: 1,
                        inspection.Code,
                        inspection.Summary),
                InstallationInstallerHostInspectionOutcome.Drift =>
                    Result(
                        InstallationInstallerOperationKind.Validate,
                        InstallationInstallerOperationOutcome.DriftDetected,
                        plan,
                        mutationAttempted: false,
                        inspectionCount: 1,
                        inspection.Code,
                        inspection.Summary),
                InstallationInstallerHostInspectionOutcome.Unknown =>
                    Result(
                        InstallationInstallerOperationKind.Validate,
                        InstallationInstallerOperationOutcome.ReconciliationRequired,
                        plan,
                        mutationAttempted: false,
                        inspectionCount: 1,
                        inspection.Code,
                        inspection.Summary),
                _ => throw InvalidHostOutcome()
            };
        }
        finally
        {
            m_operationGate.Release();
        }
    }

    public async Task<InstallationInstallerOperationResult> ApplyAsync(
        InstallationInstallerPlanReport report,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        InstallationInstallerPlan plan =
            InstallationInstallerPlanComposer.RequireExactPlan(report);
        if (!m_settings.Enabled)
        {
            return Disabled(
                InstallationInstallerOperationKind.Apply,
                plan);
        }

        await m_operationGate.WaitAsync(cancellationToken);
        try
        {
            await RequireCurrentStateAsync(plan, cancellationToken);
            InstallationInstallerHostMutationResult mutation =
                await m_host.ApplyAsync(report, cancellationToken);
            return await CompleteMutationAsync(
                InstallationInstallerOperationKind.Apply,
                InstallationInstallerOperationOutcome.Applied,
                report,
                plan,
                mutation,
                cancellationToken);
        }
        finally
        {
            m_operationGate.Release();
        }
    }

    public async Task<InstallationInstallerOperationResult> RepairAsync(
        InstallationInstallerPlanReport report,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        InstallationInstallerPlan plan =
            InstallationInstallerPlanComposer.RequireExactPlan(report);
        if (!m_settings.Enabled)
        {
            return Disabled(
                InstallationInstallerOperationKind.Repair,
                plan);
        }

        await m_operationGate.WaitAsync(cancellationToken);
        try
        {
            await RequireCurrentStateAsync(plan, cancellationToken);
            InstallationInstallerHostInspectionResult before =
                await m_host.InspectAsync(report, cancellationToken);
            if (before.Outcome == InstallationInstallerHostInspectionOutcome.Converged)
            {
                return Result(
                    InstallationInstallerOperationKind.Repair,
                    InstallationInstallerOperationOutcome.Converged,
                    plan,
                    mutationAttempted: false,
                    inspectionCount: 1,
                    before.Code,
                    before.Summary);
            }
            if (before.Outcome == InstallationInstallerHostInspectionOutcome.Unknown)
            {
                return Result(
                    InstallationInstallerOperationKind.Repair,
                    InstallationInstallerOperationOutcome.ReconciliationRequired,
                    plan,
                    mutationAttempted: false,
                    inspectionCount: 1,
                    before.Code,
                    before.Summary);
            }
            if (before.Outcome != InstallationInstallerHostInspectionOutcome.Drift)
            {
                throw InvalidHostOutcome();
            }

            InstallationInstallerHostMutationResult mutation =
                await m_host.RepairAsync(report, cancellationToken);
            return await CompleteMutationAsync(
                InstallationInstallerOperationKind.Repair,
                InstallationInstallerOperationOutcome.Repaired,
                report,
                plan,
                mutation,
                cancellationToken,
                initialInspectionCount: 1);
        }
        finally
        {
            m_operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (m_disposed)
        {
            return;
        }
        m_disposed = true;
        m_operationGate.Dispose();
    }

    private async Task<InstallationInstallerOperationResult> CompleteMutationAsync(
        InstallationInstallerOperationKind operation,
        InstallationInstallerOperationOutcome successOutcome,
        InstallationInstallerPlanReport report,
        InstallationInstallerPlan plan,
        InstallationInstallerHostMutationResult mutation,
        CancellationToken cancellationToken,
        int initialInspectionCount = 0)
    {
        if (mutation.Outcome == InstallationInstallerHostMutationOutcome.Rejected)
        {
            return Result(
                operation,
                InstallationInstallerOperationOutcome.Rejected,
                plan,
                mutationAttempted: true,
                initialInspectionCount,
                mutation.Code,
                mutation.Summary);
        }
        if (mutation.Outcome == InstallationInstallerHostMutationOutcome.Unknown)
        {
            return Result(
                operation,
                InstallationInstallerOperationOutcome.ReconciliationRequired,
                plan,
                mutationAttempted: true,
                initialInspectionCount,
                mutation.Code,
                mutation.Summary);
        }
        if (mutation.Outcome != InstallationInstallerHostMutationOutcome.Applied)
        {
            throw InvalidHostOutcome();
        }

        InstallationInstallerHostInspectionResult after =
            await m_host.InspectAsync(report, cancellationToken);
        int inspectionCount = initialInspectionCount + 1;
        if (after.Outcome == InstallationInstallerHostInspectionOutcome.Converged)
        {
            return Result(
                operation,
                successOutcome,
                plan,
                mutationAttempted: true,
                inspectionCount,
                mutation.Code,
                mutation.Summary);
        }

        return Result(
            operation,
            InstallationInstallerOperationOutcome.ReconciliationRequired,
            plan,
            mutationAttempted: true,
            inspectionCount,
            after.Code,
            after.Summary);
    }

    private async Task RequireCurrentStateAsync(
        InstallationInstallerPlan plan,
        CancellationToken cancellationToken)
    {
        InstallationSetupState current =
            await m_store.LoadAsync(cancellationToken);
        if (current.Revision != plan.State.Revision)
        {
            throw new InstallationSetupConcurrencyException(
                plan.State.Revision,
                current.Revision);
        }
        if (!Equals(current, plan.State))
        {
            throw new InvalidOperationException(
                "The current setup state no longer matches the exact installer plan.");
        }
    }

    private static InstallationInstallerOperationResult Disabled(
        InstallationInstallerOperationKind operation,
        InstallationInstallerPlan plan) =>
        Result(
            operation,
            InstallationInstallerOperationOutcome.Disabled,
            plan,
            mutationAttempted: false,
            inspectionCount: 0,
            "execution-disabled",
            "Installer host mutation is disabled.");

    private static InstallationInstallerOperationResult Result(
        InstallationInstallerOperationKind operation,
        InstallationInstallerOperationOutcome outcome,
        InstallationInstallerPlan plan,
        bool mutationAttempted,
        int inspectionCount,
        string code,
        string summary) =>
        new(
            operation,
            outcome,
            plan.PlanId,
            mutationAttempted,
            inspectionCount,
            InstallationInstallerHostResultText.ValidateCode(code),
            InstallationInstallerHostResultText.ValidateSummary(summary));

    private static InvalidOperationException InvalidHostOutcome() =>
        new("The installer host participant returned an unsupported outcome.");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);
    }
}

internal static class InstallationInstallerHostResultText
{
    internal const int MaximumCodeLength = 64;
    internal const int MaximumSummaryLength = 512;

    internal static string ValidateCode(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > MaximumCodeLength)
        {
            throw new InvalidOperationException(
                "An installer host result code must contain between 1 and 64 characters.");
        }
        foreach (char character in normalized)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.')
            {
                throw new InvalidOperationException(
                    "An installer host result code contains an unsupported character.");
            }
        }
        return normalized;
    }

    internal static string ValidateSummary(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > MaximumSummaryLength ||
            normalized.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "An installer host result summary must be bounded plain text.");
        }
        return normalized;
    }
}
