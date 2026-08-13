using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public sealed class ReleaseActivationHealthVerificationSettings
{
    public const string SectionName = "ReleaseActivationHealthVerification";

    public bool ExecutionEnabled { get; init; }
    public string ExpectedStationId { get; init; } = string.Empty;
}

public enum VerifiedReleaseActivationHealthVerificationFailureCode
{
    None = 0,
    ExecutionDisabled = 1,
    UnsupportedPlatform = 2,
    HealthPlanNotEligible = 3,
    HealthPlanUnavailable = 4,
    HealthPlanMismatch = 5,
    StatusUnavailable = 6,
    StatusMismatch = 7,
    SetupUnavailable = 8,
    SetupMismatch = 9,
    UnitActivityUnavailable = 10,
    LoopbackHealthUnavailable = 11,
    BrokerLinkUnavailable = 12,
    ObservationDrift = 13,
    HealthAlreadyVerified = 14,
    UnsupportedTopology = 15,
    StationIdentityMismatch = 16,
    ServiceControlUnavailable = 17,
    CurrentPointerSwitchUnavailable = 18
}

public sealed record VerifiedReleaseActivationHealthVerificationReport(
    bool Succeeded,
    VerifiedReleaseActivationHealthVerificationFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    bool ExecutionEnabled,
    bool ExecutionAvailable,
    int HealthTargetCount,
    int VerifiedTargetCount,
    int UnitActivityAttemptCount,
    int LoopbackHttpAttemptCount,
    int BrokerLinkObservationCount,
    bool ExactHealthPlanBound,
    bool ExactActivationPlanBound,
    bool TargetActiveBeforeVerification,
    bool TargetActiveAfterVerification,
    bool SetupStable,
    bool CanonicalGatewayHostBound,
    bool AllUnitsActive,
    bool AllHealthContractsPassed,
    bool NetworkRequestPerformed,
    bool ProcessInvocationPerformed,
    bool SystemdCommandPerformed,
    bool JournalReadPerformed,
    bool RemoteStationSnapshotRead,
    bool HealthEvidenceProduced,
    bool ServiceHealthReady,
    bool ServiceControlReady,
    bool CurrentPointerChanged,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationHealthVerificationEvidence? Evidence
    {
        get;
        init;
    }

    internal VerifiedReleaseActivationHealthVerificationPlan? FailedPlan
    {
        get;
        init;
    }

    internal static VerifiedReleaseActivationHealthVerificationReport Failure(
        VerifiedReleaseActivationHealthVerificationFailureCode failureCode,
        string message,
        ReleaseActivationHealthVerificationSettings settings,
        VerifiedReleaseActivationHealthVerificationPlanReport? planReport = null,
        HealthProbeTally? tally = null,
        bool exactPlanBound = false,
        bool targetActiveBefore = false,
        bool targetActiveAfter = false,
        bool setupStable = false,
        bool canonicalHostBound = false,
        int verifiedTargetCount = 0) =>
        new(
            false,
            failureCode,
            message,
            planReport?.SetupRevision,
            planReport?.InstalledReleaseIdentity ?? string.Empty,
            planReport?.TargetReleaseIdentity ?? string.Empty,
            settings.ExecutionEnabled,
            settings.ExecutionEnabled && OperatingSystem.IsLinux(),
            planReport?.HealthTargetCount ?? 0,
            verifiedTargetCount,
            tally?.UnitActivityAttemptCount ?? 0,
            tally?.LoopbackHttpAttemptCount ?? 0,
            tally?.BrokerLinkObservationCount ?? 0,
            ExactHealthPlanBound: exactPlanBound,
            ExactActivationPlanBound: exactPlanBound,
            TargetActiveBeforeVerification: targetActiveBefore,
            TargetActiveAfterVerification: targetActiveAfter,
            SetupStable: setupStable,
            CanonicalGatewayHostBound: canonicalHostBound,
            AllUnitsActive: false,
            AllHealthContractsPassed: false,
            NetworkRequestPerformed:
                (tally?.LoopbackHttpAttemptCount ?? 0) > 0,
            ProcessInvocationPerformed:
                (tally?.UnitActivityAttemptCount ?? 0) > 0,
            SystemdCommandPerformed:
                (tally?.UnitActivityAttemptCount ?? 0) > 0,
            JournalReadPerformed: false,
            RemoteStationSnapshotRead:
                (tally?.BrokerLinkObservationCount ?? 0) > 0,
            HealthEvidenceProduced: false,
            ServiceHealthReady: false,
            ServiceControlReady: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            FailedPlan = exactPlanBound ? planReport?.Plan : null
        };

    internal static VerifiedReleaseActivationHealthVerificationReport Success(
        ReleaseActivationHealthVerificationSettings settings,
        VerifiedReleaseActivationHealthVerificationPlanReport planReport,
        VerifiedReleaseActivationHealthVerificationEvidence evidence,
        HealthProbeTally tally) =>
        new(
            true,
            VerifiedReleaseActivationHealthVerificationFailureCode.None,
            "The exact active release passed the bounded service-health verification sequence; one in-memory exact-plan health observation was retained without changing release, service, radio, lease, or transmit state.",
            evidence.Plan.ActivationPlan.SetupRevision,
            evidence.Plan.ActivationPlan.InstalledReleaseIdentity,
            evidence.Plan.ActivationPlan.TargetReleaseIdentity,
            settings.ExecutionEnabled,
            ExecutionAvailable: true,
            evidence.Plan.Targets.Count,
            evidence.VerifiedTargetCount,
            tally.UnitActivityAttemptCount,
            tally.LoopbackHttpAttemptCount,
            tally.BrokerLinkObservationCount,
            ExactHealthPlanBound: true,
            ExactActivationPlanBound: true,
            TargetActiveBeforeVerification: true,
            TargetActiveAfterVerification: true,
            SetupStable: true,
            CanonicalGatewayHostBound: true,
            AllUnitsActive: true,
            AllHealthContractsPassed: true,
            NetworkRequestPerformed: tally.LoopbackHttpAttemptCount > 0,
            ProcessInvocationPerformed: tally.UnitActivityAttemptCount > 0,
            SystemdCommandPerformed: tally.UnitActivityAttemptCount > 0,
            JournalReadPerformed: false,
            RemoteStationSnapshotRead:
                tally.BrokerLinkObservationCount > 0,
            HealthEvidenceProduced: true,
            ServiceHealthReady: true,
            ServiceControlReady: true,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            Evidence = evidence
        };
}

