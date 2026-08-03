namespace AetherSDR.Web.Setup;

public sealed class InstallationSetupWorkflow
{
    private readonly InstallationSetupStore m_store;

    public InstallationSetupWorkflow(InstallationSetupStore store)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<InstallationSetupState> ConfigureTopologyAsync(
        long expectedRevision,
        InstallationTopologyKind topology,
        CancellationToken cancellationToken = default)
    {
        _ = InstallationTopologyProfile.For(topology);
        return UpdateStepAsync(
            expectedRevision,
            InstallationSetupStep.Topology,
            state => state with { Topology = topology },
            cancellationToken);
    }

    public Task<InstallationSetupState> ConfigurePublicUrlAsync(
        long expectedRevision,
        string canonicalPublicUrl,
        CancellationToken cancellationToken = default)
    {
        CanonicalPublicUrl publicUrl =
            CanonicalPublicUrl.Parse(canonicalPublicUrl);
        return UpdateStepAsync(
            expectedRevision,
            InstallationSetupStep.PublicUrl,
            state => state with
            {
                CanonicalPublicUrl = publicUrl.Value
            },
            cancellationToken);
    }

    public Task<InstallationSetupState> ConfigurePathsAsync(
        long expectedRevision,
        InstallationPaths paths,
        CancellationToken cancellationToken = default)
    {
        InstallationPaths.Validate(paths);
        return UpdateStepAsync(
            expectedRevision,
            InstallationSetupStep.Paths,
            state => state with { Paths = paths },
            cancellationToken);
    }

    public Task<InstallationSetupState> ConfigureUpdateChannelAsync(
        long expectedRevision,
        InstallationUpdateChannel updateChannel,
        string? pinnedRelease = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(updateChannel))
        {
            throw new InvalidOperationException(
                $"Unsupported installation update channel '{updateChannel}'.");
        }

        string normalizedPinnedRelease = pinnedRelease?.Trim() ?? string.Empty;
        if (updateChannel == InstallationUpdateChannel.Pinned)
        {
            normalizedPinnedRelease =
                InstallationReleaseIdentity.Parse(normalizedPinnedRelease);
        }
        else if (!string.IsNullOrEmpty(normalizedPinnedRelease))
        {
            throw new InvalidOperationException(
                "Only the pinned update channel may include a release identity.");
        }

        return UpdateStepAsync(
            expectedRevision,
            InstallationSetupStep.UpdateChannel,
            state => state with
            {
                UpdateChannel = updateChannel,
                PinnedRelease = normalizedPinnedRelease
            },
            cancellationToken);
    }

    public Task<InstallationSetupState> ConfirmBackupLocationAsync(
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        UpdateStepAsync(
            expectedRevision,
            InstallationSetupStep.Backup,
            state => state,
            cancellationToken);

    public Task<InstallationSetupState> ConfigureTransmitSupportAsync(
        long expectedRevision,
        bool installTransmitSupport,
        CancellationToken cancellationToken = default) =>
        UpdateStepAsync(
            expectedRevision,
            InstallationSetupStep.TransmitSupport,
            state => state with
            {
                InstallTransmitSupport = installTransmitSupport
            },
            cancellationToken);

    private Task<InstallationSetupState> UpdateStepAsync(
        long expectedRevision,
        InstallationSetupStep step,
        Func<InstallationSetupState, InstallationSetupState> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        InstallationSetupStep requiredPreviousStep = PreviousStep(step);
        return m_store.UpdateAsync(
            expectedRevision,
            state =>
            {
                RequireClaimedLock(state);
                if (state.LastCompletedStep < requiredPreviousStep)
                {
                    throw new InvalidOperationException(
                        $"Installation setup step '{step}' requires completed step " +
                        $"'{requiredPreviousStep}'.");
                }

                InstallationSetupState requested = update(state);
                InstallationSetupStep completedStep =
                    state.LastCompletedStep > step
                        ? state.LastCompletedStep
                        : step;
                return requested with
                {
                    LastCompletedStep = completedStep
                };
            },
            cancellationToken);
    }

    private static InstallationSetupStep PreviousStep(
        InstallationSetupStep step) =>
        step switch
        {
            InstallationSetupStep.Topology =>
                InstallationSetupStep.BootstrapClaim,
            InstallationSetupStep.PublicUrl =>
                InstallationSetupStep.Topology,
            InstallationSetupStep.Paths =>
                InstallationSetupStep.PublicUrl,
            InstallationSetupStep.UpdateChannel =>
                InstallationSetupStep.Paths,
            InstallationSetupStep.Backup =>
                InstallationSetupStep.UpdateChannel,
            InstallationSetupStep.TransmitSupport =>
                InstallationSetupStep.Backup,
            _ => throw new InvalidOperationException(
                $"Installation setup step '{step}' is not configurable by this workflow.")
        };

    private static void RequireClaimedLock(InstallationSetupState state)
    {
        if (state.Lock.Mode != InstallationSetupLockMode.Claimed)
        {
            throw new InvalidOperationException(
                "Installation setup changes require a claimed first-run lock.");
        }
    }
}
