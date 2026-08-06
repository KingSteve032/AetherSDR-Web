using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

internal static class HostRestartContinuationStorage
{
    internal const int MarkerSchemaVersion = 2;
    internal const int ResultSchemaVersion = 1;
    internal const string DirectoryName = "release-transactions";
    internal const string MarkerFileName = "host-restart.json";
    internal const string ResultFileName = "host-restart-result.json";
    internal const int MaximumDocumentBytes = 32 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    internal static HostRestartContinuationPaths Resolve(InstallationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InstallationPaths.Validate(paths);
        string stateRoot = Path.GetFullPath(paths.StateDirectory);
        string root = Path.GetFullPath(Path.Combine(stateRoot, DirectoryName));
        string marker = Path.GetFullPath(Path.Combine(root, MarkerFileName));
        string result = Path.GetFullPath(Path.Combine(root, ResultFileName));
        if (!string.Equals(Path.GetDirectoryName(root), stateRoot, StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(marker), root, StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(result), root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The host-restart continuation paths escaped installation state.");
        }
        return new HostRestartContinuationPaths(root, marker, result);
    }
}

internal sealed record HostRestartContinuationPaths(
    string Root,
    string Marker,
    string Result);

internal sealed record HostRestartMarker(
    int SchemaVersion,
    string TransactionId,
    long SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    string ReleaseRootPath,
    string CurrentPointerPath,
    InstallationUpdateChannel UpdateChannel,
    string PinnedReleaseIdentity,
    bool InstallTransmitSupport,
    DateTimeOffset RequestedAt,
    bool PostBootVerificationRequired);

internal sealed record HostRestartContinuationResultDocument(
    int SchemaVersion,
    string TransactionId,
    long SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    bool Succeeded,
    VerifiedReleaseActivationPostBootContinuationFailureCode FailureCode,
    bool HealthVerified,
    bool ReconciliationRequired);

public enum VerifiedReleaseActivationPostBootContinuationFailureCode
{
    None = 0,
    ExecutionDisabled = 1,
    UnsupportedPlatform = 2,
    MarkerUnsafe = 3,
    MarkerInvalid = 4,
    MarkerStale = 5,
    TerminalResultUnsafe = 6,
    TerminalResultMismatch = 7,
    PriorReconciliationRequired = 8,
    StatusUnavailable = 9,
    StatusMismatch = 10,
    SetupUnavailable = 11,
    SetupMismatch = 12,
    UnsupportedTopology = 13,
    StationIdentityMismatch = 14,
    UnitActivityUnavailable = 15,
    LoopbackHealthUnavailable = 16,
    BrokerLinkUnavailable = 17,
    ObservationDrift = 18,
    TerminalResultWriteFailed = 19,
    TransactionJournalUpdateFailed = 20,
    MarkerCleanupFailed = 21
}

public sealed record VerifiedReleaseActivationPostBootContinuationReport(
    bool Succeeded,
    VerifiedReleaseActivationPostBootContinuationFailureCode FailureCode,
    string Message,
    bool MarkerPresent,
    bool ExistingTerminalResultRead,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    bool TargetActiveBeforeVerification,
    bool TargetActiveAfterVerification,
    bool SetupStable,
    int UnitActivityAttemptCount,
    int LoopbackHttpAttemptCount,
    int BrokerLinkObservationCount,
    bool HealthVerified,
    bool TerminalResultWritten,
    bool MarkerConsumed,
    bool ReconciliationRequired,
    bool ApprovalAuthorityReconstructed,
    bool RollbackAuthorityReconstructed,
    bool CurrentPointerMutationPerformed,
    bool RadioCommandIssued,
    bool TxActionPerformed);

public sealed record VerifiedReleaseActivationPostBootContinuationDiagnostics(
    bool Registered,
    bool ExecutionEnabled,
    bool OwnerOnlyMarkerReadRegistered,
    bool StrictMarkerSchemaRegistered,
    bool MarkerFreshnessRegistered,
    bool ReleaseStatusDoubleReadRegistered,
    bool SetupStateDoubleReadRegistered,
    bool ExactActiveReleaseBindingRegistered,
    bool FixedUnitActivityRegistered,
    bool LoopbackHealthRegistered,
    bool FreshBrokerLinkRegistered,
    bool DurableTerminalResultRegistered,
    bool IdempotentMarkerConsumptionRegistered,
    bool ApprovalAuthorityReconstructionRegistered,
    bool RollbackAuthorityReconstructionRegistered,
    bool CurrentPointerMutationRegistered,
    bool ServiceControlRegistered,
    bool RadioCallerRegistered,
    bool CommandCallerRegistered,
    bool TxCallerRegistered);

public sealed record VerifiedReleaseActivationPostBootContinuationStateDiagnostics(
    bool MarkerObserved,
    bool TerminalResultObserved,
    bool ContinuationCompleted,
    bool HealthVerified,
    bool MarkerConsumed,
    bool ReconciliationRequired,
    int UnitActivityAttemptCount,
    int LoopbackHttpAttemptCount,
    int BrokerLinkObservationCount,
    bool ApprovalAuthorityReconstructed,
    bool RollbackAuthorityReconstructed,
    bool CurrentPointerMutationPerformed,
    bool RadioCommandIssued,
    bool TxActionPerformed);

/// <summary>
/// Consumes one owner-only durable host-restart marker after boot. It validates
/// the exact setup revision, installed and target release identities, release
/// root, current-pointer location, update policy, and active release before and
/// after a fixed bounded service-health sequence. A terminal result is written
/// atomically before a successful marker is removed. Failed or ambiguous state
/// remains reconciliation evidence. Approval, rollback, pointer-switch, radio,
/// command, lease, watchdog, and TX authority are never reconstructed.
/// </summary>
public sealed class VerifiedReleaseActivationPostBootContinuationService
{
    internal static readonly TimeSpan MaximumMarkerAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(5);

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;
    private readonly Func<CancellationToken, Task<InstallationSetupState>>
        m_setupReader;
    private readonly Func<RemoteStationAdministrationSnapshot>
        m_remoteStationSnapshotReader;
    private readonly IVerifiedReleaseActivationHealthProbeRuntime m_runtime;
    private readonly ReleaseActivationHostRestartSettings m_restartSettings;
    private readonly ReleaseActivationHealthVerificationSettings m_healthSettings;
    private readonly InstallationPaths m_paths;
    private readonly HostRestartContinuationPaths m_storage;
    private readonly ReleaseUpdateTransactionJournal m_journal;
    private readonly TimeProvider m_timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> m_delay;
    private readonly SemaphoreSlim m_gate = new(1, 1);
    private readonly object m_stateGate = new();
    private VerifiedReleaseActivationPostBootContinuationReport? m_lastReport;