public sealed record VerifiedReleaseActivationHealthVerificationDiagnostics(
    bool Registered,
    bool ConfigurationRegistered,
    bool ExecutionEnabled,
    bool ExecutionAvailable,
    bool ExpectedStationIdentityConfigured,
    bool ExactHealthPlanInputRegistered,
    bool ExactHealthPlanBindingRegistered,
    bool ExactActivationPlanBindingRegistered,
    bool CurrentPointerSwitchEvidenceInputRegistered,
    bool ServiceControlEvidenceInputRegistered,
    bool ReleaseStatusDoubleReadRegistered,
    bool SetupStateDoubleReadRegistered,
    bool TopologyBindingRegistered,
    bool TargetActiveRequirementRegistered,
    bool CanonicalGatewayHostBindingRegistered,
    bool UnitActivityProcessRegistered,
    bool DirectProcessRegistered,
    bool ShellRegistered,
    bool ClearedEnvironmentRegistered,
    bool LoopbackHttpRegistered,
    bool ProxyBypassRegistered,
    bool RedirectRejectionRegistered,
    bool BoundedHttpBodyRegistered,
    bool FreshBrokerSnapshotRegistered,
    bool ExactStationIdentityRegistered,
    bool BoundedDeadlineRegistered,
    bool DeterministicOrderingRegistered,
    bool ExactPlanEvidenceRegistered,
    bool JournalReadRegistered,
    bool CredentialReadRegistered,
    bool ServiceControlRegistered,
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
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record VerifiedReleaseActivationHealthVerificationStateDiagnostics(
    bool HealthVerificationReady,
    bool ExactHealthPlanBound,
    bool ExactActivationPlanBound,
    int HealthTargetCount,
    int VerifiedTargetCount,
    int UnitActivityCheckCount,
    int LoopbackHttpCheckCount,
    int FreshBrokerLinkCheckCount,
    bool TargetActiveBeforeVerification,
    bool TargetActiveAfterVerification,
    bool SetupStable,
    bool CanonicalGatewayHostBound,
    bool AllUnitsActive,
    bool AllHealthContractsPassed,
    bool ReconciliationRequired,
    bool ServiceControlReady,
    bool CurrentPointerChanged,
    bool ActivationAuthorized);

internal sealed record VerifiedReleaseActivationHealthVerificationObservation(
    bool HealthVerificationReady,
    int HealthTargetCount,
    int VerifiedTargetCount,
    int UnitActivityCheckCount,
    int LoopbackHttpCheckCount,
    int FreshBrokerLinkCheckCount,
    DateTimeOffset? CompletedAt,
    bool ReconciliationRequired);

internal sealed class VerifiedReleaseActivationHealthVerificationEvidence
{
    internal VerifiedReleaseActivationHealthVerificationEvidence(
        VerifiedReleaseActivationHealthVerificationPlan plan,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        int verifiedTargetCount,
        int unitActivityCheckCount,
        int loopbackHttpCheckCount,
        int freshBrokerLinkCheckCount)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (startedAt == default || completedAt < startedAt)
        {
            throw new InvalidOperationException(
                "Health-verification evidence timestamps are invalid.");
        }
        StartedAt = startedAt;
        CompletedAt = completedAt;
        VerifiedTargetCount = verifiedTargetCount;
        UnitActivityCheckCount = unitActivityCheckCount;
        LoopbackHttpCheckCount = loopbackHttpCheckCount;
        FreshBrokerLinkCheckCount = freshBrokerLinkCheckCount;
    }

    internal VerifiedReleaseActivationHealthVerificationPlan Plan { get; }
    internal DateTimeOffset StartedAt { get; }
    internal DateTimeOffset CompletedAt { get; }
    internal int VerifiedTargetCount { get; }
    internal int UnitActivityCheckCount { get; }
    internal int LoopbackHttpCheckCount { get; }
    internal int FreshBrokerLinkCheckCount { get; }
}

internal sealed class HealthProbeTally
{
    internal int UnitActivityAttemptCount { get; set; }
    internal int LoopbackHttpAttemptCount { get; set; }
    internal int BrokerLinkObservationCount { get; set; }
}

internal sealed record HealthProbeAttemptResult(
    bool Succeeded,
    bool Retryable,
    string Reason)
{
    internal static HealthProbeAttemptResult Success() =>
        new(true, Retryable: false, string.Empty);

    internal static HealthProbeAttemptResult Retry(string reason) =>
        new(false, Retryable: true, reason);

    internal static HealthProbeAttemptResult Reject(string reason) =>
        new(false, Retryable: false, reason);
}

