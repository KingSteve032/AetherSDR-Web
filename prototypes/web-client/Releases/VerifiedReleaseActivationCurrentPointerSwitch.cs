using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public sealed class ReleaseActivationCurrentPointerSwitchSettings
{
    public const string SectionName = "ReleaseActivationCurrentPointerSwitch";

    public bool ExecutionEnabled { get; init; }
}

public enum VerifiedReleaseActivationCurrentPointerSwitchFailureCode
{
    None = 0,
    ExecutionDisabled = 1,
    UnsupportedPlatform = 2,
    ServiceControlPlanUnavailable = 3,
    ServiceControlPlanNotEligible = 4,
    ServiceControlPlanMismatch = 5,
    PreSwitchServiceControlUnavailable = 6,
    PreSwitchServiceControlReconciliationRequired = 7,
    StatusUnavailable = 8,
    StatusMismatch = 9,
    SetupUnavailable = 10,
    SetupMismatch = 11,
    TargetReleaseUnavailable = 12,
    TargetReleaseUnsafe = 13,
    CurrentPointerUnavailable = 14,
    CurrentPointerMismatch = 15,
    TemporaryPointerConflict = 16,
    TemporaryPointerCreateFailed = 17,
    AtomicSwitchFailed = 18,
    ObservationDrift = 19,
    PointerAlreadySwitched = 20,
    ReconciliationRequired = 21
}