    public VerifiedReleaseActivationPostBootContinuationService(
        ReleaseInstallationStatusReader statusReader,
        InstallationSetupStore setupStore,
        RemoteStationCatalogService remoteStations,
        IOptions<ReleaseActivationHostRestartSettings> restartSettings,
        IOptions<ReleaseActivationHealthVerificationSettings> healthSettings,
        InstallationPaths paths)
        : this(
            statusReader is null
                ? throw new ArgumentNullException(nameof(statusReader))
                : statusReader.ReadAsync,
            setupStore is null
                ? throw new ArgumentNullException(nameof(setupStore))
                : setupStore.LoadAsync,
            remoteStations is null
                ? throw new ArgumentNullException(nameof(remoteStations))
                : remoteStations.GetAdministrationSnapshot,
            new LinuxVerifiedReleaseActivationHealthProbeRuntime(),
            restartSettings?.Value ??
                throw new ArgumentNullException(nameof(restartSettings)),
            healthSettings?.Value ??
                throw new ArgumentNullException(nameof(healthSettings)),
            paths,
            TimeProvider.System)
    {
    }

    internal VerifiedReleaseActivationPostBootContinuationService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Func<CancellationToken, Task<InstallationSetupState>> setupReader,
        Func<RemoteStationAdministrationSnapshot> remoteStationSnapshotReader,
        IVerifiedReleaseActivationHealthProbeRuntime runtime,
        ReleaseActivationHostRestartSettings restartSettings,
        ReleaseActivationHealthVerificationSettings healthSettings,
        InstallationPaths paths,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_setupReader = setupReader ??
            throw new ArgumentNullException(nameof(setupReader));
        m_remoteStationSnapshotReader = remoteStationSnapshotReader ??
            throw new ArgumentNullException(nameof(remoteStationSnapshotReader));
        m_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        m_restartSettings = restartSettings ??
            throw new ArgumentNullException(nameof(restartSettings));
        m_healthSettings = healthSettings ??
            throw new ArgumentNullException(nameof(healthSettings));
        m_paths = paths ?? throw new ArgumentNullException(nameof(paths));
        InstallationPaths.Validate(m_paths);
        ValidateHealthSettings(m_healthSettings);
        m_storage = HostRestartContinuationStorage.Resolve(m_paths);
        m_journal = new ReleaseUpdateTransactionJournal(m_paths);
        m_timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        m_delay = delay ?? ((duration, token) => Task.Delay(duration, token));

