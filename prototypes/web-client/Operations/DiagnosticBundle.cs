using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Operations;

public sealed record OperationsDiagnosticBundle(
    string FileName,
    byte[] Content,
    DateTimeOffset CreatedAt);

internal sealed record DiagnosticRuntimeSummary(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string GatewayVersion,
    string Framework,
    string OperatingSystem,
    string Architecture,
    bool SetupComplete,
    long? SetupRevision,
    string Topology,
    string AuthenticationMode,
    bool LocalAccountsEnabled,
    bool ExternalAuthenticationConfigured,
    bool CanonicalPublicUrlConfigured,
    string ActiveReleaseIdentity,
    int AvailableReleaseCount,
    bool RollbackCandidateKnown);

internal sealed record DiagnosticRadioSummary(
    string HealthState,
    bool Online,
    bool Onboarded,
    string TransmitPolicyState,
    int ConnectedClientCount,
    int OperatorCount,
    int SessionCount);

internal sealed record DiagnosticStationSummary(
    string State,
    string SoftwareVersion,
    string ReleaseIdentity,
    string StationEngineVersion,
    int RadioCount,
    int ReceiveSessionCount);

internal sealed record DiagnosticAuditAggregate(
    string Action,
    string Result,
    int Count);

internal sealed record DiagnosticInventorySummary(
    int RadioCount,
    IReadOnlyList<DiagnosticRadioSummary> Radios,
    bool RemoteStationsEnabled,
    bool BrokerReachable,
    int StationCount,
    IReadOnlyList<DiagnosticStationSummary> Stations,
    int StationCredentialRecordCount,
    IReadOnlyList<DiagnosticAuditAggregate> AdministrativeActivity);

