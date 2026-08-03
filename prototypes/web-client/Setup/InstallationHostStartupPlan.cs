namespace AetherSDR.Web.Setup;

public sealed record InstallationSetupOnlySettings
{
    public const string SectionName = "InstallationSetupOnly";

    public bool Enabled { get; init; }
    public string CanonicalAccessUrl { get; init; } = string.Empty;
}

public enum InstallationHostStartupMode
{
    Legacy = 0,
    SetupOnly = 1,
    NormalRuntime = 2
}

public sealed record InstallationHostStartupPlan(
    InstallationHostStartupMode Mode,
    InstallationPaths? Paths,
    InstallationSetupStatusReport? SetupStatus,
    InstallationRuntimeReadinessReport? RuntimeReadiness,
    string? SetupOnlyCanonicalAccessUrl)
{
    public bool SetupOnlyEligible =>
        Mode == InstallationHostStartupMode.SetupOnly;

    public bool NormalRuntimeReady =>
        Mode == InstallationHostStartupMode.NormalRuntime &&
        RuntimeReadiness?.Ready == true;
}

public static class InstallationHostStartupPlanner
{
    public static async Task<InstallationHostStartupPlan> CreateAsync(
        InstallationSetupOnlySettings setupOnlySettings,
        InstallationRuntimeSettings runtimeSettings,
        Func<InstallationPaths> resolvePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setupOnlySettings);
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        ArgumentNullException.ThrowIfNull(resolvePaths);

        if (!setupOnlySettings.Enabled &&
            !string.IsNullOrEmpty(setupOnlySettings.CanonicalAccessUrl))
        {
            throw new InvalidOperationException(
                "Disabled setup-only settings must retain an empty canonical access URL.");
        }
        if (setupOnlySettings.Enabled && runtimeSettings.Enabled)
        {
            throw new InvalidOperationException(
                "Setup-only startup and normal installation runtime cannot be enabled together.");
        }

        if (!setupOnlySettings.Enabled)
        {
            InstallationRuntimeReadinessReport? runtimeReadiness =
                await InstallationRuntimeStartupGate.RequireReadyAsync(
                    runtimeSettings,
                    resolvePaths,
                    cancellationToken);
            return runtimeReadiness is null
                ? new InstallationHostStartupPlan(
                    InstallationHostStartupMode.Legacy,
                    Paths: null,
                    SetupStatus: null,
                    RuntimeReadiness: null,
                    SetupOnlyCanonicalAccessUrl: null)
                : new InstallationHostStartupPlan(
                    InstallationHostStartupMode.NormalRuntime,
                    Paths: null,
                    SetupStatus: null,
                    runtimeReadiness,
                    SetupOnlyCanonicalAccessUrl: null);
        }

        _ = await InstallationRuntimeStartupGate.RequireReadyAsync(
            runtimeSettings,
            () => throw new InvalidOperationException(
                "Disabled normal runtime settings unexpectedly requested path resolution."),
            cancellationToken);

        CanonicalPublicUrl accessUrl =
            CanonicalPublicUrl.Parse(setupOnlySettings.CanonicalAccessUrl);
        if (!string.Equals(
                accessUrl.Value,
                setupOnlySettings.CanonicalAccessUrl,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Enabled setup-only settings require one exact canonical HTTPS access URL.");
        }

        InstallationPaths paths = resolvePaths() ??
            throw new InvalidOperationException(
                "Setup-only startup path resolution returned no paths.");
        InstallationPaths.Validate(paths);
        InstallationSetupStore store = new(paths.SetupStatePath);
        InstallationSetupState state = await store.LoadAsync(cancellationToken);
        InstallationSetupStateValidator.Validate(state);

        if (state.Lock.Mode == InstallationSetupLockMode.Complete ||
            state.LastCompletedStep == InstallationSetupStep.Administrator)
        {
            throw new InvalidOperationException(
                "Setup-only startup is forbidden after installation setup completes.");
        }

        if (state.Topology is InstallationTopologyKind topology)
        {
            InstallationTopologyProfile profile =
                InstallationTopologyProfile.For(topology);
            if (!profile.GatewayRunsHere)
            {
                throw new InvalidOperationException(
                    $"Installation topology '{topology}' does not run the web setup center on this host.");
            }
        }

        return new InstallationHostStartupPlan(
            InstallationHostStartupMode.SetupOnly,
            paths,
            InstallationSetupStatusReport.From(state),
            RuntimeReadiness: null,
            accessUrl.Value);
    }
}
