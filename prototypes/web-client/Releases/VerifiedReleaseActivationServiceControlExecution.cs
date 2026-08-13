using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public sealed class ReleaseActivationServiceControlSettings
{
    public const string SectionName = "ReleaseActivationServiceControl";

    public bool ExecutionEnabled { get; init; }
    public bool RemoteExecutionEnabled { get; init; }
    public string RemoteStationId { get; init; } = string.Empty;
}

public enum VerifiedReleaseActivationServiceControlExecutionPhase
{
    PreSwitchStop = 1,
    PostSwitchStart = 2
}

public enum VerifiedReleaseActivationServiceControlExecutionFailureCode
{
    None = 0,
    ExecutionDisabled = 1,
    UnsupportedPlatform = 2,
    ServiceControlPlanNotEligible = 3,
    ServiceControlPlanUnavailable = 4,
    ServiceControlPlanMismatch = 5,
    HostRestartUnsupported = 6,
    PhaseOrderInvalid = 7,
    ReleaseStatusUnavailable = 8,
    ReleaseStatusMismatch = 9,
    SetupUnavailable = 10,
    SetupMismatch = 11,
    RemoteServiceControlUnavailable = 12,
    UnitControlFailed = 13,
    ObservationDrift = 14,
    PhaseAlreadyCompleted = 15,
    ReconciliationRequired = 16,
    CurrentPointerSwitchUnavailable = 17
}

public sealed record VerifiedReleaseActivationServiceControlExecutionReport(
    bool Succeeded,
    VerifiedReleaseActivationServiceControlExecutionFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    VerifiedReleaseActivationServiceControlExecutionPhase Phase,
    bool ExecutionEnabled,
    bool ExecutionAvailable,
    int PlannedActionCount,
    int ExecutedActionCount,
    int TopologyNoOpActionCount,
    bool ExactServiceControlPlanBound,
    bool ExactActivationPlanBound,
    bool SetupBound,
    bool TopologyBound,
    bool InstalledReleaseActiveBefore,
    bool InstalledReleaseActiveAfter,
    bool TargetReleaseActiveBefore,
    bool TargetReleaseActiveAfter,
    bool ProcessInvocationPerformed,
    bool SystemdCommandPerformed,
    bool ShellUsed,
    bool PreSwitchStopComplete,
    bool PostSwitchStartComplete,
    bool ServiceControlReady,
    bool ReconciliationRequired,
    bool HostRestartPerformed,
    bool CurrentPointerChanged,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationServiceControlPlan? FailedPlan
    {
        get;
        init;
    }

    internal static VerifiedReleaseActivationServiceControlExecutionReport Failure(
        VerifiedReleaseActivationServiceControlExecutionFailureCode failureCode,
        string message,
        ReleaseActivationServiceControlSettings settings,
        VerifiedReleaseActivationServiceControlExecutionPhase phase,
        VerifiedReleaseActivationServiceControlPlanReport? planReport = null,
        ServiceControlPhaseTally? tally = null,
        bool exactPlanBound = false,
        bool setupBound = false,
        bool topologyBound = false,
        bool installedActiveBefore = false,
        bool installedActiveAfter = false,
        bool targetActiveBefore = false,
        bool targetActiveAfter = false,
        bool preSwitchComplete = false,
        bool postSwitchComplete = false,
        bool reconciliationRequired = false) =>
        new(
            false,
            failureCode,
            message,
            planReport?.SetupRevision,
            planReport?.InstalledReleaseIdentity ?? string.Empty,
            planReport?.TargetReleaseIdentity ?? string.Empty,
            phase,
            settings.ExecutionEnabled,
            settings.ExecutionEnabled && OperatingSystem.IsLinux(),
            tally?.PlannedActionCount ?? 0,
            tally?.ExecutedActionCount ?? 0,
            tally?.TopologyNoOpActionCount ?? 0,
            ExactServiceControlPlanBound: exactPlanBound,
            ExactActivationPlanBound: exactPlanBound,
            SetupBound: setupBound,
            TopologyBound: topologyBound,
            InstalledReleaseActiveBefore: installedActiveBefore,
            InstalledReleaseActiveAfter: installedActiveAfter,
            TargetReleaseActiveBefore: targetActiveBefore,
            TargetReleaseActiveAfter: targetActiveAfter,
            ProcessInvocationPerformed:
                (tally?.ProcessInvocationCount ?? 0) > 0,
            SystemdCommandPerformed:
                (tally?.ProcessInvocationCount ?? 0) > 0,
            ShellUsed: false,
            PreSwitchStopComplete: preSwitchComplete,
            PostSwitchStartComplete: postSwitchComplete,
            ServiceControlReady: false,
            ReconciliationRequired: reconciliationRequired,
            HostRestartPerformed: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            FailedPlan = exactPlanBound ? planReport?.Plan : null
        };

    internal static VerifiedReleaseActivationServiceControlExecutionReport Success(
        ReleaseActivationServiceControlSettings settings,
        VerifiedReleaseActivationServiceControlExecutionPhase phase,
        VerifiedReleaseActivationServiceControlPlanReport planReport,
        ServiceControlPhaseTally tally,
        bool preSwitchComplete,
        bool postSwitchComplete,
        bool serviceControlReady,
        bool noOp) =>
        new(
            true,
            VerifiedReleaseActivationServiceControlExecutionFailureCode.None,
            noOp
                ? "The exact release requires no service or host restart; service control remains ready without invoking a process."
                : phase ==
                    VerifiedReleaseActivationServiceControlExecutionPhase
                        .PreSwitchStop
                    ? "The exact locally owned pre-switch service-stop phase completed without changing the release pointer or authorizing activation."
                    : "The exact locally owned post-switch service-start phase completed and retained one in-memory exact-plan service-control observation without authorizing activation.",
            planReport.SetupRevision,
            planReport.InstalledReleaseIdentity,
            planReport.TargetReleaseIdentity,
            phase,
            settings.ExecutionEnabled,
            settings.ExecutionEnabled && OperatingSystem.IsLinux(),
            tally.PlannedActionCount,
            tally.ExecutedActionCount,
            tally.TopologyNoOpActionCount,
            ExactServiceControlPlanBound: true,
            ExactActivationPlanBound: true,
            SetupBound: !noOp,
            TopologyBound: !noOp,
            InstalledReleaseActiveBefore:
                !noOp && phase ==
                    VerifiedReleaseActivationServiceControlExecutionPhase
                        .PreSwitchStop,
            InstalledReleaseActiveAfter:
                !noOp && phase ==
                    VerifiedReleaseActivationServiceControlExecutionPhase
                        .PreSwitchStop,
            TargetReleaseActiveBefore:
                !noOp && phase ==
                    VerifiedReleaseActivationServiceControlExecutionPhase
                        .PostSwitchStart,
            TargetReleaseActiveAfter:
                !noOp && phase ==
                    VerifiedReleaseActivationServiceControlExecutionPhase
                        .PostSwitchStart,
            ProcessInvocationPerformed: tally.ProcessInvocationCount > 0,
            SystemdCommandPerformed: tally.ProcessInvocationCount > 0,
            ShellUsed: false,
            PreSwitchStopComplete: preSwitchComplete,
            PostSwitchStartComplete: postSwitchComplete,
            ServiceControlReady: serviceControlReady,
            ReconciliationRequired: false,
            HostRestartPerformed: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false);
}