public sealed record VerifiedReleaseActivationCurrentPointerSwitchReport(
    bool Succeeded,
    VerifiedReleaseActivationCurrentPointerSwitchFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    bool ExecutionEnabled,
    bool ExecutionAvailable,
    bool ExactServiceControlPlanBound,
    bool ExactActivationPlanBound,
    bool PreSwitchServiceControlReady,
    bool InstalledReleaseActiveBeforeSwitch,
    bool TargetReleaseActiveAfterSwitch,
    bool SetupStable,
    bool TargetReleaseImmutable,
    bool ExactInstalledPointerBound,
    bool ExactTargetPointerBound,
    bool TemporaryPointerCreated,
    bool TemporaryPointerCleaned,
    bool AtomicSwitchAttempted,
    bool AtomicSwitchCompleted,
    bool CurrentPointerChanged,
    bool ReconciliationRequired,
    bool PostSwitchServiceControlReady,
    bool HealthVerificationReady,
    bool RollbackPerformed,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationCurrentPointerSwitchEvidence? Evidence
    {
        get;
        init;
    }

    internal static VerifiedReleaseActivationCurrentPointerSwitchReport Failure(
        VerifiedReleaseActivationCurrentPointerSwitchFailureCode failureCode,
        string message,
        ReleaseActivationCurrentPointerSwitchSettings settings,
        VerifiedReleaseActivationServiceControlPlanReport? planReport = null,
        bool exactPlanBound = false,
        bool preSwitchReady = false,
        bool installedActive = false,
        bool targetImmutable = false,
        bool installedPointerBound = false,
        bool targetPointerBound = false,
        bool temporaryCreated = false,
        bool temporaryCleaned = false,
        bool atomicAttempted = false,
        bool atomicCompleted = false,
        bool currentChanged = false,
        bool reconciliationRequired = false) =>
        new(
            false,
            failureCode,
            message,
            planReport?.SetupRevision,
            planReport?.InstalledReleaseIdentity ?? string.Empty,
            planReport?.TargetReleaseIdentity ?? string.Empty,
            settings.ExecutionEnabled,
            settings.ExecutionEnabled && OperatingSystem.IsLinux(),
            ExactServiceControlPlanBound: exactPlanBound,
            ExactActivationPlanBound: exactPlanBound,
            PreSwitchServiceControlReady: preSwitchReady,
            InstalledReleaseActiveBeforeSwitch: installedActive,
            TargetReleaseActiveAfterSwitch: false,
            SetupStable: false,
            TargetReleaseImmutable: targetImmutable,
            ExactInstalledPointerBound: installedPointerBound,
            ExactTargetPointerBound: targetPointerBound,
            TemporaryPointerCreated: temporaryCreated,
            TemporaryPointerCleaned: temporaryCleaned,
            AtomicSwitchAttempted: atomicAttempted,
            AtomicSwitchCompleted: atomicCompleted,
            CurrentPointerChanged: currentChanged,
            ReconciliationRequired: reconciliationRequired,
            PostSwitchServiceControlReady: false,
            HealthVerificationReady: false,
            RollbackPerformed: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationCurrentPointerSwitchReport Success(
        ReleaseActivationCurrentPointerSwitchSettings settings,
        VerifiedReleaseActivationServiceControlPlanReport planReport,
        VerifiedReleaseActivationCurrentPointerSwitchEvidence evidence) =>
        new(
            true,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode.None,
            "The exact current pointer was atomically switched to the verified immutable target and revalidated without starting services, probing health, rolling back, authorizing activation, operating a radio, altering a lease, sending a command, or transmitting.",
            evidence.ActivationPlan.SetupRevision,
            evidence.ActivationPlan.InstalledReleaseIdentity,
            evidence.ActivationPlan.TargetReleaseIdentity,
            settings.ExecutionEnabled,
            ExecutionAvailable: true,
            ExactServiceControlPlanBound: true,
            ExactActivationPlanBound: true,
            PreSwitchServiceControlReady: true,
            InstalledReleaseActiveBeforeSwitch: true,
            TargetReleaseActiveAfterSwitch: true,
            SetupStable: true,
            TargetReleaseImmutable: true,
            ExactInstalledPointerBound: true,
            ExactTargetPointerBound: true,
            TemporaryPointerCreated: true,
            TemporaryPointerCleaned: true,
            AtomicSwitchAttempted: true,
            AtomicSwitchCompleted: true,
            CurrentPointerChanged: true,
            ReconciliationRequired: false,
            PostSwitchServiceControlReady: false,
            HealthVerificationReady: false,
            RollbackPerformed: false,
            ActivationAuthorized: false)
        {
            Evidence = evidence
        };
}

public sealed record VerifiedReleaseActivationCurrentPointerSwitchDiagnostics(
    bool Registered,
    bool ConfigurationRegistered,
    bool ExecutionEnabled,
    bool ExecutionAvailable,
    bool ExactServiceControlPlanInputRegistered,
    bool ExactServiceControlPlanBindingRegistered,
    bool ExactActivationPlanBindingRegistered,
    bool ExactPreSwitchEvidenceRegistered,
    bool ReleaseStatusDoubleReadRegistered,
    bool SetupStateDoubleReadRegistered,
    bool InstalledActiveRequirementRegistered,
    bool TargetActiveVerificationRegistered,
    bool ImmutableTargetRevalidationRegistered,
    bool ExactInstalledLinkTargetRegistered,
    bool ExactTargetLinkTargetRegistered,
    bool SameDirectoryTemporaryLinkRegistered,
    bool AtomicLinkReplacementRegistered,
    bool PostSwitchObservationRegistered,
    bool ExactPlanEvidenceRegistered,
    bool PartialFailureReconciliationRegistered,
    bool AutomaticRetryRegistered,
    bool ServiceStartRegistered,
    bool HostRestartRegistered,
    bool RemoteServiceControlRegistered,
    bool HealthProbeRegistered,
    bool RollbackRegistered,
    bool ActivationAuthorityRegistered,
    bool OperationalCallerRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool HttpCallerRegistered,
    bool WebSocketCallerRegistered,
    bool HostedServiceCallerRegistered,
    bool TimerCallerRegistered,
    bool AetherRemoteCommandCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record VerifiedReleaseActivationCurrentPointerSwitchStateDiagnostics(
    bool PointerSwitchReady,
    bool ExactServiceControlPlanBound,
    bool ExactActivationPlanBound,
    bool PreSwitchServiceControlReady,
    bool CurrentPointerChanged,
    bool TargetReleaseActive,
    bool SetupStable,
    bool TargetReleaseImmutable,
    bool AtomicSwitchCompleted,
    bool ReconciliationRequired,
    bool PostSwitchServiceControlReady,
    bool HealthVerificationReady,
    bool RollbackPerformed,
    bool ActivationAuthorized);

internal sealed record VerifiedReleaseActivationCurrentPointerSwitchObservation(
    bool PointerSwitchReady,
    bool ExactServiceControlPlanBound,
    bool ExactActivationPlanBound,
    bool PreSwitchServiceControlReady,
    DateTimeOffset? CompletedAt,
    bool ReconciliationRequired);

internal sealed class VerifiedReleaseActivationCurrentPointerSwitchEvidence
{
    internal VerifiedReleaseActivationCurrentPointerSwitchEvidence(
        VerifiedReleaseActivationServiceControlPlan serviceControlPlan,
        VerifiedReleaseActivationServiceControlPreSwitchEvidence? preSwitchEvidence,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        ServiceControlPlan = serviceControlPlan ??
            throw new ArgumentNullException(nameof(serviceControlPlan));
        if (startedAt == default || completedAt < startedAt)
        {
            throw new InvalidOperationException(
                "Current-pointer switch evidence timestamps are invalid.");
        }
        if (serviceControlPlan.ServiceControlRequired &&
            !serviceControlPlan.HostRestartRequired &&
            (preSwitchEvidence is null ||
             !ReferenceEquals(preSwitchEvidence.Plan, serviceControlPlan)))
        {
            throw new InvalidOperationException(
                "Current-pointer switch evidence requires the exact pre-switch service-control token.");
        }
        if (serviceControlPlan.HostRestartRequired && preSwitchEvidence is not null)
        {
            throw new InvalidOperationException(
                "Host-restart pointer evidence cannot retain a service-stop token.");
        }
        if (!serviceControlPlan.ServiceControlRequired && preSwitchEvidence is not null)
        {
            throw new InvalidOperationException(
                "No-op service-control plans cannot retain a pre-switch execution token.");
        }
        PreSwitchEvidence = preSwitchEvidence;
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    internal VerifiedReleaseActivationServiceControlPlan ServiceControlPlan { get; }
    internal VerifiedReleaseActivationPlan ActivationPlan =>
        ServiceControlPlan.ActivationPlan;
    internal VerifiedReleaseActivationServiceControlPreSwitchEvidence?
        PreSwitchEvidence
    {
        get;
    }
    internal DateTimeOffset StartedAt { get; }
    internal DateTimeOffset CompletedAt { get; }
}

internal sealed record CurrentPointerRuntimeSnapshot(
    bool EntryPresent,
    bool IsSymbolicLink,
    string LinkTarget);

internal interface IVerifiedReleaseActivationCurrentPointerRuntime
{
    CurrentPointerRuntimeSnapshot Read(string path);
    void CreateSymbolicLink(string path, string linkTarget);
    void ReplaceAtomically(string temporaryPath, string currentPath);
    void DeleteTemporary(string path);
}

internal sealed class LinuxVerifiedReleaseActivationCurrentPointerRuntime :
    IVerifiedReleaseActivationCurrentPointerRuntime
{
    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(string oldPath, string newPath);
    public CurrentPointerRuntimeSnapshot Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        DirectoryInfo info = new(path);
        info.Refresh();
        string? linkTarget = info.LinkTarget;
        bool entryPresent = linkTarget is not null ||
            Directory.Exists(path) || File.Exists(path);
        return new CurrentPointerRuntimeSnapshot(
            entryPresent,
            linkTarget is not null,
            linkTarget ?? string.Empty);
    }

    public void CreateSymbolicLink(string path, string linkTarget)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(linkTarget);
        Directory.CreateSymbolicLink(path, linkTarget);
    }

    public void ReplaceAtomically(string temporaryPath, string currentPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(temporaryPath);
        ArgumentException.ThrowIfNullOrEmpty(currentPath);
        if (Rename(temporaryPath, currentPath) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Atomic current-pointer rename failed: " +
                new Win32Exception(error).Message,
                error);
        }
    }

    public void DeleteTemporary(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        DirectoryInfo info = new(path);
        info.Refresh();
        if (info.LinkTarget is not null)
        {
            File.Delete(path);
            return;
        }
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }
        if (Directory.Exists(path))
        {
            Directory.Delete(path);
        }
    }
}

