using System.Security.Claims;
using System.Security.Cryptography;
using AetherSDR.Web.Auth;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed record RadioSessionSummary(
    string SessionId,
    string UserId,
    string DisplayName,
    string RadioId,
    string Host,
    int Port,
    int ClientCount,
    DateTimeOffset LastActivity);

public sealed class RadioSession : IAsyncDisposable
{
    private readonly BackgroundService m_transport;
    private readonly ILogger m_logger;
    private int m_clientCount;
    private long m_browserConnectionAttempts;
    private long m_successfulBrowserConnections;
    private long m_browserReconnects;
    private long m_rejectedBrowserConnections;
    private long m_lastBrowserConnectedUnixMilliseconds;
    private long m_lastBrowserDisconnectedUnixMilliseconds;
    private long m_lastRecoveryMilliseconds = -1;
    private long m_lastActivityUnixMilliseconds =
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private int m_stopped;

    internal RadioSession(
        string sessionId,
        string browserClientId,
        string userId,
        string displayName,
        SelectedRadioEndpoint endpoint,
        RadioCoordinator coordinator,
        StationTxProductionLifecycle txLifecycle,
        SessionRadioSelection selection,
        BackgroundService transport,
        ILogger logger)
    {
        SessionId = sessionId;
        BrowserClientId = browserClientId;
        UserId = userId;
        DisplayName = displayName;
        Endpoint = endpoint;
        Coordinator = coordinator;
        TxLifecycle = txLifecycle;
        Selection = selection;
        m_transport = transport;
        m_logger = logger;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string SessionId { get; }
    public string BrowserClientId { get; }
    public string GuiClientId =>
        Guid.ParseExact(BrowserClientId, "N").ToString();
    public string UserId { get; }
    public string DisplayName { get; }
    public SelectedRadioEndpoint Endpoint { get; }
    public RadioCoordinator Coordinator { get; }
    internal StationTxProductionLifecycle TxLifecycle { get; }
    public SessionRadioSelection Selection { get; }
    public DateTimeOffset CreatedAt { get; }
    public int ClientCount => Volatile.Read(ref m_clientCount);

    public DateTimeOffset LastActivity =>
        DateTimeOffset.FromUnixTimeMilliseconds(
            Volatile.Read(ref m_lastActivityUnixMilliseconds));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await TxLifecycle.StartAsync(cancellationToken);
            await m_transport.StartAsync(cancellationToken);
        }
        catch
        {
            await TxLifecycle.DisposeAsync();
            throw;
        }