internal interface IVerifiedReleaseActivationHealthProbeRuntime
{
    Task<HealthProbeAttemptResult> CheckUnitActiveAsync(
        string unitIdentity,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<HealthProbeAttemptResult> CheckLoopbackHealthAsync(
        VerifiedReleaseActivationHealthVerificationTarget target,
        string canonicalGatewayAuthority,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class LinuxVerifiedReleaseActivationHealthProbeRuntime :
    IVerifiedReleaseActivationHealthProbeRuntime
{
    internal const string SystemctlPath = "/usr/bin/systemctl";
    internal const int MaximumProcessOutputCharacters = 4096;
    internal const int MaximumHealthResponseBytes = 256 * 1024;

    private static readonly HttpClient DefaultLoopbackClient =
        CreateLoopbackClient();

    private readonly string m_systemctlPath;
    private readonly HttpClient m_loopbackClient;

    internal LinuxVerifiedReleaseActivationHealthProbeRuntime()
        : this(SystemctlPath, DefaultLoopbackClient)
    {
    }

    internal LinuxVerifiedReleaseActivationHealthProbeRuntime(
        string systemctlPath,
        HttpClient loopbackClient)
    {
        if (string.IsNullOrWhiteSpace(systemctlPath) ||
            !Path.IsPathRooted(systemctlPath))
        {
            throw new InvalidOperationException(
                "The systemctl probe path must be absolute.");
        }
        m_systemctlPath = Path.GetFullPath(systemctlPath);
        m_loopbackClient = loopbackClient ??
            throw new ArgumentNullException(nameof(loopbackClient));
    }

    public async Task<HealthProbeAttemptResult> CheckUnitActiveAsync(
        string unitIdentity,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IsExpectedUnitIdentity(unitIdentity) ||
            timeout <= TimeSpan.Zero)
        {
            return HealthProbeAttemptResult.Reject(
                "The planned unit-activity check is invalid.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = m_systemctlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("is-active");
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add(unitIdentity);
        ConfigureProcessEnvironment(startInfo);

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return HealthProbeAttemptResult.Retry(
                    "The unit-activity process could not start.");
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or IOException)
        {
            return HealthProbeAttemptResult.Retry(
                "The unit-activity process is unavailable.");
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
                return HealthProbeAttemptResult.Retry(
                    "The unit-activity process returned unexpected output.");
            }
            return process.ExitCode == 0
                ? HealthProbeAttemptResult.Success()
                : HealthProbeAttemptResult.Retry(
                    "The planned unit is not active.");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            return HealthProbeAttemptResult.Retry(
                "The unit-activity process exceeded its timeout.");
        }
        catch (InvalidDataException)
        {
            KillProcess(process);
            return HealthProbeAttemptResult.Reject(
                "The unit-activity process exceeded its output bound.");
        }
        finally
        {
            if (!process.HasExited)
            {
                KillProcess(process);
            }
        }
    }

    public async Task<HealthProbeAttemptResult> CheckLoopbackHealthAsync(
        VerifiedReleaseActivationHealthVerificationTarget target,
        string canonicalGatewayAuthority,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!ValidateLoopbackTarget(target) || timeout <= TimeSpan.Zero)
        {
            return HealthProbeAttemptResult.Reject(
                "The planned loopback health check is invalid.");
        }
        if (target.RequireCanonicalHostHeader &&
            string.IsNullOrEmpty(canonicalGatewayAuthority))
        {
            return HealthProbeAttemptResult.Reject(
                "The gateway health check requires its canonical host binding.");
        }

        Uri uri = new(
            $"http://127.0.0.1:{target.LoopbackPort!.Value}" +
            target.HealthPath,
            UriKind.Absolute);
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.ConnectionClose = true;
        if (target.RequireCanonicalHostHeader)
        {
            request.Headers.Host = canonicalGatewayAuthority;
        }

        using CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await m_loopbackClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operation.Token);
            if ((int)response.StatusCode != target.ExpectedHttpStatusCode)
            {
                return HealthProbeAttemptResult.Retry(
                    "The loopback health endpoint returned an unexpected status.");
            }
            byte[] body = await ReadBoundedContentAsync(
                response.Content,
                operation.Token);
            return IsOkHealthDocument(body)
                ? HealthProbeAttemptResult.Success()
                : HealthProbeAttemptResult.Retry(
                    "The loopback health endpoint returned an invalid body.");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return HealthProbeAttemptResult.Retry(
                "The loopback health request exceeded its timeout.");
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException or
                InvalidDataException or JsonException or NotSupportedException)
        {
            return HealthProbeAttemptResult.Retry(
                "The loopback health endpoint is unavailable.");
        }
    }

    private static HttpClient CreateLoopbackClient()
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = 32,
            PooledConnectionLifetime = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(2)
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaximumHealthResponseBytes
        };
    }

    private static async Task<byte[]> ReadBoundedContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumHealthResponseBytes)
        {
            throw new InvalidDataException(
                "The loopback health response exceeds its size bound.");
        }
        await using Stream stream =
            await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > MaximumHealthResponseBytes)
            {
                throw new InvalidDataException(
                    "The loopback health response exceeds its size bound.");
            }
            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
    }

    private static bool IsOkHealthDocument(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }
        using JsonDocument document = JsonDocument.Parse(
            body,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        int statusCount = 0;
        bool statusOk = false;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!property.NameEquals("status"))
            {
                continue;
            }
            statusCount++;
            statusOk = property.Value.ValueKind == JsonValueKind.String &&
                string.Equals(
                    property.Value.GetString(),
                    "ok",
                    StringComparison.Ordinal);
        }
        return statusCount == 1 && statusOk;
    }

    private static bool ValidateLoopbackTarget(
        VerifiedReleaseActivationHealthVerificationTarget target) =>
        target.ContractKind ==
            VerifiedReleaseActivationHealthContractKind.LoopbackHttp &&
        target.LoopbackPort is
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .GatewayWebLoopbackPort or
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .BrokerLoopbackPort or
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .StationEngineLoopbackPort &&
        string.Equals(
            target.HealthPath,
            VerifiedReleaseActivationHealthVerificationPlanComposer.HealthPath,
            StringComparison.Ordinal) &&
        target.ExpectedHttpStatusCode ==
            VerifiedReleaseActivationHealthVerificationPlanComposer
                .ExpectedHttpStatusCode;

    private static bool IsExpectedUnitIdentity(string unitIdentity) =>
        unitIdentity is
            VerifiedReleaseActivationServiceControlPlanComposer
                .GatewayWebUnitIdentity or
            VerifiedReleaseActivationServiceControlPlanComposer
                .BrokerUnitIdentity or
            VerifiedReleaseActivationServiceControlPlanComposer
                .AetherRemoteAgentUnitIdentity or
            VerifiedReleaseActivationServiceControlPlanComposer
                .StationEngineUnitIdentity;

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
/// Disabled-by-default, callerless execution of one exact post-switch health
/// plan. The boundary requires the exact target release to be active before and
/// after verification and binds persisted topology plus canonical gateway
/// authority. It directly checks only locally owned units and loopback endpoints;
/// a hybrid remote agent is observed through one exact broker station identity,
/// a topology-declared absent agent is a no-op, and a remote station engine fails
/// closed until a reviewed remote probe transport exists. Success retains one
/// exact-plan in-memory health observation. It does not control a service, read a
/// credential or journal, mutate current, roll back, authorize activation,
/// operate a radio, alter a lease or watchdog, send a command, or transmit.
/// </summary>
public sealed class VerifiedReleaseActivationHealthVerificationService
{
    internal static readonly TimeSpan ProbeAttemptTimeout =
        TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);
    internal const int MaximumAttemptsPerTarget = 256;

    private readonly Func<CancellationToken, Task<ReleaseStatusReadResult>>
        m_statusReader;
    private readonly Func<CancellationToken, Task<InstallationSetupState>>
        m_setupReader;
    private readonly Func<RemoteStationAdministrationSnapshot>
        m_remoteStationSnapshotReader;
    private readonly Func<
        VerifiedReleaseActivationPlan,
        VerifiedReleaseActivationCurrentPointerSwitchObservation>
        m_pointerSwitchReader;
    private readonly Func<
        VerifiedReleaseActivationServiceControlPlan,
        VerifiedReleaseActivationServiceControlObservation>
        m_serviceControlReader;
    private readonly IVerifiedReleaseActivationHealthProbeRuntime m_runtime;
    private readonly ReleaseActivationHealthVerificationSettings m_settings;
    private readonly TimeProvider m_timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> m_delay;
    private readonly SemaphoreSlim m_executionGate = new(1, 1);
    private readonly object m_stateGate = new();
    private VerifiedReleaseActivationHealthVerificationEvidence? m_completed;

    public VerifiedReleaseActivationHealthVerificationService(
        ReleaseInstallationStatusReader statusReader,
        InstallationSetupStore setupStore,
        RemoteStationCatalogService remoteStations,
        VerifiedReleaseActivationCurrentPointerSwitchService pointerSwitch,
        VerifiedReleaseActivationServiceControlExecutionService serviceControl,
        IOptions<ReleaseActivationHealthVerificationSettings> settings)
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
            pointerSwitch is null
                ? throw new ArgumentNullException(nameof(pointerSwitch))
                : pointerSwitch.Observe,
            serviceControl is null
                ? throw new ArgumentNullException(nameof(serviceControl))
                : serviceControl.ObservePlan,
            new LinuxVerifiedReleaseActivationHealthProbeRuntime(),
            settings?.Value ??
                throw new ArgumentNullException(nameof(settings)),
            TimeProvider.System)
    {
    }

    internal VerifiedReleaseActivationHealthVerificationService(
        Func<CancellationToken, Task<ReleaseStatusReadResult>> statusReader,
        Func<CancellationToken, Task<InstallationSetupState>> setupReader,
        Func<RemoteStationAdministrationSnapshot> remoteStationSnapshotReader,
        Func<
            VerifiedReleaseActivationPlan,
            VerifiedReleaseActivationCurrentPointerSwitchObservation>
            pointerSwitchReader,
        Func<
            VerifiedReleaseActivationServiceControlPlan,
            VerifiedReleaseActivationServiceControlObservation>
            serviceControlReader,
        IVerifiedReleaseActivationHealthProbeRuntime runtime,
        ReleaseActivationHealthVerificationSettings settings,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        m_statusReader = statusReader ??
            throw new ArgumentNullException(nameof(statusReader));
        m_setupReader = setupReader ??
            throw new ArgumentNullException(nameof(setupReader));
        m_remoteStationSnapshotReader = remoteStationSnapshotReader ??
            throw new ArgumentNullException(nameof(remoteStationSnapshotReader));
        m_pointerSwitchReader = pointerSwitchReader ??
            throw new ArgumentNullException(nameof(pointerSwitchReader));
        m_serviceControlReader = serviceControlReader ??
            throw new ArgumentNullException(nameof(serviceControlReader));
        m_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        m_settings = ValidateSettings(settings);
        m_timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        m_delay = delay ?? ((duration, token) => Task.Delay(duration, token));

        Snapshot = new VerifiedReleaseActivationHealthVerificationDiagnostics(
            Registered: true,
            ConfigurationRegistered: true,
            m_settings.ExecutionEnabled,
            ExecutionAvailable:
                m_settings.ExecutionEnabled && OperatingSystem.IsLinux(),
            ExpectedStationIdentityConfigured:
                !string.IsNullOrEmpty(m_settings.ExpectedStationId),
            ExactHealthPlanInputRegistered: true,
            ExactHealthPlanBindingRegistered: true,
            ExactActivationPlanBindingRegistered: true,
            CurrentPointerSwitchEvidenceInputRegistered: true,
            ServiceControlEvidenceInputRegistered: true,
            ReleaseStatusDoubleReadRegistered: true,
            SetupStateDoubleReadRegistered: true,
            TopologyBindingRegistered: true,
            TargetActiveRequirementRegistered: true,
            CanonicalGatewayHostBindingRegistered: true,
            UnitActivityProcessRegistered: true,
            DirectProcessRegistered: true,
            ShellRegistered: false,
            ClearedEnvironmentRegistered: true,
            LoopbackHttpRegistered: true,
            ProxyBypassRegistered: true,
            RedirectRejectionRegistered: true,
            BoundedHttpBodyRegistered: true,
            FreshBrokerSnapshotRegistered: true,
            ExactStationIdentityRegistered: true,
            BoundedDeadlineRegistered: true,
            DeterministicOrderingRegistered: true,
            ExactPlanEvidenceRegistered: true,
            JournalReadRegistered: false,
            CredentialReadRegistered: false,
            ServiceControlRegistered: false,
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
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationHealthVerificationDiagnostics Snapshot
    {
        get;
    }

    public VerifiedReleaseActivationHealthVerificationStateDiagnostics State
    {
        get
        {
            lock (m_stateGate)
            {
                VerifiedReleaseActivationHealthVerificationEvidence? completed =
                    m_completed;
                return new VerifiedReleaseActivationHealthVerificationStateDiagnostics(
                    HealthVerificationReady: completed is not null,
                    ExactHealthPlanBound: completed is not null,
                    ExactActivationPlanBound: completed is not null,
                    HealthTargetCount: completed?.Plan.Targets.Count ?? 0,
                    VerifiedTargetCount: completed?.VerifiedTargetCount ?? 0,
                    UnitActivityCheckCount:
                        completed?.UnitActivityCheckCount ?? 0,
                    LoopbackHttpCheckCount:
                        completed?.LoopbackHttpCheckCount ?? 0,
                    FreshBrokerLinkCheckCount:
                        completed?.FreshBrokerLinkCheckCount ?? 0,
                    TargetActiveBeforeVerification: completed is not null,
                    TargetActiveAfterVerification: completed is not null,
                    SetupStable: completed is not null,
                    CanonicalGatewayHostBound: completed is not null,
                    AllUnitsActive: completed is not null,
                    AllHealthContractsPassed: completed is not null,
                    ReconciliationRequired: false,
                    ServiceControlReady: completed is not null,
                    CurrentPointerChanged: false,
                    ActivationAuthorized: false);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    internal async Task<VerifiedReleaseActivationHealthVerificationReport>
        ExecuteAsync(
            VerifiedReleaseActivationHealthVerificationPlanReport planReport,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planReport);
        cancellationToken.ThrowIfCancellationRequested();
        HealthProbeTally tally = new();

        if (!m_settings.ExecutionEnabled)
        {
            return VerifiedReleaseActivationHealthVerificationReport.Failure(
                VerifiedReleaseActivationHealthVerificationFailureCode
                    .ExecutionDisabled,
                "Release health-verification execution is disabled.",
                m_settings,
                planReport);
        }
        if (!OperatingSystem.IsLinux())
        {
            return VerifiedReleaseActivationHealthVerificationReport.Failure(
                VerifiedReleaseActivationHealthVerificationFailureCode
                    .UnsupportedPlatform,
                "Release health-verification execution requires Linux.",
                m_settings,
                planReport);
        }

        VerifiedReleaseActivationHealthVerificationPlan? plan =
            ValidatePlanReport(planReport);
        if (plan is null)
        {
            return VerifiedReleaseActivationHealthVerificationReport.Failure(
                planReport.Plan is null
                    ? VerifiedReleaseActivationHealthVerificationFailureCode
                        .HealthPlanUnavailable
                    : VerifiedReleaseActivationHealthVerificationFailureCode
                        .HealthPlanNotEligible,
                "A successful exact non-executing health-verification plan is required.",
                m_settings,
                planReport);
        }
        if (!ValidatePlanShape(planReport, plan))
        {
            return VerifiedReleaseActivationHealthVerificationReport.Failure(
                VerifiedReleaseActivationHealthVerificationFailureCode
                    .HealthPlanMismatch,
                "The health-verification plan no longer matches its exact activation transaction.",
                m_settings,
                planReport);
        }
        VerifiedReleaseActivationCurrentPointerSwitchObservation pointerSwitch =
            m_pointerSwitchReader(plan.ActivationPlan);
        if (!ValidatePointerSwitchObservation(pointerSwitch))
        {
            return VerifiedReleaseActivationHealthVerificationReport.Failure(
                VerifiedReleaseActivationHealthVerificationFailureCode
                    .CurrentPointerSwitchUnavailable,
                "Exact current-pointer switch evidence is required before post-switch health verification.",
                m_settings,
                planReport,
                exactPlanBound: true);
        }
        VerifiedReleaseActivationServiceControlObservation serviceControl =
            m_serviceControlReader(plan.ServiceControlPlan);
        if (!ValidateServiceControlObservation(
                serviceControl,
                plan.ServiceControlPlan))
        {
            return VerifiedReleaseActivationHealthVerificationReport.Failure(
                VerifiedReleaseActivationHealthVerificationFailureCode
                    .ServiceControlUnavailable,
                "Exact service-control completion is required before post-switch health verification.",
                m_settings,
                planReport,
                exactPlanBound: true);
        }

        await m_executionGate.WaitAsync(cancellationToken);
        try
        {
            lock (m_stateGate)
            {
                if (m_completed is not null)
                {
                    return VerifiedReleaseActivationHealthVerificationReport.Failure(
                        VerifiedReleaseActivationHealthVerificationFailureCode
                            .HealthAlreadyVerified,
                        "Health verification is already retained for this service lifetime.",
                        m_settings,
                        planReport,
                        exactPlanBound: true);
                }
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
                when (IsObservationException(exception))
            {
                return VerifiedReleaseActivationHealthVerificationReport.Failure(
                    VerifiedReleaseActivationHealthVerificationFailureCode
                        .StatusUnavailable,
                    "Release or setup status could not be read before health verification.",
                    m_settings,
                    planReport,
                    tally,
                    exactPlanBound: true);
            }

            if (!MatchesTargetActiveStatus(beforeStatus, plan.ActivationPlan))
            {
                return VerifiedReleaseActivationHealthVerificationReport.Failure(
                    beforeStatus.Succeeded
                        ? VerifiedReleaseActivationHealthVerificationFailureCode
                            .StatusMismatch
                        : VerifiedReleaseActivationHealthVerificationFailureCode
                            .StatusUnavailable,
                    "The exact target release is not the stable active release before health verification.",
                    m_settings,
                    planReport,
                    tally,
                    exactPlanBound: true);
            }
            if (!TryBindSetup(
                    beforeSetup,
                    plan.ActivationPlan,
                    out string canonicalGatewayAuthority,
                    out InstallationTopologyProfile topology))
            {
                return VerifiedReleaseActivationHealthVerificationReport.Failure(
                    VerifiedReleaseActivationHealthVerificationFailureCode
                        .SetupMismatch,
                    "Completed setup no longer matches the exact activation plan.",
                    m_settings,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    targetActiveBefore: true);
            }
            if (!TryResolveSupportedTopology(
                    topology,
                    out bool remoteAgentRequired))
            {
                return VerifiedReleaseActivationHealthVerificationReport.Failure(
                    VerifiedReleaseActivationHealthVerificationFailureCode
                        .UnsupportedTopology,
                    "The completed setup topology requires a remote station-engine health transport that is not registered.",
                    m_settings,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    targetActiveBefore: true,
                    canonicalHostBound: true);
            }
            bool stationIdentityConfigured =
                !string.IsNullOrEmpty(m_settings.ExpectedStationId);
            if (stationIdentityConfigured != remoteAgentRequired)
            {
                return VerifiedReleaseActivationHealthVerificationReport.Failure(
                    VerifiedReleaseActivationHealthVerificationFailureCode
                        .StationIdentityMismatch,
                    remoteAgentRequired
                        ? "The completed setup topology requires one exact remote station identity."
                        : "The completed setup topology must not configure a remote station identity.",
                    m_settings,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    targetActiveBefore: true,
                    canonicalHostBound: true);
            }

            DateTimeOffset startedAt = m_timeProvider.GetUtcNow();
            int verifiedTargets = 0;
            int unitChecks = 0;
            int httpChecks = 0;
            int brokerChecks = 0;
            foreach (VerifiedReleaseActivationHealthVerificationTarget target in
                     plan.Targets)
            {
                DateTimeOffset deadline = startedAt.AddMilliseconds(
                    target.DeadlineMilliseconds);
                if (target.ServiceRole ==
                    VerifiedReleaseActivationServiceRole.AetherRemoteAgent)
                {
                    if (remoteAgentRequired)
                    {
                        bool agentReady = await WaitForAttemptAsync(
                            deadline,
                            _ =>
                            {
                                tally.BrokerLinkObservationCount++;
                                return Task.FromResult(
                                    ObserveFreshBrokerLink(startedAt));
                            },
                            cancellationToken);
                        if (!agentReady)
                        {
                            return VerifiedReleaseActivationHealthVerificationReport
                                .Failure(
                                    VerifiedReleaseActivationHealthVerificationFailureCode
                                        .BrokerLinkUnavailable,
                                    "The exact remote station agent did not establish a fresh broker link within its bounded deadline.",
                                    m_settings,
                                    planReport,
                                    tally,
                                    exactPlanBound: true,
                                    targetActiveBefore: true,
                                    canonicalHostBound: true,
                                    verifiedTargetCount: verifiedTargets);
                        }
                        brokerChecks++;
                    }
                    verifiedTargets++;
                    continue;
                }

                bool unitActive = await WaitForAttemptAsync(
                    deadline,
                    timeout =>
                    {
                        tally.UnitActivityAttemptCount++;
                        return m_runtime.CheckUnitActiveAsync(
                            target.UnitIdentity,
                            timeout,
                            cancellationToken);
                    },
                    cancellationToken);
                if (!unitActive)
                {
                    return VerifiedReleaseActivationHealthVerificationReport.Failure(
                        VerifiedReleaseActivationHealthVerificationFailureCode
                            .UnitActivityUnavailable,
                        "A planned service unit did not become active within its bounded deadline.",
                        m_settings,
                        planReport,
                        tally,
                        exactPlanBound: true,
                        targetActiveBefore: true,
                        canonicalHostBound: true,
                        verifiedTargetCount: verifiedTargets);
                }
                unitChecks++;

                bool contractReady;
                if (target.ContractKind ==
                    VerifiedReleaseActivationHealthContractKind.LoopbackHttp)
                {
                    contractReady = await WaitForAttemptAsync(
                        deadline,
                        timeout =>
                        {
                            tally.LoopbackHttpAttemptCount++;
                            return m_runtime.CheckLoopbackHealthAsync(
                                target,
                                canonicalGatewayAuthority,
                                timeout,
                                cancellationToken);
                        },
                        cancellationToken);
                    if (contractReady)
                    {
                        httpChecks++;
                    }
                }
                else
                {
                    contractReady = await WaitForAttemptAsync(
                        deadline,
                        _ =>
                        {
                            tally.BrokerLinkObservationCount++;
                            return Task.FromResult(
                                ObserveFreshBrokerLink(startedAt));
                        },
                        cancellationToken);
                    if (contractReady)
                    {
                        brokerChecks++;
                    }
                }

                if (!contractReady)
                {
                    return VerifiedReleaseActivationHealthVerificationReport.Failure(
                        target.ContractKind ==
                            VerifiedReleaseActivationHealthContractKind.LoopbackHttp
                            ? VerifiedReleaseActivationHealthVerificationFailureCode
                                .LoopbackHealthUnavailable
                            : VerifiedReleaseActivationHealthVerificationFailureCode
                                .BrokerLinkUnavailable,
                        "A planned service-health contract did not become ready within its bounded deadline.",
                        m_settings,
                        planReport,
                        tally,
                        exactPlanBound: true,
                        targetActiveBefore: true,
                        canonicalHostBound: true,
                        verifiedTargetCount: verifiedTargets);
                }
                verifiedTargets++;
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
                when (IsObservationException(exception))
            {
                return VerifiedReleaseActivationHealthVerificationReport.Failure(
                    VerifiedReleaseActivationHealthVerificationFailureCode
                        .StatusUnavailable,
                    "Release or setup status could not be read after health verification.",
                    m_settings,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    targetActiveBefore: true,
                    canonicalHostBound: true,
                    verifiedTargetCount: verifiedTargets);
            }

            bool afterActive =
                MatchesTargetActiveStatus(afterStatus, plan.ActivationPlan);
            bool setupStable =
                EquivalentSetup(beforeSetup, afterSetup) &&
                TryBindSetup(
                    afterSetup,
                    plan.ActivationPlan,
                    out string afterAuthority,
                    out InstallationTopologyProfile afterTopology) &&
                Equals(topology, afterTopology) &&
                string.Equals(
                    canonicalGatewayAuthority,
                    afterAuthority,
                    StringComparison.Ordinal);
            if (!afterActive ||
                !EquivalentStatus(beforeStatus, afterStatus) ||
                !setupStable)
            {
                return VerifiedReleaseActivationHealthVerificationReport.Failure(
                    VerifiedReleaseActivationHealthVerificationFailureCode
                        .ObservationDrift,
                    "Release or setup state changed during health verification.",
                    m_settings,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    targetActiveBefore: true,
                    targetActiveAfter: afterActive,
                    setupStable: setupStable,
                    canonicalHostBound: true,
                    verifiedTargetCount: verifiedTargets);
            }

            DateTimeOffset completedAt = m_timeProvider.GetUtcNow();
            if (completedAt < startedAt)
            {
                return VerifiedReleaseActivationHealthVerificationReport.Failure(
                    VerifiedReleaseActivationHealthVerificationFailureCode
                        .ObservationDrift,
                    "The health-verification observation clock moved backwards.",
                    m_settings,
                    planReport,
                    tally,
                    exactPlanBound: true,
                    targetActiveBefore: true,
                    targetActiveAfter: true,
                    setupStable: true,
                    canonicalHostBound: true,
                    verifiedTargetCount: verifiedTargets);
            }

            VerifiedReleaseActivationHealthVerificationEvidence evidence = new(
                plan,
                startedAt,
                completedAt,
                verifiedTargets,
                unitChecks,
                httpChecks,
                brokerChecks);
            lock (m_stateGate)
            {
                m_completed = evidence;
            }
            return VerifiedReleaseActivationHealthVerificationReport.Success(
                m_settings,
                planReport,
                evidence,
                tally);
        }
        finally
        {
            m_executionGate.Release();
        }
    }

    internal VerifiedReleaseActivationHealthVerificationObservation Observe(
        VerifiedReleaseActivationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (m_stateGate)
        {
            VerifiedReleaseActivationHealthVerificationEvidence? completed =
                m_completed;
            if (completed is null ||
                !ReferenceEquals(completed.Plan.ActivationPlan, plan))
            {
                return new VerifiedReleaseActivationHealthVerificationObservation(
                    HealthVerificationReady: false,
                    HealthTargetCount: 0,
                    VerifiedTargetCount: 0,
                    UnitActivityCheckCount: 0,
                    LoopbackHttpCheckCount: 0,
                    FreshBrokerLinkCheckCount: 0,
                    CompletedAt: null,
                    ReconciliationRequired: false);
            }
            return new VerifiedReleaseActivationHealthVerificationObservation(
                HealthVerificationReady: true,
                completed.Plan.Targets.Count,
                completed.VerifiedTargetCount,
                completed.UnitActivityCheckCount,
                completed.LoopbackHttpCheckCount,
                completed.FreshBrokerLinkCheckCount,
                completed.CompletedAt,
                ReconciliationRequired: false);
        }
    }

    private async Task<bool> WaitForAttemptAsync(
        DateTimeOffset deadline,
        Func<TimeSpan, Task<HealthProbeAttemptResult>> attempt,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < MaximumAttemptsPerTarget; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            TimeSpan remaining = deadline - now;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }
            TimeSpan timeout = remaining < ProbeAttemptTimeout
                ? remaining
                : ProbeAttemptTimeout;
            HealthProbeAttemptResult result = await attempt(timeout);
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
            TimeSpan delay = remaining < PollInterval
                ? remaining
                : PollInterval;
            await m_delay(delay, cancellationToken);
        }
        return false;
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
                m_settings.ExpectedStationId,
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
            station.LastSeen > now.AddSeconds(5) ||
            station.ConnectedAt > station.LastSeen ||
            station.HeartbeatSequence < 1 ||
            station.InventorySequence < 1)
        {
            return HealthProbeAttemptResult.Retry(
                "The expected station link is not freshly online.");
        }
        return HealthProbeAttemptResult.Success();
    }

    private static ReleaseActivationHealthVerificationSettings ValidateSettings(
        ReleaseActivationHealthVerificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string stationId = settings.ExpectedStationId ?? string.Empty;
        if (!settings.ExecutionEnabled)
        {
            if (stationId.Length != 0)
            {
                throw new InvalidOperationException(
                    "Disabled release health verification must not configure a station identity.");
            }
            return new ReleaseActivationHealthVerificationSettings();
        }
        if (stationId.Length != 0 && !IsCanonicalStationId(stationId))
        {
            throw new InvalidOperationException(
                "An enabled release health-verification station identity must be canonical when configured.");
        }
        return new ReleaseActivationHealthVerificationSettings
        {
            ExecutionEnabled = true,
            ExpectedStationId = stationId
        };
    }

    private static bool IsCanonicalStationId(string value)
    {
        if (value.Length is < 1 or > 64 ||
            !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }
        return value.All(character =>
            IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static VerifiedReleaseActivationHealthVerificationPlan?
        ValidatePlanReport(
            VerifiedReleaseActivationHealthVerificationPlanReport report)
    {
        if (!report.Succeeded ||
            report.FailureCode !=
                VerifiedReleaseActivationHealthVerificationPlanFailureCode.None ||
            report.SetupRevision is not > 0 ||
            string.IsNullOrEmpty(report.InstalledReleaseIdentity) ||
            string.IsNullOrEmpty(report.TargetReleaseIdentity) ||
            !report.HealthVerificationRequired ||
            report.HealthTargetCount != 4 ||
            report.UnitActivityCheckCount != 4 ||
            report.LoopbackHttpCheckCount != 3 ||
            report.FreshBrokerLinkCheckCount != 1 ||
            !report.ExactServiceControlPlanBound ||
            !report.ExactActivationPlanBound ||
            !report.CompleteServiceCoverageBound ||
            !report.FixedHealthContractMappingBound ||
            !report.DeterministicOrderingBound ||
            !report.LoopbackOnlyHttpBound ||
            !report.CanonicalGatewayHostBindingRequired ||
            !report.BoundedDeadlinePlanningBound ||
            !report.PostSwitchVerificationPlanned ||
            report.NetworkRequestPerformed ||
            report.ProcessInvocationPerformed ||
            report.SystemdCommandPerformed ||
            report.JournalReadPerformed ||
            report.HealthEvidenceProduced ||
            report.ServiceHealthReady ||
            report.CurrentPointerChanged ||
            report.ActivationAuthorized)
        {
            return null;
        }
        return report.Plan;
    }

    private static bool ValidatePlanShape(
        VerifiedReleaseActivationHealthVerificationPlanReport report,
        VerifiedReleaseActivationHealthVerificationPlan plan)
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
            report.ServiceControlRequired !=
                plan.ServiceControlPlan.ServiceControlRequired ||
            !activation.ServiceHealthVerificationRequired ||
            !activation.AtomicCurrentPointerSwitchRequired ||
            !activation.AutomaticRollbackRequired ||
            !activation.OperatorApprovalRequired ||
            plan.Targets.Count != 4)
        {
            return false;
        }

        (VerifiedReleaseActivationServiceRole Role,
            string Unit,
            VerifiedReleaseActivationHealthContractKind Kind,
            int? Port,
            bool CanonicalHost,
            int Deadline)[] expected =
        [
            (
                VerifiedReleaseActivationServiceRole.StationEngine,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .StationEngineUnitIdentity,
                VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .StationEngineLoopbackPort,
                false,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .StationEngineDeadlineMilliseconds),
            (
                VerifiedReleaseActivationServiceRole.Broker,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .BrokerUnitIdentity,
                VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .BrokerLoopbackPort,
                false,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .BrokerDeadlineMilliseconds),
            (
                VerifiedReleaseActivationServiceRole.AetherRemoteAgent,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .AetherRemoteAgentUnitIdentity,
                VerifiedReleaseActivationHealthContractKind.FreshBrokerLink,
                null,
                false,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .AetherRemoteAgentDeadlineMilliseconds),
            (
                VerifiedReleaseActivationServiceRole.GatewayWeb,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .GatewayWebUnitIdentity,
                VerifiedReleaseActivationHealthContractKind.LoopbackHttp,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .GatewayWebLoopbackPort,
                true,
                VerifiedReleaseActivationHealthVerificationPlanComposer
                    .GatewayWebDeadlineMilliseconds)
        ];

        for (int index = 0; index < expected.Length; index++)
        {
            VerifiedReleaseActivationHealthVerificationTarget target =
                plan.Targets[index];
            var item = expected[index];
            if (target.Sequence != index + 1 ||
                target.ServiceRole != item.Role ||
                !string.Equals(target.UnitIdentity, item.Unit, StringComparison.Ordinal) ||
                target.ContractKind != item.Kind ||
                target.LoopbackPort != item.Port ||
                target.RequireCanonicalHostHeader != item.CanonicalHost ||
                !target.RequireUnitActive ||
                !target.RequireFreshObservation ||
                target.DeadlineMilliseconds != item.Deadline ||
                target.DeadlineMilliseconds is < 1 or >
                    VerifiedReleaseActivationHealthVerificationPlanComposer
                        .MaximumDeadlineMilliseconds)
            {
                return false;
            }
            if (target.ContractKind ==
                VerifiedReleaseActivationHealthContractKind.LoopbackHttp)
            {
                if (!string.Equals(
                        target.HealthPath,
                        VerifiedReleaseActivationHealthVerificationPlanComposer
                            .HealthPath,
                        StringComparison.Ordinal) ||
                    target.ExpectedHttpStatusCode !=
                        VerifiedReleaseActivationHealthVerificationPlanComposer
                            .ExpectedHttpStatusCode)
                {
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(target.HealthPath) ||
                target.ExpectedHttpStatusCode is not null)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidatePointerSwitchObservation(
        VerifiedReleaseActivationCurrentPointerSwitchObservation observation) =>
        observation.PointerSwitchReady &&
        observation.ExactServiceControlPlanBound &&
        observation.ExactActivationPlanBound &&
        observation.PreSwitchServiceControlReady &&
        observation.CompletedAt is not null &&
        !observation.ReconciliationRequired;

    private static bool ValidateServiceControlObservation(
        VerifiedReleaseActivationServiceControlObservation observation,
        VerifiedReleaseActivationServiceControlPlan plan)
    {
        if (plan.HostRestartRequired)
        {
            return false;
        }
        if (!plan.ServiceControlRequired)
        {
            return observation.ServiceControlReady &&
                !observation.ServiceControlRequired &&
                observation.PlannedStopActionCount == 0 &&
                observation.ExecutedStopActionCount == 0 &&
                observation.TopologyNoOpStopActionCount == 0 &&
                observation.PlannedStartActionCount == 0 &&
                observation.ExecutedStartActionCount == 0 &&
                observation.TopologyNoOpStartActionCount == 0 &&
                observation.CompletedAt is not null &&
                !observation.ReconciliationRequired;
        }
        return observation.ServiceControlReady &&
            observation.ServiceControlRequired &&
            observation.PlannedStopActionCount == plan.StopActions.Count &&
            observation.ExecutedStopActionCount >= 0 &&
            observation.TopologyNoOpStopActionCount >= 0 &&
            observation.ExecutedStopActionCount +
                observation.TopologyNoOpStopActionCount ==
                observation.PlannedStopActionCount &&
            observation.PlannedStartActionCount == plan.StartActions.Count &&
            observation.ExecutedStartActionCount >= 0 &&
            observation.TopologyNoOpStartActionCount >= 0 &&
            observation.ExecutedStartActionCount +
                observation.TopologyNoOpStartActionCount ==
                observation.PlannedStartActionCount &&
            observation.CompletedAt is not null &&
            !observation.ReconciliationRequired;
    }

    private static bool MatchesTargetActiveStatus(
        ReleaseStatusReadResult status,
        VerifiedReleaseActivationPlan activation)
    {
        if (!status.Succeeded ||
            status.FailureCode != ReleaseStatusFailureCode.None ||
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
            !status.CurrentPointerPresent ||
            !string.Equals(
                status.ActiveReleaseIdentity,
                activation.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            status.AvailableReleaseCount !=
                status.AvailableReleaseIdentities.Count ||
            status.AvailableReleaseCount is < 2 or >
                ReleaseInstallationStatusReader.MaximumReleaseCount ||
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

    private static bool TryBindSetup(
        InstallationSetupState state,
        VerifiedReleaseActivationPlan activation,
        out string canonicalGatewayAuthority,
        out InstallationTopologyProfile topology)
    {
        canonicalGatewayAuthority = string.Empty;
        topology = null!;
        try
        {
            InstallationSetupStateValidator.Validate(state);
            if (state.Revision != activation.SetupRevision ||
                state.Lock.Mode != InstallationSetupLockMode.Complete ||
                state.LastCompletedStep != InstallationSetupStep.Administrator ||
                state.Topology is null ||
                state.Paths is null ||
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
            CanonicalPublicUrl publicUrl =
                CanonicalPublicUrl.Parse(state.CanonicalPublicUrl);
            canonicalGatewayAuthority = publicUrl.Uri.Authority;
            topology = InstallationTopologyProfile.For(state.Topology.Value);
            return !string.IsNullOrEmpty(canonicalGatewayAuthority);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool EquivalentStatus(
        ReleaseStatusReadResult first,
        ReleaseStatusReadResult second) =>
        first == second ||
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
        InstallationSetupState second) =>
        first.SchemaVersion == second.SchemaVersion &&
        first.Revision == second.Revision &&
        first.UpdatedAt == second.UpdatedAt &&
        first.LastCompletedStep == second.LastCompletedStep &&
        Equals(first.Lock, second.Lock) &&
        first.Topology == second.Topology &&
        string.Equals(
            first.CanonicalPublicUrl,
            second.CanonicalPublicUrl,
            StringComparison.Ordinal) &&
        Equals(first.Paths, second.Paths) &&
        first.UpdateChannel == second.UpdateChannel &&
        string.Equals(
            first.PinnedRelease,
            second.PinnedRelease,
            StringComparison.Ordinal) &&
        first.InstallTransmitSupport == second.InstallTransmitSupport;

    private static bool IsObservationException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or
            NotSupportedException or OverflowException or
            System.Security.SecurityException;
}
