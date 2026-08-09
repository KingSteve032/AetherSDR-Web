using AetherSDR.Web.Releases;

namespace AetherSDR.Web.Setup;

public sealed class InstallationInstallerUbuntuMutationRequest
{
    private readonly IReadOnlyList<InstallationInstallerPlanAction> m_actions;

    internal InstallationInstallerUbuntuMutationRequest(
        string planId,
        long setupRevision,
        string releaseIdentity,
        InstallationInstallerArchitecture architecture,
        string immutableStagingPath,
        string targetReleasePath,
        bool repair,
        IReadOnlyList<InstallationInstallerPlanAction> actions,
        string? installerAssetRoot = null,
        VerifiedReleaseStagingReport? verifiedStaging = null,
        VerifiedReleaseInstallationPlan? verifiedInstallationPlan = null)
    {
        PlanId = planId;
        SetupRevision = setupRevision;
        ReleaseIdentity = releaseIdentity;
        Architecture = architecture;
        ImmutableStagingPath = immutableStagingPath;
        TargetReleasePath = targetReleasePath;
        Repair = repair;
        InstallerAssetRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(installerAssetRoot)
                ? AppContext.BaseDirectory
                : installerAssetRoot);
        VerifiedStaging = verifiedStaging;
        VerifiedInstallationPlan = verifiedInstallationPlan;
        m_actions = Array.AsReadOnly(actions.ToArray());
    }

    public string PlanId { get; }
    public long SetupRevision { get; }
    public string ReleaseIdentity { get; }
    public InstallationInstallerArchitecture Architecture { get; }
    public string ImmutableStagingPath { get; }
    public string TargetReleasePath { get; }
    public bool Repair { get; }
    public string InstallerAssetRoot { get; }
    internal VerifiedReleaseStagingReport? VerifiedStaging { get; }
    internal VerifiedReleaseInstallationPlan? VerifiedInstallationPlan { get; }
    public IReadOnlyList<InstallationInstallerPlanAction> Actions => m_actions;
}

