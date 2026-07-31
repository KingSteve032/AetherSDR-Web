using System.Collections.Concurrent;
using System.Diagnostics;
using AetherSDR.TxWatchdog.Protocol;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed class IndependentTxWatchdogSettings
{
    public const string SectionName = "IndependentTxWatchdog";

    public bool Enabled { get; set; } = true;
    public string ExecutablePath { get; set; } = string.Empty;
    public int RequestTimeoutMilliseconds { get; set; } = 2000;
    public int RestartDelayMilliseconds { get; set; } = 1000;
}

public sealed record StationTxIndependentWatchdogDiagnostics(
    bool SupervisionEnabled,
    bool ProcessRunning,
    int? ProcessId,
    string? HostInstanceId,
    DateTimeOffset? ProcessStartedAt,
    string State,
    string Reason,
    bool IpcConnected,
    bool Registered,
    bool Connected,
    bool LeaseBound,
    long LastSequence,
    long RestartCount,
    string LastObservation,
    DateTimeOffset? LastObservedAt,
    string? LastError,
    bool RadioCommandTransportAvailable,
    bool ArmingAvailable);

public sealed record StationTxIndependentWatchdogAggregate(
    bool SupervisionRegistered,
    int SessionCount,
    int RunningProcessCount,
    int ConnectedProcessCount,
    int RegisteredIdentityCount,
    long RestartCount,
    bool CommandTransportAvailable,
    bool ArmingAvailable,
    string State);

internal enum StationTxIndependentWatchdogEventKind
{
    Ready,
    Lost
}

internal sealed record StationTxIndependentWatchdogEvent(
    StationTxIndependentWatchdogEventKind Kind,
    string Reason,
    string? HostInstanceId,
    DateTimeOffset ObservedAt);