        m_logger.LogInformation(
            "Started isolated session {SessionId} for radio {RadioId}",
            SessionId,
            Endpoint.RadioId);
    }

    public bool TryAddClient()
    {
        Interlocked.Increment(ref m_browserConnectionAttempts);
        if (Interlocked.CompareExchange(ref m_clientCount, 1, 0) != 0)
        {
            Interlocked.Increment(ref m_rejectedBrowserConnections);
            return false;
        }

        long connectedAt =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long successfulConnections =
            Interlocked.Increment(ref m_successfulBrowserConnections);
        if (successfulConnections > 1)
        {
            Interlocked.Increment(ref m_browserReconnects);
            long disconnectedAt = Volatile.Read(
                ref m_lastBrowserDisconnectedUnixMilliseconds);
            if (disconnectedAt > 0)
            {
                Interlocked.Exchange(
                    ref m_lastRecoveryMilliseconds,
                    Math.Max(0, connectedAt - disconnectedAt));
            }
        }
        Interlocked.Exchange(
            ref m_lastBrowserConnectedUnixMilliseconds,
            connectedAt);
        Touch();
        return true;
    }

    public void ReleaseClient()
    {
        if (Interlocked.Exchange(ref m_clientCount, 0) != 0)
        {
            Interlocked.Exchange(
                ref m_lastBrowserDisconnectedUnixMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        Touch();
    }

    public void Touch()
    {
        Interlocked.Exchange(
            ref m_lastActivityUnixMilliseconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public bool SetLowBandwidth(bool enabled)
    {
        Touch();
        RadioSnapshot snapshot = Coordinator.Snapshot;
        IReadOnlyList<PanadapterSnapshot> panadapters =
            snapshot.Panadapters ?? [snapshot.Panadapter];
        return Selection.SetLowBandwidth(enabled, panadapters);
    }

    public RadioSessionDiagnostics GetDiagnostics()
    {
        RadioSnapshot snapshot = Coordinator.Snapshot;
        RadioTransportDiagnostics transport =
            m_transport is IRadioTransportDiagnostics diagnostics
                ? diagnostics.GetDiagnostics()
                : new RadioTransportDiagnostics(
                    m_transport.GetType().Name,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    []);
        PanadapterSnapshot[] panadapters =
            (snapshot.Panadapters ?? [snapshot.Panadapter]).ToArray();

        return new RadioSessionDiagnostics(
            SessionId,
            GuiClientId,
            UserId,
            DisplayName,
            Endpoint.RadioId,
            Endpoint.Host,
            Endpoint.Port,
            CreatedAt,
            LastActivity,
            ClientCount,
            GetReconnectDiagnostics(),
            Selection.LowBandwidth,
            snapshot.Version,
            snapshot.Connected,
            snapshot.ConnectionState,
            snapshot.ConnectionError,
            snapshot.RadioModel,
            snapshot.Serial,
            transport,
            Coordinator.ClientDiagnostics,
            panadapters,
            snapshot.Slices.ToArray(),
            Coordinator.TxOccupancy,
            Coordinator.TuneDiagnostics,
            TxLifecycle.Snapshot);
    }

    private RadioBrowserReconnectDiagnostics GetReconnectDiagnostics()
    {
        long lastConnected = Volatile.Read(
            ref m_lastBrowserConnectedUnixMilliseconds);
        long lastDisconnected = Volatile.Read(
            ref m_lastBrowserDisconnectedUnixMilliseconds);
        long lastRecovery = Volatile.Read(ref m_lastRecoveryMilliseconds);
        return new RadioBrowserReconnectDiagnostics(
            Volatile.Read(ref m_browserConnectionAttempts),
            Volatile.Read(ref m_successfulBrowserConnections),
            Volatile.Read(ref m_browserReconnects),
            Volatile.Read(ref m_rejectedBrowserConnections),
            FromUnixMilliseconds(lastConnected),
            FromUnixMilliseconds(lastDisconnected),
            lastRecovery >= 0 ? lastRecovery : null);
    }

    private static DateTimeOffset? FromUnixMilliseconds(long value) =>
        value > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_stopped, 1) != 0)
        {
            return;
        }

        try
        {
            await m_transport.StopAsync(CancellationToken.None);
        }
        finally
        {
            try
            {
                m_transport.Dispose();
            }
            finally
            {
                try
                {
                    Coordinator.Dispose();
                }
                finally
                {
                    await TxLifecycle.DisposeAsync();
                }
            }
        }

        m_logger.LogInformation(
            "Stopped isolated session {SessionId} for radio {RadioId}",
            SessionId,
            Endpoint.RadioId);
    }
}

