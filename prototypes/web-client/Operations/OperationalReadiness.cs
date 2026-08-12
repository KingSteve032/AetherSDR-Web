using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Operations;

public sealed class OperationsSettings
{
    public const string SectionName = "Operations";

    public int MaximumBackupAgeHours { get; init; } = 168;
    public int CertificateWarningDays { get; init; } = 21;
    public int MinimumStorageFreePercent { get; init; } = 10;
    public int ActiveCheckTimeoutSeconds { get; init; } = 10;
    public int MaximumDiagnosticBundleBytes { get; init; } = 8 * 1024 * 1024;
}

public static class OperationsCheckStates
{
    public const string Healthy = "healthy";
    public const string Warning = "warning";
    public const string Failed = "failed";
    public const string NotApplicable = "not-applicable";
}

public static class OperationsAlertSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

public sealed record OperationsCheck(
    string Id,
    string State,
    string Summary,
    string Action,
    DateTimeOffset ObservedAt);

public sealed record OperationsAlert(
    string Code,
    string Severity,
    string Message,
    string Action);

public sealed record OperationsMetrics(
    double MinimumStorageFreePercent,
    double? BackupAgeHours,
    double? CertificateDaysRemaining,
    int RadioCount,
    int OnlineRadioCount,
    int RemoteStationCount,
    int OnlineRemoteStationCount,
    int AvailableReleaseCount,
    int WarningCount,
    int CriticalCount);

public sealed record OperationsReadinessSnapshot(
    int SchemaVersion,
    DateTimeOffset ObservedAt,
    bool Ready,
    bool ActiveConnectivityChecked,
    IReadOnlyList<OperationsCheck> Checks,
    IReadOnlyList<OperationsAlert> Alerts,
    OperationsMetrics Metrics);

/// <summary>
/// Produces bounded operator-facing readiness without issuing a radio command,
/// acquiring a TX lease, changing a service, mutating release state, or reading
/// credential values. Active checks are explicit and constrained to the exact
/// persisted canonical AetherSDR origin plus fixed gateway routes.
/// </summary>
internal sealed class OperationsReadinessService
{
    public const int SchemaVersion = 1;
    private const int MaximumHealthBodyBytes = 64 * 1024;

    private readonly OperationsSettings m_settings;
    private readonly InstallationPaths m_paths;
    private readonly InstallationSetupStore m_setupStore;
    private readonly InstallationBackupService m_backup;
    private readonly ReleaseInstallationStatusReader m_releaseStatus;
    private readonly RadioAdministrationService m_radios;
    private readonly RemoteStationCatalogService m_remoteStations;
    private readonly AetherRemoteBootstrapService m_bootstrap;
    private readonly AetherAuthenticationTopology m_authentication;
    private readonly ReverseProxySettings m_reverseProxy;
    private readonly TimeProvider m_timeProvider;
    private readonly ILogger<OperationsReadinessService> m_logger;
    private readonly SemaphoreSlim m_activeGate = new(1, 1);
    private ActiveConnectivityEvidence? m_lastActiveEvidence;