/// <summary>
/// Builds a bounded support bundle from already-redacted operational projections.
/// It intentionally excludes raw configuration, logs, environment variables,
/// request headers, URLs, user/actor identifiers, radio/station identifiers,
/// serial numbers, credentials, token material, key material, and file contents.
/// </summary>
internal sealed class OperationsDiagnosticBundleService
{
    private const int BundleSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 32
    };

    private readonly OperationsSettings m_settings;
    private readonly OperationsReadinessService m_readiness;
    private readonly InstallationSetupStore m_setupStore;
    private readonly ReleaseInstallationStatusReader m_releaseStatus;
    private readonly RadioAdministrationService m_radios;
    private readonly RemoteStationCatalogService m_remoteStations;
    private readonly AdministrativeAuditStore m_audit;
    private readonly AetherAuthenticationTopology m_authentication;
    private readonly TimeProvider m_timeProvider;

    public OperationsDiagnosticBundleService(
        IOptions<OperationsSettings> settings,
        OperationsReadinessService readiness,
        InstallationSetupStore setupStore,
        ReleaseInstallationStatusReader releaseStatus,
        RadioAdministrationService radios,
        RemoteStationCatalogService remoteStations,
        AdministrativeAuditStore audit,
        AetherAuthenticationTopology authentication,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        m_settings = settings.Value ?? new OperationsSettings();
        OperationsReadinessService.ValidateSettings(m_settings);
        m_readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        m_setupStore = setupStore ?? throw new ArgumentNullException(nameof(setupStore));
        m_releaseStatus = releaseStatus ?? throw new ArgumentNullException(nameof(releaseStatus));
        m_radios = radios ?? throw new ArgumentNullException(nameof(radios));
        m_remoteStations = remoteStations ??
            throw new ArgumentNullException(nameof(remoteStations));
        m_audit = audit ?? throw new ArgumentNullException(nameof(audit));
        m_authentication = authentication ??
            throw new ArgumentNullException(nameof(authentication));
        m_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<OperationsDiagnosticBundle> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        OperationsReadinessSnapshot readiness =
            await m_readiness.GetSnapshotAsync(cancellationToken);
        InstallationSetupState? setup = await TryLoadSetupAsync(cancellationToken);
        ReleaseStatusReadResult release =
            await m_releaseStatus.ReadAsync(cancellationToken);
        IReadOnlyList<AdminRadioSnapshot> radios = m_radios.GetInventory();
        RemoteStationAdministrationSnapshot remote =
            m_remoteStations.GetAdministrationSnapshot();

        DiagnosticRuntimeSummary runtime = new(
            BundleSchemaVersion,
            now,
            typeof(OperationsDiagnosticBundleService).Assembly.GetName().Version?.ToString()
                ?? "unknown",
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            setup?.Lock.Mode == InstallationSetupLockMode.Complete,
            setup?.Revision,
            setup?.Topology?.ToString() ?? "unknown",
            m_authentication.Mode.ToString(),
            m_authentication.LocalAccountsEnabled,
            m_authentication.ExternalProvider is not null,
            setup is not null && !string.IsNullOrWhiteSpace(setup.CanonicalPublicUrl),
            release.Succeeded ? release.ActiveReleaseIdentity : string.Empty,
            release.Succeeded ? release.AvailableReleaseCount : 0,
            release.Succeeded && release.AvailableReleaseIdentities.Any(identity =>
                !string.Equals(identity, release.ActiveReleaseIdentity, StringComparison.Ordinal)));

        DiagnosticInventorySummary inventory = new(
            radios.Count,
            radios.Select(radio => new DiagnosticRadioSummary(
                    radio.Health.State,
                    radio.Online,
                    radio.Onboarding.Onboarded,
                    radio.Onboarding.TransmitPolicyState,
                    radio.ConnectedClients.Count,
                    radio.Operators.Count,
                    radio.Sessions.Count))
                .ToArray(),
            remote.Enabled,
            remote.BrokerReachable,
            remote.Stations.Count,
            remote.Stations.Select(station => new DiagnosticStationSummary(
                    station.State,
                    BoundVersion(station.SoftwareVersion),
                    BoundReleaseIdentity(station.ReleaseIdentity),
                    BoundVersion(station.StationEngineVersion),
                    station.Radios.Count,
                    station.ReceiveSessions.Count))
                .ToArray(),
            remote.Credentials.Count,
            AggregateAudit());

        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddJsonAsync(archive, "runtime.json", runtime, cancellationToken);
            await AddJsonAsync(archive, "operations.json", readiness, cancellationToken);
            await AddJsonAsync(archive, "inventory.json", inventory, cancellationToken);
            await AddTextAsync(
                archive,
                "README.txt",
                BuildReadme(),
                cancellationToken);
        }
        if (buffer.Length <= 0 || buffer.Length > m_settings.MaximumDiagnosticBundleBytes)
        {
            throw new InvalidOperationException(
                "The redacted diagnostic bundle exceeded the configured size bound.");
        }

        byte[] content = buffer.ToArray();
        string fileName = $"aethersdr-diagnostics-{now:yyyyMMddTHHmmssZ}.zip";
        return new OperationsDiagnosticBundle(fileName, content, now);
    }

    private IReadOnlyList<DiagnosticAuditAggregate> AggregateAudit() =>
        m_audit.GetRecent(200)
            .GroupBy(
                entry => (entry.Action, entry.Result),
                StringTupleComparer.Instance)
            .Select(group => new DiagnosticAuditAggregate(
                BoundToken(group.Key.Action),
                BoundToken(group.Key.Result),
                group.Count()))
            .OrderBy(entry => entry.Action, StringComparer.Ordinal)
            .ThenBy(entry => entry.Result, StringComparer.Ordinal)
            .ToArray();

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

    private static async Task AddJsonAsync<T>(
        ZipArchive archive,
        string name,
        T value,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using Stream stream = entry.Open();
        await JsonSerializer.SerializeAsync(
            stream,
            value,
            JsonOptions,
            cancellationToken);
    }

    private static async Task AddTextAsync(
        ZipArchive archive,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using Stream stream = entry.Open();
        byte[] content = Encoding.UTF8.GetBytes(value);
        await stream.WriteAsync(content, cancellationToken);
    }

    private static string BuildReadme() =>
        "AetherSDR diagnostic bundle schema 1\n" +
        "\n" +
        "This support bundle is intentionally strongly redacted. It contains only " +
        "bounded runtime/version metadata, aggregate operational readiness, radio/station " +
        "health counts, release identities, and aggregate administrative action/result " +
        "counts. It does not include passwords, password hashes, MFA seeds/recovery " +
        "material, Data Protection keys, private signing keys, public-key bytes, station " +
        "credentials, runtime/admin credentials, enrollment codes, bearer/session/CSRF " +
        "tokens, authentication client secrets, environment variables, raw configuration, " +
        "URLs, logs, request headers, user identifiers, radio identifiers/serial numbers, " +
        "station identifiers, IP addresses, or file contents.\n";

    private static string BoundVersion(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Length <= 64
                ? value.Trim()
                : "invalid-or-oversized";

    private static string BoundReleaseIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return string.Empty;
        }
        try
        {
            return string.Equals(
                    InstallationReleaseIdentity.Parse(value),
                    value,
                    StringComparison.Ordinal)
                ? value
                : string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string BoundToken(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Length <= 96 && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
                ? value
                : "redacted";

    private sealed class StringTupleComparer : IEqualityComparer<(string Action, string Result)>
    {
        internal static StringTupleComparer Instance { get; } = new();

        public bool Equals(
            (string Action, string Result) left,
            (string Action, string Result) right) =>
            string.Equals(left.Action, right.Action, StringComparison.Ordinal) &&
            string.Equals(left.Result, right.Result, StringComparison.Ordinal);

        public int GetHashCode((string Action, string Result) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.Action ?? string.Empty),
                StringComparer.Ordinal.GetHashCode(value.Result ?? string.Empty));
    }
}