public sealed class RadioSessionRegistry(
    RadioSelectionManager radioCatalog,
    RadioAccessPolicyStore accessPolicies,
    IOptions<RadioSettings> settings,
    TxLeaseManager txLeaseManager,
    RadioTxOccupancyRegistry txOccupancyRegistry,
    ILoggerFactory loggerFactory,
    ILogger<RadioSessionRegistry> logger,
    IOptions<RemoteStationSettings>? remoteSettings = null,
    StationTxIndependentWatchdogRegistry? independentWatchdogs = null,
    StationTxCommandTrustRegistry? stationCommandTrust = null,
    StationTxCommandEnvelopeCoordinator? stationCommandCoordinator = null)
    : BackgroundService
{
    // Mobile browsers suspend WebSockets as soon as the operator changes apps
    // or locks the screen. Keep the radio-authoritative session alive long
    // enough for that page to resume without deleting and rebuilding its
    // slice at the configured startup frequency. A genuinely closed page
    // still releases its FLEX GUI slot after this bounded grace period.
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private readonly string m_gatewayInstanceId =
        $"gateway-{Guid.NewGuid():N}";
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(5);
    private readonly object m_gate = new();
    private readonly SemaphoreSlim m_creationGate = new(1, 1);
    private readonly Dictionary<SessionKey, RadioSession> m_sessionsByKey = [];
    private readonly Dictionary<string, RadioSession> m_sessionsById =
        new(StringComparer.Ordinal);

    public async Task<RadioSession> GetDefaultAsync(
        ClaimsPrincipal user,
        string? browserClientId,
        CancellationToken cancellationToken)
    {
        string userId = GetRequiredUserId(user);
        string normalizedBrowserClientId =
            GetRequiredBrowserClientId(browserClientId);
        string displayName = GetDisplayName(user, userId);
        bool administratorBypass = user.IsInRole(AetherRoles.Admin);
        SelectedRadioEndpoint selected = radioCatalog.Selected;
        try
        {
            return await GetOrCreateAsync(
                userId,
                normalizedBrowserClientId,
                displayName,
                administratorBypass,
                selected,
                cancellationToken);
        }
        catch (RadioAccessDeniedException firstDenial)
        {
            foreach (
                RadioSelectionOption option in
                radioCatalog.GetSnapshot().Radios)
            {
                if (string.Equals(
                        option.RadioId,
                        selected.RadioId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !option.CanSelect ||
                    !radioCatalog.TryResolve(
                        option.RadioId,
                        out SelectedRadioEndpoint alternative,
                        out _))
                {
                    continue;
                }

                try
                {
                    return await GetOrCreateAsync(
                        userId,
                        normalizedBrowserClientId,
                        displayName,
                        administratorBypass,
                        alternative,
                        cancellationToken);
                }
                catch (RadioAccessDeniedException)
                {
                    // Try the next discovered radio before surfacing denial.
                }
            }

            throw new RadioAccessDeniedException(
                firstDenial.RadioId,
                firstDenial.Message);
        }
    }

    public async Task<RadioSession> GetOrCreateAsync(
        ClaimsPrincipal user,
        string? browserClientId,
        SelectedRadioEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        return await GetOrCreateAsync(
            user,
            browserClientId,
            endpoint,
            initialLowBandwidth: null,
            cancellationToken: cancellationToken);
    }

    public async Task<RadioSession> GetOrCreateAsync(
        ClaimsPrincipal user,
        string? browserClientId,
        SelectedRadioEndpoint endpoint,
        bool? initialLowBandwidth,
        CancellationToken cancellationToken)
    {
        string userId = GetRequiredUserId(user);
        return await GetOrCreateAsync(
            userId,
            GetRequiredBrowserClientId(browserClientId),
            GetDisplayName(user, userId),
            user.IsInRole(AetherRoles.Admin),
            endpoint,
            cancellationToken,
            initialLowBandwidth);
    }

    public IReadOnlyList<RadioSessionSummary> GetSnapshots()
    {
        lock (m_gate)
        {
            return m_sessionsById.Values
                .Select(session => new RadioSessionSummary(
                    session.SessionId,
                    session.UserId,
                    session.DisplayName,
                    session.Endpoint.RadioId,
                    session.Endpoint.Host,
                    session.Endpoint.Port,
                    session.ClientCount,
                    session.LastActivity))
                .OrderBy(
                    session => session.RadioId,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.UserId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<RadioSessionDiagnostics> GetDiagnostics()
    {
        lock (m_gate)
        {
            return m_sessionsById.Values
                .Select(session => session.GetDiagnostics())
                .OrderBy(
                    session => session.RadioId,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.UserId, StringComparer.Ordinal)
                .ThenBy(session => session.CreatedAt)
                .ToArray();
        }
    }

    public async Task<int> TerminateUserSessionsAsync(
        string radioId,
        string userId)
    {
        string normalizedRadioId = radioId?.Trim() ?? string.Empty;
        string normalizedUserId = userId?.Trim() ?? string.Empty;
        if (normalizedRadioId.Length is 0 or > 128 ||
            normalizedUserId.Length is 0 or > 256)
        {
            throw new ArgumentException(
                "A valid radio and user identifier are required.");
        }

        List<RadioSession> removed = [];
        await m_creationGate.WaitAsync();
        try
        {
            lock (m_gate)
            {
                foreach (
                    KeyValuePair<SessionKey, RadioSession> entry in
                    m_sessionsByKey.ToArray())
                {
                    RadioSession session = entry.Value;
                    if (!string.Equals(
                            session.Endpoint.RadioId,
                            normalizedRadioId,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(
                            session.UserId,
                            normalizedUserId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    m_sessionsByKey.Remove(entry.Key);
                    m_sessionsById.Remove(session.SessionId);
                    removed.Add(session);
                }
            }
        }
        finally
        {
            m_creationGate.Release();
        }

        foreach (RadioSession session in removed)
        {
            await session.DisposeAsync();
        }

        return removed.Count;
    }

    public async Task<bool> TerminateOwnedSessionAsync(
        string? sessionId,
        ClaimsPrincipal user)
    {
        if (!IsValidSessionId(sessionId) ||
            !TryGetUserId(user, out string userId))
        {
            return false;
        }

        RadioSession? removed = null;
        await m_creationGate.WaitAsync();
        try
        {
            lock (m_gate)
            {
                if (!m_sessionsById.TryGetValue(
                        sessionId!,
                        out RadioSession? found) ||
                    !string.Equals(
                        found.UserId,
                        userId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                m_sessionsById.Remove(found.SessionId);
                m_sessionsByKey.Remove(
                    SessionKey.From(
                        found.UserId,
                        found.BrowserClientId,
                        found.Endpoint));
                removed = found;
            }
        }
        finally
        {
            m_creationGate.Release();
        }

        await removed.DisposeAsync();
        return true;
    }

    public bool TryGetOwned(
        string? sessionId,
        ClaimsPrincipal user,
        out RadioSession? session)
    {
        session = null;
        if (!IsValidSessionId(sessionId) ||
            !TryGetUserId(user, out string userId))
        {
            return false;
        }

        lock (m_gate)
        {
            if (!m_sessionsById.TryGetValue(sessionId!, out RadioSession? found) ||
                !string.Equals(found.UserId, userId, StringComparison.Ordinal))
            {
                return false;
            }

            found.Touch();
            session = found;
            return true;
        }
    }

    public bool TryAcquire(
        string? sessionId,
        ClaimsPrincipal user,
        out RadioSession? session)
    {
        if (!TryGetOwned(sessionId, user, out session) ||
            session is null)
        {
            return false;
        }

        return session.TryAddClient();
    }

    public static bool TryGetUserId(
        ClaimsPrincipal user,
        out string userId)
    {
        userId =
            user.FindFirstValue("oid") ??
            user.FindFirstValue(ClaimTypes.NameIdentifier) ??
            user.FindFirstValue("sub") ??
            string.Empty;
        userId = userId.Trim();
        return user.Identity?.IsAuthenticated == true &&
               userId.Length is > 0 and <= 256;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RemoveIdleSessionsAsync();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        List<RadioSession> sessions;
        lock (m_gate)
        {
            sessions = m_sessionsById.Values.Distinct().ToList();
            m_sessionsById.Clear();
            m_sessionsByKey.Clear();
        }

        foreach (RadioSession session in sessions)
        {
            await session.DisposeAsync();
        }
    }

    private async Task<RadioSession> GetOrCreateAsync(
        string userId,
        string browserClientId,
        string displayName,
        bool administratorBypass,
        SelectedRadioEndpoint endpoint,
        CancellationToken cancellationToken,
        bool? initialLowBandwidth = null)
    {
        SessionKey key = SessionKey.From(
            userId,
            browserClientId,
            endpoint);
        lock (m_gate)
        {
            if (m_sessionsByKey.TryGetValue(key, out RadioSession? existing))
            {
                existing.Touch();
                if (initialLowBandwidth.HasValue)
                {
                    existing.SetLowBandwidth(initialLowBandwidth.Value);
                }
                return existing;
            }
        }

        await m_creationGate.WaitAsync(cancellationToken);
        try
        {
            lock (m_gate)
            {
                if (m_sessionsByKey.TryGetValue(
                        key,
                        out RadioSession? existing))
                {
                    existing.Touch();
                    if (initialLowBandwidth.HasValue)
                    {
                        existing.SetLowBandwidth(
                            initialLowBandwidth.Value);
                    }
                    return existing;
                }
            }

            string[] activeUserIds;
            lock (m_gate)
            {
                activeUserIds = m_sessionsById.Values
                    .Where(session => string.Equals(
                        session.Endpoint.RadioId,
                        endpoint.RadioId,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(session => session.UserId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }

            RadioAccessDecision decision = accessPolicies.Evaluate(
                endpoint.RadioId,
                userId,
                activeUserIds,
                administratorBypass);
            if (!decision.Allowed)
            {
                throw new RadioAccessDeniedException(
                    endpoint.RadioId,
                    decision.Reason ?? "Access to this radio is denied.");
            }

            RadioSession created = CreateSession(
                userId,
                browserClientId,
                displayName,
                endpoint,
                initialLowBandwidth);
            await created.StartAsync(cancellationToken);
            lock (m_gate)
            {
                m_sessionsByKey.Add(key, created);
                m_sessionsById.Add(created.SessionId, created);
            }
            return created;
        }
        finally
        {
            m_creationGate.Release();
        }
    }

    private RadioSession CreateSession(
        string userId,
        string browserClientId,
        string displayName,
        SelectedRadioEndpoint endpoint,
        bool? initialLowBandwidth)
    {
        string sessionId = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
        RadioSettings sessionSettings = CloneSettings(
            settings.Value,
            endpoint,
            sessionId,
            browserClientId);
        IOptions<RadioSettings> sessionOptions =
            Options.Create(sessionSettings);
        FlexRadioCommandRouter commandRouter = new();
        SessionRadioSelection selection = new(
            endpoint,
            initialLowBandwidth ??
                sessionSettings.LowBandwidthConnect);
        bool isRemote = string.Equals(
            endpoint.Source,
            "remote",
            StringComparison.Ordinal);
        RemoteRadioIntentRouter? remoteIntentRouter =
            isRemote ? new RemoteRadioIntentRouter() : null;
        StationTxProductionLifecycle txLifecycle = new(
            endpoint.RadioId,
            sessionId,
            browserClientId,
            m_gatewayInstanceId,
            txLeaseManager,
            txOccupancyRegistry,
            loggerFactory.CreateLogger<StationTxProductionLifecycle>(),
            independentWatchdogFactory: independentWatchdogs,
            stationCommandVerifier: stationCommandTrust?.Verifier,
            stationCommandSubmitter: stationCommandCoordinator);
        RadioCoordinator coordinator = new(
            loggerFactory.CreateLogger<RadioCoordinator>(),
            sessionOptions,
            txLeaseManager,
            commandRouter,
            remoteIntentRouter,
            txOccupancyRegistry,
            txLifecycle);
        BackgroundService transport =
            isRemote
                ? new RemoteRadioProjectionService(
                    coordinator,
                    remoteIntentRouter!,
                    selection,
                    remoteSettings ??
                        Options.Create(new RemoteStationSettings()),
                    Guid.ParseExact(browserClientId, "N").ToString(),
                    loggerFactory.CreateLogger<
                        RemoteRadioProjectionService>())
                : string.Equals(
                sessionSettings.Mode,
                "Simulation",
                StringComparison.OrdinalIgnoreCase)
                ? new SpectrumSimulationService(
                    coordinator,
                    sessionOptions,
                    loggerFactory.CreateLogger<SpectrumSimulationService>())
                : new FlexRadioRxService(
                    coordinator,
                    commandRouter,
                    selection,
                    sessionOptions,
                    txOccupancyRegistry,
                    loggerFactory.CreateLogger<FlexRadioRxService>());

        return new RadioSession(
            sessionId,
            browserClientId,
            userId,
            displayName,
            endpoint,
            coordinator,
            txLifecycle,
            selection,
            transport,
            loggerFactory.CreateLogger<RadioSession>());
    }

    private async Task RemoveIdleSessionsAsync()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - IdleTimeout;
        List<RadioSession> expired = [];
        lock (m_gate)
        {
            foreach (
                KeyValuePair<SessionKey, RadioSession> entry in
                m_sessionsByKey.ToArray())
            {
                RadioSession session = entry.Value;
                if (session.ClientCount != 0 ||
                    session.LastActivity >= cutoff)
                {
                    continue;
                }

                m_sessionsByKey.Remove(entry.Key);
                m_sessionsById.Remove(session.SessionId);
                expired.Add(session);
            }
        }

        foreach (RadioSession session in expired)
        {
            logger.LogInformation(
                "Removing idle isolated session {SessionId}",
                session.SessionId);
            await session.DisposeAsync();
        }
    }

    private static string GetRequiredUserId(ClaimsPrincipal user)
    {
        if (!TryGetUserId(user, out string userId))
        {
            throw new InvalidOperationException(
                "An authenticated stable user identifier is required.");
        }

        return userId;
    }

    private static string GetRequiredBrowserClientId(
        string? browserClientId)
    {
        string normalized = browserClientId?.Trim().ToLowerInvariant() ??
            string.Empty;
        if (normalized.Length != 32 ||
            !Guid.TryParseExact(normalized, "N", out _))
        {
            throw new ArgumentException(
                "A valid browser client identifier is required.",
                nameof(browserClientId));
        }

        return normalized;
    }

    private static string GetDisplayName(
        ClaimsPrincipal user,
        string userId)
    {
        string displayName =
            user.FindFirstValue("name") ??
            user.Identity?.Name ??
            user.FindFirstValue("preferred_username") ??
            userId;
        displayName = displayName.Trim();
        return displayName.Length is > 0 and <= 256
            ? displayName
            : userId;
    }

    private static bool IsValidSessionId(string? sessionId) =>
        sessionId is { Length: 32 } &&
        sessionId.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static RadioSettings CloneSettings(
        RadioSettings source,
        SelectedRadioEndpoint endpoint,
        string sessionId,
        string browserClientId) =>
        new()
        {
            Mode = string.Equals(
                endpoint.Source,
                "remote",
                StringComparison.Ordinal)
                ? "Remote"
                : source.Mode,
            AllowTransmit = false,
            BrowserTxLeaseEnabled =
                source.BrowserTxLeaseEnabled &&
                !string.Equals(
                    endpoint.Source,
                    "remote",
                    StringComparison.Ordinal),
            RadioId = endpoint.RadioId,
            SessionId = sessionId,
            Host = endpoint.Host,
            TcpPort = endpoint.Port,
            CenterFrequencyHz = source.CenterFrequencyHz,
            BandwidthHz = source.BandwidthHz,
            InitialSliceFrequencyHz = source.InitialSliceFrequencyHz,
            SecondarySliceFrequencyHz = source.SecondarySliceFrequencyHz,
            MinDbm = source.MinDbm,
            MaxDbm = source.MaxDbm,
            XPixels = source.XPixels,
            YPixels = source.YPixels,
            FramesPerSecond = source.FramesPerSecond,
            NetworkMtu = source.NetworkMtu,
            LowBandwidthConnect = source.LowBandwidthConnect,
            StationName = $"{source.StationName}-{sessionId[..8]}",
            // A page reconnect uses the same radio identity, while a separate
            // browser page gets its own GUID and therefore its own FLEX GUI
            // client. The radio remains authoritative for admission.
            GuiClientId = Guid.ParseExact(
                    browserClientId,
                    "N")
                .ToString()
        };

    private readonly record struct SessionKey(
        string UserId,
        string BrowserClientId,
        string RadioId)
    {
        public static SessionKey From(
            string userId,
            string browserClientId,
            SelectedRadioEndpoint endpoint) =>
            new(
                userId,
                browserClientId,
                endpoint.RadioId.Trim().ToUpperInvariant());
    }
}