/// <summary>
/// Disabled-by-default, callerless Linux boundary that atomically replaces only
/// the exact planned deployment-root current symlink after the exact pre-switch
/// service-control token exists. It creates one same-directory temporary symlink,
/// verifies both exact relative targets, performs one atomic replacement, and
/// double-reads setup and release status. It does not start a service, restart a
/// host, probe health, roll back, authorize activation, operate a radio, alter a
/// lease or watchdog, send a radio command, or transmit.
/// </summary>
public sealed class VerifiedReleaseActivationCurrentPointerSwitchService
{
    private const UnixFileMode AnyWritableUnixModes =
        UnixFileMode.UserWrite |
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;
    private const int MaximumDirectoryCount =
        VerifiedReleaseStagingService.MaximumDirectoryCount;
    private const int LegacyExpectedFileCount =
        LocalOfflineReleaseBundleVerificationService.RequiredPackageCount + 1;

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;
    private readonly Func<CancellationToken, Task<InstallationSetupState>>
        m_setupReader;
    private readonly VerifiedReleaseActivationServiceControlExecutionService
        m_serviceControl;
    private readonly IVerifiedReleaseActivationCurrentPointerRuntime m_runtime;
    private readonly ReleaseActivationCurrentPointerSwitchSettings m_settings;
    private readonly TimeProvider m_timeProvider;
    private readonly Func<string> m_nonceFactory;
    private readonly SemaphoreSlim m_executionGate = new(1, 1);
    private readonly object m_stateGate = new();
    private VerifiedReleaseActivationCurrentPointerSwitchEvidence? m_completed;
    private VerifiedReleaseActivationServiceControlPlan? m_reconciliationPlan;

    public VerifiedReleaseActivationCurrentPointerSwitchService(
        ReleaseInstallationStatusReader statusReader,
        InstallationSetupStore setupStore,
        VerifiedReleaseActivationServiceControlExecutionService serviceControl,
        IOptions<ReleaseActivationCurrentPointerSwitchSettings> settings)
        : this(
            statusReader is null
                ? throw new ArgumentNullException(nameof(statusReader))
                : statusReader.ReadAsync,
            setupStore is null
                ? throw new ArgumentNullException(nameof(setupStore))
                : setupStore.LoadAsync,
            serviceControl,
            new LinuxVerifiedReleaseActivationCurrentPointerRuntime(),
            settings?.Value ?? throw new ArgumentNullException(nameof(settings)),
            TimeProvider.System,
            () => Guid.NewGuid().ToString("N"))
    {
    }