public sealed record VerifiedReleaseActivationServiceControlExecutionDiagnostics(
    bool Registered,
    bool ConfigurationRegistered,
    bool ExecutionEnabled,
    bool ExecutionAvailable,
    bool ExactServiceControlPlanInputRegistered,
    bool ExactServiceControlPlanBindingRegistered,
    bool ExactActivationPlanBindingRegistered,
    bool ExactCurrentPointerSwitchEvidenceInputRegistered,
    bool ReleaseStatusDoubleReadRegistered,
    bool SetupStateDoubleReadRegistered,
    bool TopologyBindingRegistered,
    bool PreSwitchStopPhaseRegistered,
    bool PostSwitchStartPhaseRegistered,
    bool NoOpResolutionRegistered,
    bool DeterministicOrderingRegistered,
    bool FixedUnitMappingRegistered,
    bool DirectProcessRegistered,
    bool ShellRegistered,
    bool ClearedEnvironmentRegistered,
    bool UserUnitScopeRegistered,
    bool SystemUnitScopeRegistered,
    bool BoundedOutputRegistered,
    bool HardTimeoutRegistered,
    bool ProcessTreeTerminationRegistered,
    bool ExactPlanEvidenceRegistered,
    bool PartialFailureReconciliationRegistered,
    bool AutomaticRetryRegistered,
    bool HostRestartExecutionRegistered,
    bool RemoteServiceControlRegistered,
    bool CurrentPointerMutationRegistered,
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
    bool HealthProbeCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record VerifiedReleaseActivationServiceControlExecutionStateDiagnostics(
    bool ServiceControlReady,
    bool ExactServiceControlPlanBound,
    bool ExactActivationPlanBound,
    bool PreSwitchStopComplete,
    bool PostSwitchStartComplete,
    int PlannedStopActionCount,
    int ExecutedStopActionCount,
    int TopologyNoOpStopActionCount,
    int PlannedStartActionCount,
    int ExecutedStartActionCount,
    int TopologyNoOpStartActionCount,
    bool SetupStable,
    bool TopologyStable,
    bool InstalledReleaseActiveDuringStop,
    bool TargetReleaseActiveDuringStart,
    bool ReconciliationRequired,
    bool HostRestartPerformed,
    bool CurrentPointerChanged,
    bool RollbackPerformed,
    bool ActivationAuthorized);

internal sealed record VerifiedReleaseActivationServiceControlObservation(
    bool ServiceControlReady,
    bool ServiceControlRequired,
    int PlannedStopActionCount,
    int ExecutedStopActionCount,
    int TopologyNoOpStopActionCount,
    int PlannedStartActionCount,
    int ExecutedStartActionCount,
    int TopologyNoOpStartActionCount,
    DateTimeOffset? CompletedAt,
    bool ReconciliationRequired);

internal sealed class VerifiedReleaseActivationServiceControlEvidence
{
    internal VerifiedReleaseActivationServiceControlEvidence(
        VerifiedReleaseActivationServiceControlPlan plan,
        InstallationTopologyKind topology,
        int executedStopActionCount,
        int topologyNoOpStopActionCount,
        int executedStartActionCount,
        int topologyNoOpStartActionCount,
        DateTimeOffset completedAt)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Topology = topology;
        ExecutedStopActionCount = executedStopActionCount;
        TopologyNoOpStopActionCount = topologyNoOpStopActionCount;
        ExecutedStartActionCount = executedStartActionCount;
        TopologyNoOpStartActionCount = topologyNoOpStartActionCount;
        CompletedAt = completedAt;
    }

    internal VerifiedReleaseActivationServiceControlPlan Plan { get; }
    internal InstallationTopologyKind Topology { get; }
    internal int ExecutedStopActionCount { get; }
    internal int TopologyNoOpStopActionCount { get; }
    internal int ExecutedStartActionCount { get; }
    internal int TopologyNoOpStartActionCount { get; }
    internal DateTimeOffset CompletedAt { get; }
}

internal sealed class VerifiedReleaseActivationServiceControlPreSwitchEvidence
{
    internal VerifiedReleaseActivationServiceControlPreSwitchEvidence(
        VerifiedReleaseActivationServiceControlPlan plan,
        InstallationTopologyKind topology,
        int executedActionCount,
        int topologyNoOpActionCount,
        DateTimeOffset completedAt)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Topology = topology;
        ExecutedActionCount = executedActionCount;
        TopologyNoOpActionCount = topologyNoOpActionCount;
        CompletedAt = completedAt;
    }

    internal VerifiedReleaseActivationServiceControlPlan Plan { get; }
    internal InstallationTopologyKind Topology { get; }
    internal int ExecutedActionCount { get; }
    internal int TopologyNoOpActionCount { get; }
    internal DateTimeOffset CompletedAt { get; }
}

internal sealed record ServiceControlAttemptResult(
    bool Succeeded,
    bool ProcessStarted,
    bool MutationAttempted,
    bool OutcomeKnown,
    string Reason)
{
    internal static ServiceControlAttemptResult Success() =>
        new(
            true,
            ProcessStarted: true,
            MutationAttempted: true,
            OutcomeKnown: true,
            string.Empty);

    internal static ServiceControlAttemptResult RemoteSuccess() =>
        new(
            true,
            ProcessStarted: false,
            MutationAttempted: true,
            OutcomeKnown: true,
            string.Empty);

    internal static ServiceControlAttemptResult NotStarted(string reason) =>
        new(
            false,
            ProcessStarted: false,
            MutationAttempted: false,
            OutcomeKnown: true,
            reason);

    internal static ServiceControlAttemptResult Unknown(string reason) =>
        new(
            false,
            ProcessStarted: true,
            MutationAttempted: true,
            OutcomeKnown: false,
            reason);

    internal static ServiceControlAttemptResult RemoteUnknown(string reason) =>
        new(
            false,
            ProcessStarted: false,
            MutationAttempted: true,
            OutcomeKnown: false,
            reason);
}