internal interface IStationTxIndependentWatchdog : IAsyncDisposable
{
    StationTxIndependentWatchdogDiagnostics Snapshot { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<StationTxIndependentWatchdogDiagnostics> RegisterAsync(
        WatchdogIdentity identity,
        CancellationToken cancellationToken = default);

    Task<StationTxIndependentWatchdogDiagnostics> HeartbeatAsync(
        WatchdogIdentity identity,
        CancellationToken cancellationToken = default);

    Task<StationTxIndependentWatchdogDiagnostics> DisconnectAndResetAsync(
        WatchdogIdentity identity,
        CancellationToken cancellationToken = default);
}

internal sealed record StationTxIndependentWatchdogOwner(
    string RadioId,
    string SessionId,
    string BrowserClientId,
    string GatewayInstanceId,
    string EngineInstanceId);

internal interface IStationTxIndependentWatchdogFactory
{
    IStationTxIndependentWatchdog Create(
        StationTxIndependentWatchdogOwner owner,
        Func<StationTxIndependentWatchdogEvent, ValueTask> eventSink);
}

internal sealed class StationTxUnavailableIndependentWatchdog :
    IStationTxIndependentWatchdog
{
    public StationTxIndependentWatchdogDiagnostics Snapshot { get; } = new(
        SupervisionEnabled: false,
        ProcessRunning: false,
        ProcessId: null,
        HostInstanceId: null,
        ProcessStartedAt: null,
        State: "Disarmed",
        Reason: "supervision-not-registered",
        IpcConnected: false,
        Registered: false,
        Connected: false,
        LeaseBound: false,
        LastSequence: 0,
        RestartCount: 0,
        LastObservation: "supervision-not-registered",
        LastObservedAt: null,
        LastError: null,
        RadioCommandTransportAvailable: false,
        ArmingAvailable: false);

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<StationTxIndependentWatchdogDiagnostics> RegisterAsync(
        WatchdogIdentity identity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot);

    public Task<StationTxIndependentWatchdogDiagnostics> HeartbeatAsync(
        WatchdogIdentity identity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot);

    public Task<StationTxIndependentWatchdogDiagnostics> DisconnectAndResetAsync(
        WatchdogIdentity identity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class StationTxIndependentWatchdogRegistry :
    IStationTxIndependentWatchdogFactory
{
    private readonly ConcurrentDictionary<
        string,
        StationTxIndependentWatchdogClient> m_clients =
        new(StringComparer.Ordinal);
    private readonly IndependentTxWatchdogSettings m_settings;
    private readonly IWebHostEnvironment m_environment;
    private readonly ILoggerFactory m_loggerFactory;

    public StationTxIndependentWatchdogRegistry(
        IOptions<IndependentTxWatchdogSettings> settings,
        IWebHostEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        m_settings = Validate(settings.Value);
        m_environment = environment;
        m_loggerFactory = loggerFactory;
    }

    public StationTxIndependentWatchdogAggregate Snapshot
    {
        get
        {
            StationTxIndependentWatchdogDiagnostics[] snapshots =
                m_clients.Values.Select(client => client.Snapshot).ToArray();
            return new StationTxIndependentWatchdogAggregate(
                SupervisionRegistered: true,
                snapshots.Length,
                snapshots.Count(snapshot => snapshot.ProcessRunning),
                snapshots.Count(snapshot => snapshot.IpcConnected),
                snapshots.Count(snapshot => snapshot.Registered),
                snapshots.Sum(snapshot => snapshot.RestartCount),
                CommandTransportAvailable: false,
                ArmingAvailable: false,
                State: snapshots.Length == 0
                    ? "supervised-empty-disarmed"
                    : snapshots.All(snapshot => snapshot.ProcessRunning &&
                        snapshot.IpcConnected)
                        ? "supervised-disarmed"
                        : "supervised-degraded-disarmed");
        }
    }

    IStationTxIndependentWatchdog
        IStationTxIndependentWatchdogFactory.Create(
            StationTxIndependentWatchdogOwner owner,
            Func<StationTxIndependentWatchdogEvent, ValueTask> eventSink) =>
        Create(owner, eventSink);

    internal IStationTxIndependentWatchdog Create(
        StationTxIndependentWatchdogOwner owner,
        Func<StationTxIndependentWatchdogEvent, ValueTask> eventSink)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(eventSink);
        IndependentTxWatchdogLaunchCommand command = ResolveCommand();
        StationTxIndependentWatchdogClient client = new(
            owner,
            m_settings,
            command,
            eventSink,
            () => m_clients.TryRemove(owner.SessionId, out _),
            m_loggerFactory.CreateLogger<StationTxIndependentWatchdogClient>());
        if (!m_clients.TryAdd(owner.SessionId, client))
        {
            throw new InvalidOperationException(
                "An independent watchdog already exists for this radio session.");
        }
        return client;
    }

    private IndependentTxWatchdogLaunchCommand ResolveCommand()
    {
        string configured = m_settings.ExecutablePath.Trim();
        string executable = configured.Length > 0
            ? Path.GetFullPath(configured, m_environment.ContentRootPath)
            : Path.Combine(
                AppContext.BaseDirectory,
                "watchdog",
                OperatingSystem.IsWindows()
                    ? "AetherSDR.TxWatchdog.exe"
                    : "AetherSDR.TxWatchdog");
        string expectedFileName = OperatingSystem.IsWindows()
            ? "AetherSDR.TxWatchdog.exe"
            : "AetherSDR.TxWatchdog";
        if (!string.Equals(
                Path.GetFileName(executable),
                expectedFileName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "IndependentTxWatchdog:ExecutablePath must name the reviewed watchdog executable.");
        }
        return new IndependentTxWatchdogLaunchCommand(executable, ["--stdio"]);
    }

    private static IndependentTxWatchdogSettings Validate(
        IndependentTxWatchdogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.RequestTimeoutMilliseconds is < 250 or > 15000)
        {
            throw new InvalidOperationException(
                "IndependentTxWatchdog:RequestTimeoutMilliseconds must be between 250 and 15000.");
        }
        if (settings.RestartDelayMilliseconds is < 100 or > 30000)
        {
            throw new InvalidOperationException(
                "IndependentTxWatchdog:RestartDelayMilliseconds must be between 100 and 30000.");
        }
        if (settings.ExecutablePath.Length > 1024 ||
            settings.ExecutablePath.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "IndependentTxWatchdog:ExecutablePath is invalid.");
        }
        return settings;
    }
}

internal sealed record IndependentTxWatchdogLaunchCommand(
    string FileName,
    IReadOnlyList<string> Arguments);

