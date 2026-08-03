namespace AetherSDR.Web.Setup;

public enum InstallationRuntimeRole
{
    Gateway = 1,
    RemoteStationNode = 2
}

public sealed record InstallationRuntimeBinding(
    long SetupRevision,
    InstallationRuntimeRole RuntimeRole,
    InstallationTopologyKind Topology,
    string CanonicalPublicUrl,
    InstallationPaths Paths,
    bool InstallTransmitSupport);

public sealed record InstallationRuntimeReadinessReport(
    int SetupSchemaVersion,
    long SetupRevision,
    InstallationRuntimeRole RuntimeRole,
    InstallationTopologyKind? Topology,
    bool Ready,
    IReadOnlyList<string> BlockingReasons);

public sealed class InstallationRuntimeReadiness
{
    private readonly InstallationSetupStore m_store;

    public InstallationRuntimeReadiness(InstallationSetupStore store)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<InstallationRuntimeReadinessReport> EvaluateAsync(
        InstallationRuntimeBinding binding,
        CancellationToken cancellationToken = default)
    {
        ValidateBinding(binding);
        InstallationSetupState state = await m_store.LoadAsync(cancellationToken);
        List<string> blockingReasons = [];

        if (state.Lock.Mode != InstallationSetupLockMode.Complete ||
            state.LastCompletedStep != InstallationSetupStep.Administrator)
        {
            blockingReasons.Add(
                "Installation setup is not complete and normal runtime must remain disabled.");
        }
        if (state.Revision != binding.SetupRevision)
        {
            blockingReasons.Add(
                $"Runtime configuration targets setup revision {binding.SetupRevision}, " +
                $"but persisted setup is revision {state.Revision}.");
        }

        InstallationTopologyKind? topology = state.Topology;
        if (topology is null)
        {
            blockingReasons.Add("Persisted setup has no installation topology.");
        }
        else
        {
            if (topology.Value != binding.Topology)
            {
                blockingReasons.Add(
                    $"Runtime topology '{binding.Topology}' does not match persisted " +
                    $"topology '{topology.Value}'.");
            }

            InstallationTopologyProfile profile =
                InstallationTopologyProfile.For(topology.Value);
            bool roleAllowed = binding.RuntimeRole switch
            {
                InstallationRuntimeRole.Gateway => profile.GatewayRunsHere,
                InstallationRuntimeRole.RemoteStationNode =>
                    profile.AgentRunsHere && profile.StationEngineRunsHere,
                _ => false
            };
            if (!roleAllowed)
            {
                blockingReasons.Add(
                    $"Runtime role '{binding.RuntimeRole}' is not permitted by " +
                    $"topology '{topology.Value}'.");
            }
        }

        if (!string.Equals(
                state.CanonicalPublicUrl,
                binding.CanonicalPublicUrl,
                StringComparison.Ordinal))
        {
            blockingReasons.Add(
                "Runtime canonical public URL does not match persisted setup.");
        }
        if (state.Paths is null || !PathsEqual(state.Paths, binding.Paths))
        {
            blockingReasons.Add(
                "Runtime installation paths do not match persisted setup.");
        }
        if (state.InstallTransmitSupport != binding.InstallTransmitSupport)
        {
            blockingReasons.Add(
                "Runtime TX-support installation choice does not match persisted setup.");
        }

        return new InstallationRuntimeReadinessReport(
            state.SchemaVersion,
            state.Revision,
            binding.RuntimeRole,
            topology,
            blockingReasons.Count == 0,
            blockingReasons.AsReadOnly());
    }

    public async Task<InstallationRuntimeReadinessReport> RequireReadyAsync(
        InstallationRuntimeBinding binding,
        CancellationToken cancellationToken = default)
    {
        InstallationRuntimeReadinessReport report =
            await EvaluateAsync(binding, cancellationToken);
        if (!report.Ready)
        {
            throw new InvalidOperationException(
                "Installation runtime is not ready: " +
                string.Join(" ", report.BlockingReasons));
        }

        return report;
    }

    private static void ValidateBinding(InstallationRuntimeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.SetupRevision < 0 ||
            !Enum.IsDefined(binding.RuntimeRole) ||
            !Enum.IsDefined(binding.Topology))
        {
            throw new InvalidOperationException(
                "Installation runtime binding contains an invalid revision or enum value.");
        }

        CanonicalPublicUrl publicUrl =
            CanonicalPublicUrl.Parse(binding.CanonicalPublicUrl);
        if (!string.Equals(
                publicUrl.Value,
                binding.CanonicalPublicUrl,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Installation runtime binding requires a canonical public URL.");
        }
        InstallationPaths.Validate(binding.Paths);
    }

    private static bool PathsEqual(
        InstallationPaths expected,
        InstallationPaths actual) =>
        DirectoryEquals(
            expected.ConfigurationDirectory,
            actual.ConfigurationDirectory) &&
        DirectoryEquals(expected.StateDirectory, actual.StateDirectory) &&
        DirectoryEquals(expected.SecretDirectory, actual.SecretDirectory) &&
        DirectoryEquals(expected.ReleaseDirectory, actual.ReleaseDirectory) &&
        DirectoryEquals(expected.BackupDirectory, actual.BackupDirectory) &&
        DirectoryEquals(expected.LogDirectory, actual.LogDirectory);

    private static bool DirectoryEquals(string expected, string actual)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        string normalizedExpected = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(expected));
        string normalizedActual = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(actual));
        return comparer.Equals(normalizedExpected, normalizedActual);
    }
}