        Snapshot = new VerifiedReleaseActivationPostBootContinuationDiagnostics(
            Registered: true,
            ExecutionEnabled:
                m_restartSettings.ExecutionEnabled &&
                m_healthSettings.ExecutionEnabled,
            OwnerOnlyMarkerReadRegistered: true,
            StrictMarkerSchemaRegistered: true,
            MarkerFreshnessRegistered: true,
            ReleaseStatusDoubleReadRegistered: true,
            SetupStateDoubleReadRegistered: true,
            ExactActiveReleaseBindingRegistered: true,
            FixedUnitActivityRegistered: true,
            LoopbackHealthRegistered: true,
            FreshBrokerLinkRegistered: true,
            DurableTerminalResultRegistered: true,
            IdempotentMarkerConsumptionRegistered: true,
            ApprovalAuthorityReconstructionRegistered: false,
            RollbackAuthorityReconstructionRegistered: false,
            CurrentPointerMutationRegistered: false,
            ServiceControlRegistered: false,
            RadioCallerRegistered: false,
            CommandCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationPostBootContinuationDiagnostics Snapshot
    {
        get;
    }

    public VerifiedReleaseActivationPostBootContinuationStateDiagnostics State
    {
        get
        {
            lock (m_stateGate)
            {
                VerifiedReleaseActivationPostBootContinuationReport? report =
                    m_lastReport;
                return new(
                    MarkerObserved: report?.MarkerPresent ?? false,
                    TerminalResultObserved:
                        report?.ExistingTerminalResultRead ?? false,
                    ContinuationCompleted:
                        report?.Succeeded == true && report.HealthVerified,
                    HealthVerified: report?.HealthVerified ?? false,
                    MarkerConsumed: report?.MarkerConsumed ?? false,
                    ReconciliationRequired:
                        report?.ReconciliationRequired ?? false,
                    UnitActivityAttemptCount:
                        report?.UnitActivityAttemptCount ?? 0,
                    LoopbackHttpAttemptCount:
                        report?.LoopbackHttpAttemptCount ?? 0,
                    BrokerLinkObservationCount:
                        report?.BrokerLinkObservationCount ?? 0,
                    ApprovalAuthorityReconstructed: false,
                    RollbackAuthorityReconstructed: false,
                    CurrentPointerMutationPerformed: false,
                    RadioCommandIssued: false,
                    TxActionPerformed: false);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    public async Task<VerifiedReleaseActivationPostBootContinuationReport>
        ContinueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                return Remember(Failure(
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .UnsupportedPlatform,
                    "Post-boot release continuation requires Linux.",
                    markerPresent: false));
            }

            bool markerPresent;
            try
            {
                markerPresent = PathEntryExists(m_storage.Marker);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    NotSupportedException)
            {
                return Remember(Failure(
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .MarkerUnsafe,
                    "The host-restart continuation marker could not be inspected safely.",
                    markerPresent: true,
                    reconciliationRequired: true));
            }
            if (!markerPresent)
            {
                return Remember(SuccessNoMarker());
            }
            if (!m_restartSettings.ExecutionEnabled ||
                !m_healthSettings.ExecutionEnabled)
            {
                return Remember(Failure(
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .ExecutionDisabled,
                    "A host-restart continuation marker exists, but post-boot health execution is disabled.",
                    markerPresent: true,
                    reconciliationRequired: true));
            }

            HostRestartMarker marker;
            try
            {
                marker = ReadMarker();
            }
            catch (InvalidDataException exception)
            {
                return Remember(Failure(
                    exception.Message.Contains("stale", StringComparison.Ordinal)
                        ? VerifiedReleaseActivationPostBootContinuationFailureCode
                            .MarkerStale
                        : VerifiedReleaseActivationPostBootContinuationFailureCode
                            .MarkerInvalid,
                    "The host-restart continuation marker is invalid or stale.",
                    markerPresent: true,
                    reconciliationRequired: true));
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    JsonException or NotSupportedException or
                    InvalidOperationException)
            {
                return Remember(Failure(
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .MarkerUnsafe,
                    "The host-restart continuation marker is unsafe or unreadable.",
                    markerPresent: true,
                    reconciliationRequired: true));
            }

            HostRestartContinuationResultDocument? prior;
            try
            {
                prior = ReadTerminalResultIfPresent();
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    JsonException or InvalidDataException or
                    NotSupportedException or InvalidOperationException)
            {
                return Remember(Failure(
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .TerminalResultUnsafe,
                    "The prior host-restart continuation result is unsafe or unreadable.",
                    markerPresent: true,
                    marker,
                    reconciliationRequired: true));
            }
            if (prior is not null)
            {
                if (!Matches(prior, marker))
                {
                    return Remember(Failure(
                        VerifiedReleaseActivationPostBootContinuationFailureCode
                            .TerminalResultMismatch,
                        "The prior host-restart continuation result does not match the active marker.",
                        markerPresent: true,
                        marker,
                        existingTerminalResultRead: true,
                        reconciliationRequired: true));
                }
                try
                {
                    await m_journal.UpdatePostBootAsync(
                        marker,
                        prior.Succeeded,
                        cancellationToken);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException or
                        InvalidDataException or InvalidOperationException or
                        NotSupportedException or JsonException)
                {
                    return Remember(Failure(
                        VerifiedReleaseActivationPostBootContinuationFailureCode
                            .TransactionJournalUpdateFailed,
                        "The prior post-boot result could not be bound to the exact pending transaction journal.",
                        markerPresent: true,
                        marker,
                        existingTerminalResultRead: true,
                        healthVerified: prior.HealthVerified,
                        reconciliationRequired: true));
                }
                if (prior.ReconciliationRequired || !prior.Succeeded)
                {
                    return Remember(Failure(
                        VerifiedReleaseActivationPostBootContinuationFailureCode
                            .PriorReconciliationRequired,
                        "A prior post-boot continuation attempt requires local reconciliation and will not be retried automatically.",
                        markerPresent: true,
                        marker,
                        existingTerminalResultRead: true,
                        reconciliationRequired: true));
                }

                try
                {
                    DeleteMarker();
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException or
                        InvalidOperationException or NotSupportedException)
                {
                    return Remember(Failure(
                        VerifiedReleaseActivationPostBootContinuationFailureCode
                            .MarkerCleanupFailed,
                        "Post-boot health was already verified, but the consumed marker could not be removed safely.",
                        markerPresent: true,
                        marker,
                        existingTerminalResultRead: true,
                        healthVerified: true,
                        reconciliationRequired: true));
                }
                return Remember(Success(
                    marker,
                    existingTerminalResultRead: true,
                    unitAttempts: 0,
                    httpAttempts: 0,
                    brokerAttempts: 0,
                    terminalResultWritten: false,
                    markerConsumed: true));
            }

            try
            {
                m_journal.ValidatePostBootPending(marker);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or InvalidOperationException or
                    NotSupportedException or JsonException)
            {
                return Remember(Failure(
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .TransactionJournalUpdateFailed,
                    "The host-restart marker does not match one exact durable pending transaction.",
                    markerPresent: true,
                    marker,
                    reconciliationRequired: true));
            }

            ReleaseStatusReadResult beforeStatus;
            InstallationSetupState beforeSetup;
            try
            {
                beforeStatus = await m_statusReader(cancellationToken);
                beforeSetup = await m_setupReader(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    InvalidOperationException or NotSupportedException or
                    JsonException)
            {
                return await PersistFailureAsync(
                    marker,
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .StatusUnavailable,
                    "Release or setup status could not be read before post-boot verification.",
                    cancellationToken);
            }

            if (!MatchesActiveStatus(beforeStatus, marker))
            {
                return await PersistFailureAsync(
                    marker,
                    beforeStatus.Succeeded
                        ? VerifiedReleaseActivationPostBootContinuationFailureCode
                            .StatusMismatch
                        : VerifiedReleaseActivationPostBootContinuationFailureCode
                            .StatusUnavailable,
                    "The exact target release is not the stable active release after reboot.",
                    cancellationToken);
            }
            if (!TryBindSetup(
                    beforeSetup,
                    marker,
                    out string canonicalAuthority,
                    out InstallationTopologyProfile topology))
            {
                return await PersistFailureAsync(
                    marker,
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .SetupMismatch,
                    "Completed setup no longer matches the host-restart marker.",
                    cancellationToken,
                    targetActiveBefore: true);
            }
            if (!TryResolveSupportedTopology(topology, out bool remoteAgentRequired))
            {
                return await PersistFailureAsync(
                    marker,
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .UnsupportedTopology,
                    "The completed topology requires a post-boot health transport that is not registered.",
                    cancellationToken,
                    targetActiveBefore: true);
            }
            bool stationConfigured =
                !string.IsNullOrEmpty(m_healthSettings.ExpectedStationId);
            if (stationConfigured != remoteAgentRequired)
            {
                return await PersistFailureAsync(
                    marker,
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .StationIdentityMismatch,
                    remoteAgentRequired
                        ? "The topology requires one exact remote station identity for post-boot health."
                        : "The local-only topology must not configure a remote station identity for post-boot health.",
                    cancellationToken,
                    targetActiveBefore: true);
            }

            DateTimeOffset startedAt = m_timeProvider.GetUtcNow();
            ProbeCounts counts = new();
            foreach (VerifiedReleaseActivationHealthVerificationTarget target in
                     CreateFixedTargets())
            {
                DateTimeOffset deadline = startedAt.AddMilliseconds(
                    target.DeadlineMilliseconds);
                if (target.ServiceRole ==
                    VerifiedReleaseActivationServiceRole.AetherRemoteAgent)
                {
                    if (remoteAgentRequired)
                    {
                        bool linkReady = await WaitForAttemptAsync(
                            deadline,
                            _ =>
                            {
                                counts.Broker++;
                                return Task.FromResult(
                                    ObserveFreshBrokerLink(startedAt));
                            },
                            cancellationToken);
                        if (!linkReady)
                        {
                            return await PersistFailureAsync(
                                marker,
                                VerifiedReleaseActivationPostBootContinuationFailureCode
                                    .BrokerLinkUnavailable,
                                "The exact remote station did not establish a fresh broker link after reboot.",
                                cancellationToken,
                                counts,
                                targetActiveBefore: true);
                        }
                    }
                    continue;
                }

                bool unitReady = await WaitForAttemptAsync(
                    deadline,
                    timeout =>
                    {
                        counts.Unit++;
                        return m_runtime.CheckUnitActiveAsync(
                            target.UnitIdentity,
                            timeout,
                            cancellationToken);
                    },
                    cancellationToken);
                if (!unitReady)
                {
                    return await PersistFailureAsync(
                        marker,
                        VerifiedReleaseActivationPostBootContinuationFailureCode
                            .UnitActivityUnavailable,
                        "A fixed release service unit did not become active after reboot.",
                        cancellationToken,
                        counts,
                        targetActiveBefore: true);
                }

                bool healthReady = await WaitForAttemptAsync(
                    deadline,
                    timeout =>
                    {
                        counts.Http++;
                        return m_runtime.CheckLoopbackHealthAsync(
                            target,
                            canonicalAuthority,
                            timeout,
                            cancellationToken);
                    },
                    cancellationToken);
                if (!healthReady)
                {
                    return await PersistFailureAsync(
                        marker,
                        VerifiedReleaseActivationPostBootContinuationFailureCode
                            .LoopbackHealthUnavailable,
                        "A fixed loopback service-health contract did not become ready after reboot.",
                        cancellationToken,
                        counts,
                        targetActiveBefore: true);
                }
            }

            ReleaseStatusReadResult afterStatus;
            InstallationSetupState afterSetup;
            try
            {
                afterStatus = await m_statusReader(cancellationToken);
                afterSetup = await m_setupReader(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    InvalidOperationException or NotSupportedException or
                    JsonException)
            {
                return await PersistFailureAsync(
                    marker,
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .StatusUnavailable,
                    "Release or setup status could not be read after post-boot verification.",
                    cancellationToken,
                    counts,
                    targetActiveBefore: true);
            }

            bool targetActiveAfter = MatchesActiveStatus(afterStatus, marker);
            bool setupStable =
                EquivalentStatus(beforeStatus, afterStatus) &&
                EquivalentSetup(beforeSetup, afterSetup) &&
                TryBindSetup(
                    afterSetup,
                    marker,
                    out string afterAuthority,
                    out InstallationTopologyProfile afterTopology) &&
                string.Equals(
                    canonicalAuthority,
                    afterAuthority,
                    StringComparison.Ordinal) &&
                Equals(topology, afterTopology);
            if (!targetActiveAfter || !setupStable)
            {
                return await PersistFailureAsync(
                    marker,
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .ObservationDrift,
                    "Release or setup state changed during post-boot health verification.",
                    cancellationToken,
                    counts,
                    targetActiveBefore: true,
                    targetActiveAfter,
                    setupStable);
            }

            try
            {
                await WriteTerminalResultAsync(
                    marker,
                    succeeded: true,
                    VerifiedReleaseActivationPostBootContinuationFailureCode.None,
                    healthVerified: true,
                    reconciliationRequired: false,
                    cancellationToken);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    JsonException or InvalidOperationException or
                    NotSupportedException)
            {
                return Remember(Failure(
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .TerminalResultWriteFailed,
                    "Post-boot health passed, but its terminal result could not be written durably.",
                    markerPresent: true,
                    marker,
                    counts,
                    targetActiveBefore: true,
                    targetActiveAfter: true,
                    setupStable: true,
                    healthVerified: true,
                    reconciliationRequired: true));
            }

            try
            {
                await m_journal.UpdatePostBootAsync(
                    marker,
                    succeeded: true,
                    CancellationToken.None);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or InvalidOperationException or
                    NotSupportedException or JsonException)
            {
                return Remember(Failure(
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .TransactionJournalUpdateFailed,
                    "Post-boot health passed and was recorded, but the exact pending transaction journal could not be completed.",
                    markerPresent: true,
                    marker,
                    counts,
                    targetActiveBefore: true,
                    targetActiveAfter: true,
                    setupStable: true,
                    healthVerified: true,
                    terminalResultWritten: true,
                    reconciliationRequired: true));
            }

            try
            {
                DeleteMarker();
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or
                    InvalidOperationException or NotSupportedException)
            {
                return Remember(Failure(
                    VerifiedReleaseActivationPostBootContinuationFailureCode
                        .MarkerCleanupFailed,
                    "Post-boot health passed and was recorded, but the consumed marker could not be removed safely.",
                    markerPresent: true,
                    marker,
                    counts,
                    targetActiveBefore: true,
                    targetActiveAfter: true,
                    setupStable: true,
                    healthVerified: true,
                    terminalResultWritten: true,
                    reconciliationRequired: true));
            }

            return Remember(Success(
                marker,
                existingTerminalResultRead: false,
                counts.Unit,
                counts.Http,
                counts.Broker,
                terminalResultWritten: true,
                markerConsumed: true));
        }
        finally
        {
            m_gate.Release();
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task<VerifiedReleaseActivationPostBootContinuationReport>
        PersistFailureAsync(
            HostRestartMarker marker,
            VerifiedReleaseActivationPostBootContinuationFailureCode code,
            string message,
            CancellationToken cancellationToken,
            ProbeCounts? counts = null,
            bool targetActiveBefore = false,
            bool targetActiveAfter = false,
            bool setupStable = false)
    {
        counts ??= new ProbeCounts();
        try
        {
            await WriteTerminalResultAsync(
                marker,
                succeeded: false,
                code,
                healthVerified: false,
                reconciliationRequired: true,
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                JsonException or InvalidOperationException or
                NotSupportedException)
        {
            return Remember(Failure(
                VerifiedReleaseActivationPostBootContinuationFailureCode
                    .TerminalResultWriteFailed,
                "Post-boot verification failed and its reconciliation result could not be written durably.",
                markerPresent: true,
                marker,
                counts,
                targetActiveBefore,
                targetActiveAfter,
                setupStable,
                reconciliationRequired: true));
        }
        try
        {
            await m_journal.UpdatePostBootAsync(
                marker,
                succeeded: false,
                CancellationToken.None);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or InvalidOperationException or
                NotSupportedException or JsonException)
        {
            return Remember(Failure(
                VerifiedReleaseActivationPostBootContinuationFailureCode
                    .TransactionJournalUpdateFailed,
                "Post-boot verification failed and was recorded, but the exact pending transaction journal could not be moved to reconciliation.",
                markerPresent: true,
                marker,
                counts,
                targetActiveBefore,
                targetActiveAfter,
                setupStable,
                terminalResultWritten: true,
                reconciliationRequired: true));
        }
        return Remember(Failure(
            code,
            message,
            markerPresent: true,
            marker,
            counts,
            targetActiveBefore,
            targetActiveAfter,
            setupStable,
            terminalResultWritten: true,
            reconciliationRequired: true));
    }

    [SupportedOSPlatform("linux")]
    private HostRestartMarker ReadMarker()
    {
        ValidateRoot();
        byte[] bytes = ReadOwnerOnlyDocument(m_storage.Marker);
        HostRestartMarker? marker = JsonSerializer.Deserialize<HostRestartMarker>(
            bytes,
            HostRestartContinuationStorage.JsonOptions);
        if (marker is null || !ValidateMarker(marker))
        {
            throw new InvalidDataException(
                "The host-restart continuation marker is invalid.");
        }
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        if (marker.RequestedAt > now + MaximumFutureSkew ||
            now - marker.RequestedAt > MaximumMarkerAge)
        {
            throw new InvalidDataException(
                "The host-restart continuation marker is stale.");
        }
        return marker;
    }

    [SupportedOSPlatform("linux")]
    private HostRestartContinuationResultDocument? ReadTerminalResultIfPresent()
    {
        if (!PathEntryExists(m_storage.Result))
        {
            return null;
        }
        ValidateRoot();
        byte[] bytes = ReadOwnerOnlyDocument(m_storage.Result);
        HostRestartContinuationResultDocument? result =
            JsonSerializer.Deserialize<HostRestartContinuationResultDocument>(
                bytes,
                HostRestartContinuationStorage.JsonOptions);
        if (result is null ||
            result.SchemaVersion !=
                HostRestartContinuationStorage.ResultSchemaVersion ||
            !IsTransactionId(result.TransactionId) ||
            result.SetupRevision < 1 ||
            !IsCanonicalReleaseIdentity(result.InstalledReleaseIdentity) ||
            !IsCanonicalReleaseIdentity(result.TargetReleaseIdentity) ||
            result.InstalledReleaseIdentity == result.TargetReleaseIdentity ||
            result.RequestedAt < DateTimeOffset.UnixEpoch ||
            result.CompletedAt < result.RequestedAt ||
            result.Succeeded != result.HealthVerified ||
            result.ReconciliationRequired == result.Succeeded ||
            result.FailureCode == VerifiedReleaseActivationPostBootContinuationFailureCode.None !=
                result.Succeeded)
        {
            throw new InvalidDataException(
                "The host-restart continuation result is invalid.");
        }
        return result;
    }

    [SupportedOSPlatform("linux")]
    private async Task WriteTerminalResultAsync(
        HostRestartMarker marker,
        bool succeeded,
        VerifiedReleaseActivationPostBootContinuationFailureCode failureCode,
        bool healthVerified,
        bool reconciliationRequired,
        CancellationToken cancellationToken)
    {
        ValidateRoot();
        HostRestartContinuationResultDocument result = new(
            HostRestartContinuationStorage.ResultSchemaVersion,
            marker.TransactionId,
            marker.SetupRevision,
            marker.InstalledReleaseIdentity,
            marker.TargetReleaseIdentity,
            marker.RequestedAt,
            m_timeProvider.GetUtcNow(),
            succeeded,
            failureCode,
            healthVerified,
            reconciliationRequired);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            result,
            HostRestartContinuationStorage.JsonOptions);
        string temporary = Path.Combine(
            m_storage.Root,
            $".{HostRestartContinuationStorage.ResultFileName}.{Guid.NewGuid():N}");
        try
        {
            await using (FileStream stream = new(
                temporary,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough
                }))
            {
                File.SetUnixFileMode(
                    temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, m_storage.Result, overwrite: false);
            File.SetUnixFileMode(m_storage.Result, UnixFileMode.UserRead);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                }
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private void DeleteMarker()
    {
        ValidateRoot();
        _ = ReadOwnerOnlyDocument(m_storage.Marker);
        File.SetUnixFileMode(
            m_storage.Marker,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Delete(m_storage.Marker);
    }

    [SupportedOSPlatform("linux")]
    private void ValidateRoot()
    {
        DirectoryInfo root = new(m_storage.Root);
        root.Refresh();
        if (!root.Exists ||
            root.LinkTarget is not null ||
            (root.Attributes & FileAttributes.ReparsePoint) != 0 ||
            File.GetUnixFileMode(m_storage.Root) !=
                (UnixFileMode.UserRead |
                 UnixFileMode.UserWrite |
                 UnixFileMode.UserExecute))
        {
            throw new InvalidOperationException(
                "The host-restart continuation directory is unsafe.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static byte[] ReadOwnerOnlyDocument(string path)
    {
        FileInfo file = new(path);
        file.Refresh();
        if (!file.Exists ||
            file.LinkTarget is not null ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.Length is < 2 or >
                HostRestartContinuationStorage.MaximumDocumentBytes ||
            File.GetUnixFileMode(path) != UnixFileMode.UserRead)
        {
            throw new InvalidDataException(
                "The host-restart continuation document is unsafe.");
        }
        return File.ReadAllBytes(path);
    }

    private bool ValidateMarker(HostRestartMarker marker)
    {
        if (marker.SchemaVersion !=
                HostRestartContinuationStorage.MarkerSchemaVersion ||
            !IsTransactionId(marker.TransactionId) ||
            marker.SetupRevision < 1 ||
            !IsCanonicalReleaseIdentity(marker.InstalledReleaseIdentity) ||
            !IsCanonicalReleaseIdentity(marker.TargetReleaseIdentity) ||
            marker.InstalledReleaseIdentity == marker.TargetReleaseIdentity ||
            marker.RequestedAt < DateTimeOffset.UnixEpoch ||
            !marker.PostBootVerificationRequired ||
            !Enum.IsDefined(marker.UpdateChannel))
        {
            return false;
        }
        if (marker.UpdateChannel == InstallationUpdateChannel.Pinned)
        {
            if (!string.Equals(
                    marker.PinnedReleaseIdentity,
                    marker.TargetReleaseIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (!string.IsNullOrEmpty(marker.PinnedReleaseIdentity))
        {
            return false;
        }

        string releaseRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(m_paths.ReleaseDirectory));
        string expectedPointer = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(releaseRoot) ?? string.Empty,
                "current"));
        return string.Equals(
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(marker.ReleaseRootPath)),
                releaseRoot,
                StringComparison.Ordinal) &&
            string.Equals(
                Path.GetFullPath(marker.CurrentPointerPath),
                expectedPointer,
                StringComparison.Ordinal);
    }

    private static bool Matches(
        HostRestartContinuationResultDocument result,
        HostRestartMarker marker) =>
        string.Equals(
            result.TransactionId,
            marker.TransactionId,
            StringComparison.Ordinal) &&
        result.SetupRevision == marker.SetupRevision &&
        string.Equals(
            result.InstalledReleaseIdentity,
            marker.InstalledReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            result.TargetReleaseIdentity,
            marker.TargetReleaseIdentity,
            StringComparison.Ordinal) &&
        result.RequestedAt == marker.RequestedAt;

    private static bool MatchesActiveStatus(
        ReleaseStatusReadResult status,
        HostRestartMarker marker) =>
        status.Succeeded &&
        status.FailureCode == ReleaseStatusFailureCode.None &&
        status.SetupRevision == marker.SetupRevision &&
        status.SetupComplete &&
        status.SetupLockMode == InstallationSetupLockMode.Complete &&
        status.LastCompletedStep == InstallationSetupStep.Administrator &&
        status.UpdateChannel == marker.UpdateChannel &&
        string.Equals(
            status.PinnedReleaseIdentity,
            marker.PinnedReleaseIdentity,
            StringComparison.Ordinal) &&
        status.InstallTransmitSupport == marker.InstallTransmitSupport &&
        status.ReleaseDirectoryPresent &&
        status.CurrentPointerPresent &&
        string.Equals(
            status.ActiveReleaseIdentity,
            marker.TargetReleaseIdentity,
            StringComparison.Ordinal) &&
        status.AvailableReleaseCount == status.AvailableReleaseIdentities.Count &&
        status.AvailableReleaseCount is >= 2 and <=
            ReleaseInstallationStatusReader.MaximumReleaseCount &&
        status.AvailableReleaseIdentities
            .Distinct(StringComparer.Ordinal).Count() ==
                status.AvailableReleaseIdentities.Count &&
        status.AvailableReleaseIdentities.Contains(
            marker.InstalledReleaseIdentity,
            StringComparer.Ordinal) &&
        status.AvailableReleaseIdentities.Contains(
            marker.TargetReleaseIdentity,
            StringComparer.Ordinal);

    private static bool TryBindSetup(
        InstallationSetupState state,
        HostRestartMarker marker,
        out string canonicalAuthority,
        out InstallationTopologyProfile topology)
    {
        canonicalAuthority = string.Empty;
        topology = null!;
        try
        {
            InstallationSetupStateValidator.Validate(state);
            if (state.Revision != marker.SetupRevision ||
                state.Lock.Mode != InstallationSetupLockMode.Complete ||
                state.LastCompletedStep != InstallationSetupStep.Administrator ||
                state.Topology is null ||
                state.Paths is null ||
                state.UpdateChannel != marker.UpdateChannel ||
                !string.Equals(
                    state.PinnedRelease,
                    marker.PinnedReleaseIdentity,
                    StringComparison.Ordinal) ||
                state.InstallTransmitSupport != marker.InstallTransmitSupport ||
                !string.Equals(
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(state.Paths.ReleaseDirectory)),
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(marker.ReleaseRootPath)),
                    StringComparison.Ordinal))
            {
                return false;
            }
            canonicalAuthority =
                CanonicalPublicUrl.Parse(state.CanonicalPublicUrl).Uri.Authority;
            topology = InstallationTopologyProfile.For(state.Topology.Value);
            return !string.IsNullOrEmpty(canonicalAuthority);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryResolveSupportedTopology(
        InstallationTopologyProfile topology,
        out bool remoteAgentRequired)
    {
        remoteAgentRequired = false;
        if (!topology.GatewayRunsHere ||
            !topology.BrokerRunsHere ||
            !topology.StationEngineRunsHere ||
            topology.AgentRunsHere)
        {
            return false;
        }
        remoteAgentRequired = topology.AcceptsRemoteStations;
        return topology.Kind is
            InstallationTopologyKind.PersonalSingleStation or
            InstallationTopologyKind.LocalStationGateway or
            InstallationTopologyKind.HybridGateway;
    }

    private static IReadOnlyList<VerifiedReleaseActivationHealthVerificationTarget>
        CreateFixedTargets() =>
    [
        new(
            Sequence: 1,
            VerifiedReleaseActivationServiceRole.StationEngine,
            VerifiedReleaseActivationServiceControlPlanComposer
                .StationEngineUnitIdentity,
            VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .StationEngineLoopbackPort,
            VerifiedReleaseActivationHealthVerificationPlanComposer.HealthPath,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .ExpectedHttpStatusCode,
            RequireCanonicalHostHeader: false,
            RequireUnitActive: true,
            RequireFreshObservation: true,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .StationEngineDeadlineMilliseconds),
        new(
            Sequence: 2,
            VerifiedReleaseActivationServiceRole.Broker,
            VerifiedReleaseActivationServiceControlPlanComposer
                .BrokerUnitIdentity,
            VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .BrokerLoopbackPort,
            VerifiedReleaseActivationHealthVerificationPlanComposer.HealthPath,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .ExpectedHttpStatusCode,
            RequireCanonicalHostHeader: false,
            RequireUnitActive: true,
            RequireFreshObservation: true,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .BrokerDeadlineMilliseconds),
        new(
            Sequence: 3,
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
            VerifiedReleaseActivationServiceControlPlanComposer
                .AetherRemoteAgentUnitIdentity,
            VerifiedReleaseActivationHealthContractKind.FreshBrokerLink,
            LoopbackPort: null,
            HealthPath: string.Empty,
            ExpectedHttpStatusCode: null,
            RequireCanonicalHostHeader: false,
            RequireUnitActive: true,
            RequireFreshObservation: true,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .AetherRemoteAgentDeadlineMilliseconds),
        new(
            Sequence: 4,
            VerifiedReleaseActivationServiceRole.GatewayWeb,
            VerifiedReleaseActivationServiceControlPlanComposer
                .GatewayWebUnitIdentity,
            VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .GatewayWebLoopbackPort,
            VerifiedReleaseActivationHealthVerificationPlanComposer.HealthPath,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .ExpectedHttpStatusCode,
            RequireCanonicalHostHeader: true,
            RequireUnitActive: true,
            RequireFreshObservation: true,
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .GatewayWebDeadlineMilliseconds)
    ];

    private async Task<bool> WaitForAttemptAsync(
        DateTimeOffset deadline,
        Func<TimeSpan, Task<HealthProbeAttemptResult>> attempt,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            TimeSpan remaining = deadline - now;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }
            HealthProbeAttemptResult result = await attempt(remaining);
            if (result.Succeeded)
            {
                return true;
            }
            if (!result.Retryable)
            {
                return false;
            }
            now = m_timeProvider.GetUtcNow();
            remaining = deadline - now;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }
            TimeSpan delay = remaining <
                VerifiedReleaseActivationHealthVerificationService.PollInterval
                ? remaining
                : VerifiedReleaseActivationHealthVerificationService.PollInterval;
            await m_delay(delay, cancellationToken);
        }
    }

    private HealthProbeAttemptResult ObserveFreshBrokerLink(
        DateTimeOffset startedAt)
    {
        RemoteStationAdministrationSnapshot snapshot;
        try
        {
            snapshot = m_remoteStationSnapshotReader();
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or OverflowException)
        {
            return HealthProbeAttemptResult.Retry(
                "The broker station snapshot is unavailable.");
        }
        if (!snapshot.Enabled ||
            !snapshot.BrokerReachable ||
            snapshot.RefreshedAt is null ||
            snapshot.RefreshedAt < startedAt)
        {
            return HealthProbeAttemptResult.Retry(
                "The broker station snapshot is not fresh.");
        }
        RemoteStationAdministrationEntry[] matches = snapshot.Stations
            .Where(station => string.Equals(
                station.StationId,
                m_healthSettings.ExpectedStationId,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            return HealthProbeAttemptResult.Retry(
                "The expected station link is not uniquely present.");
        }
        RemoteStationAdministrationEntry station = matches[0];
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        if (!string.Equals(station.State, "online", StringComparison.Ordinal) ||
            station.LastSeen < startedAt ||
            station.LastSeen > now + MaximumFutureSkew ||
            station.ConnectedAt > station.LastSeen ||
            station.HeartbeatSequence < 1 ||
            station.InventorySequence < 1)
        {
            return HealthProbeAttemptResult.Retry(
                "The expected station link is not freshly online.");
        }
        return HealthProbeAttemptResult.Success();
    }

    private static bool EquivalentStatus(
        ReleaseStatusReadResult first,
        ReleaseStatusReadResult second) =>
        first.Succeeded == second.Succeeded &&
        first.FailureCode == second.FailureCode &&
        first.SetupSchemaVersion == second.SetupSchemaVersion &&
        first.SetupRevision == second.SetupRevision &&
        first.SetupComplete == second.SetupComplete &&
        first.SetupLockMode == second.SetupLockMode &&
        first.LastCompletedStep == second.LastCompletedStep &&
        first.UpdateChannel == second.UpdateChannel &&
        string.Equals(
            first.PinnedReleaseIdentity,
            second.PinnedReleaseIdentity,
            StringComparison.Ordinal) &&
        first.InstallTransmitSupport == second.InstallTransmitSupport &&
        first.ReleaseDirectoryPresent == second.ReleaseDirectoryPresent &&
        first.AvailableReleaseCount == second.AvailableReleaseCount &&
        first.AvailableReleaseIdentities.SequenceEqual(
            second.AvailableReleaseIdentities,
            StringComparer.Ordinal) &&
        first.CurrentPointerPresent == second.CurrentPointerPresent &&
        string.Equals(
            first.ActiveReleaseIdentity,
            second.ActiveReleaseIdentity,
            StringComparison.Ordinal) &&
        first.RollbackCandidateKnown == second.RollbackCandidateKnown;

    private static bool EquivalentSetup(
        InstallationSetupState first,
        InstallationSetupState second)
    {
        try
        {
            InstallationSetupStateValidator.Validate(first);
            InstallationSetupStateValidator.Validate(second);
            return first == second;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void ValidateHealthSettings(
        ReleaseActivationHealthVerificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string stationId = settings.ExpectedStationId ?? string.Empty;
        if (!settings.ExecutionEnabled && stationId.Length != 0)
        {
            throw new InvalidOperationException(
                "Disabled post-boot health verification must not configure a station identity.");
        }
        if (stationId.Length > 0 &&
            (stationId.Length > 64 ||
             !IsAsciiLetterOrDigit(stationId[0]) ||
             stationId.Any(character =>
                 !IsAsciiLetterOrDigit(character) &&
                 character is not '-' and not '_' and not '.')))
        {
            throw new InvalidOperationException(
                "The post-boot health station identity is not canonical.");
        }
    }

    private static bool IsTransactionId(string value) =>
        value is { Length: 32 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCanonicalReleaseIdentity(string value)
    {
        const string prefix = "aethersdr-";
        try
        {
            return value.StartsWith(prefix, StringComparison.Ordinal) &&
                string.Equals(
                    InstallationReleaseIdentity.Parse(value),
                    value,
                    StringComparison.Ordinal) &&
                ReleaseSemanticVersion.TryParse(value[prefix.Length..], out _);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private VerifiedReleaseActivationPostBootContinuationReport Remember(
        VerifiedReleaseActivationPostBootContinuationReport report)
    {
        lock (m_stateGate)
        {
            m_lastReport = report;
        }
        return report;
    }

    private static VerifiedReleaseActivationPostBootContinuationReport
        SuccessNoMarker() =>
        new(
            Succeeded: true,
            VerifiedReleaseActivationPostBootContinuationFailureCode.None,
            "No host-restart continuation marker is present.",
            MarkerPresent: false,
            ExistingTerminalResultRead: false,
            SetupRevision: null,
            InstalledReleaseIdentity: string.Empty,
            TargetReleaseIdentity: string.Empty,
            TargetActiveBeforeVerification: false,
            TargetActiveAfterVerification: false,
            SetupStable: false,
            UnitActivityAttemptCount: 0,
            LoopbackHttpAttemptCount: 0,
            BrokerLinkObservationCount: 0,
            HealthVerified: false,
            TerminalResultWritten: false,
            MarkerConsumed: false,
            ReconciliationRequired: false,
            ApprovalAuthorityReconstructed: false,
            RollbackAuthorityReconstructed: false,
            CurrentPointerMutationPerformed: false,
            RadioCommandIssued: false,
            TxActionPerformed: false);

    private static VerifiedReleaseActivationPostBootContinuationReport Success(
        HostRestartMarker marker,
        bool existingTerminalResultRead,
        int unitAttempts,
        int httpAttempts,
        int brokerAttempts,
        bool terminalResultWritten,
        bool markerConsumed) =>
        new(
            Succeeded: true,
            VerifiedReleaseActivationPostBootContinuationFailureCode.None,
            "The exact rebooted release passed bounded post-boot health verification; the terminal result is durable and the continuation marker was consumed.",
            MarkerPresent: true,
            existingTerminalResultRead,
            marker.SetupRevision,
            marker.InstalledReleaseIdentity,
            marker.TargetReleaseIdentity,
            TargetActiveBeforeVerification: true,
            TargetActiveAfterVerification: true,
            SetupStable: true,
            unitAttempts,
            httpAttempts,
            brokerAttempts,
            HealthVerified: true,
            terminalResultWritten,
            markerConsumed,
            ReconciliationRequired: false,
            ApprovalAuthorityReconstructed: false,
            RollbackAuthorityReconstructed: false,
            CurrentPointerMutationPerformed: false,
            RadioCommandIssued: false,
            TxActionPerformed: false);

    private static VerifiedReleaseActivationPostBootContinuationReport Failure(
        VerifiedReleaseActivationPostBootContinuationFailureCode code,
        string message,
        bool markerPresent,
        HostRestartMarker? marker = null,
        ProbeCounts? counts = null,
        bool existingTerminalResultRead = false,
        bool targetActiveBefore = false,
        bool targetActiveAfter = false,
        bool setupStable = false,
        bool healthVerified = false,
        bool terminalResultWritten = false,
        bool reconciliationRequired = false) =>
        new(
            Succeeded: false,
            code,
            message,
            markerPresent,
            existingTerminalResultRead,
            marker?.SetupRevision,
            marker?.InstalledReleaseIdentity ?? string.Empty,
            marker?.TargetReleaseIdentity ?? string.Empty,
            targetActiveBefore,
            targetActiveAfter,
            setupStable,
            counts?.Unit ?? 0,
            counts?.Http ?? 0,
            counts?.Broker ?? 0,
            healthVerified,
            terminalResultWritten,
            MarkerConsumed: false,
            reconciliationRequired,
            ApprovalAuthorityReconstructed: false,
            RollbackAuthorityReconstructed: false,
            CurrentPointerMutationPerformed: false,
            RadioCommandIssued: false,
            TxActionPerformed: false);

    private sealed class ProbeCounts
    {
        internal int Unit { get; set; }
        internal int Http { get; set; }
        internal int Broker { get; set; }
    }
}

/// <summary>
/// Runs post-boot continuation only after the normal gateway host has started,
/// allowing its own loopback health endpoint and hosted remote-station catalog
/// to provide fresh evidence. Failure is logged and retained durably for Admin
/// reconciliation; it does not trigger an automatic retry, service mutation,
/// radio command, or TX action.
/// </summary>
public sealed class VerifiedReleaseActivationPostBootContinuationHostedService :
    BackgroundService
{
    private readonly VerifiedReleaseActivationPostBootContinuationService
        m_continuation;
    private readonly IHostApplicationLifetime m_lifetime;
    private readonly ILogger<
        VerifiedReleaseActivationPostBootContinuationHostedService> m_logger;

    public VerifiedReleaseActivationPostBootContinuationHostedService(
        VerifiedReleaseActivationPostBootContinuationService continuation,
        IHostApplicationLifetime lifetime,
        ILogger<VerifiedReleaseActivationPostBootContinuationHostedService> logger)
    {
        m_continuation = continuation ??
            throw new ArgumentNullException(nameof(continuation));
        m_lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        m_logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration startedRegistration =
            m_lifetime.ApplicationStarted.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                started);
        using CancellationTokenRegistration stoppingRegistration =
            stoppingToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                started);
        try
        {
            await started.Task;
            if (!OperatingSystem.IsLinux())
            {
                return;
            }
            VerifiedReleaseActivationPostBootContinuationReport report =
                await m_continuation.ContinueAsync(stoppingToken);
            if (!report.MarkerPresent)
            {
                return;
            }
            if (report.Succeeded)
            {
                m_logger.LogInformation(
                    "Post-boot release continuation completed for the exact active release");
            }
            else
            {
                m_logger.LogCritical(
                    "Post-boot release continuation requires local reconciliation: {FailureCode}",
                    report.FailureCode);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