internal sealed class StationTxIndependentWatchdogClient :
    IStationTxIndependentWatchdog
{
    private readonly object m_stateGate = new();
    private readonly SemaphoreSlim m_ioGate = new(1, 1);
    private readonly StationTxIndependentWatchdogOwner m_owner;
    private readonly IndependentTxWatchdogSettings m_settings;
    private readonly IndependentTxWatchdogLaunchCommand m_command;
    private readonly Func<StationTxIndependentWatchdogEvent, ValueTask> m_eventSink;
    private readonly Action m_disposedCallback;
    private readonly ILogger<StationTxIndependentWatchdogClient> m_logger;
    private readonly CancellationTokenSource m_disposeCancellation = new();

    private Process? m_process;
    private WatchdogIdentity? m_identity;
    private string? m_hostInstanceId;
    private DateTimeOffset? m_processStartedAt;
    private int? m_processId;
    private bool m_processRunning;
    private bool m_ipcConnected;
    private bool m_registered;
    private bool m_connected;
    private bool m_leaseBound;
    private long m_lastSequence;
    private long m_restartCount;
    private string m_state = "Disarmed";
    private string m_reason = "supervisor-not-started";
    private string m_lastObservation = "supervisor-created";
    private DateTimeOffset? m_lastObservedAt;
    private string? m_lastError;
    private int m_expectedExit;
    private int m_restartScheduled;
    private int m_started;
    private int m_disposed;

    public StationTxIndependentWatchdogClient(
        StationTxIndependentWatchdogOwner owner,
        IndependentTxWatchdogSettings settings,
        IndependentTxWatchdogLaunchCommand command,
        Func<StationTxIndependentWatchdogEvent, ValueTask> eventSink,
        Action disposedCallback,
        ILogger<StationTxIndependentWatchdogClient> logger)
    {
        m_owner = owner;
        m_settings = settings;
        m_command = command;
        m_eventSink = eventSink;
        m_disposedCallback = disposedCallback;
        m_logger = logger;
    }

    public StationTxIndependentWatchdogDiagnostics Snapshot
    {
        get
        {
            lock (m_stateGate)
            {
                return new StationTxIndependentWatchdogDiagnostics(
                    m_settings.Enabled,
                    m_processRunning,
                    m_processId,
                    m_hostInstanceId,
                    m_processStartedAt,
                    m_state,
                    m_reason,
                    m_ipcConnected,
                    m_registered,
                    m_connected,
                    m_leaseBound,
                    m_lastSequence,
                    m_restartCount,
                    m_lastObservation,
                    m_lastObservedAt,
                    m_lastError,
                    RadioCommandTransportAvailable: false,
                    ArmingAvailable: false);
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref m_started, 1) != 0)
        {
            return;
        }
        if (!m_settings.Enabled)
        {
            RecordState(
                "Disarmed",
                "supervision-disabled",
                "watchdog-supervision-disabled",
                error: null);
            return;
        }

        await m_ioGate.WaitAsync(cancellationToken);
        StationTxIndependentWatchdogEvent? eventToPublish = null;
        try
        {
            eventToPublish = await StartProcessLockedAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or
                System.ComponentModel.Win32Exception or
                UnauthorizedAccessException)
        {
            m_logger.LogError(
                exception,
                "Independent watchdog startup failed for radio {RadioId} session {SessionId}",
                m_owner.RadioId,
                m_owner.SessionId);
            eventToPublish = await FaultProcessLockedAsync(
                "watchdog-start-failed",
                restart: true,
                CancellationToken.None);
        }
        finally
        {
            m_ioGate.Release();
        }
        if (eventToPublish?.Kind ==
            StationTxIndependentWatchdogEventKind.Lost)
        {
            ScheduleRestart();
        }
        await PublishAsync(eventToPublish);
    }

    public Task<StationTxIndependentWatchdogDiagnostics> RegisterAsync(
        WatchdogIdentity identity,
        CancellationToken cancellationToken = default) =>
        SendAuthorityAsync(
            WatchdogRequestKind.Register,
            identity,
            resetAfter: false,
            cancellationToken);

    public Task<StationTxIndependentWatchdogDiagnostics> HeartbeatAsync(
        WatchdogIdentity identity,
        CancellationToken cancellationToken = default) =>
        SendAuthorityAsync(
            WatchdogRequestKind.Heartbeat,
            identity,
            resetAfter: false,
            cancellationToken);

    public Task<StationTxIndependentWatchdogDiagnostics> DisconnectAndResetAsync(
        WatchdogIdentity identity,
        CancellationToken cancellationToken = default) =>
        SendAuthorityAsync(
            WatchdogRequestKind.Disconnect,
            identity,
            resetAfter: true,
            cancellationToken);

    private async Task<StationTxIndependentWatchdogDiagnostics>
        SendAuthorityAsync(
        WatchdogRequestKind kind,
        WatchdogIdentity identity,
        bool resetAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        StationTxIndependentWatchdogEvent? eventToPublish = null;
        await m_ioGate.WaitAsync(cancellationToken);
        try
        {
            if (!m_settings.Enabled || Volatile.Read(ref m_disposed) != 0)
            {
                eventToPublish = LostEvent("watchdog-supervision-unavailable");
                return Snapshot;
            }
            if (m_process is null || m_process.HasExited || !m_ipcConnected)
            {
                eventToPublish = await StartProcessLockedAsync(cancellationToken);
                if (eventToPublish?.Kind ==
                    StationTxIndependentWatchdogEventKind.Lost)
                {
                    return Snapshot;
                }
            }

            if (kind == WatchdogRequestKind.Register)
            {
                if (m_identity is not null && !Equals(m_identity, identity))
                {
                    eventToPublish = await FaultProcessLockedAsync(
                        "local-identity-mismatch",
                        restart: true,
                        cancellationToken);
                    return Snapshot;
                }
            }
            else if (m_identity is null || !Equals(m_identity, identity))
            {
                eventToPublish = await FaultProcessLockedAsync(
                    "local-identity-mismatch",
                    restart: true,
                    cancellationToken);
                return Snapshot;
            }

            long sequence = checked(m_lastSequence + 1);
            WatchdogRequest request = new(
                WatchdogProtocol.Version,
                $"{kind.ToString().ToLowerInvariant()}-{sequence}",
                kind,
                sequence,
                identity);
            WatchdogResponse response = await SendLockedAsync(
                request,
                cancellationToken);
            if (!response.Ok)
            {
                eventToPublish = await FaultProcessLockedAsync(
                    $"watchdog-rejected-{response.Error ?? "unknown"}",
                    restart: true,
                    cancellationToken);
                return Snapshot;
            }

            ApplySnapshotLocked(response.Snapshot);
            m_identity = identity;
            m_lastSequence = response.Snapshot.LastSequence;
            if (resetAfter)
            {
                await StopProcessLockedAsync();
                ClearAuthorityLocked("disconnect-reset-disarmed");
                StationTxIndependentWatchdogEvent? ready =
                    await StartProcessLockedAsync(cancellationToken);
                if (ready?.Kind == StationTxIndependentWatchdogEventKind.Lost)
                {
                    eventToPublish = ready;
                }
            }
            return Snapshot;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or
                OperationCanceledException or TimeoutException or
                OverflowException)
        {
            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            m_logger.LogError(
                exception,
                "Independent watchdog IPC failed for radio {RadioId} session {SessionId}",
                m_owner.RadioId,
                m_owner.SessionId);
            eventToPublish = await FaultProcessLockedAsync(
                "watchdog-ipc-fault",
                restart: true,
                CancellationToken.None);
        }
        finally
        {
            m_ioGate.Release();
            await PublishAsync(eventToPublish);
        }
        return Snapshot;
    }

    private async Task<StationTxIndependentWatchdogEvent?> StartProcessLockedAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref m_disposed) != 0 || !m_settings.Enabled)
        {
            return LostEvent("watchdog-supervision-unavailable");
        }
        if (m_process is { HasExited: false } && m_ipcConnected)
        {
            return null;
        }
        if (m_process is not null)
        {
            await StopProcessLockedAsync();
        }
        if (!File.Exists(m_command.FileName))
        {
            RecordState(
                "Disarmed",
                "watchdog-binary-missing",
                "watchdog-start-failed",
                m_command.FileName);
            ScheduleRestart();
            return LostEvent("watchdog-binary-missing");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = m_command.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(m_command.FileName) ??
                AppContext.BaseDirectory
        };
        foreach (string argument in m_command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "The independent watchdog process could not be started.");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = ObserveUnexpectedExitAsync(process);
        m_process = process;
        Interlocked.Exchange(ref m_expectedExit, 0);
        lock (m_stateGate)
        {
            m_processId = process.Id;
            m_processStartedAt = DateTimeOffset.UtcNow;
            m_processRunning = true;
            m_ipcConnected = false;
            m_lastObservation = "watchdog-process-started";
            m_lastObservedAt = DateTimeOffset.UtcNow;
            m_lastError = null;
        }

        WatchdogResponse response = await SendLockedAsync(
            new WatchdogRequest(
                WatchdogProtocol.Version,
                "startup-status",
                WatchdogRequestKind.Status,
                Sequence: null,
                Identity: null),
            cancellationToken);
        if (!response.Ok ||
            response.Snapshot.State != "Disarmed" ||
            response.Snapshot.RadioCommandTransportAvailable ||
            response.Snapshot.ArmingAvailable ||
            response.Snapshot.Registered ||
            response.Snapshot.Connected ||
            response.Snapshot.LeaseBound ||
            response.Snapshot.LastSequence != 0)
        {
            return await FaultProcessLockedAsync(
                "watchdog-startup-not-empty-disarmed",
                restart: true,
                cancellationToken);
        }

        ApplySnapshotLocked(response.Snapshot);
        ClearAuthorityLocked("watchdog-process-ready-disarmed");
        return new StationTxIndependentWatchdogEvent(
            StationTxIndependentWatchdogEventKind.Ready,
            "watchdog-process-ready-disarmed",
            response.Snapshot.HostInstanceId,
            DateTimeOffset.UtcNow);
    }

    private async Task<WatchdogResponse> SendLockedAsync(
        WatchdogRequest request,
        CancellationToken cancellationToken)
    {
        Process process = m_process ??
            throw new InvalidOperationException(
                "The independent watchdog process is unavailable.");
        string json = WatchdogProtocol.SerializeRequest(request);
        if (json.Length > WatchdogProtocol.MaximumMessageCharacters)
        {
            throw new InvalidOperationException(
                "The independent watchdog request exceeded its protocol bound.");
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                m_disposeCancellation.Token);
        timeout.CancelAfter(m_settings.RequestTimeoutMilliseconds);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), timeout.Token);
        await process.StandardInput.FlushAsync(timeout.Token);
        string? responseLine = await process.StandardOutput.ReadLineAsync(
            timeout.Token);
        if (responseLine is null)
        {
            string error = await process.StandardError.ReadToEndAsync(timeout.Token);
            throw new IOException(
                $"The independent watchdog returned no response. {error}");
        }
        if (!WatchdogProtocol.TryParseResponse(
                responseLine,
                out WatchdogResponse? response,
                out string parseError))
        {
            throw new InvalidOperationException(
                $"The independent watchdog returned {parseError}.");
        }
        if (!string.Equals(
                response!.RequestId,
                request.RequestId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The independent watchdog response request ID did not match.");
        }
        return response;
    }

    private async Task ObserveUnexpectedExitAsync(Process process)
    {
        if (Volatile.Read(ref m_expectedExit) != 0 ||
            Volatile.Read(ref m_disposed) != 0)
        {
            return;
        }

        StationTxIndependentWatchdogEvent? eventToPublish = null;
        await m_ioGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(process, m_process) ||
                Volatile.Read(ref m_expectedExit) != 0 ||
                Volatile.Read(ref m_disposed) != 0)
            {
                return;
            }
            eventToPublish = await FaultProcessLockedAsync(
                "watchdog-process-exited",
                restart: true,
                CancellationToken.None);
        }
        finally
        {
            m_ioGate.Release();
        }
        await PublishAsync(eventToPublish);
    }

    private async Task<StationTxIndependentWatchdogEvent> FaultProcessLockedAsync(
        string reason,
        bool restart,
        CancellationToken cancellationToken)
    {
        string? hostInstanceId;
        lock (m_stateGate)
        {
            hostInstanceId = m_hostInstanceId;
            m_lastError = reason;
            m_reason = reason;
            m_lastObservation = reason;
            m_lastObservedAt = DateTimeOffset.UtcNow;
            m_ipcConnected = false;
            m_registered = false;
            m_connected = false;
            m_leaseBound = false;
        }
        await StopProcessLockedAsync();
        m_identity = null;
        m_lastSequence = 0;

        if (restart && Volatile.Read(ref m_disposed) == 0)
        {
            ScheduleRestart();
        }

        return new StationTxIndependentWatchdogEvent(
            StationTxIndependentWatchdogEventKind.Lost,
            reason,
            hostInstanceId,
            DateTimeOffset.UtcNow);
    }

    private void ScheduleRestart()
    {
        if (Interlocked.CompareExchange(ref m_restartScheduled, 1, 0) != 0 ||
            Volatile.Read(ref m_disposed) != 0)
        {
            return;
        }
        _ = Task.Run(RestartLoopAsync);
    }

    private async Task RestartLoopAsync()
    {
        try
        {
            while (Volatile.Read(ref m_disposed) == 0)
            {
                try
                {
                    await Task.Delay(
                        m_settings.RestartDelayMilliseconds,
                        m_disposeCancellation.Token);
                }
                catch (OperationCanceledException) when (
                    m_disposeCancellation.IsCancellationRequested)
                {
                    return;
                }

                StationTxIndependentWatchdogEvent? eventToPublish = null;
                await m_ioGate.WaitAsync(m_disposeCancellation.Token);
                try
                {
                    if (Volatile.Read(ref m_disposed) != 0)
                    {
                        return;
                    }
                    if (m_process is { HasExited: false } && m_ipcConnected)
                    {
                        return;
                    }
                    lock (m_stateGate)
                    {
                        m_restartCount++;
                    }
                    try
                    {
                        eventToPublish = await StartProcessLockedAsync(
                            m_disposeCancellation.Token);
                    }
                    catch (Exception exception) when (
                        exception is IOException or InvalidOperationException or
                            System.ComponentModel.Win32Exception or
                            UnauthorizedAccessException)
                    {
                        RecordState(
                            "Disarmed",
                            "watchdog-restart-failed",
                            "watchdog-restart-failed",
                            exception.Message);
                        eventToPublish = LostEvent(
                            "watchdog-restart-failed");
                    }
                }
                finally
                {
                    m_ioGate.Release();
                }

                await PublishAsync(eventToPublish);
                if (eventToPublish?.Kind ==
                    StationTxIndependentWatchdogEventKind.Ready)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (
            m_disposeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref m_restartScheduled, 0);
        }
    }

    private async Task StopProcessLockedAsync()
    {
        Process? process = m_process;
        m_process = null;
        if (process is null)
        {
            return;
        }

        Interlocked.Exchange(ref m_expectedExit, 1);
        try
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromMilliseconds(
                            m_settings.RequestTimeoutMilliseconds));
                }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            process.Dispose();
            lock (m_stateGate)
            {
                m_processId = null;
                m_processStartedAt = null;
                m_processRunning = false;
                m_ipcConnected = false;
            }
        }
    }

    private void ApplySnapshotLocked(WatchdogSnapshot snapshot)
    {
        lock (m_stateGate)
        {
            m_hostInstanceId = snapshot.HostInstanceId;
            m_state = snapshot.State;
            m_reason = snapshot.Reason;
            m_ipcConnected = true;
            m_registered = snapshot.Registered;
            m_connected = snapshot.Connected;
            m_leaseBound = snapshot.LeaseBound;
            m_lastSequence = snapshot.LastSequence;
            m_lastObservation = snapshot.LastObservation;
            m_lastObservedAt = snapshot.LastObservedAt ?? DateTimeOffset.UtcNow;
            m_lastError = null;
        }
    }

    private void ClearAuthorityLocked(string observation)
    {
        m_identity = null;
        m_lastSequence = 0;
        lock (m_stateGate)
        {
            m_registered = false;
            m_connected = false;
            m_leaseBound = false;
            m_lastSequence = 0;
            m_lastObservation = observation;
            m_lastObservedAt = DateTimeOffset.UtcNow;
        }
    }

    private void RecordState(
        string state,
        string reason,
        string observation,
        string? error)
    {
        lock (m_stateGate)
        {
            m_state = state;
            m_reason = reason;
            m_lastObservation = observation;
            m_lastObservedAt = DateTimeOffset.UtcNow;
            m_lastError = error;
        }
    }

    private StationTxIndependentWatchdogEvent LostEvent(string reason) =>
        new(
            StationTxIndependentWatchdogEventKind.Lost,
            reason,
            m_hostInstanceId,
            DateTimeOffset.UtcNow);

    private async Task PublishAsync(
        StationTxIndependentWatchdogEvent? watchdogEvent)
    {
        if (watchdogEvent is null || Volatile.Read(ref m_disposed) != 0)
        {
            return;
        }
        try
        {
            await m_eventSink(watchdogEvent);
        }
        catch (Exception exception)
        {
            m_logger.LogCritical(
                exception,
                "Independent watchdog event sink failed for radio {RadioId} session {SessionId}",
                m_owner.RadioId,
                m_owner.SessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
        {
            return;
        }
        m_disposeCancellation.Cancel();
        await m_ioGate.WaitAsync();
        try
        {
            await StopProcessLockedAsync();
        }
        finally
        {
            m_ioGate.Release();
            m_ioGate.Dispose();
            m_disposeCancellation.Dispose();
            m_disposedCallback();
        }
    }
}
