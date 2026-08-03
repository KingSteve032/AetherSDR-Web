namespace AetherSDR.Web.Setup;

public sealed record InstallationRuntimeSettings
{
    public const string SectionName = "InstallationRuntime";

    public bool Enabled { get; init; }
    public long SetupRevision { get; init; } = -1;
    public InstallationRuntimeRole RuntimeRole { get; init; } =
        InstallationRuntimeRole.Gateway;
    public InstallationTopologyKind Topology { get; init; } =
        InstallationTopologyKind.PersonalSingleStation;
    public string CanonicalPublicUrl { get; init; } = string.Empty;
    public bool InstallTransmitSupport { get; init; }
}

public static class InstallationRuntimeStartupGate
{
    public static async Task<InstallationRuntimeReadinessReport?> RequireReadyAsync(
        InstallationRuntimeSettings settings,
        Func<InstallationPaths> resolvePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resolvePaths);

        if (!settings.Enabled)
        {
            ValidateDisabled(settings);
            return null;
        }

        if (settings.SetupRevision < 0 ||
            !Enum.IsDefined(settings.RuntimeRole) ||
            !Enum.IsDefined(settings.Topology))
        {
            throw new InvalidOperationException(
                "Enabled installation runtime settings require a non-negative setup " +
                "revision and supported role and topology values.");
        }
        if (settings.RuntimeRole != InstallationRuntimeRole.Gateway)
        {
            throw new InvalidOperationException(
                "The AetherSDR web process supports only the Gateway installation " +
                "runtime role.");
        }

        InstallationTopologyProfile profile =
            InstallationTopologyProfile.For(settings.Topology);
        if (!profile.GatewayRunsHere)
        {
            throw new InvalidOperationException(
                $"Installation topology '{settings.Topology}' does not run the web " +
                "gateway on this host.");
        }

        CanonicalPublicUrl publicUrl =
            CanonicalPublicUrl.Parse(settings.CanonicalPublicUrl);
        if (!string.Equals(
                publicUrl.Value,
                settings.CanonicalPublicUrl,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Enabled installation runtime settings require a canonical public URL.");
        }

        InstallationPaths paths = resolvePaths() ??
            throw new InvalidOperationException(
                "Installation runtime path resolution returned no paths.");
        InstallationPaths.Validate(paths);
        InstallationRuntimeBinding binding = new(
            settings.SetupRevision,
            settings.RuntimeRole,
            settings.Topology,
            publicUrl.Value,
            paths,
            settings.InstallTransmitSupport);
        InstallationSetupStore store = new(paths.SetupStatePath);
        return await new InstallationRuntimeReadiness(store)
            .RequireReadyAsync(binding, cancellationToken);
    }

    private static void ValidateDisabled(InstallationRuntimeSettings settings)
    {
        if (settings.SetupRevision != -1 ||
            settings.RuntimeRole != InstallationRuntimeRole.Gateway ||
            settings.Topology !=
                InstallationTopologyKind.PersonalSingleStation ||
            !string.IsNullOrEmpty(settings.CanonicalPublicUrl) ||
            settings.InstallTransmitSupport)
        {
            throw new InvalidOperationException(
                "Disabled installation runtime settings must retain the exact " +
                "empty default binding.");
        }
    }
}