    internal VerifiedReleaseActivationCurrentPointerSwitchService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Func<CancellationToken, Task<InstallationSetupState>> setupReader,
        VerifiedReleaseActivationServiceControlExecutionService serviceControl,
        IVerifiedReleaseActivationCurrentPointerRuntime runtime,
        ReleaseActivationCurrentPointerSwitchSettings settings,
        TimeProvider timeProvider,
        Func<string>? nonceFactory = null)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_setupReader = setupReader ??
            throw new ArgumentNullException(nameof(setupReader));
        m_serviceControl = serviceControl ??
            throw new ArgumentNullException(nameof(serviceControl));
        m_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        m_settings = settings ?? throw new ArgumentNullException(nameof(settings));
        m_timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        m_nonceFactory = nonceFactory ?? (() => Guid.NewGuid().ToString("N"));

        Snapshot = new VerifiedReleaseActivationCurrentPointerSwitchDiagnostics(
            Registered: true,
            ConfigurationRegistered: true,
            m_settings.ExecutionEnabled,
            ExecutionAvailable:
                m_settings.ExecutionEnabled && OperatingSystem.IsLinux(),
            ExactServiceControlPlanInputRegistered: true,
            ExactServiceControlPlanBindingRegistered: true,
            ExactActivationPlanBindingRegistered: true,
            ExactPreSwitchEvidenceRegistered: true,
            ReleaseStatusDoubleReadRegistered: true,
            SetupStateDoubleReadRegistered: true,
            InstalledActiveRequirementRegistered: true,
            TargetActiveVerificationRegistered: true,
            ImmutableTargetRevalidationRegistered: true,
            ExactInstalledLinkTargetRegistered: true,
            ExactTargetLinkTargetRegistered: true,
            SameDirectoryTemporaryLinkRegistered: true,
            AtomicLinkReplacementRegistered: true,
            PostSwitchObservationRegistered: true,
            ExactPlanEvidenceRegistered: true,
            PartialFailureReconciliationRegistered: true,
            AutomaticRetryRegistered: false,
            ServiceStartRegistered: false,
            HostRestartRegistered: false,
            RemoteServiceControlRegistered: false,
            HealthProbeRegistered: false,
            RollbackRegistered: false,
            ActivationAuthorityRegistered: false,
            OperationalCallerRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            HttpCallerRegistered: false,
            WebSocketCallerRegistered: false,
            HostedServiceCallerRegistered: false,
            TimerCallerRegistered: false,
            AetherRemoteCommandCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationCurrentPointerSwitchDiagnostics Snapshot
    {
        get;
    }

    public VerifiedReleaseActivationCurrentPointerSwitchStateDiagnostics State
    {
        get
        {
            lock (m_stateGate)
            {
                VerifiedReleaseActivationCurrentPointerSwitchEvidence? completed =
                    m_completed;
                return new VerifiedReleaseActivationCurrentPointerSwitchStateDiagnostics(
                    PointerSwitchReady: completed is not null,
                    ExactServiceControlPlanBound:
                        completed is not null || m_reconciliationPlan is not null,
                    ExactActivationPlanBound:
                        completed is not null || m_reconciliationPlan is not null,
                    PreSwitchServiceControlReady: completed is not null,
                    CurrentPointerChanged: completed is not null,
                    TargetReleaseActive: completed is not null,
                    SetupStable: completed is not null,
                    TargetReleaseImmutable: completed is not null,
                    AtomicSwitchCompleted: completed is not null,
                    ReconciliationRequired: m_reconciliationPlan is not null,
                    PostSwitchServiceControlReady: false,
                    HealthVerificationReady: false,
                    RollbackPerformed: false,
                    ActivationAuthorized: false);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    internal async Task<VerifiedReleaseActivationCurrentPointerSwitchReport>
        ExecuteAsync(
            VerifiedReleaseActivationServiceControlPlanReport planReport,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planReport);
        cancellationToken.ThrowIfCancellationRequested();

        if (!m_settings.ExecutionEnabled)
        {
            return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                    .ExecutionDisabled,
                "Release current-pointer switching is disabled.",
                m_settings,
                planReport);
        }
        if (!OperatingSystem.IsLinux())
        {
            return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                    .UnsupportedPlatform,
                "Release current-pointer switching requires Linux.",
                m_settings,
                planReport);
        }

        VerifiedReleaseActivationServiceControlPlan? plan =
            ValidatePlanReport(planReport);
        if (plan is null)
        {
            return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                planReport.Plan is null
                    ? VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .ServiceControlPlanUnavailable
                    : VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .ServiceControlPlanNotEligible,
                "A successful exact non-executing service-control plan is required.",
                m_settings,
                planReport);
        }
        if (!ValidatePlanShape(planReport, plan))
        {
            return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                    .ServiceControlPlanMismatch,
                "The service-control plan no longer matches its exact activation transaction.",
                m_settings,
                planReport,
                exactPlanBound: true);
        }
        VerifiedReleaseActivationServiceControlPreSwitchEvidence? preSwitchEvidence =
            null;
        if (!plan.HostRestartRequired)
        {
            VerifiedReleaseActivationServiceControlObservation serviceObservation =
                m_serviceControl.ObservePlan(plan);
            if (serviceObservation.ReconciliationRequired)
            {
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .PreSwitchServiceControlReconciliationRequired,
                    "Pre-switch service control requires reconciliation.",
                    m_settings,
                    planReport,
                    exactPlanBound: true,
                    reconciliationRequired: true);
            }
            preSwitchEvidence = plan.ServiceControlRequired
                ? m_serviceControl.GetPreSwitchEvidence(plan)
                : null;
            if (!ValidatePreSwitchServiceControl(
                    plan,
                    serviceObservation,
                    preSwitchEvidence))
            {
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .PreSwitchServiceControlUnavailable,
                    "The exact pre-switch service-control phase is not complete.",
                    m_settings,
                    planReport,
                    exactPlanBound: true);
            }
        }

        await m_executionGate.WaitAsync(cancellationToken);
        try
        {
            lock (m_stateGate)
            {
                if (m_reconciliationPlan is not null)
                {
                    return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                        VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .ReconciliationRequired,
                        "Current-pointer switching is blocked until the retained reconciliation state is resolved.",
                        m_settings,
                        planReport,
                        exactPlanBound: true,
                        preSwitchReady: true,
                        reconciliationRequired: true);
                }
                if (m_completed is not null)
                {
                    return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                        VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .PointerAlreadySwitched,
                        "The current pointer was already switched during this service lifetime.",
                        m_settings,
                        planReport,
                        exactPlanBound:
                            ReferenceEquals(m_completed.ServiceControlPlan, plan),
                        preSwitchReady: true,
                        currentChanged: true);
                }
            }

            DateTimeOffset startedAt = m_timeProvider.GetUtcNow();
            ReleaseStatusReadResult firstStatus;
            InstallationSetupState firstSetup;
            try
            {
                firstStatus = await m_statusReader(cancellationToken);
                firstSetup = await m_setupReader(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsObservationException(exception))
            {
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .StatusUnavailable,
                    "Release or setup status could not be read before the pointer switch.",
                    m_settings,
                    planReport,
                    exactPlanBound: true,
                    preSwitchReady: true);
            }

            VerifiedReleaseActivationPlan activation = plan.ActivationPlan;
            if (!MatchesExpectedStatus(firstStatus, activation, targetActive: false))
            {
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .StatusMismatch,
                    "The installed release is not the exact active release required by the activation plan.",
                    m_settings,
                    planReport,
                    exactPlanBound: true,
                    preSwitchReady: true);
            }
            if (!TryBindSetup(firstSetup, activation, out SetupBinding? firstBinding))
            {
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .SetupMismatch,
                    "Completed setup no longer matches the activation plan.",
                    m_settings,
                    planReport,
                    exactPlanBound: true,
                    preSwitchReady: true,
                    installedActive: true);
            }
            if (!await ValidateTargetReleaseAsync(
                    activation,
                    cancellationToken))
            {
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    Directory.Exists(activation.TargetReleasePath)
                        ? VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .TargetReleaseUnsafe
                        : VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .TargetReleaseUnavailable,
                    "The target release is unavailable or no longer immutable.",
                    m_settings,
                    planReport,
                    exactPlanBound: true,
                    preSwitchReady: true,
                    installedActive: true);
            }

            CurrentPointerRuntimeSnapshot current;
            try
            {
                current = m_runtime.Read(activation.CurrentPointerPath);
            }
            catch (Exception exception) when (IsFilesystemException(exception))
            {
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .CurrentPointerUnavailable,
                    "The current pointer could not be read.",
                    m_settings,
                    planReport,
                    exactPlanBound: true,
                    preSwitchReady: true,
                    installedActive: true,
                    targetImmutable: true);
            }
            if (!MatchesPointer(current, activation.InstalledCurrentLinkTarget))
            {
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .CurrentPointerMismatch,
                    "The current pointer does not match the exact installed link target.",
                    m_settings,
                    planReport,
                    exactPlanBound: true,
                    preSwitchReady: true,
                    installedActive: true,
                    targetImmutable: true);
            }

            string temporaryPath;
            try
            {
                temporaryPath = CreateTemporaryPath(activation);
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or ArgumentException or
                    NotSupportedException or PathTooLongException)
            {
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                        .TemporaryPointerConflict,
                    "A safe same-directory temporary pointer identity could not be created.",
                    m_settings,
                    planReport,
                    exactPlanBound: true,
                    preSwitchReady: true,
                    installedActive: true,
                    targetImmutable: true,
                    installedPointerBound: true);
            }

            bool temporaryCreated = false;
            bool temporaryCleaned = false;
            bool atomicAttempted = false;
            bool atomicCompleted = false;
            try
            {
                CurrentPointerRuntimeSnapshot existingTemporary =
                    m_runtime.Read(temporaryPath);
                if (existingTemporary.EntryPresent)
                {
                    return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                        VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .TemporaryPointerConflict,
                        "The same-directory temporary pointer identity already exists.",
                        m_settings,
                        planReport,
                        exactPlanBound: true,
                        preSwitchReady: true,
                        installedActive: true,
                        targetImmutable: true,
                        installedPointerBound: true);
                }

                m_runtime.CreateSymbolicLink(
                    temporaryPath,
                    activation.TargetCurrentLinkTarget);
                temporaryCreated = true;
                CurrentPointerRuntimeSnapshot temporary = m_runtime.Read(temporaryPath);
                if (!MatchesPointer(temporary, activation.TargetCurrentLinkTarget))
                {
                    temporaryCleaned = TryCleanupTemporary(temporaryPath);
                    if (!temporaryCleaned)
                    {
                        MarkReconciliation(plan);
                    }
                    return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                        VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .TemporaryPointerCreateFailed,
                        "The temporary pointer did not retain the exact target link value.",
                        m_settings,
                        planReport,
                        exactPlanBound: true,
                        preSwitchReady: true,
                        installedActive: true,
                        targetImmutable: true,
                        installedPointerBound: true,
                        temporaryCreated: true,
                        temporaryCleaned: temporaryCleaned,
                        reconciliationRequired: !temporaryCleaned);
                }

                ReleaseStatusReadResult secondStatus =
                    await m_statusReader(cancellationToken);
                InstallationSetupState secondSetup =
                    await m_setupReader(cancellationToken);
                CurrentPointerRuntimeSnapshot secondCurrent =
                    m_runtime.Read(activation.CurrentPointerPath);
                if (!EquivalentStatus(firstStatus, secondStatus) ||
                    !MatchesExpectedStatus(secondStatus, activation, targetActive: false) ||
                    !TryBindSetup(secondSetup, activation, out SetupBinding? secondBinding) ||
                    firstBinding != secondBinding ||
                    !MatchesPointer(
                        secondCurrent,
                        activation.InstalledCurrentLinkTarget) ||
                    !(await ValidateTargetReleaseAsync(
                        activation,
                        cancellationToken)))
                {
                    temporaryCleaned = TryCleanupTemporary(temporaryPath);
                    if (!temporaryCleaned)
                    {
                        MarkReconciliation(plan);
                    }
                    return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                        VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .ObservationDrift,
                        "Release, setup, target, or pointer state changed before the atomic switch.",
                        m_settings,
                        planReport,
                        exactPlanBound: true,
                        preSwitchReady: true,
                        installedActive: true,
                        targetImmutable: true,
                        installedPointerBound: true,
                        temporaryCreated: true,
                        temporaryCleaned: temporaryCleaned,
                        reconciliationRequired: !temporaryCleaned);
                }

                cancellationToken.ThrowIfCancellationRequested();
                atomicAttempted = true;
                m_runtime.ReplaceAtomically(
                    temporaryPath,
                    activation.CurrentPointerPath);
                atomicCompleted = true;
                CurrentPointerRuntimeSnapshot consumed = m_runtime.Read(temporaryPath);
                CurrentPointerRuntimeSnapshot switched =
                    m_runtime.Read(activation.CurrentPointerPath);
                temporaryCleaned = !consumed.EntryPresent;
                if (!temporaryCleaned ||
                    !MatchesPointer(switched, activation.TargetCurrentLinkTarget))
                {
                    MarkReconciliation(plan);
                    return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                        VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .ReconciliationRequired,
                        "The atomic switch outcome could not be proven exactly.",
                        m_settings,
                        planReport,
                        exactPlanBound: true,
                        preSwitchReady: true,
                        installedActive: true,
                        targetImmutable: true,
                        installedPointerBound: true,
                        targetPointerBound:
                            MatchesPointer(
                                switched,
                                activation.TargetCurrentLinkTarget),
                        temporaryCreated: true,
                        temporaryCleaned: temporaryCleaned,
                        atomicAttempted: true,
                        atomicCompleted: true,
                        currentChanged: true,
                        reconciliationRequired: true);
                }

                ReleaseStatusReadResult afterStatus =
                    await m_statusReader(cancellationToken);
                InstallationSetupState afterSetup =
                    await m_setupReader(cancellationToken);
                if (!MatchesExpectedStatus(afterStatus, activation, targetActive: true) ||
                    !TryBindSetup(afterSetup, activation, out SetupBinding? afterBinding) ||
                    firstBinding != afterBinding ||
                    !(await ValidateTargetReleaseAsync(
                        activation,
                        cancellationToken)))
                {
                    MarkReconciliation(plan);
                    return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                        VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .ObservationDrift,
                        "The switched target could not be revalidated against stable setup and release status.",
                        m_settings,
                        planReport,
                        exactPlanBound: true,
                        preSwitchReady: true,
                        installedActive: true,
                        targetImmutable: true,
                        installedPointerBound: true,
                        targetPointerBound: true,
                        temporaryCreated: true,
                        temporaryCleaned: true,
                        atomicAttempted: true,
                        atomicCompleted: true,
                        currentChanged: true,
                        reconciliationRequired: true);
                }

                DateTimeOffset completedAt = m_timeProvider.GetUtcNow();
                VerifiedReleaseActivationCurrentPointerSwitchEvidence evidence =
                    new(
                        plan,
                        preSwitchEvidence,
                        startedAt,
                        completedAt);
                lock (m_stateGate)
                {
                    m_completed = evidence;
                }
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Success(
                    m_settings,
                    planReport,
                    evidence);
            }
            catch (OperationCanceledException)
            {
                if (atomicAttempted)
                {
                    MarkReconciliation(plan);
                }
                else
                {
                    temporaryCleaned = TryCleanupTemporary(temporaryPath);
                    if (!temporaryCleaned)
                    {
                        MarkReconciliation(plan);
                    }
                }
                throw;
            }
            catch (Exception exception) when (IsFilesystemException(exception))
            {
                if (atomicAttempted)
                {
                    MarkReconciliation(plan);
                }
                else
                {
                    temporaryCleaned = TryCleanupTemporary(temporaryPath);
                    if (!temporaryCleaned)
                    {
                        MarkReconciliation(plan);
                    }
                }
                return VerifiedReleaseActivationCurrentPointerSwitchReport.Failure(
                    atomicAttempted
                        ? VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .AtomicSwitchFailed
                        : VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                            .TemporaryPointerCreateFailed,
                    atomicAttempted
                        ? "The atomic pointer replacement failed with an unknown outcome."
                        : "The temporary pointer could not be created or validated.",
                    m_settings,
                    planReport,
                    exactPlanBound: true,
                    preSwitchReady: true,
                    installedActive: true,
                    targetImmutable: true,
                    installedPointerBound: true,
                    temporaryCreated: temporaryCreated,
                    temporaryCleaned: temporaryCleaned,
                    atomicAttempted: atomicAttempted,
                    atomicCompleted: atomicCompleted,
                    currentChanged: atomicCompleted,
                    reconciliationRequired:
                        atomicAttempted || !temporaryCleaned);
            }
        }
        finally
        {
            m_executionGate.Release();
        }
    }

    internal VerifiedReleaseActivationCurrentPointerSwitchObservation Observe(
        VerifiedReleaseActivationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (m_stateGate)
        {
            if (m_completed is not null &&
                ReferenceEquals(m_completed.ActivationPlan, plan))
            {
                return new VerifiedReleaseActivationCurrentPointerSwitchObservation(
                    PointerSwitchReady: true,
                    ExactServiceControlPlanBound: true,
                    ExactActivationPlanBound: true,
                    PreSwitchServiceControlReady: true,
                    m_completed.CompletedAt,
                    ReconciliationRequired: false);
            }
            bool reconciliation = m_reconciliationPlan is not null &&
                ReferenceEquals(m_reconciliationPlan.ActivationPlan, plan);
            return new VerifiedReleaseActivationCurrentPointerSwitchObservation(
                PointerSwitchReady: false,
                ExactServiceControlPlanBound: false,
                ExactActivationPlanBound: false,
                PreSwitchServiceControlReady: false,
                CompletedAt: null,
                ReconciliationRequired: reconciliation);
        }
    }

    internal VerifiedReleaseActivationCurrentPointerSwitchEvidence? GetEvidence(
        VerifiedReleaseActivationServiceControlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (m_stateGate)
        {
            return m_completed is not null &&
                ReferenceEquals(m_completed.ServiceControlPlan, plan)
                ? m_completed
                : null;
        }
    }

    internal static bool ValidateEvidenceReport(
        VerifiedReleaseActivationCurrentPointerSwitchReport report,
        VerifiedReleaseActivationServiceControlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(plan);
        VerifiedReleaseActivationCurrentPointerSwitchEvidence? evidence =
            report.Evidence;
        return report.Succeeded &&
            report.FailureCode ==
                VerifiedReleaseActivationCurrentPointerSwitchFailureCode.None &&
            evidence is not null &&
            ReferenceEquals(evidence.ServiceControlPlan, plan) &&
            ReferenceEquals(evidence.ActivationPlan, plan.ActivationPlan) &&
            report.ExactServiceControlPlanBound &&
            report.ExactActivationPlanBound &&
            report.PreSwitchServiceControlReady &&
            report.InstalledReleaseActiveBeforeSwitch &&
            report.TargetReleaseActiveAfterSwitch &&
            report.SetupStable &&
            report.TargetReleaseImmutable &&
            report.ExactInstalledPointerBound &&
            report.ExactTargetPointerBound &&
            report.TemporaryPointerCreated &&
            report.TemporaryPointerCleaned &&
            report.AtomicSwitchAttempted &&
            report.AtomicSwitchCompleted &&
            report.CurrentPointerChanged &&
            !report.ReconciliationRequired &&
            !report.PostSwitchServiceControlReady &&
            !report.HealthVerificationReady &&
            !report.RollbackPerformed &&
            !report.ActivationAuthorized;
    }

    private void MarkReconciliation(
        VerifiedReleaseActivationServiceControlPlan plan)
    {
        lock (m_stateGate)
        {
            m_reconciliationPlan = plan;
        }
    }

    private bool TryCleanupTemporary(string path)
    {
        try
        {
            m_runtime.DeleteTemporary(path);
            return !m_runtime.Read(path).EntryPresent;
        }
        catch (Exception exception) when (IsFilesystemException(exception))
        {
            return false;
        }
    }

    private string CreateTemporaryPath(VerifiedReleaseActivationPlan activation)
    {
        string nonce = m_nonceFactory();
        if (nonce.Length is < 16 or > 64 ||
            nonce.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidOperationException(
                "The current-pointer temporary identity is invalid.");
        }
        string parent = Path.GetDirectoryName(activation.CurrentPointerPath) ??
            throw new InvalidOperationException(
                "The current pointer has no deployment parent.");
        string path = Path.GetFullPath(
            Path.Combine(parent, $".current-switch-{nonce}"));
        if (!string.Equals(
                Path.GetDirectoryName(path),
                parent,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The temporary pointer must remain in the current pointer directory.");
        }
        return path;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<bool> ValidateTargetReleaseAsync(
        VerifiedReleaseActivationPlan activation,
        CancellationToken cancellationToken)
    {
        try
        {
            string expectedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(activation.TargetReleasePath));
            DirectoryInfo target = new(expectedRoot);
            target.Refresh();
            if (!ValidateImmutableDirectory(target) ||
                !string.Equals(
                    Path.TrimEndingDirectorySeparator(target.FullName),
                    expectedRoot,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    Path.GetDirectoryName(expectedRoot),
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(activation.ReleaseRootPath)),
                    StringComparison.Ordinal))
            {
                return false;
            }

            Dictionary<string, FileInfo> files = new(StringComparer.Ordinal);
            Stack<DirectoryInfo> pending = new();
            pending.Push(target);
            int directoryCount = 0;
            int maximumDirectoryCount = activation.UsesExtractedRoleTree
                ? VerifiedReleaseArchiveExtractionService
                    .MaximumExtractedDirectoryCount + 1
                : MaximumDirectoryCount;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectoryInfo directory = pending.Pop();
                if (!ValidateImmutableDirectory(directory) ||
                    ++directoryCount > maximumDirectoryCount)
                {
                    return false;
                }
                FileSystemInfo[] entries = directory.GetFileSystemInfos();
                if (!string.Equals(
                        directory.FullName,
                        expectedRoot,
                        StringComparison.Ordinal) &&
                    entries.Length == 0)
                {
                    return false;
                }
                foreach (FileSystemInfo entry in entries)
                {
                    entry.Refresh();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        entry.LinkTarget is not null)
                    {
                        return false;
                    }
                    string relative = Path.GetRelativePath(
                        expectedRoot,
                        entry.FullName);
                    string portableRelative = relative.Replace(
                        Path.DirectorySeparatorChar,
                        '/');
                    if (!ReleasePackagePath.IsSafe(portableRelative))
                    {
                        return false;
                    }
                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child);
                        continue;
                    }
                    if (entry is not FileInfo file ||
                        (file.Attributes & FileAttributes.Directory) != 0 ||
                        (File.GetUnixFileMode(file.FullName) &
                            AnyWritableUnixModes) != 0 ||
                        !files.TryAdd(relative, file))
                    {
                        return false;
                    }
                }
            }

            if (activation.UsesExtractedRoleTree)
            {
                if (files.Count != activation.Files.Count ||
                    directoryCount != activation.ExtractedDirectoryCount + 1)
                {
                    return false;
                }
                foreach (VerifiedReleaseActivationFilePlan expected in
                    activation.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relative = expected.RelativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar);
                    if (!files.TryGetValue(relative, out FileInfo? file) ||
                        !string.Equals(
                            Path.GetFullPath(file.FullName),
                            Path.GetFullPath(expected.PublishedPath),
                            StringComparison.Ordinal) ||
                        !await HashMatchesAsync(
                            file,
                            expected.Length,
                            expected.Sha256.ToArray(),
                            cancellationToken,
                            expected.Executable))
                    {
                        return false;
                    }
                }
                return true;
            }

            if (files.Count != LegacyExpectedFileCount ||
                !files.TryGetValue(
                    LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                    out FileInfo? manifest) ||
                !await HashMatchesAsync(
                    manifest,
                    activation.ManifestLength,
                    activation.ManifestSha256.ToArray(),
                    cancellationToken))
            {
                return false;
            }
            foreach (VerifiedReleaseActivationPackagePlan package in
                activation.Packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string expectedPath = Path.GetFullPath(package.PublishedPath);
                string relative = Path.GetRelativePath(expectedRoot, expectedPath);
                if (!files.TryGetValue(relative, out FileInfo? file) ||
                    !string.Equals(
                        Path.GetFullPath(file.FullName),
                        expectedPath,
                        StringComparison.Ordinal) ||
                    !await HashMatchesAsync(
                        file,
                        package.Length,
                        package.Sha256.ToArray(),
                        cancellationToken))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (IsFilesystemException(exception))
        {
            return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<bool> HashMatchesAsync(
        FileInfo file,
        long expectedLength,
        ReadOnlyMemory<byte> expectedSha256,
        CancellationToken cancellationToken,
        bool? executable = null)
    {
        file.Refresh();
        UnixFileMode mode = file.Exists
            ? File.GetUnixFileMode(file.FullName)
            : 0;
        UnixFileMode? requiredMode = executable switch
        {
            true => UnixFileMode.UserRead | UnixFileMode.UserExecute,
            false => UnixFileMode.UserRead,
            null => null
        };
        if (!file.Exists ||
            file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.Length != expectedLength ||
            expectedSha256.Length != 32 ||
            (mode & AnyWritableUnixModes) != 0 ||
            requiredMode is not null && mode != requiredMode.Value)
        {
            return false;
        }

        await using FileStream stream = new(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(64 * 1024);
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            total = checked(total + read);
            if (total > expectedLength)
            {
                return false;
            }
            hash.AppendData(buffer, 0, read);
        }
        byte[] actual = hash.GetHashAndReset();
        file.Refresh();
        UnixFileMode finalMode = File.GetUnixFileMode(file.FullName);
        return total == expectedLength &&
            file.Exists &&
            file.LinkTarget is null &&
            file.Length == expectedLength &&
            (finalMode & AnyWritableUnixModes) == 0 &&
            (requiredMode is null || finalMode == requiredMode.Value) &&
            CryptographicOperations.FixedTimeEquals(
                actual,
                expectedSha256.Span);
    }

    [SupportedOSPlatform("linux")]
    private static bool ValidateImmutableDirectory(DirectoryInfo directory)
    {
        directory.Refresh();
        return directory.Exists &&
            directory.LinkTarget is null &&
            (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
            (File.GetUnixFileMode(directory.FullName) & AnyWritableUnixModes) == 0;
    }

    private static bool MatchesPointer(
        CurrentPointerRuntimeSnapshot snapshot,
        string expectedTarget) =>
        snapshot.EntryPresent &&
        snapshot.IsSymbolicLink &&
        string.Equals(snapshot.LinkTarget, expectedTarget, StringComparison.Ordinal);

    private static VerifiedReleaseActivationServiceControlPlan?
        ValidatePlanReport(VerifiedReleaseActivationServiceControlPlanReport report)
    {
        if (!report.Succeeded ||
            report.FailureCode !=
                VerifiedReleaseActivationServiceControlPlanFailureCode.None ||
            report.Plan is null ||
            report.SetupRevision is null or < 1 ||
            string.IsNullOrEmpty(report.InstalledReleaseIdentity) ||
            string.IsNullOrEmpty(report.TargetReleaseIdentity) ||
            report.RestartServiceCount is < 0 or > 4 ||
            !report.ExactActivationPlanBound ||
            !report.FixedServiceMappingBound ||
            !report.DeterministicOrderingBound ||
            report.ProcessInvocationPerformed ||
            report.SystemdCommandPerformed ||
            report.HostRestartPerformed ||
            report.CurrentPointerChanged ||
            report.ActivationAuthorized)
        {
            return null;
        }
        return report.Plan;
    }

    private static bool ValidatePlanShape(
        VerifiedReleaseActivationServiceControlPlanReport report,
        VerifiedReleaseActivationServiceControlPlan plan)
    {
        VerifiedReleaseActivationPlan activation = plan.ActivationPlan;
        return report.SetupRevision == activation.SetupRevision &&
            string.Equals(
                report.InstalledReleaseIdentity,
                activation.InstalledReleaseIdentity,
                StringComparison.Ordinal) &&
            string.Equals(
                report.TargetReleaseIdentity,
                activation.TargetReleaseIdentity,
                StringComparison.Ordinal) &&
            report.RestartServiceCount == activation.RestartServiceCount &&
            report.HostRestartRequired == activation.RestartHost &&
            report.ServiceControlRequired == plan.ServiceControlRequired &&
            report.NoOpServiceControlResolved == !plan.ServiceControlRequired &&
            report.StopActionCount == plan.StopActions.Count &&
            report.StartActionCount == plan.StartActions.Count &&
            report.HostRestartActionCount == plan.HostRestartActions.Count &&
            report.PreSwitchStopPlanned == (plan.StopActions.Count > 0) &&
            report.PostSwitchStartPlanned == (plan.StartActions.Count > 0) &&
            report.HostRestartPlanned == (plan.HostRestartActions.Count == 1) &&
            report.ServiceControlReady == !plan.ServiceControlRequired &&
            activation.AtomicCurrentPointerSwitchRequired &&
            activation.ServiceHealthVerificationRequired &&
            activation.AutomaticRollbackRequired &&
            activation.OperatorApprovalRequired;
    }

    private static bool ValidatePreSwitchServiceControl(
        VerifiedReleaseActivationServiceControlPlan plan,
        VerifiedReleaseActivationServiceControlObservation observation,
        VerifiedReleaseActivationServiceControlPreSwitchEvidence? evidence)
    {
        if (!plan.ServiceControlRequired)
        {
            return evidence is null &&
                observation.ServiceControlReady &&
                !observation.ServiceControlRequired &&
                observation.PlannedStopActionCount == 0 &&
                observation.ExecutedStopActionCount == 0 &&
                observation.TopologyNoOpStopActionCount == 0 &&
                !observation.ReconciliationRequired;
        }
        return evidence is not null &&
            ReferenceEquals(evidence.Plan, plan) &&
            !observation.ServiceControlReady &&
            observation.ServiceControlRequired &&
            observation.PlannedStopActionCount == plan.StopActions.Count &&
            observation.ExecutedStopActionCount == evidence.ExecutedActionCount &&
            observation.TopologyNoOpStopActionCount ==
                evidence.TopologyNoOpActionCount &&
            observation.ExecutedStopActionCount +
                observation.TopologyNoOpStopActionCount ==
                observation.PlannedStopActionCount &&
            !observation.ReconciliationRequired;
    }

    private static bool MatchesExpectedStatus(
        ReleaseStatusReadResult status,
        VerifiedReleaseActivationPlan activation,
        bool targetActive)
    {
        if (!status.Succeeded ||
            status.FailureCode != ReleaseStatusFailureCode.None ||
            status.SetupSchemaVersion is null or < 1 ||
            status.SetupRevision != activation.SetupRevision ||
            !status.SetupComplete ||
            status.SetupLockMode != InstallationSetupLockMode.Complete ||
            status.LastCompletedStep != InstallationSetupStep.Administrator ||
            status.UpdateChannel != activation.UpdateChannel ||
            !string.Equals(
                status.PinnedReleaseIdentity,
                activation.PinnedReleaseIdentity,
                StringComparison.Ordinal) ||
            status.InstallTransmitSupport != activation.InstallTransmitSupport ||
            !status.ReleaseDirectoryPresent ||
            status.AvailableReleaseIdentities is null ||
            status.AvailableReleaseCount !=
                status.AvailableReleaseIdentities.Count ||
            status.AvailableReleaseCount is < 2 or >
                ReleaseInstallationStatusReader.MaximumReleaseCount ||
            !status.CurrentPointerPresent)
        {
            return false;
        }
        string expectedActive = targetActive
            ? activation.TargetReleaseIdentity
            : activation.InstalledReleaseIdentity;
        return string.Equals(
                status.ActiveReleaseIdentity,
                expectedActive,
                StringComparison.Ordinal) &&
            status.AvailableReleaseIdentities
                .Distinct(StringComparer.Ordinal).Count() ==
                status.AvailableReleaseIdentities.Count &&
            status.AvailableReleaseIdentities.Contains(
                activation.InstalledReleaseIdentity,
                StringComparer.Ordinal) &&
            status.AvailableReleaseIdentities.Contains(
                activation.TargetReleaseIdentity,
                StringComparer.Ordinal);
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
        first.AvailableReleaseIdentities is not null &&
        second.AvailableReleaseIdentities is not null &&
        first.AvailableReleaseIdentities.SequenceEqual(
            second.AvailableReleaseIdentities,
            StringComparer.Ordinal) &&
        first.CurrentPointerPresent == second.CurrentPointerPresent &&
        string.Equals(
            first.ActiveReleaseIdentity,
            second.ActiveReleaseIdentity,
            StringComparison.Ordinal) &&
        first.RollbackCandidateKnown == second.RollbackCandidateKnown;

    private static bool TryBindSetup(
        InstallationSetupState state,
        VerifiedReleaseActivationPlan activation,
        out SetupBinding? binding)
    {
        binding = null;
        try
        {
            InstallationSetupStateValidator.Validate(state);
            if (state.Lock.Mode != InstallationSetupLockMode.Complete ||
                state.LastCompletedStep != InstallationSetupStep.Administrator ||
                state.Paths is null ||
                state.Revision != activation.SetupRevision ||
                state.UpdateChannel != activation.UpdateChannel ||
                !string.Equals(
                    state.PinnedRelease,
                    activation.PinnedReleaseIdentity,
                    StringComparison.Ordinal) ||
                state.InstallTransmitSupport != activation.InstallTransmitSupport)
            {
                return false;
            }
            string releaseRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(state.Paths.ReleaseDirectory));
            if (!string.Equals(
                    releaseRoot,
                    activation.ReleaseRootPath,
                    StringComparison.Ordinal))
            {
                return false;
            }
            binding = new SetupBinding(
                state.SchemaVersion,
                state.Revision,
                state.UpdatedAt,
                state.UpdateChannel,
                state.PinnedRelease,
                state.InstallTransmitSupport,
                releaseRoot);
            return true;
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsObservationException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException or
            FileNotFoundException or PathTooLongException;

    private static bool IsFilesystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException or
            FileNotFoundException or DirectoryNotFoundException or
            PathTooLongException;

    private sealed record SetupBinding(
        int SchemaVersion,
        long Revision,
        DateTimeOffset UpdatedAt,
        InstallationUpdateChannel UpdateChannel,
        string PinnedReleaseIdentity,
        bool InstallTransmitSupport,
        string ReleaseRootPath);
}