public interface IInstallationInstallerUbuntuRuntime
{
    Task<InstallationInstallerHostInspectionResult> InspectAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<InstallationInstallerHostMutationResult> MutateAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class InstallationInstallerVerifiedReleaseBinding
{
    private readonly VerifiedReleaseInstallationPlan m_plan;
    private readonly VerifiedReleaseStagingReport? m_stagingReport;

    private InstallationInstallerVerifiedReleaseBinding(
        VerifiedReleaseInstallationPlan plan,
        VerifiedReleaseStagingReport? stagingReport)
    {
        m_plan = plan ?? throw new ArgumentNullException(nameof(plan));
        m_stagingReport = stagingReport;
    }

    public string ReleaseIdentity => m_plan.TargetReleaseIdentity;

    public long SetupRevision => m_plan.SetupRevision;

    internal int TargetConfigurationSchemaVersion =>
        m_plan.TargetConfigurationSchemaVersion;

    public static InstallationInstallerVerifiedReleaseBinding Create(
        VerifiedReleaseStagingReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        VerifiedStagedRelease staged = report.StagedRelease ??
            throw new InvalidOperationException(
                "Installer mutation requires a retained verified staged release.");
        if (!report.Succeeded ||
            report.FailureCode != VerifiedReleaseStagingFailureCode.None ||
            report.SetupRevision != staged.Plan.SetupRevision ||
            !string.Equals(
                report.TargetReleaseIdentity,
                staged.Plan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            report.PackageCount != staged.Plan.Packages.Count ||
            report.StagedBytes != staged.StagedBytes ||
            !report.ManifestStaged ||
            !report.ImmutableStagingTree ||
            report.TargetPublished ||
            report.CurrentPointerChanged ||
            report.CleanupRequired)
        {
            throw new InvalidOperationException(
                "The staged-release summary does not match its retained verified release.");
        }
        return new InstallationInstallerVerifiedReleaseBinding(
            staged.Plan,
            report);
    }

    public static InstallationInstallerVerifiedReleaseBinding CreateInitial(
        OfflineReleaseInstallPreflightResult preflight,
        InstallationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(paths);
        VerifiedReleaseInstallationPlanCompositionResult composition =
            new VerifiedReleaseInstallationPlanComposer().ComposeInitial(
                preflight,
                paths);
        VerifiedReleaseInstallationPlan plan = composition.Plan ??
            throw new InvalidOperationException(
                "Initial installation requires one retained verified release plan.");
        if (!composition.Succeeded ||
            composition.FailureCode !=
                VerifiedReleaseInstallationPlanFailureCode.None ||
            composition.SetupRevision != plan.SetupRevision ||
            !string.Equals(
                composition.TargetReleaseIdentity,
                plan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            composition.PackageCount != plan.Packages.Count ||
            !composition.ImmutableTargetRequired ||
            !composition.TemporaryStagingRequired ||
            !composition.AtomicDirectoryPublishRequired ||
            !composition.AtomicCurrentPointerSwitchRequired ||
            !composition.StablePreflightRevalidationRequired)
        {
            throw new InvalidOperationException(
                "The initial verified release plan summary is inconsistent.");
        }
        if (composition.MigrationRequired !=
                (plan.MigrationKind == ReleaseMigrationKind.Required) ||
            composition.MigrationKind != plan.MigrationKind ||
            composition.HostRestartRequired != plan.RestartHost)
        {
            throw new InvalidOperationException(
                "The initial verified release execution summary is inconsistent.");
        }
        RequireInitialExecutionBoundary(plan);
        return new InstallationInstallerVerifiedReleaseBinding(
            plan,
            stagingReport: null);
    }

    internal static void RequireInitialExecutionBoundary(
        VerifiedReleaseInstallationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.MigrationKind != ReleaseMigrationKind.None ||
            plan.RestartHost)
        {
            throw new InvalidOperationException(
                "Initial installation does not execute release migrations or host restarts.");
        }
    }

    internal InstallationInstallerUbuntuMutationRequest Bind(
        InstallationInstallerPlan exact,
        bool repair)
    {
        VerifiedReleaseInstallationPlan release = m_plan;
        InstallationInstallerArchitecture architecture =
            release.Architecture switch
            {
                ReleaseManifestArchitecture.LinuxX64 =>
                    InstallationInstallerArchitecture.LinuxX64,
                ReleaseManifestArchitecture.LinuxArm64 =>
                    InstallationInstallerArchitecture.LinuxArm64,
                _ => throw new InvalidOperationException(
                    "The verified release architecture is unsupported.")
            };
        if (release.SetupRevision != exact.State.Revision ||
            !string.Equals(
                release.TargetReleaseIdentity,
                exact.Selection.ReleaseIdentity,
                StringComparison.Ordinal) ||
            architecture != exact.Selection.Architecture ||
            release.InstallTransmitSupport !=
                exact.State.InstallTransmitSupport)
        {
            throw new InvalidOperationException(
                "The verified staged release does not match the exact installer plan.");
        }

        return new InstallationInstallerUbuntuMutationRequest(
            exact.PlanId,
            exact.State.Revision,
            release.TargetReleaseIdentity,
            architecture,
            m_stagingReport?.StagedRelease?.StagingPath ??
                Path.Combine(
                    release.DeploymentRootPath,
                    VerifiedReleaseStagingService.StagingDirectoryName),
            release.TargetReleasePath,
            repair,
            exact.Actions,
            installerAssetRoot: null,
            verifiedStaging: m_stagingReport,
            verifiedInstallationPlan: m_plan);
    }
}

public sealed class InstallationInstallerUbuntuHostTransaction :
    IInstallationInstallerHostTransaction
{
    private readonly IInstallationInstallerUbuntuRuntime m_runtime;
    private readonly InstallationInstallerVerifiedReleaseBinding? m_release;

    public InstallationInstallerUbuntuHostTransaction(
        IInstallationInstallerUbuntuRuntime runtime,
        InstallationInstallerVerifiedReleaseBinding? release = null)
    {
        m_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        m_release = release;
    }

    public async Task<InstallationInstallerHostInspectionResult> InspectAsync(
        InstallationInstallerPlanReport plan,
        CancellationToken cancellationToken = default)
    {
        InstallationInstallerPlan exact =
            InstallationInstallerPlanComposer.RequireExactPlan(plan);
        try
        {
            InstallationInstallerUbuntuMutationRequest request =
                m_release?.Bind(exact, repair: false) ??
                CreateInspectionRequest(exact);
            return await m_runtime.InspectAsync(
                request,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return InstallationInstallerHostInspectionResult.Unknown(
                "ubuntu-inspection-failed",
                "The Ubuntu host inspection did not complete.");
        }
    }

    public Task<InstallationInstallerHostMutationResult> ApplyAsync(
        InstallationInstallerPlanReport plan,
        CancellationToken cancellationToken = default) =>
        MutateAsync(plan, repair: false, cancellationToken);

    public Task<InstallationInstallerHostMutationResult> RepairAsync(
        InstallationInstallerPlanReport plan,
        CancellationToken cancellationToken = default) =>
        MutateAsync(plan, repair: true, cancellationToken);

    private Task<InstallationInstallerHostMutationResult> MutateAsync(
        InstallationInstallerPlanReport plan,
        bool repair,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstallationInstallerPlan exact =
            InstallationInstallerPlanComposer.RequireExactPlan(plan);
        if (m_release is null)
        {
            return Task.FromResult(
                InstallationInstallerHostMutationResult.Rejected(
                    "verified-release-unbound",
                    "Host mutation requires an exact verified immutable release payload."));
        }

        InstallationInstallerUbuntuMutationRequest request =
            m_release.Bind(exact, repair);
        return m_runtime.MutateAsync(request, cancellationToken);
    }

    private static InstallationInstallerUbuntuMutationRequest
        CreateInspectionRequest(InstallationInstallerPlan exact)
    {
        InstallationPaths paths = exact.State.Paths ??
            throw new InvalidOperationException(
                "Exact installer inspection requires installation paths.");
        string deploymentRoot = Path.GetDirectoryName(paths.ReleaseDirectory) ??
            throw new InvalidOperationException(
                "Exact installer inspection requires a release parent.");
        return new InstallationInstallerUbuntuMutationRequest(
            exact.PlanId,
            exact.State.Revision,
            exact.Selection.ReleaseIdentity,
            exact.Selection.Architecture,
            Path.Combine(
                deploymentRoot,
                ".release-staging",
                exact.PlanId),
            Path.Combine(
                paths.ReleaseDirectory,
                exact.Selection.ReleaseIdentity),
            repair: false,
            exact.Actions);
    }
}

public sealed class InstallationInstallerUbuntuRuntimeSettings
{
    public const string SectionName = "InstallationInstallerUbuntu";

    public bool MutationEnabled { get; init; }
}

public sealed class LocalInstallationInstallerUbuntuRuntime :
    IInstallationInstallerUbuntuRuntime
{
    private readonly InstallationInstallerUbuntuMutationExecutor? m_executor;
    private readonly InstallationInstallerUbuntuRuntimeSettings m_settings;

    public LocalInstallationInstallerUbuntuRuntime(
        InstallationInstallerUbuntuMutationExecutor? executor = null,
        InstallationInstallerUbuntuRuntimeSettings? settings = null)
    {
        m_executor = executor;
        m_settings = settings ??
            new InstallationInstallerUbuntuRuntimeSettings();
    }

    public Task<InstallationInstallerHostInspectionResult> InspectAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (m_executor is null)
        {
            return Task.FromResult(
                InstallationInstallerHostInspectionResult.Unknown(
                    "ubuntu-inspection-unregistered",
                    "Exact Ubuntu installer plan inspection is not registered."));
        }
        return m_executor.InspectAsync(request, cancellationToken);
    }

    public Task<InstallationInstallerHostMutationResult> MutateAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!m_settings.MutationEnabled)
        {
            return Task.FromResult(
                InstallationInstallerHostMutationResult.Rejected(
                    "ubuntu-mutation-disabled",
                    "Ubuntu installer mutation is disabled."));
        }
        if (m_executor is null)
        {
            return Task.FromResult(
                InstallationInstallerHostMutationResult.Rejected(
                    "ubuntu-mutation-unregistered",
                    "Ubuntu installer mutation is not registered in the local runtime."));
        }
        return m_executor.ExecuteAsync(request, cancellationToken);
    }

}
