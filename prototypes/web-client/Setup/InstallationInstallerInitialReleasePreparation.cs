using AetherSDR.Web.Releases;

namespace AetherSDR.Web.Setup;

/// <summary>
/// Verifies a clean-install bundle with the same signed-manifest and immutable
/// package boundary used by M8B. It supplies only a virtual pre-activation
/// release status so the update-only preflight can validate a host that
/// deliberately has no current release yet. It performs no writes.
/// </summary>
public sealed class InstallationInstallerInitialReleasePreparation
{
    private readonly LocalOfflineReleaseBundleVerificationService
        m_bundleVerification;

    public InstallationInstallerInitialReleasePreparation(
        LocalOfflineReleaseBundleVerificationService bundleVerification)
    {
        m_bundleVerification = bundleVerification ??
            throw new ArgumentNullException(nameof(bundleVerification));
    }

    public async Task<InstallationInstallerVerifiedReleaseBinding> PrepareAsync(
        string bundleDirectory,
        InstallationInstallerArchitecture architecture,
        string expectedReleaseIdentity,
        int configurationSchemaVersion,
        int protocolVersion,
        InstallationSetupState state,
        InstallationPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedReleaseIdentity);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() ||
            configurationSchemaVersion < 1 ||
            protocolVersion < 1 ||
            state.Lock.Mode != InstallationSetupLockMode.Claimed ||
            state.LastCompletedStep < InstallationSetupStep.TransmitSupport ||
            !Enum.IsDefined(state.UpdateChannel) ||
            state.Revision < 1)
        {
            throw new InvalidOperationException(
                "Initial release preparation requires one eligible claimed Linux setup state.");
        }
        InstallationPaths.Validate(paths);

        ReleaseManifestArchitecture releaseArchitecture =
            architecture switch
            {
                InstallationInstallerArchitecture.LinuxX64 =>
                    ReleaseManifestArchitecture.LinuxX64,
                InstallationInstallerArchitecture.LinuxArm64 =>
                    ReleaseManifestArchitecture.LinuxArm64,
                _ => throw new InvalidOperationException(
                    "The initial release architecture is unsupported.")
            };
        if (releaseArchitecture != ResolveCurrentArchitecture())
        {
            throw new InvalidOperationException(
                "The initial release architecture does not match this host.");
        }

        ReleaseStatusReadResult virtualStatus = new(
            Succeeded: true,
            ReleaseStatusFailureCode.None,
            "Clean-install signed release verification uses one immutable virtual pre-activation status.",
            SetupSchemaVersion: state.SchemaVersion,
            SetupRevision: state.Revision,
            SetupComplete: true,
            InstallationSetupLockMode.Complete,
            InstallationSetupStep.Administrator,
            state.UpdateChannel,
            state.PinnedRelease,
            state.InstallTransmitSupport,
            ReleaseDirectoryPresent: true,
            AvailableReleaseCount: 1,
            AvailableReleaseIdentities:
            [
                VerifiedReleaseInstallationPlanComposer
                    .InitialInstallationBootstrapIdentity
            ],
            CurrentPointerPresent: true,
            ActiveReleaseIdentity:
                VerifiedReleaseInstallationPlanComposer
                    .InitialInstallationBootstrapIdentity,
            RollbackCandidateKnown: false);

        OfflineReleaseInstallPreflightPlanner preflightPlanner = new(
            _ => Task.FromResult(virtualStatus),
            (directory, context) =>
                m_bundleVerification.VerifyDirectoryDetailed(
                    directory,
                    context),
            () => releaseArchitecture);
        OfflineReleaseInstallPreflightResult preflight =
            await preflightPlanner.CreateAsync(
                new OfflineReleaseInstallPreflightCommandLine(
                    OfflineReleaseInstallPreflightCommandKind.Preflight,
                    Path.GetFullPath(bundleDirectory),
                    VerifiedReleaseInstallationPlanComposer
                        .InitialInstallationBootstrapIdentity,
                    InstalledVersion: string.Empty,
                    configurationSchemaVersion,
                    protocolVersion,
                    ApplicationArguments: []),
                cancellationToken);
        if (!preflight.Succeeded ||
            preflight.FailureCode !=
                OfflineReleaseInstallPreflightFailureCode.None ||
            !preflight.CurrentPointerVerified ||
            !preflight.TargetAbsentFromInventory ||
            !preflight.StatusStable ||
            preflight.SetupRevision != state.Revision ||
            preflight.Architecture != releaseArchitecture ||
            !string.Equals(
                preflight.TargetReleaseIdentity,
                expectedReleaseIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The clean-install bundle did not pass exact signed release preflight.");
        }

        InstallationInstallerVerifiedReleaseBinding binding =
            InstallationInstallerVerifiedReleaseBinding.CreateInitial(
                preflight,
                paths);
        if (!string.Equals(
                binding.ReleaseIdentity,
                expectedReleaseIdentity,
                StringComparison.Ordinal) ||
            binding.SetupRevision != state.Revision ||
            binding.TargetConfigurationSchemaVersion !=
                configurationSchemaVersion)
        {
            throw new InvalidOperationException(
                "The retained initial release binding does not match the installer plan.");
        }
        return binding;
    }

    private static ReleaseManifestArchitecture ResolveCurrentArchitecture() =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
        switch
        {
            System.Runtime.InteropServices.Architecture.X64 =>
                ReleaseManifestArchitecture.LinuxX64,
            System.Runtime.InteropServices.Architecture.Arm64 =>
                ReleaseManifestArchitecture.LinuxArm64,
            _ => throw new InvalidOperationException(
                "Initial release preparation requires Linux x64 or ARM64.")
        };
}