internal interface IVerifiedReleaseActivationServiceControlRuntime
{
    Task<ServiceControlAttemptResult> ControlUnitAsync(
        VerifiedReleaseActivationServiceControlAction action,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class LinuxVerifiedReleaseActivationServiceControlRuntime :
    IVerifiedReleaseActivationServiceControlRuntime
{
    internal const string SystemctlPath = "/usr/bin/systemctl";
    internal const int MaximumProcessOutputCharacters = 4096;

    private readonly string m_systemctlPath;

    internal LinuxVerifiedReleaseActivationServiceControlRuntime()
        : this(SystemctlPath)
    {
    }

    internal LinuxVerifiedReleaseActivationServiceControlRuntime(
        string systemctlPath)
    {
        if (string.IsNullOrWhiteSpace(systemctlPath) ||
            !Path.IsPathRooted(systemctlPath))
        {
            throw new InvalidOperationException(
                "The systemctl service-control path must be absolute.");
        }
        m_systemctlPath = Path.GetFullPath(systemctlPath);
    }

    public async Task<ServiceControlAttemptResult> ControlUnitAsync(
        VerifiedReleaseActivationServiceControlAction action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidateAction(action) || timeout <= TimeSpan.Zero)
        {
            return ServiceControlAttemptResult.NotStarted(
                "The planned service-control action is invalid.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = m_systemctlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(
            action.Kind == VerifiedReleaseActivationServiceControlActionKind.Stop
                ? "stop"
                : "start");
        startInfo.ArgumentList.Add(action.UnitIdentity);
        ConfigureProcessEnvironment(startInfo);

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return ServiceControlAttemptResult.NotStarted(
                    "The service-control process could not start.");
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or IOException)
        {
            return ServiceControlAttemptResult.NotStarted(
                "The service-control process is unavailable.");
        }

        using CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(timeout);
        Task<string> stdout = ReadBoundedAsync(
            process.StandardOutput,
            MaximumProcessOutputCharacters,
            operation.Token);
        Task<string> stderr = ReadBoundedAsync(
            process.StandardError,
            MaximumProcessOutputCharacters,
            operation.Token);
        try
        {
            await process.WaitForExitAsync(operation.Token);
            string output = await stdout;
            string error = await stderr;
            if (output.Length != 0 || error.Length != 0)
            {
                return ServiceControlAttemptResult.Unknown(
                    "The service-control process returned unexpected output.");
            }
            return process.ExitCode == 0
                ? ServiceControlAttemptResult.Success()
                : ServiceControlAttemptResult.Unknown(
                    "The service-control process returned a nonzero exit code.");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            return ServiceControlAttemptResult.Unknown(
                "The service-control process exceeded its timeout.");
        }
        catch (InvalidDataException)
        {
            KillProcess(process);
            return ServiceControlAttemptResult.Unknown(
                "The service-control process exceeded its output bound.");
        }
        finally
        {
            if (!process.HasExited)
            {
                KillProcess(process);
            }
        }
    }

    private static bool ValidateAction(
        VerifiedReleaseActivationServiceControlAction action) =>
        action.Sequence > 0 &&
        action.Kind is VerifiedReleaseActivationServiceControlActionKind.Stop or
            VerifiedReleaseActivationServiceControlActionKind.Start &&
        action.ServiceRole is not null &&
        action.ServiceRole.Value switch
        {
            VerifiedReleaseActivationServiceRole.GatewayWeb => string.Equals(
                action.UnitIdentity,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .GatewayWebUnitIdentity,
                StringComparison.Ordinal),
            VerifiedReleaseActivationServiceRole.Broker => string.Equals(
                action.UnitIdentity,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .BrokerUnitIdentity,
                StringComparison.Ordinal),
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent =>
                string.Equals(
                    action.UnitIdentity,
                    VerifiedReleaseActivationServiceControlPlanComposer
                        .AetherRemoteAgentUnitIdentity,
                    StringComparison.Ordinal),
            VerifiedReleaseActivationServiceRole.StationEngine => string.Equals(
                action.UnitIdentity,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .StationEngineUnitIdentity,
                StringComparison.Ordinal),
            _ => false
        };

    private static void ConfigureProcessEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["LC_ALL"] = "C";
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[512];
        StringBuilder output = new();
        while (true)
        {
            int read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToString();
            }
            if (output.Length + read > maximumCharacters)
            {
                throw new InvalidDataException(
                    "Process output exceeded its configured bound.");
            }
            output.Append(buffer, 0, read);
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}

/// <summary>
/// Disabled-by-default, callerless execution of one exact service-control plan.
/// The pre-switch method stops only topology-owned local units while the exact
/// installed release remains active. The post-switch method requires that exact
/// pre-switch token and starts only the same topology-owned units while the exact
/// target release is active. Topology-declared absent local agents are no-ops;
/// required remote-node actions and host restart fail closed. A partial or unknown
/// outcome requires reconciliation and is never retried automatically. Success
/// retains one exact-plan in-memory observation. The boundary does not mutate the
/// current pointer, restart the host, roll back, authorize activation, operate a
/// radio, alter a lease or watchdog, send a radio command, or transmit.
/// </summary>
public sealed class VerifiedReleaseActivationServiceControlExecutionService
{
    internal static readonly TimeSpan UnitControlTimeout =
        TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;
    private readonly Func<CancellationToken, Task<InstallationSetupState>>
        m_setupReader;
    private readonly IVerifiedReleaseActivationServiceControlRuntime m_runtime;
    private readonly Func<
        VerifiedReleaseActivationServiceControlAction,
        VerifiedReleaseActivationPlan,
        VerifiedReleaseActivationServiceControlExecutionPhase,
        CancellationToken,
        Task<ServiceControlAttemptResult>> m_remoteRuntime;
    private readonly ReleaseActivationServiceControlSettings m_settings;
    private readonly TimeProvider m_timeProvider;
    private readonly SemaphoreSlim m_executionGate = new(1, 1);
    private readonly object m_stateGate = new();
    private VerifiedReleaseActivationServiceControlPreSwitchEvidence? m_preSwitch;
    private VerifiedReleaseActivationServiceControlEvidence? m_completed;
    private VerifiedReleaseActivationServiceControlPlan? m_reconciliationPlan;
    private ServiceControlPhaseTally? m_reconciliationTally;

    public VerifiedReleaseActivationServiceControlExecutionService(
        ReleaseInstallationStatusReader statusReader,
        InstallationSetupStore setupStore,
        IOptions<ReleaseActivationServiceControlSettings> settings,
        RemoteStationCatalogService remoteStations)
        : this(
            statusReader is null
                ? throw new ArgumentNullException(nameof(statusReader))
                : statusReader.ReadAsync,
            setupStore is null
                ? throw new ArgumentNullException(nameof(setupStore))
                : setupStore.LoadAsync,
            new LinuxVerifiedReleaseActivationServiceControlRuntime(),
            settings?.Value ?? throw new ArgumentNullException(nameof(settings)),
            TimeProvider.System,
            CreateRemoteRuntime(
                remoteStations,
                settings?.Value ?? throw new ArgumentNullException(nameof(settings))))
    {
    }