    public OperationsReadinessService(
        IOptions<OperationsSettings> settings,
        InstallationPaths paths,
        InstallationSetupStore setupStore,
        InstallationBackupService backup,
        ReleaseInstallationStatusReader releaseStatus,
        RadioAdministrationService radios,
        RemoteStationCatalogService remoteStations,
        AetherRemoteBootstrapService bootstrap,
        AetherAuthenticationTopology authentication,
        IOptions<ReverseProxySettings> reverseProxy,
        TimeProvider timeProvider,
        ILogger<OperationsReadinessService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        m_settings = settings.Value ?? new OperationsSettings();
        ValidateSettings(m_settings);
        m_paths = paths ?? throw new ArgumentNullException(nameof(paths));
        InstallationPaths.Validate(m_paths);
        m_setupStore = setupStore ?? throw new ArgumentNullException(nameof(setupStore));
        m_backup = backup ?? throw new ArgumentNullException(nameof(backup));
        m_releaseStatus = releaseStatus ?? throw new ArgumentNullException(nameof(releaseStatus));
        m_radios = radios ?? throw new ArgumentNullException(nameof(radios));
        m_remoteStations = remoteStations ??
            throw new ArgumentNullException(nameof(remoteStations));
        m_bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        m_authentication = authentication ??
            throw new ArgumentNullException(nameof(authentication));
        ArgumentNullException.ThrowIfNull(reverseProxy);
        m_reverseProxy = reverseProxy.Value ?? new ReverseProxySettings();
        m_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        m_logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OperationsReadinessSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        List<OperationsCheck> checks = [];
        InstallationSetupState? setup = await TryLoadSetupAsync(cancellationToken);
        bool setupComplete = setup?.Lock.Mode == InstallationSetupLockMode.Complete;
        Add(checks,
            "setup",
            setupComplete ? OperationsCheckStates.Healthy : OperationsCheckStates.Failed,
            setupComplete
                ? "Installation setup is complete."
                : "Installation setup is incomplete or unavailable.",
            setupComplete ? string.Empty : "Complete the protected setup workflow.",
            now);

        bool canonical = setupComplete &&
            TryCanonicalPublicUrl(setup!.CanonicalPublicUrl, out _);
        Add(checks,
            "public-url",
            canonical ? OperationsCheckStates.Healthy : OperationsCheckStates.Failed,
            canonical
                ? "The persisted public URL is canonical HTTPS."
                : "The canonical public HTTPS URL is unavailable or invalid.",
            canonical ? string.Empty : "Correct the installation public URL before exposing the gateway.",
            now);
        Add(checks,
            "proxy-forwarded-headers",
            m_reverseProxy.Enabled
                ? OperationsCheckStates.Healthy
                : OperationsCheckStates.Warning,
            m_reverseProxy.Enabled
                ? $"Forwarded-header processing trusts {m_reverseProxy.KnownProxies.Length} configured proxy endpoint(s) with a one-hop limit."
                : "Forwarded-header processing is disabled.",
            m_reverseProxy.Enabled
                ? string.Empty
                : "Enable reviewed forwarded-header processing when HTTPS terminates at a reverse proxy.",
            now);

        StorageEvidence storage = InspectStorage();
        Add(checks,
            "storage",
            storage.MinimumFreePercent >= m_settings.MinimumStorageFreePercent
                ? OperationsCheckStates.Healthy
                : OperationsCheckStates.Failed,
            $"Minimum free storage across installation roots is {storage.MinimumFreePercent:F1}%.",
            storage.MinimumFreePercent >= m_settings.MinimumStorageFreePercent
                ? string.Empty
                : "Free disk space or move installation data before backup/update operations.",
            now);

        InstallationBackupReadiness backup =
            await m_backup.InspectReadinessAsync(cancellationToken);
        bool backupFresh = backup.LatestBackupAgeSeconds is not null &&
            backup.LatestBackupAgeSeconds.Value <=
                checked((long)m_settings.MaximumBackupAgeHours * 3600L);
        string backupState = !backup.Ready
            ? OperationsCheckStates.Failed
            : backupFresh
                ? OperationsCheckStates.Healthy
                : OperationsCheckStates.Warning;
        Add(checks,
            "backup",
            backupState,
            !backup.Ready
                ? backup.Message
                : backup.LatestBackupCreatedAt is null
                    ? "Backup prerequisites are ready, but no encrypted backup has been observed."
                    : backupFresh
                        ? "The latest encrypted backup is within the configured age objective."
                        : "The latest encrypted backup is older than the configured age objective.",
            backupState == OperationsCheckStates.Healthy
                ? string.Empty
                : "Create and verify a new encrypted backup.",
            now);

        ReleaseStatusReadResult release =
            await m_releaseStatus.ReadAsync(cancellationToken);
        bool updateReady = release.Succeeded && release.SetupComplete &&
            release.CurrentPointerPresent &&
            !string.IsNullOrEmpty(release.ActiveReleaseIdentity);
        Add(checks,
            "update-readiness",
            updateReady ? OperationsCheckStates.Healthy : OperationsCheckStates.Failed,
            updateReady
                ? "The active release and immutable release inventory are consistent."
                : "Release status is not ready for a supported update.",
            updateReady ? string.Empty : "Reconcile setup and the active immutable release before updating.",
            now);
        int rollbackCandidates = release.Succeeded
            ? release.AvailableReleaseIdentities.Count(identity =>
                !string.Equals(identity, release.ActiveReleaseIdentity, StringComparison.Ordinal))
            : 0;
        Add(checks,
            "rollback-readiness",
            rollbackCandidates > 0
                ? OperationsCheckStates.Healthy
                : OperationsCheckStates.Warning,
            rollbackCandidates > 0
                ? $"{rollbackCandidates} inactive immutable release candidate(s) are retained."
                : "No inactive immutable release is currently retained for rollback.",
            rollbackCandidates > 0
                ? string.Empty
                : "Retain the previous verified release through the next successful update.",
            now);

        IReadOnlyList<AdminRadioSnapshot> radios = m_radios.GetInventory();
        int onlineRadios = radios.Count(radio => radio.Health.State == AdminRadioHealthStates.Healthy);
        Add(checks,
            "radio-discovery",
            radios.Count == 0
                ? OperationsCheckStates.Warning
                : onlineRadios > 0
                    ? OperationsCheckStates.Healthy
                    : OperationsCheckStates.Warning,
            radios.Count == 0
                ? "No local or projected FLEX radio is currently discovered."
                : $"{onlineRadios} of {radios.Count} discovered radio(s) report healthy operational state.",
            onlineRadios > 0
                ? string.Empty
                : "Check FLEX discovery, station connectivity, and radio client capacity.",
            now);

        RemoteStationAdministrationSnapshot remote =
            m_remoteStations.GetAdministrationSnapshot();
        int onlineStations = remote.Stations.Count(station =>
            string.Equals(station.State, "online", StringComparison.Ordinal));
        string stationState = !remote.Enabled
            ? OperationsCheckStates.NotApplicable
            : remote.BrokerReachable
                ? OperationsCheckStates.Healthy
                : OperationsCheckStates.Warning;
        Add(checks,
            "station-websocket",
            stationState,
            !remote.Enabled
                ? "Remote stations are disabled for this topology."
                : remote.BrokerReachable
                    ? $"The station broker is reachable; {onlineStations} station(s) are online."
                    : "The configured station broker is not currently reachable.",
            remote.Enabled && !remote.BrokerReachable
                ? "Check the scoped /aetherremote/broker proxy route and broker service."
                : string.Empty,
            now);

        AetherRemoteBootstrapAdminGuide bootstrap =
            await m_bootstrap.GetAdminGuideAsync(null, cancellationToken);
        string compatibilityState = !bootstrap.Enabled
            ? OperationsCheckStates.NotApplicable
            : bootstrap.Ready
                ? OperationsCheckStates.Healthy
                : OperationsCheckStates.Warning;
        Add(checks,
            "aetherremote-compatibility",
            compatibilityState,
            bootstrap.Message,
            bootstrap.Enabled && !bootstrap.Ready
                ? "Reconcile the active signed release and release trust before station bootstrap/update."
                : string.Empty,
            now);

        bool authReady = m_authentication.Mode switch
        {
            AetherAuthenticationMode.Development => true,
            AetherAuthenticationMode.Local => m_authentication.LocalAccountsEnabled,
            AetherAuthenticationMode.MicrosoftEntraId or
                AetherAuthenticationMode.OpenIdConnect =>
                    m_authentication.ExternalProvider is not null,
            AetherAuthenticationMode.Combined =>
                m_authentication.LocalAccountsEnabled &&
                m_authentication.ExternalProvider is not null,
            AetherAuthenticationMode.ServiceBoundary => true,
            _ => false
        };
        Add(checks,
            "authentication-callback",
            authReady ? OperationsCheckStates.Healthy : OperationsCheckStates.Failed,
            authReady
                ? m_authentication.ExternalProvider is null
                    ? "Authentication does not require an external callback for the active mode."
                    : $"External authentication callback path {m_authentication.ExternalProvider.CallbackPath} is configured."
                : "Authentication topology is incomplete.",
            authReady ? string.Empty : "Reconcile authentication provider configuration before sign-in.",
            now);

        Add(checks,
            "browser-websocket",
            canonical ? OperationsCheckStates.Healthy : OperationsCheckStates.Failed,
            canonical
                ? "The authenticated browser WebSocket route is registered behind the canonical origin."
                : "Browser WebSocket readiness requires the canonical public origin.",
            canonical ? string.Empty : "Correct the canonical public URL and reverse proxy.",
            now);

        bool txEligible = radios.Any(radio => string.Equals(
            radio.Onboarding.TransmitPolicyState,
            RadioTransmitPolicyStates.TxEligible,
            StringComparison.Ordinal));
        Add(checks,
            "tx-prerequisites",
            txEligible
                ? OperationsCheckStates.Warning
                : OperationsCheckStates.Healthy,
            txEligible
                ? "One or more radios are policy-eligible for TX; executable authority still requires the independent production TX safety boundary."
                : "No discovered radio is policy-eligible for TX; receive-only operation remains fail closed.",
            "Use the existing per-radio production TX preflight; diagnostics never key or acquire a TX lease.",
            now);

        ActiveConnectivityEvidence? active = Volatile.Read(ref m_lastActiveEvidence);
        if (active is not null)
        {
            Add(checks,
                "tls",
                active.TlsValid && active.CertificateDaysRemaining >= 0
                    ? active.CertificateDaysRemaining < m_settings.CertificateWarningDays
                        ? OperationsCheckStates.Warning
                        : OperationsCheckStates.Healthy
                    : OperationsCheckStates.Failed,
                active.TlsValid
                    ? $"Canonical TLS validated; certificate has {active.CertificateDaysRemaining:F1} day(s) remaining."
                    : active.ErrorMessage,
                active.TlsValid && active.CertificateDaysRemaining >= m_settings.CertificateWarningDays
                    ? string.Empty
                    : "Renew or correct the canonical TLS certificate and trust chain.",
                active.ObservedAt);
            Add(checks,
                "proxy-security-headers",
                active.SecurityHeadersValid
                    ? OperationsCheckStates.Healthy
                    : OperationsCheckStates.Warning,
                active.SecurityHeadersValid
                    ? "Canonical health response includes the required public security headers."
                    : "Canonical health response is missing one or more expected security headers.",
                active.SecurityHeadersValid
                    ? string.Empty
                    : "Reconcile reverse-proxy and gateway response-header configuration.",
                active.ObservedAt);
            if (m_authentication.ExternalProvider is not null)
            {
                Add(checks,
                    "authentication-callback-active",
                    active.AuthenticationCallbackBoundaryReached
                        ? OperationsCheckStates.Healthy
                        : OperationsCheckStates.Warning,
                    active.AuthenticationCallbackBoundaryReached
                        ? "The configured external authentication callback path is reachable through the canonical public origin."
                        : "The configured external authentication callback path was not reached through the canonical public origin.",
                    active.AuthenticationCallbackBoundaryReached
                        ? string.Empty
                        : "Check reverse-proxy routing for the configured authentication callback path.",
                    active.ObservedAt);
            }
            Add(checks,
                "browser-websocket-active",
                active.BrowserWebSocketBoundaryReached
                    ? OperationsCheckStates.Healthy
                    : OperationsCheckStates.Warning,
                active.BrowserWebSocketBoundaryReached
                    ? "The canonical browser WebSocket route reached its authentication boundary."
                    : "The canonical browser WebSocket route was not reached through the public origin.",
                active.BrowserWebSocketBoundaryReached
                    ? string.Empty
                    : "Check WebSocket upgrade forwarding for /ws/radio.",
                active.ObservedAt);
            if (remote.Enabled)
            {
                Add(checks,
                    "station-websocket-active",
                    active.StationWebSocketBoundaryReached
                        ? OperationsCheckStates.Healthy
                        : OperationsCheckStates.Warning,
                    active.StationWebSocketBoundaryReached
                        ? "The canonical station WebSocket route reached the broker authentication boundary."
                        : "The canonical station WebSocket route was not reached through the scoped proxy prefix.",
                    active.StationWebSocketBoundaryReached
                        ? string.Empty
                        : "Check /aetherremote/broker WebSocket proxy forwarding and broker health.",
                    active.ObservedAt);
            }
        }

        List<OperationsAlert> alerts = BuildAlerts(checks);
        double? backupAgeHours = backup.LatestBackupAgeSeconds is null
            ? null
            : backup.LatestBackupAgeSeconds.Value / 3600d;
        OperationsMetrics metrics = new(
            storage.MinimumFreePercent,
            backupAgeHours,
            active?.CertificateDaysRemaining,
            radios.Count,
            onlineRadios,
            remote.Stations.Count,
            onlineStations,
            release.AvailableReleaseCount,
            alerts.Count(alert => alert.Severity == OperationsAlertSeverities.Warning),
            alerts.Count(alert => alert.Severity == OperationsAlertSeverities.Critical));
        bool ready = checks.All(check =>
            check.State is OperationsCheckStates.Healthy or
                OperationsCheckStates.NotApplicable or
                OperationsCheckStates.Warning) &&
            alerts.All(alert => alert.Severity != OperationsAlertSeverities.Critical);
        return new OperationsReadinessSnapshot(
            SchemaVersion,
            now,
            ready,
            active is not null,
            checks,
            alerts,
            metrics);
    }

