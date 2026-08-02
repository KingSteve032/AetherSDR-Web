using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.Web.Radio;

/// <summary>
/// Lifecycle-only transaction participant that coordinates the existing
/// in-process ownership-safe supervisor with the independent watchdog process.
/// It accepts no browser-supplied authority. The exact watchdog identity is
/// resolved from lifecycle-owned state after the existing station command
/// authority has been revalidated.
/// </summary>
internal sealed class StationTxIndependentSafetyArmParticipant :
    IStationTxSafetyArmTransactionParticipant
{
    private readonly IStationTxSafetyArmTransactionParticipant m_inner;
    private readonly IStationTxIndependentWatchdog m_watchdog;
    private readonly Func<string?, StationTxCommandAuthorityResolution>
        m_authorityResolver;
    private readonly Func<StationTxCommandAuthority, WatchdogIdentity?>
        m_identityResolver;

    public StationTxIndependentSafetyArmParticipant(
        IStationTxSafetyArmTransactionParticipant inner,
        IStationTxIndependentWatchdog watchdog,
        Func<string?, StationTxCommandAuthorityResolution> authorityResolver,
        Func<StationTxCommandAuthority, WatchdogIdentity?> identityResolver)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(watchdog);
        ArgumentNullException.ThrowIfNull(authorityResolver);
        ArgumentNullException.ThrowIfNull(identityResolver);
        m_inner = inner;
        m_watchdog = watchdog;
        m_authorityResolver = authorityResolver;
        m_identityResolver = identityResolver;
    }

    public StationTxSafetyArmCompositionDiagnostics Snapshot
    {
        get
        {
            StationTxSafetyArmCompositionDiagnostics inner = m_inner.Snapshot;
            StationTxIndependentWatchdogDiagnostics watchdog =
                m_watchdog.Snapshot;
            bool watchdogReady =
                watchdog.SupervisionEnabled &&
                watchdog.ProcessRunning &&
                watchdog.IpcConnected &&
                watchdog.Registered &&
                watchdog.Connected &&
                watchdog.LeaseBound;
            bool armAvailable =
                inner.ArmAvailable &&
                watchdogReady &&
                watchdog.ArmingAvailable &&
                !watchdog.Armed &&
                string.Equals(
                    watchdog.State,
                    "Disarmed",
                    StringComparison.Ordinal);
            bool heartbeatAvailable =
                inner.HeartbeatAvailable &&
                watchdogReady &&
                watchdog.ArmingAvailable &&
                watchdog.Armed &&
                string.Equals(
                    watchdog.State,
                    "Armed",
                    StringComparison.Ordinal);
            string reason = armAvailable || heartbeatAvailable ||
                inner.AbortAvailable
                ? inner.Reason
                : WatchdogReason(watchdog, inner.Reason);
            return inner with
            {
                ArmAvailable = armAvailable,
                HeartbeatAvailable = heartbeatAvailable,
                Reason = reason
            };
        }
    }

    public async Task<StationTxSafetyArmCompositionResult> ArmAsync(
        StationTxSafetyArmCompositionArmRequest request,
        CancellationToken cancellationToken = default)
    {
        ResolvedWatchdogIdentity resolved = Resolve(
            request?.ConnectionClientId);
        if (!resolved.Success)
        {
            return Rejected(resolved.Code, resolved.Message);
        }

        StationTxIndependentWatchdogDiagnostics watchdog =
            await m_watchdog.ArmAsync(
                resolved.Identity!,
                request!.HeartbeatTimeout,
                cancellationToken);
        if (!WatchdogArmed(watchdog))
        {
            return Rejected(
                "independent_watchdog_arm_failed",
                "The independent watchdog did not accept the exact safety arm.");
        }

        StationTxSafetyArmCompositionResult inner =
            await m_inner.ArmAsync(request, cancellationToken);
        if (inner.Success)
        {
            return inner;
        }

        StationTxIndependentWatchdogDiagnostics cleanup =
            await m_watchdog.DisarmAsync(
                resolved.Identity!,
                CancellationToken.None);
        return cleanup.Armed
            ? Rejected(
                "independent_watchdog_arm_reconciliation_required",
                "The local safety arm failed and the independent watchdog arm could not be cleared.",
                inner.SafetyResult)
            : inner;
    }

    public async Task<StationTxSafetyArmCompositionResult> HeartbeatAsync(
        StationTxSafetyArmCompositionHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ResolvedWatchdogIdentity resolved = Resolve(
            request?.ConnectionClientId);
        if (!resolved.Success)
        {
            return Rejected(resolved.Code, resolved.Message);
        }

        StationTxIndependentWatchdogDiagnostics watchdog =
            await m_watchdog.SafetyHeartbeatAsync(
                resolved.Identity!,
                request!.HeartbeatTimeout,
                cancellationToken);
        if (!WatchdogArmed(watchdog))
        {
            return Rejected(
                "independent_watchdog_heartbeat_failed",
                "The independent watchdog did not renew the exact safety heartbeat.");
        }
        return await m_inner.HeartbeatAsync(request, cancellationToken);
    }

    public async Task<StationTxSafetyArmCompositionResult> AbortAsync(
        StationTxSafetyArmCompositionAbortRequest request,
        CancellationToken cancellationToken = default)
    {
        ResolvedWatchdogIdentity resolved = Resolve(
            request?.ConnectionClientId);
        if (!resolved.Success)
        {
            return Rejected(resolved.Code, resolved.Message);
        }

        StationTxSafetyArmCompositionResult inner =
            await m_inner.AbortAsync(request!, cancellationToken);
        if (!inner.Success ||
            inner.SafetyResult?.Snapshot.State != StationTxSafetyState.Disarmed)
        {
            return inner;
        }

        StationTxIndependentWatchdogDiagnostics watchdog =
            await m_watchdog.DisarmAsync(
                resolved.Identity!,
                CancellationToken.None);
        return watchdog.Armed
            ? Rejected(
                "independent_watchdog_disarm_failed",
                "The independent watchdog remained armed after local radio-confirmed cleanup.",
                inner.SafetyResult)
            : inner;
    }

    private ResolvedWatchdogIdentity Resolve(string? connectionClientId)
    {
        StationTxCommandAuthorityResolution authority;
        try
        {
            authority = m_authorityResolver(connectionClientId);
        }
        catch
        {
            return ResolvedWatchdogIdentity.Rejected(
                "watchdog_authority_resolution_failed",
                "The lifecycle-owned watchdog authority could not be resolved.");
        }
        if (!authority.Success || authority.Authority is null)
        {
            return ResolvedWatchdogIdentity.Rejected(
                NormalizeCode(authority.Code),
                string.IsNullOrWhiteSpace(authority.Message)
                    ? "The exact lifecycle-owned watchdog authority is unavailable."
                    : authority.Message);
        }

        WatchdogIdentity? identity;
        try
        {
            identity = m_identityResolver(authority.Authority);
        }
        catch
        {
            identity = null;
        }
        return identity is null
            ? ResolvedWatchdogIdentity.Rejected(
                "independent_watchdog_identity_unavailable",
                "The exact registered independent watchdog identity is unavailable.")
            : ResolvedWatchdogIdentity.Accepted(identity);
    }

    private StationTxSafetyArmCompositionResult Rejected(
        string code,
        string message,
        StationTxSafetyResult? safetyResult = null) =>
        new(
            Success: false,
            NormalizeCode(code),
            message,
            Snapshot,
            safetyResult);

    private static bool WatchdogArmed(
        StationTxIndependentWatchdogDiagnostics watchdog) =>
        watchdog.SupervisionEnabled &&
        watchdog.ProcessRunning &&
        watchdog.IpcConnected &&
        watchdog.Registered &&
        watchdog.Connected &&
        watchdog.LeaseBound &&
        watchdog.RadioCommandTransportAvailable &&
        watchdog.ArmingAvailable &&
        watchdog.Armed &&
        string.Equals(watchdog.State, "Armed", StringComparison.Ordinal) &&
        watchdog.HeartbeatDeadlineAt is not null;

    private static string WatchdogReason(
        StationTxIndependentWatchdogDiagnostics watchdog,
        string innerReason)
    {
        if (!watchdog.SupervisionEnabled)
        {
            return "independent-watchdog-supervision-disabled";
        }
        if (!watchdog.ProcessRunning || !watchdog.IpcConnected)
        {
            return "independent-watchdog-process-unavailable";
        }
        if (!watchdog.RadioCommandTransportAvailable)
        {
            return "independent-watchdog-unkey-transport-unavailable";
        }
        if (!watchdog.ArmingAvailable)
        {
            return "independent-watchdog-arming-unavailable";
        }
        if (string.Equals(
                watchdog.State,
                "ReconciliationRequired",
                StringComparison.Ordinal))
        {
            return "independent-watchdog-reconciliation-required";
        }
        return innerReason;
    }

    private static string NormalizeCode(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length is > 0 and <= 64 &&
            normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '-')
            ? normalized
            : "independent_watchdog_rejected";
    }

    private sealed record ResolvedWatchdogIdentity(
        bool Success,
        string Code,
        string Message,
        WatchdogIdentity? Identity)
    {
        public static ResolvedWatchdogIdentity Accepted(
            WatchdogIdentity identity) =>
            new(true, "accepted", string.Empty, identity);

        public static ResolvedWatchdogIdentity Rejected(
            string code,
            string message) =>
            new(false, code, message, null);
    }
}
