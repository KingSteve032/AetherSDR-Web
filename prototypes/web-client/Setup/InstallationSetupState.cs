namespace AetherSDR.Web.Setup;

public enum InstallationSetupLockMode
{
    BootstrapRequired = 1,
    Claimed = 2,
    Complete = 3
}

public enum InstallationSetupStep
{
    None = 0,
    BootstrapClaim = 1,
    Topology = 2,
    PublicUrl = 3,
    Paths = 4,
    UpdateChannel = 5,
    Backup = 6,
    TransmitSupport = 7,
    Preflight = 8,
    Administrator = 9
}

public enum InstallationUpdateChannel
{
    Stable = 1,
    Beta = 2,
    Pinned = 3
}

public sealed record InstallationSetupLock
{
    public InstallationSetupLockMode Mode { get; init; } =
        InstallationSetupLockMode.BootstrapRequired;
    public string BootstrapTokenHash { get; init; } = string.Empty;
    public DateTimeOffset? BootstrapTokenIssuedAt { get; init; }
    public DateTimeOffset? BootstrapTokenExpiresAt { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record InstallationSetupState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long Revision { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public InstallationSetupStep LastCompletedStep { get; init; }
    public InstallationSetupLock Lock { get; init; } = new();
    public InstallationTopologyKind? Topology { get; init; }
    public string CanonicalPublicUrl { get; init; } = string.Empty;
    public InstallationPaths? Paths { get; init; }
    public InstallationUpdateChannel UpdateChannel { get; init; } =
        InstallationUpdateChannel.Stable;
    public string PinnedRelease { get; init; } = string.Empty;
    public bool InstallTransmitSupport { get; init; }

    public static InstallationSetupState CreateInitial(DateTimeOffset now) =>
        new()
        {
            CreatedAt = now,
            UpdatedAt = now
        };
}

public static class InstallationSetupStateValidator
{
    public static void Validate(InstallationSetupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != InstallationSetupState.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported installation setup schema version " +
                $"'{state.SchemaVersion}'.");
        }
        if (state.Revision < 0 ||
            state.CreatedAt == default ||
            state.UpdatedAt == default ||
            state.UpdatedAt < state.CreatedAt)
        {
            throw new InvalidOperationException(
                "Installation setup state contains invalid revision or timestamps.");
        }
        if (!Enum.IsDefined(state.LastCompletedStep) ||
            !Enum.IsDefined(state.Lock.Mode) ||
            !Enum.IsDefined(state.UpdateChannel))
        {
            throw new InvalidOperationException(
                "Installation setup state contains an unsupported enum value.");
        }

        ValidateLock(state);
        ValidateProgress(state);
    }

    private static void ValidateLock(InstallationSetupState state)
    {
        InstallationSetupLock setupLock = state.Lock ??
            throw new InvalidOperationException(
                "Installation setup state requires a setup lock.");
        switch (setupLock.Mode)
        {
            case InstallationSetupLockMode.BootstrapRequired:
            {
                if (setupLock.ClaimedAt is not null ||
                    setupLock.CompletedAt is not null)
                {
                    throw new InvalidOperationException(
                        "A bootstrap-required setup lock cannot be claimed or complete.");
                }

                bool hasHash = !string.IsNullOrWhiteSpace(
                    setupLock.BootstrapTokenHash);
                bool hasIssuedAt = setupLock.BootstrapTokenIssuedAt is not null;
                bool hasExpiresAt = setupLock.BootstrapTokenExpiresAt is not null;
                if (hasHash != hasIssuedAt || hasHash != hasExpiresAt)
                {
                    throw new InvalidOperationException(
                        "Bootstrap token hash and lifetime must be persisted together.");
                }
                if (hasHash &&
                    (!IsSha256Hex(setupLock.BootstrapTokenHash) ||
                     setupLock.BootstrapTokenExpiresAt <=
                        setupLock.BootstrapTokenIssuedAt))
                {
                    throw new InvalidOperationException(
                        "Bootstrap token state is malformed.");
                }
                break;
            }
            case InstallationSetupLockMode.Claimed:
                RequireClearedToken(setupLock);
                if (setupLock.ClaimedAt is null ||
                    setupLock.CompletedAt is not null)
                {
                    throw new InvalidOperationException(
                        "A claimed setup lock requires a claim timestamp and cannot be complete.");
                }
                break;
            case InstallationSetupLockMode.Complete:
                RequireClearedToken(setupLock);
                if (setupLock.ClaimedAt is null ||
                    setupLock.CompletedAt is null ||
                    setupLock.CompletedAt < setupLock.ClaimedAt)
                {
                    throw new InvalidOperationException(
                        "A complete setup lock requires ordered claim and completion timestamps.");
                }
                break;
            default:
                throw new InvalidOperationException(
                    "Installation setup lock mode is unsupported.");
        }
    }

    private static void ValidateProgress(InstallationSetupState state)
    {
        if (state.Lock.Mode == InstallationSetupLockMode.Claimed &&
            state.LastCompletedStep < InstallationSetupStep.BootstrapClaim)
        {
            throw new InvalidOperationException(
                "A claimed setup lock requires a completed bootstrap claim step.");
        }
        if (state.Lock.Mode == InstallationSetupLockMode.Complete &&
            state.LastCompletedStep != InstallationSetupStep.Administrator)
        {
            throw new InvalidOperationException(
                "A complete setup lock requires the administrator step to be complete.");
        }
        if (state.LastCompletedStep == InstallationSetupStep.Administrator &&
            state.Lock.Mode != InstallationSetupLockMode.Complete)
        {
            throw new InvalidOperationException(
                "The administrator step cannot be complete while setup remains locked.");
        }

        if (state.LastCompletedStep >= InstallationSetupStep.Topology)
        {
            if (state.Topology is null || !Enum.IsDefined(state.Topology.Value))
            {
                throw new InvalidOperationException(
                    "Completed topology setup requires a supported topology.");
            }
            _ = InstallationTopologyProfile.For(state.Topology.Value);
        }
        if (state.LastCompletedStep >= InstallationSetupStep.PublicUrl)
        {
            CanonicalPublicUrl publicUrl =
                CanonicalPublicUrl.Parse(state.CanonicalPublicUrl);
            if (!string.Equals(
                    publicUrl.Value,
                    state.CanonicalPublicUrl,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The persisted public URL is not in canonical form.");
            }
        }
        if (state.LastCompletedStep >= InstallationSetupStep.Paths)
        {
            InstallationPaths.Validate(
                state.Paths ??
                throw new InvalidOperationException(
                    "Completed path setup requires resolved installation paths."));
        }
        if (state.LastCompletedStep >= InstallationSetupStep.UpdateChannel)
        {
            if (state.UpdateChannel == InstallationUpdateChannel.Pinned)
            {
                string releaseIdentity =
                    InstallationReleaseIdentity.Parse(state.PinnedRelease);
                if (!string.Equals(
                        releaseIdentity,
                        state.PinnedRelease,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The pinned release identity is not in canonical form.");
                }
            }
            else if (!string.IsNullOrEmpty(state.PinnedRelease))
            {
                throw new InvalidOperationException(
                    "Only the pinned update channel may retain a pinned release identity.");
            }
        }
    }

    private static void RequireClearedToken(InstallationSetupLock setupLock)
    {
        if (!string.IsNullOrEmpty(setupLock.BootstrapTokenHash) ||
            setupLock.BootstrapTokenIssuedAt is not null ||
            setupLock.BootstrapTokenExpiresAt is not null)
        {
            throw new InvalidOperationException(
                "A claimed or complete setup lock cannot retain bootstrap token material.");
        }
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