    public async Task<OperationsReadinessSnapshot> RunActiveChecksAsync(
        CancellationToken cancellationToken = default)
    {
        await m_activeGate.WaitAsync(cancellationToken);
        try
        {
            InstallationSetupState setup = await m_setupStore.LoadAsync(cancellationToken);
            InstallationSetupStateValidator.Validate(setup);
            if (setup.Lock.Mode != InstallationSetupLockMode.Complete ||
                !TryCanonicalPublicUrl(setup.CanonicalPublicUrl, out Uri? origin))
            {
                throw new InvalidOperationException(
                    "Active operational checks require complete canonical HTTPS setup.");
            }

            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(m_settings.ActiveCheckTimeoutSeconds));
            ActiveConnectivityEvidence evidence = await ProbeCanonicalOriginAsync(
                origin!,
                m_remoteStations.GetAdministrationSnapshot().Enabled,
                m_authentication.ExternalProvider?.CallbackPath,
                timeout.Token);
            Volatile.Write(ref m_lastActiveEvidence, evidence);
            m_logger.LogInformation(
                "Operational active check completed: tls={TlsValid} headers={HeadersValid} authCallback={AuthCallback} browserWs={BrowserWs} stationWs={StationWs}",
                evidence.TlsValid,
                evidence.SecurityHeadersValid,
                evidence.AuthenticationCallbackBoundaryReached,
                evidence.BrowserWebSocketBoundaryReached,
                evidence.StationWebSocketBoundaryReached);
            return await GetSnapshotAsync(cancellationToken);
        }
        finally
        {
            m_activeGate.Release();
        }
    }

    private async Task<ActiveConnectivityEvidence> ProbeCanonicalOriginAsync(
        Uri origin,
        bool stationRouteExpected,
        string? authenticationCallbackPath,
        CancellationToken cancellationToken)
    {
        X509Certificate2? observedCertificate = null;
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback =
                (_, certificate, _, errors) =>
                {
                    if (certificate is not null)
                    {
                        observedCertificate = new X509Certificate2(certificate);
                    }
                    return errors == SslPolicyErrors.None;
                }
        };
        using (handler)
        using (HttpClient client = new(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        })
        {
            try
            {
                using HttpResponseMessage health = await client.GetAsync(
                    new Uri(origin, "/healthz"),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                byte[] body = await ReadBoundedAsync(
                    health.Content,
                    MaximumHealthBodyBytes,
                    cancellationToken);
                bool healthOk = health.StatusCode == HttpStatusCode.OK && body.Length > 0;
                bool headers = health.Headers.Contains("Strict-Transport-Security") &&
                    health.Headers.Contains("X-Content-Type-Options") &&
                    health.Headers.Contains("Referrer-Policy");
                bool authenticationCallback =
                    string.IsNullOrEmpty(authenticationCallbackPath) ||
                    await ProbeHttpBoundaryAsync(
                        client,
                        new Uri(origin, authenticationCallbackPath),
                        cancellationToken);
                bool browserWs = await ProbeProtectedWebSocketRouteAsync(
                    client,
                    new Uri(origin, "/ws/radio"),
                    cancellationToken);
                bool stationWs = !stationRouteExpected ||
                    await ProbeProtectedWebSocketRouteAsync(
                        client,
                        new Uri(origin, "/aetherremote/broker/station/v1"),
                        cancellationToken);
                double days = observedCertificate is null
                    ? -1
                    : (observedCertificate.NotAfter.ToUniversalTime() -
                        m_timeProvider.GetUtcNow().UtcDateTime).TotalDays;
                return new ActiveConnectivityEvidence(
                    m_timeProvider.GetUtcNow(),
                    healthOk && observedCertificate is not null,
                    headers,
                    authenticationCallback,
                    browserWs,
                    stationWs,
                    days,
                    healthOk
                        ? string.Empty
                        : "The canonical public health endpoint did not return the expected healthy response.");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or
                    IOException or WebSocketException)
            {
                return new ActiveConnectivityEvidence(
                    m_timeProvider.GetUtcNow(),
                    false,
                    false,
                    string.IsNullOrEmpty(authenticationCallbackPath),
                    false,
                    !stationRouteExpected,
                    -1,
                    $"Canonical public connectivity check failed: {exception.GetType().Name}.");
            }
            finally
            {
                observedCertificate?.Dispose();
            }
        }
    }

    private static async Task<bool> ProbeHttpBoundaryAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return response.StatusCode != HttpStatusCode.NotFound &&
            (int)response.StatusCode < 500;
    }

    private static async Task<bool> ProbeProtectedWebSocketRouteAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        request.Headers.TryAddWithoutValidation(
            "Sec-WebSocket-Key",
            Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return response.StatusCode is HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or HttpStatusCode.Redirect or
            HttpStatusCode.BadRequest or HttpStatusCode.UpgradeRequired;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream output = new();
        byte[] buffer = new byte[4096];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException(
                    "The operational health response exceeded the supported bound.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private StorageEvidence InspectStorage()
    {
        HashSet<string> roots = new(StringComparer.Ordinal);
        List<double> percentages = [];
        foreach (string path in new[]
        {
            m_paths.ConfigurationDirectory,
            m_paths.StateDirectory,
            m_paths.SecretDirectory,
            m_paths.ReleaseDirectory,
            m_paths.BackupDirectory,
            m_paths.LogDirectory
        })
        {
            string existing = ExistingAncestor(path);
            string root = Path.GetPathRoot(existing) ?? existing;
            if (!roots.Add(root))
            {
                continue;
            }
            DriveInfo drive = new(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                percentages.Add(0);
                continue;
            }
            percentages.Add(100d * drive.AvailableFreeSpace / drive.TotalSize);
        }
        return new StorageEvidence(percentages.Count == 0 ? 0 : percentages.Min());
    }

    private static string ExistingAncestor(string path)
    {
        string current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return Path.GetPathRoot(current) ?? current;
            }
            current = parent;
        }
        return current;
    }

    private async Task<InstallationSetupState?> TryLoadSetupAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            InstallationSetupState state = await m_setupStore.LoadAsync(cancellationToken);
            InstallationSetupStateValidator.Validate(state);
            return state;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or InvalidOperationException or
                InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryCanonicalPublicUrl(string value, out Uri? uri)
    {
        uri = null;
        try
        {
            CanonicalPublicUrl canonical = CanonicalPublicUrl.Parse(value);
            uri = canonical.Uri;
            return string.Equals(canonical.Value, value, StringComparison.Ordinal) &&
                uri.Scheme == Uri.UriSchemeHttps;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static List<OperationsAlert> BuildAlerts(
        IReadOnlyList<OperationsCheck> checks)
    {
        List<OperationsAlert> alerts = [];
        foreach (OperationsCheck check in checks)
        {
            if (check.State is OperationsCheckStates.Healthy or
                OperationsCheckStates.NotApplicable)
            {
                continue;
            }
            string severity = check.State == OperationsCheckStates.Failed
                ? OperationsAlertSeverities.Critical
                : OperationsAlertSeverities.Warning;
            alerts.Add(new OperationsAlert(
                check.Id,
                severity,
                check.Summary,
                check.Action));
        }
        return alerts;
    }

    private static void Add(
        ICollection<OperationsCheck> checks,
        string id,
        string state,
        string summary,
        string action,
        DateTimeOffset observedAt) =>
        checks.Add(new OperationsCheck(id, state, summary, action, observedAt));

    public static void ValidateSettings(OperationsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaximumBackupAgeHours is < 1 or > 24 * 365 ||
            settings.CertificateWarningDays is < 1 or > 365 ||
            settings.MinimumStorageFreePercent is < 1 or > 50 ||
            settings.ActiveCheckTimeoutSeconds is < 2 or > 60 ||
            settings.MaximumDiagnosticBundleBytes is < 1024 * 1024 or > 64 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "Operations configuration contains an unsupported safety or resource bound.");
        }
    }

    private sealed record StorageEvidence(double MinimumFreePercent);

    private sealed record ActiveConnectivityEvidence(
        DateTimeOffset ObservedAt,
        bool TlsValid,
        bool SecurityHeadersValid,
        bool AuthenticationCallbackBoundaryReached,
        bool BrowserWebSocketBoundaryReached,
        bool StationWebSocketBoundaryReached,
        double CertificateDaysRemaining,
        string ErrorMessage);
}