    internal VerifiedReleaseActivationServiceControlExecutionService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Func<CancellationToken, Task<InstallationSetupState>> setupReader,
        IVerifiedReleaseActivationServiceControlRuntime runtime,
        ReleaseActivationServiceControlSettings settings,
        TimeProvider timeProvider,
        Func<
            VerifiedReleaseActivationServiceControlAction,
            VerifiedReleaseActivationPlan,
            VerifiedReleaseActivationServiceControlExecutionPhase,
            CancellationToken,
            Task<ServiceControlAttemptResult>>? remoteRuntime = null)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_setupReader = setupReader ??
            throw new ArgumentNullException(nameof(setupReader));
        m_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        m_settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (m_settings.RemoteExecutionEnabled)
        {
            RemoteStationManagementValidator.ValidateStationId(
                m_settings.RemoteStationId);
        }
        else if (!string.IsNullOrEmpty(m_settings.RemoteStationId))
        {
            throw new InvalidOperationException(
                "A remote release-control station ID requires remote execution to be enabled.");
        }
        m_remoteRuntime = remoteRuntime ??
            ((_, _, _, _) => Task.FromResult(
                ServiceControlAttemptResult.NotStarted(
                    "Remote release service control is unavailable.")));
        m_timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));

        Snapshot = new VerifiedReleaseActivationServiceControlExecutionDiagnostics(
            Registered: true,
            ConfigurationRegistered: true,
            m_settings.ExecutionEnabled,
            ExecutionAvailable:
                m_settings.ExecutionEnabled && OperatingSystem.IsLinux(),
            ExactServiceControlPlanInputRegistered: true,
            ExactServiceControlPlanBindingRegistered: true,
            ExactActivationPlanBindingRegistered: true,
            ExactCurrentPointerSwitchEvidenceInputRegistered: true,
            ReleaseStatusDoubleReadRegistered: true,
            SetupStateDoubleReadRegistered: true,
            TopologyBindingRegistered: true,
            PreSwitchStopPhaseRegistered: true,
            PostSwitchStartPhaseRegistered: true,
            NoOpResolutionRegistered: true,
            DeterministicOrderingRegistered: true,
            FixedUnitMappingRegistered: true,
            DirectProcessRegistered: true,
            ShellRegistered: false,
            ClearedEnvironmentRegistered: true,
            UserUnitScopeRegistered: true,
            SystemUnitScopeRegistered: true,
            BoundedOutputRegistered: true,
            HardTimeoutRegistered: true,
            ProcessTreeTerminationRegistered: true,
            ExactPlanEvidenceRegistered: true,
            PartialFailureReconciliationRegistered: true,
            AutomaticRetryRegistered: false,
            HostRestartExecutionRegistered: false,
            RemoteServiceControlRegistered:
                m_settings.RemoteExecutionEnabled,
            CurrentPointerMutationRegistered: false,
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
            HealthProbeCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    private static Func<
        VerifiedReleaseActivationServiceControlAction,
        VerifiedReleaseActivationPlan,
        VerifiedReleaseActivationServiceControlExecutionPhase,
        CancellationToken,
        Task<ServiceControlAttemptResult>> CreateRemoteRuntime(
            RemoteStationCatalogService remoteStations,
            ReleaseActivationServiceControlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(remoteStations);
        ArgumentNullException.ThrowIfNull(settings);
        return async (action, activation, phase, cancellationToken) =>
        {
            if (!settings.RemoteExecutionEnabled)
            {
                return ServiceControlAttemptResult.NotStarted(
                    "Remote release service control is disabled.");
            }
            if (action.ServiceRole is not
                    (VerifiedReleaseActivationServiceRole.AetherRemoteAgent or
                     VerifiedReleaseActivationServiceRole.StationEngine))
            {
                return ServiceControlAttemptResult.NotStarted(
                    "The planned remote service role is unsupported.");
            }
            string role = action.ServiceRole ==
                VerifiedReleaseActivationServiceRole.AetherRemoteAgent
                ? "aetherremote-agent"
                : "station-engine";
            try
            {
                RemoteReleaseServiceControlResult result =
                    await remoteStations.ControlReleaseServiceAsync(
                        new RemoteReleaseServiceControlRequest(
                            settings.RemoteStationId,
                            activation.TargetReleaseIdentity,
                            phase ==
                                VerifiedReleaseActivationServiceControlExecutionPhase
                                    .PreSwitchStop
                                ? "pre-switch-stop"
                                : "post-switch-start",
                            action.Kind ==
                                VerifiedReleaseActivationServiceControlActionKind.Stop
                                ? "stop"
                                : "start",
                            role,
                            action.UnitIdentity),
                        cancellationToken);
                return result.Succeeded
                    ? ServiceControlAttemptResult.RemoteSuccess()
                    : ServiceControlAttemptResult.NotStarted(
                        "The remote station rejected the fixed service-control action.");
            }
            catch (Exception exception)
                when (exception is RemoteStationManagementException or
                    HttpRequestException or IOException or InvalidDataException or
                    OperationCanceledException)
            {
                return ServiceControlAttemptResult.RemoteUnknown(
                    "The remote release service-control outcome is unknown.");
            }
        };
    }

    public VerifiedReleaseActivationServiceControlExecutionDiagnostics Snapshot
    {
        get;
    }

    public VerifiedReleaseActivationServiceControlExecutionStateDiagnostics State
    {
        get
        {
            lock (m_stateGate)
            {
                VerifiedReleaseActivationServiceControlEvidence? completed =
                    m_completed;
                VerifiedReleaseActivationServiceControlPreSwitchEvidence? pre =
                    m_preSwitch;
                bool reconciliation = m_reconciliationPlan is not null;
                ServiceControlPhaseTally? reconciliationTally =
                    m_reconciliationTally;
                VerifiedReleaseActivationServiceControlPlan? plan =
                    completed?.Plan ?? pre?.Plan ?? m_reconciliationPlan;
                return new VerifiedReleaseActivationServiceControlExecutionStateDiagnostics(
                    ServiceControlReady: completed is not null,
                    ExactServiceControlPlanBound: plan is not null,
                    ExactActivationPlanBound: plan is not null,
                    PreSwitchStopComplete: completed is not null || pre is not null,
                    PostSwitchStartComplete: completed is not null,
                    PlannedStopActionCount: plan?.StopActions.Count ?? 0,
                    ExecutedStopActionCount:
                        completed?.ExecutedStopActionCount ??
                        pre?.ExecutedActionCount ??
                        (reconciliationTally?.Phase ==
                            VerifiedReleaseActivationServiceControlExecutionPhase
                                .PreSwitchStop
                            ? reconciliationTally.ExecutedActionCount
                            : 0),
                    TopologyNoOpStopActionCount:
                        completed?.TopologyNoOpStopActionCount ??
                        pre?.TopologyNoOpActionCount ??
                        (reconciliationTally?.Phase ==
                            VerifiedReleaseActivationServiceControlExecutionPhase
                                .PreSwitchStop
                            ? reconciliationTally.TopologyNoOpActionCount
                            : 0),
                    PlannedStartActionCount: plan?.StartActions.Count ?? 0,
                    ExecutedStartActionCount:
                        completed?.ExecutedStartActionCount ??
                        (reconciliationTally?.Phase ==
                            VerifiedReleaseActivationServiceControlExecutionPhase
                                .PostSwitchStart
                            ? reconciliationTally.ExecutedActionCount
                            : 0),
                    TopologyNoOpStartActionCount:
                        completed?.TopologyNoOpStartActionCount ??
                        (reconciliationTally?.Phase ==
                            VerifiedReleaseActivationServiceControlExecutionPhase
                                .PostSwitchStart
                            ? reconciliationTally.TopologyNoOpActionCount
                            : 0),
                    SetupStable: completed is not null || pre is not null,
                    TopologyStable: completed is not null || pre is not null,
                    InstalledReleaseActiveDuringStop:
                        completed is not null || pre is not null,
                    TargetReleaseActiveDuringStart: completed is not null,
                    ReconciliationRequired: reconciliation,
                    HostRestartPerformed: false,
                    CurrentPointerChanged: false,
                    RollbackPerformed: false,
                    ActivationAuthorized: false);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    internal Task<VerifiedReleaseActivationServiceControlExecutionReport>
        ExecutePreSwitchStopAsync(
            VerifiedReleaseActivationServiceControlPlanReport planReport,
            CancellationToken cancellationToken = default) =>
        ExecutePhaseAsync(
            VerifiedReleaseActivationServiceControlExecutionPhase.PreSwitchStop,
            planReport,
            pointerSwitchReport: null,
            cancellationToken);

    [SupportedOSPlatform("linux")]
    internal Task<VerifiedReleaseActivationServiceControlExecutionReport>
        ExecutePostSwitchStartAsync(
            VerifiedReleaseActivationServiceControlPlanReport planReport,
            VerifiedReleaseActivationCurrentPointerSwitchReport
                pointerSwitchReport,
            CancellationToken cancellationToken = default) =>
        ExecutePhaseAsync(
            VerifiedReleaseActivationServiceControlExecutionPhase.PostSwitchStart,
            planReport,
            pointerSwitchReport,
            cancellationToken);

    internal VerifiedReleaseActivationServiceControlObservation Observe(
        VerifiedReleaseActivationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ObserveCore(
            plan,
            serviceControlPlan =>
                ReferenceEquals(serviceControlPlan.ActivationPlan, plan));
    }

    internal VerifiedReleaseActivationServiceControlObservation ObservePlan(
        VerifiedReleaseActivationServiceControlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ObserveCore(
            plan.ActivationPlan,
            serviceControlPlan => ReferenceEquals(serviceControlPlan, plan));
    }

    internal VerifiedReleaseActivationServiceControlPreSwitchEvidence?
        GetPreSwitchEvidence(
            VerifiedReleaseActivationServiceControlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (m_stateGate)
        {
            return m_preSwitch is not null &&
                ReferenceEquals(m_preSwitch.Plan, plan) &&
                m_reconciliationPlan is null
                ? m_preSwitch
                : null;
        }
    }

    private VerifiedReleaseActivationServiceControlObservation ObserveCore(
        VerifiedReleaseActivationPlan activationPlan,
        Func<VerifiedReleaseActivationServiceControlPlan, bool> matchesPlan)
    {
        if (activationPlan.RestartServiceCount == 0 && !activationPlan.RestartHost)
        {
            return new VerifiedReleaseActivationServiceControlObservation(
                ServiceControlReady: true,
                ServiceControlRequired: false,
                PlannedStopActionCount: 0,
                ExecutedStopActionCount: 0,
                TopologyNoOpStopActionCount: 0,
                PlannedStartActionCount: 0,
                ExecutedStartActionCount: 0,
                TopologyNoOpStartActionCount: 0,
                CompletedAt: DateTimeOffset.UnixEpoch,
                ReconciliationRequired: false);
        }

        lock (m_stateGate)
        {
            if (m_completed is not null && matchesPlan(m_completed.Plan))
            {
                return new VerifiedReleaseActivationServiceControlObservation(
                    ServiceControlReady: true,
                    ServiceControlRequired: true,
                    m_completed.Plan.StopActions.Count,
                    m_completed.ExecutedStopActionCount,
                    m_completed.TopologyNoOpStopActionCount,
                    m_completed.Plan.StartActions.Count,
                    m_completed.ExecutedStartActionCount,
                    m_completed.TopologyNoOpStartActionCount,
                    m_completed.CompletedAt,
                    ReconciliationRequired: false);
            }
            bool reconciliation = m_reconciliationPlan is not null &&
                matchesPlan(m_reconciliationPlan);
            VerifiedReleaseActivationServiceControlPreSwitchEvidence? pre =
                m_preSwitch is not null && matchesPlan(m_preSwitch.Plan)
                    ? m_preSwitch
                    : null;
            return new VerifiedReleaseActivationServiceControlObservation(
                ServiceControlReady: false,
                ServiceControlRequired: true,
                PlannedStopActionCount: pre?.Plan.StopActions.Count ?? 0,
                ExecutedStopActionCount: pre?.ExecutedActionCount ?? 0,
                TopologyNoOpStopActionCount: pre?.TopologyNoOpActionCount ?? 0,
                PlannedStartActionCount: pre?.Plan.StartActions.Count ?? 0,
                ExecutedStartActionCount: 0,
                TopologyNoOpStartActionCount: 0,
                CompletedAt: null,
                ReconciliationRequired: reconciliation);
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task<VerifiedReleaseActivationServiceControlExecutionReport>
        ExecutePhaseAsync(
            VerifiedReleaseActivationServiceControlExecutionPhase phase,
            VerifiedReleaseActivationServiceControlPlanReport planReport,
            VerifiedReleaseActivationCurrentPointerSwitchReport?
                pointerSwitchReport,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planReport);
        cancellationToken.ThrowIfCancellationRequested();
        ServiceControlPhaseTally tally = new(phase);

        if (!m_settings.ExecutionEnabled)
        {
            return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                VerifiedReleaseActivationServiceControlExecutionFailureCode
                    .ExecutionDisabled,
                "Release service-control execution is disabled.",
                m_settings,
                phase,
                planReport);
        }
        if (!OperatingSystem.IsLinux())
        {
            return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                VerifiedReleaseActivationServiceControlExecutionFailureCode
                    .UnsupportedPlatform,
                "Release service-control execution requires Linux.",
                m_settings,
                phase,
                planReport);
        }

        VerifiedReleaseActivationServiceControlPlan? plan =
            ValidatePlanReport(planReport);
        if (plan is null)
        {
            return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                planReport.Plan is null
                    ? VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .ServiceControlPlanUnavailable
                    : VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .ServiceControlPlanNotEligible,
                "A successful exact non-executing service-control plan is required.",
                m_settings,
                phase,
                planReport);
        }
        if (!ValidatePlanShape(planReport, plan))
        {
            return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                VerifiedReleaseActivationServiceControlExecutionFailureCode
                    .ServiceControlPlanMismatch,
                "The service-control plan no longer matches its exact activation transaction.",
                m_settings,
                phase,
                planReport,
                exactPlanBound: true);
        }
        if (plan.HostRestartRequired)
        {
            return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                VerifiedReleaseActivationServiceControlExecutionFailureCode
                    .HostRestartUnsupported,
                "Host restart remains outside this service-control execution boundary.",
                m_settings,
                phase,
                planReport,
                exactPlanBound: true);
        }
        if (!plan.ServiceControlRequired)
        {
            if (phase !=
                VerifiedReleaseActivationServiceControlExecutionPhase.PreSwitchStop)
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .PhaseOrderInvalid,
                    "The exact no-op service-control plan resolves at the pre-switch phase.",
                    m_settings,
                    phase,
                    planReport,
                    exactPlanBound: true);
            }
            return VerifiedReleaseActivationServiceControlExecutionReport.Success(
                m_settings,
                phase,
                planReport,
                tally,
                preSwitchComplete: true,
                postSwitchComplete: true,
                serviceControlReady: true,
                noOp: true);
        }

        await m_executionGate.WaitAsync(cancellationToken);
        try
        {
            VerifiedReleaseActivationServiceControlExecutionReport? phaseFailure =
                ValidatePhaseState(phase, planReport, plan, tally);
            if (phaseFailure is not null)
            {
                return phaseFailure;
            }
            if (phase ==
                    VerifiedReleaseActivationServiceControlExecutionPhase
                        .PostSwitchStart &&
                (pointerSwitchReport is null ||
                 !VerifiedReleaseActivationCurrentPointerSwitchService
                    .ValidateEvidenceReport(pointerSwitchReport, plan)))
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .CurrentPointerSwitchUnavailable,
                    "The exact current-pointer switch evidence is required before the post-switch service-start phase.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    preSwitchComplete: true);
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
            catch (Exception exception) when (IsObservationException(exception))
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .ReleaseStatusUnavailable,
                    "Release or setup status could not be read before service control.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    preSwitchComplete: phase ==
                        VerifiedReleaseActivationServiceControlExecutionPhase
                            .PostSwitchStart);
            }

            bool preSwitch = phase ==
                VerifiedReleaseActivationServiceControlExecutionPhase.PreSwitchStop;
            if (!MatchesExpectedStatus(
                    beforeStatus,
                    plan.ActivationPlan,
                    targetActive: !preSwitch))
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    beforeStatus.Succeeded
                        ? VerifiedReleaseActivationServiceControlExecutionFailureCode
                            .ReleaseStatusMismatch
                        : VerifiedReleaseActivationServiceControlExecutionFailureCode
                            .ReleaseStatusUnavailable,
                    preSwitch
                        ? "The exact installed release is not the stable active release before the stop phase."
                        : "The exact target release is not the stable active release before the start phase.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    installedActiveBefore: preSwitch,
                    targetActiveBefore: !preSwitch,
                    preSwitchComplete: !preSwitch);
            }
            if (!TryBindSetup(
                    beforeSetup,
                    plan.ActivationPlan,
                    out SetupBinding? setupBinding))
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .SetupMismatch,
                    "Completed setup no longer matches the exact activation plan.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    installedActiveBefore: preSwitch,
                    targetActiveBefore: !preSwitch,
                    preSwitchComplete: !preSwitch);
            }
            SetupBinding boundSetup = setupBinding ??
                throw new InvalidOperationException(
                    "Validated setup binding was unexpectedly unavailable.");
            if (phase ==
                    VerifiedReleaseActivationServiceControlExecutionPhase
                        .PostSwitchStart &&
                !MatchesPreSwitchTopology(plan, boundSetup.Topology.Kind))
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .SetupMismatch,
                    "Installation topology changed between service-control phases.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    setupBound: true,
                    topologyBound: true,
                    targetActiveBefore: true,
                    preSwitchComplete: true);
            }

            IReadOnlyList<VerifiedReleaseActivationServiceControlAction> actions =
                preSwitch ? plan.StopActions : plan.StartActions;
            tally.PlannedActionCount = actions.Count;
            if (!TryClassifyActions(
                    actions,
                    boundSetup.Topology,
                    out ServiceControlBoundAction[] boundActions))
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .RemoteServiceControlUnavailable,
                    "The exact plan requires service control on a remote node, and no reviewed remote service-control transport is registered.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    setupBound: true,
                    topologyBound: true,
                    installedActiveBefore: preSwitch,
                    targetActiveBefore: !preSwitch,
                    preSwitchComplete: !preSwitch);
            }

            if (boundActions.Any(action => action.Remote) &&
                !m_settings.RemoteExecutionEnabled)
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .RemoteServiceControlUnavailable,
                    "The exact plan requires service control on a remote node, and the fixed remote release-control transport is disabled.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    setupBound: true,
                    topologyBound: true,
                    installedActiveBefore: preSwitch,
                    targetActiveBefore: !preSwitch,
                    preSwitchComplete: !preSwitch);
            }

            try
            {
                foreach (ServiceControlBoundAction boundAction in boundActions)
                {
                    if (boundAction.TopologyNoOp)
                    {
                        tally.TopologyNoOpActionCount++;
                        continue;
                    }
                    tally.ControlAttemptCount++;
                    ServiceControlAttemptResult result = boundAction.Remote
                        ? await m_remoteRuntime(
                            boundAction.Action,
                            plan.ActivationPlan,
                            phase,
                            cancellationToken)
                        : await m_runtime.ControlUnitAsync(
                            boundAction.Action,
                            UnitControlTimeout,
                            cancellationToken);
                    if (result.ProcessStarted)
                    {
                        tally.ProcessInvocationCount++;
                    }
                    if (!result.Succeeded)
                    {
                        bool reconciliation =
                            result.MutationAttempted ||
                            tally.ExecutedActionCount > 0;
                        if (reconciliation)
                        {
                            MarkReconciliation(plan, tally);
                        }
                        return VerifiedReleaseActivationServiceControlExecutionReport
                            .Failure(
                                VerifiedReleaseActivationServiceControlExecutionFailureCode
                                    .UnitControlFailed,
                                "A planned service-control action did not complete with a known successful outcome.",
                                m_settings,
                                phase,
                                planReport,
                                tally,
                                exactPlanBound: true,
                                setupBound: true,
                                topologyBound: true,
                                installedActiveBefore: preSwitch,
                                targetActiveBefore: !preSwitch,
                                preSwitchComplete: !preSwitch,
                                reconciliationRequired: reconciliation);
                    }
                    tally.ExecutedActionCount++;
                }
            }
            catch (OperationCanceledException)
            {
                if (tally.ControlAttemptCount > 0 ||
                    tally.ProcessInvocationCount > 0 ||
                    tally.ExecutedActionCount > 0)
                {
                    MarkReconciliation(plan, tally);
                }
                throw;
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
                MarkReconciliation(plan, tally);
                throw;
            }
            catch (Exception exception) when (IsObservationException(exception))
            {
                MarkReconciliation(plan, tally);
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .ReleaseStatusUnavailable,
                    "Release or setup status could not be read after service control.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    setupBound: true,
                    topologyBound: true,
                    installedActiveBefore: preSwitch,
                    targetActiveBefore: !preSwitch,
                    preSwitchComplete: !preSwitch,
                    reconciliationRequired: true);
            }

            if (!EquivalentStatus(beforeStatus, afterStatus) ||
                !TryBindSetup(
                    afterSetup,
                    plan.ActivationPlan,
                    out SetupBinding? afterBinding) ||
                boundSetup != afterBinding)
            {
                MarkReconciliation(plan, tally);
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .ObservationDrift,
                    "Release or setup status changed during service control.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    setupBound: true,
                    topologyBound: true,
                    installedActiveBefore: preSwitch,
                    targetActiveBefore: !preSwitch,
                    preSwitchComplete: !preSwitch,
                    reconciliationRequired: true);
            }

            DateTimeOffset completedAt = m_timeProvider.GetUtcNow();
            if (preSwitch)
            {
                lock (m_stateGate)
                {
                    m_preSwitch =
                        new VerifiedReleaseActivationServiceControlPreSwitchEvidence(
                            plan,
                            boundSetup.Topology.Kind,
                            tally.ExecutedActionCount,
                            tally.TopologyNoOpActionCount,
                            completedAt);
                }
                return VerifiedReleaseActivationServiceControlExecutionReport.Success(
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    preSwitchComplete: true,
                    postSwitchComplete: false,
                    serviceControlReady: false,
                    noOp: false);
            }

            VerifiedReleaseActivationServiceControlPreSwitchEvidence preEvidence;
            lock (m_stateGate)
            {
                preEvidence = m_preSwitch!;
                m_completed = new VerifiedReleaseActivationServiceControlEvidence(
                    plan,
                    boundSetup.Topology.Kind,
                    preEvidence.ExecutedActionCount,
                    preEvidence.TopologyNoOpActionCount,
                    tally.ExecutedActionCount,
                    tally.TopologyNoOpActionCount,
                    completedAt);
            }
            return VerifiedReleaseActivationServiceControlExecutionReport.Success(
                m_settings,
                phase,
                planReport,
                tally,
                preSwitchComplete: true,
                postSwitchComplete: true,
                serviceControlReady: true,
                noOp: false);
        }
        finally
        {
            m_executionGate.Release();
        }
    }

    private VerifiedReleaseActivationServiceControlExecutionReport?
        ValidatePhaseState(
            VerifiedReleaseActivationServiceControlExecutionPhase phase,
            VerifiedReleaseActivationServiceControlPlanReport planReport,
            VerifiedReleaseActivationServiceControlPlan plan,
            ServiceControlPhaseTally tally)
    {
        lock (m_stateGate)
        {
            if (m_reconciliationPlan is not null)
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .ReconciliationRequired,
                    "A prior service-control attempt requires reconciliation before any further action.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: ReferenceEquals(
                        m_reconciliationPlan,
                        plan),
                    preSwitchComplete: m_preSwitch is not null,
                    reconciliationRequired: true);
            }
            if (m_completed is not null)
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .PhaseAlreadyCompleted,
                    "The exact service-control transaction has already completed.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: ReferenceEquals(m_completed.Plan, plan),
                    setupBound: true,
                    topologyBound: true,
                    preSwitchComplete: true,
                    postSwitchComplete: true);
            }
            if (phase ==
                VerifiedReleaseActivationServiceControlExecutionPhase.PreSwitchStop)
            {
                if (m_preSwitch is not null)
                {
                    return VerifiedReleaseActivationServiceControlExecutionReport
                        .Failure(
                            VerifiedReleaseActivationServiceControlExecutionFailureCode
                                .PhaseAlreadyCompleted,
                            "The pre-switch service-stop phase has already completed.",
                            m_settings,
                            phase,
                            planReport,
                            tally,
                            exactPlanBound: ReferenceEquals(m_preSwitch.Plan, plan),
                            setupBound: true,
                            topologyBound: true,
                            preSwitchComplete: true);
                }
                return null;
            }
            if (m_preSwitch is null ||
                !ReferenceEquals(m_preSwitch.Plan, plan))
            {
                return VerifiedReleaseActivationServiceControlExecutionReport.Failure(
                    VerifiedReleaseActivationServiceControlExecutionFailureCode
                        .PhaseOrderInvalid,
                    "The exact pre-switch service-stop phase must complete before the post-switch start phase.",
                    m_settings,
                    phase,
                    planReport,
                    tally,
                    exactPlanBound: true);
            }
            return null;
        }
    }

    private bool MatchesPreSwitchTopology(
        VerifiedReleaseActivationServiceControlPlan plan,
        InstallationTopologyKind topology)
    {
        lock (m_stateGate)
        {
            return m_preSwitch is not null &&
                ReferenceEquals(m_preSwitch.Plan, plan) &&
                m_preSwitch.Topology == topology;
        }
    }

    private void MarkReconciliation(
        VerifiedReleaseActivationServiceControlPlan plan,
        ServiceControlPhaseTally tally)
    {
        lock (m_stateGate)
        {
            m_reconciliationPlan = plan;
            m_reconciliationTally = tally.Clone();
        }
    }

    private static VerifiedReleaseActivationServiceControlPlan?
        ValidatePlanReport(
            VerifiedReleaseActivationServiceControlPlanReport report)
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
        if (report.SetupRevision != activation.SetupRevision ||
            !string.Equals(
                report.InstalledReleaseIdentity,
                activation.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.TargetReleaseIdentity,
                activation.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            report.RestartServiceCount != activation.RestartServiceCount ||
            report.HostRestartRequired != activation.RestartHost ||
            report.ServiceControlRequired != plan.ServiceControlRequired ||
            report.NoOpServiceControlResolved != !plan.ServiceControlRequired ||
            report.StopActionCount != plan.StopActions.Count ||
            report.StartActionCount != plan.StartActions.Count ||
            report.HostRestartActionCount != plan.HostRestartActions.Count ||
            report.PreSwitchStopPlanned != (plan.StopActions.Count > 0) ||
            report.PostSwitchStartPlanned != (plan.StartActions.Count > 0) ||
            report.HostRestartPlanned != (plan.HostRestartActions.Count == 1) ||
            report.HostRestartSupersedesServiceActions !=
                (plan.HostRestartRequired &&
                 plan.StopActions.Count == 0 &&
                 plan.StartActions.Count == 0) ||
            report.ServiceControlReady != !plan.ServiceControlRequired ||
            !activation.AtomicCurrentPointerSwitchRequired ||
            !activation.ServiceHealthVerificationRequired ||
            !activation.AutomaticRollbackRequired ||
            !activation.OperatorApprovalRequired)
        {
            return false;
        }
        if (plan.HostRestartRequired)
        {
            return plan.StopActions.Count == 0 &&
                plan.StartActions.Count == 0 &&
                plan.HostRestartActions.Count == 1;
        }
        return ValidateActionList(
                plan.StopActions,
                VerifiedReleaseActivationServiceControlActionKind.Stop) &&
            ValidateActionList(
                plan.StartActions,
                VerifiedReleaseActivationServiceControlActionKind.Start) &&
            plan.HostRestartActions.Count == 0 &&
            plan.StopActions.Count == activation.RestartServiceCount &&
            plan.StartActions.Count == activation.RestartServiceCount;
    }

    private static bool ValidateActionList(
        IReadOnlyList<VerifiedReleaseActivationServiceControlAction> actions,
        VerifiedReleaseActivationServiceControlActionKind expectedKind)
    {
        for (int index = 0; index < actions.Count; index++)
        {
            VerifiedReleaseActivationServiceControlAction action = actions[index];
            if (action.Sequence != index + 1 ||
                action.Kind != expectedKind ||
                action.ServiceRole is null ||
                !IsExpectedUnit(action.ServiceRole.Value, action.UnitIdentity))
            {
                return false;
            }
        }
        return actions.Select(action => action.UnitIdentity)
            .Distinct(StringComparer.Ordinal).Count() == actions.Count;
    }

    private static bool IsExpectedUnit(
        VerifiedReleaseActivationServiceRole role,
        string unitIdentity) =>
        role switch
        {
            VerifiedReleaseActivationServiceRole.GatewayWeb => string.Equals(
                unitIdentity,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .GatewayWebUnitIdentity,
                StringComparison.Ordinal),
            VerifiedReleaseActivationServiceRole.Broker => string.Equals(
                unitIdentity,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .BrokerUnitIdentity,
                StringComparison.Ordinal),
            VerifiedReleaseActivationServiceRole.AetherRemoteAgent =>
                string.Equals(
                    unitIdentity,
                    VerifiedReleaseActivationServiceControlPlanComposer
                        .AetherRemoteAgentUnitIdentity,
                    StringComparison.Ordinal),
            VerifiedReleaseActivationServiceRole.StationEngine => string.Equals(
                unitIdentity,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .StationEngineUnitIdentity,
                StringComparison.Ordinal),
            _ => false
        };

    private static bool TryClassifyActions(
        IReadOnlyList<VerifiedReleaseActivationServiceControlAction> actions,
        InstallationTopologyProfile topology,
        out ServiceControlBoundAction[] boundActions)
    {
        List<ServiceControlBoundAction> result = [];
        foreach (VerifiedReleaseActivationServiceControlAction action in actions)
        {
            VerifiedReleaseActivationServiceRole role = action.ServiceRole!.Value;
            bool local = role switch
            {
                VerifiedReleaseActivationServiceRole.GatewayWeb =>
                    topology.GatewayRunsHere,
                VerifiedReleaseActivationServiceRole.Broker =>
                    topology.BrokerRunsHere,
                VerifiedReleaseActivationServiceRole.AetherRemoteAgent =>
                    topology.AgentRunsHere,
                VerifiedReleaseActivationServiceRole.StationEngine =>
                    topology.StationEngineRunsHere,
                _ => false
            };
            if (local)
            {
                result.Add(
                    new ServiceControlBoundAction(
                        action,
                        TopologyNoOp: false,
                        Remote: false));
                continue;
            }
            if (role == VerifiedReleaseActivationServiceRole.AetherRemoteAgent &&
                !topology.AcceptsRemoteStations)
            {
                result.Add(
                    new ServiceControlBoundAction(
                        action,
                        TopologyNoOp: true,
                        Remote: false));
                continue;
            }
            if (topology.AcceptsRemoteStations &&
                role is VerifiedReleaseActivationServiceRole.AetherRemoteAgent or
                    VerifiedReleaseActivationServiceRole.StationEngine)
            {
                result.Add(
                    new ServiceControlBoundAction(
                        action,
                        TopologyNoOp: false,
                        Remote: true));
                continue;
            }
            boundActions = [];
            return false;
        }
        boundActions = result.ToArray();
        return true;
    }

    private static bool TryBindSetup(
        InstallationSetupState state,
        VerifiedReleaseActivationPlan activation,
        out SetupBinding? binding)
    {
        binding = null;
        try
        {
            InstallationSetupStateValidator.Validate(state);
            if (state.Revision != activation.SetupRevision ||
                state.Lock.Mode != InstallationSetupLockMode.Complete ||
                state.LastCompletedStep != InstallationSetupStep.Administrator ||
                state.Paths is null ||
                state.Topology is null ||
                state.UpdateChannel != activation.UpdateChannel ||
                !string.Equals(
                    state.PinnedRelease,
                    activation.PinnedReleaseIdentity,
                    StringComparison.Ordinal) ||
                state.InstallTransmitSupport != activation.InstallTransmitSupport ||
                !string.Equals(
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(state.Paths.ReleaseDirectory)),
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(activation.ReleaseRootPath)),
                    StringComparison.Ordinal))
            {
                return false;
            }
            InstallationTopologyProfile topology =
                InstallationTopologyProfile.For(state.Topology.Value);
            if (!topology.GatewayRunsHere || !topology.BrokerRunsHere)
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
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(state.Paths.ReleaseDirectory)),
                topology);
            return true;
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
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
        if (!string.Equals(
                status.ActiveReleaseIdentity,
                expectedActive,
                StringComparison.Ordinal) ||
            status.AvailableReleaseIdentities
                .Distinct(StringComparer.Ordinal).Count() !=
                status.AvailableReleaseIdentities.Count)
        {
            return false;
        }
        return status.AvailableReleaseIdentities.Contains(
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

    private static bool IsObservationException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException or
            FileNotFoundException or PathTooLongException;

    private sealed record SetupBinding(
        int SchemaVersion,
        long Revision,
        DateTimeOffset UpdatedAt,
        InstallationUpdateChannel UpdateChannel,
        string PinnedRelease,
        bool InstallTransmitSupport,
        string ReleaseDirectory,
        InstallationTopologyProfile Topology);

    private sealed record ServiceControlBoundAction(
        VerifiedReleaseActivationServiceControlAction Action,
        bool TopologyNoOp,
        bool Remote);
}

internal sealed class ServiceControlPhaseTally
{
    internal ServiceControlPhaseTally(
        VerifiedReleaseActivationServiceControlExecutionPhase phase)
    {
        Phase = phase;
    }

    internal VerifiedReleaseActivationServiceControlExecutionPhase Phase { get; }
    internal int PlannedActionCount { get; set; }
    internal int ControlAttemptCount { get; set; }
    internal int ExecutedActionCount { get; set; }
    internal int TopologyNoOpActionCount { get; set; }
    internal int ProcessInvocationCount { get; set; }

    internal ServiceControlPhaseTally Clone() =>
        new(Phase)
        {
            PlannedActionCount = PlannedActionCount,
            ControlAttemptCount = ControlAttemptCount,
            ExecutedActionCount = ExecutedActionCount,
            TopologyNoOpActionCount = TopologyNoOpActionCount,
            ProcessInvocationCount = ProcessInvocationCount
        };
}
