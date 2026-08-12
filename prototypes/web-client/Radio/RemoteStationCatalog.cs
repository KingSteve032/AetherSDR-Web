using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed class RemoteStationSettings
{
    public const string SectionName = "RemoteStations";

    public bool Enabled { get; set; }
    public string BrokerUrl { get; set; } =
        "http://127.0.0.1:5090";
    public string RuntimeCredentialFile { get; set; } = string.Empty;
    public string AdministrationCredentialFile { get; set; } = string.Empty;
    public int RefreshSeconds { get; set; } = 3;
}

public sealed record RemoteRadioCatalogEntry(
    string SelectorId,
    string StationId,
    string SourceRadioId,
    string Model,
    string Serial,
    string Nickname,
    string Status,
    bool StationOnline,
    bool ReceiveProjectionReady,
    int AvailableClients,
    int LicensedClients,
    DateTimeOffset LastSeen);

public sealed record RemoteStationRadioAdministrationEntry(
    string RadioId,
    string Model,
    string Serial,
    string Nickname,
    string Status,
    int AvailableClients,
    int LicensedClients);

public sealed record RemoteReceiveSessionAdministrationEntry(
    string SessionId,
    string StationId,
    string RadioId,
    string State,
    string RadioModel,
    string Serial,
    string ClientHandle,
    DateTimeOffset OpenedAt);

public sealed record RemoteStationAdministrationEntry(
    string StationId,
    string InstanceId,
    string State,
    string SoftwareVersion,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeen,
    long HeartbeatSequence,
    long InventorySequence,
    int ConnectionCount,
    DateTimeOffset? LastDisconnectedAt,
    string? LastDisconnectReason,
    DateTimeOffset? LastRecoveredAt,
    long? LastRecoveryMilliseconds,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<RemoteStationRadioAdministrationEntry> Radios,
    IReadOnlyList<RemoteReceiveSessionAdministrationEntry> ReceiveSessions,
    string ReleaseIdentity = "",
    string StationEngineVersion = "");

public sealed record RemoteStationCredentialAdministrationEntry(
    string StationId,
    string State,
    string Source,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? RotatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RemoteStationAdministrationSnapshot(
    bool Enabled,
    bool BrokerReachable,
    DateTimeOffset? RefreshedAt,
    string? Error,
    IReadOnlyList<RemoteStationAdministrationEntry> Stations,
    IReadOnlyList<RemoteStationCredentialAdministrationEntry> Credentials);

public sealed record CreateRemoteStationEnrollmentRequest(string StationId);

public sealed record RedeemRemoteStationEnrollmentRequest(
    string EnrollmentCode,
    string CredentialSha256);

public sealed record RemoteStationEnrollmentCodeResult(
    string StationId,
    string EnrollmentCode,
    string Purpose,
    DateTimeOffset ExpiresAt);

public sealed record RemoteStationEnrollmentResult(
    string StationId,
    string State,
    string Purpose,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? RotatedAt);

public sealed record RemoteReleaseServiceControlRequest(
    string StationId,
    string ReleaseIdentity,
    string Phase,
    string Action,
    string ServiceRole,
    string UnitIdentity);

public sealed record RemoteReleaseServiceControlResult(
    string StationId,
    string CorrelationId,
    string ReleaseIdentity,
    string Phase,
    string Action,
    string ServiceRole,
    string UnitIdentity,
    bool Succeeded,
    string Outcome);

public sealed record RemoteReleaseUpdateRequest(
    string StationId,
    string ReleaseIdentity);

public sealed record RemoteReleaseUpdateResult(
    string StationId,
    string CorrelationId,
    string ReleaseIdentity,
    bool Succeeded,
    string Outcome,
    string ActiveReleaseIdentity,
    bool RolledBack);

public sealed class RemoteStationManagementException(
    HttpStatusCode statusCode,
    string message)
    : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed record RemoteStationCatalogSnapshot(
    IReadOnlyList<RemoteRadioCatalogEntry> Radios,
    IReadOnlyList<RemoteStationAdministrationEntry> Stations);

public sealed class RemoteStationCatalogService(
    IOptions<RemoteStationSettings> options,
    IHttpClientFactory httpClientFactory,
    RadioSelectionManager radioCatalog,
    ILogger<RemoteStationCatalogService> logger)
    : BackgroundService
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly RemoteStationSettings m_settings = options.Value;
    private RemoteStationAdministrationSnapshot m_administration =
        new(
            options.Value.Enabled,
            false,
            null,
            null,
            [],
            []);

    public RemoteStationAdministrationSnapshot GetAdministrationSnapshot() =>
        Volatile.Read(ref m_administration);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (!m_settings.Enabled)
        {
            return;
        }

        Uri inventoryUri = new(
            RemoteStationSettingsValidator.GetBrokerBaseUri(m_settings),
            "api/stations");
        Uri sessionsUri = new(
            RemoteStationSettingsValidator.GetBrokerBaseUri(m_settings),
            "api/receive-sessions");
        Uri credentialsUri = new(
            RemoteStationSettingsValidator.GetBrokerBaseUri(m_settings),
            "api/station-credentials");
        string runtimeCredential =
            RemoteStationSettingsValidator.ReadCredential(
                m_settings.RuntimeCredentialFile,
                "runtime");
        string administrationCredential =
            RemoteStationSettingsValidator.ReadCredential(
                m_settings.AdministrationCredentialFile,
                "administration");
        if (RemoteStationSettingsValidator.CredentialsMatch(
                runtimeCredential,
                administrationCredential))
        {
            throw new InvalidOperationException(
                "Remote station runtime and administration credentials " +
                "must be distinct.");
        }
        using PeriodicTimer timer = new(
            TimeSpan.FromSeconds(m_settings.RefreshSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RemoteStationCatalogSnapshot catalog =
                    RemoteStationCatalogParser.ParseSnapshot(
                        await FetchAsync(
                            inventoryUri,
                            runtimeCredential,
                            "The remote station inventory is too large.",
                            stoppingToken));
                radioCatalog.ReplaceRemoteRadios(catalog.Radios);

                IReadOnlyList<RemoteReceiveSessionAdministrationEntry>
                    receiveSessions = [];
                IReadOnlyList<RemoteStationCredentialAdministrationEntry>
                    stationCredentials =
                        GetAdministrationSnapshot().Credentials;
                List<string> partialErrors = [];
                try
                {
                    receiveSessions =
                        RemoteReceiveSessionInventoryParser.Parse(
                            await FetchAsync(
                                sessionsUri,
                                runtimeCredential,
                                "The remote receive-session inventory is " +
                                "too large.",
                                stoppingToken));
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                    when (exception is HttpRequestException or
                          IOException or
                          JsonException or
                          InvalidDataException)
                {
                    logger.LogWarning(
                        exception,
                        "Remote receive-session inventory refresh failed");
                    partialErrors.Add(
                        "Remote receive-session details are temporarily " +
                        "unavailable.");
                }

                try
                {
                    stationCredentials =
                        RemoteStationCredentialInventoryParser.Parse(
                            await FetchAsync(
                                credentialsUri,
                                administrationCredential,
                                "The station credential inventory is too " +
                                "large.",
                                stoppingToken));
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                    when (exception is HttpRequestException or
                          IOException or
                          JsonException or
                          InvalidDataException)
                {
                    logger.LogWarning(
                        exception,
                        "Remote station credential refresh failed");
                    partialErrors.Add(
                        "Station security details are temporarily " +
                        "unavailable.");
                }

                RemoteStationAdministrationEntry[] stations =
                    catalog.Stations
                        .Select(station => station with
                        {
                            ReceiveSessions = receiveSessions
                                .Where(session => string.Equals(
                                    session.StationId,
                                    station.StationId,
                                    StringComparison.Ordinal))
                                .OrderBy(session => session.OpenedAt)
                                .ToArray()
                        })
                        .ToArray();
                Volatile.Write(
                    ref m_administration,
                    new RemoteStationAdministrationSnapshot(
                        true,
                        true,
                        DateTimeOffset.UtcNow,
                        partialErrors.Count == 0
                            ? null
                            : string.Join(" ", partialErrors),
                        stations,
                        stationCredentials));
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                      IOException or
                      JsonException or
                      InvalidDataException)
            {
                logger.LogWarning(
                    exception,
                    "Remote station inventory refresh failed");
                RemoteStationAdministrationSnapshot previous =
                    GetAdministrationSnapshot();
                Volatile.Write(
                    ref m_administration,
                    previous with
                    {
                        BrokerReachable = false,
                        Error =
                            "Remote station management is temporarily " +
                            "unavailable."
                    });
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }

    public async Task<RemoteStationEnrollmentCodeResult>
        CreateEnrollmentCodeAsync(
            string stationId,
            CancellationToken cancellationToken)
    {
        RemoteStationManagementValidator.ValidateStationId(stationId);
        return await PostAsync<
            CreateRemoteStationEnrollmentRequest,
            RemoteStationEnrollmentCodeResult>(
                "api/enrollment-codes",
                new CreateRemoteStationEnrollmentRequest(stationId),
                m_settings.AdministrationCredentialFile,
                cancellationToken);
    }

    public async Task<RemoteStationCredentialAdministrationEntry>
        SetCredentialStateAsync(
            string stationId,
            string action,
            CancellationToken cancellationToken)
    {
        RemoteStationManagementValidator.ValidateStationId(stationId);
        if (action is not ("enable" or "disable" or "revoke"))
        {
            throw new ArgumentException(
                "The station credential action is invalid.",
                nameof(action));
        }
        return await PostAsync<
            object,
            RemoteStationCredentialAdministrationEntry>(
                $"api/station-credentials/" +
                $"{Uri.EscapeDataString(stationId)}/{action}",
                new { },
                m_settings.AdministrationCredentialFile,
                cancellationToken);
    }

    public async Task<RemoteReleaseServiceControlResult>
        ControlReleaseServiceAsync(
            RemoteReleaseServiceControlRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RemoteStationManagementValidator.ValidateStationId(request.StationId);
        RemoteStationManagementValidator.ValidateReleaseServiceControl(request);
        return await PostAsync<
            RemoteReleaseServiceControlRequest,
            RemoteReleaseServiceControlResult>(
                "api/release-service-control",
                request,
                m_settings.AdministrationCredentialFile,
                cancellationToken);
    }

    public async Task<RemoteReleaseUpdateResult> UpdateReleaseAsync(
        RemoteReleaseUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RemoteStationManagementValidator.ValidateReleaseUpdate(request);
        return await PostAsync<
            RemoteReleaseUpdateRequest,
            RemoteReleaseUpdateResult>(
                "api/release-updates",
                request,
                m_settings.AdministrationCredentialFile,
                cancellationToken);
    }

    public async Task<RemoteStationEnrollmentResult> RedeemEnrollmentAsync(
        RedeemRemoteStationEnrollmentRequest request,
        CancellationToken cancellationToken)
    {
        RemoteStationManagementValidator.ValidateEnrollmentRequest(request);
        return await PostAsync<
            RedeemRemoteStationEnrollmentRequest,
            RemoteStationEnrollmentResult>(
                "api/enrollments/redeem",
                request,
                credentialFile: null,
                cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string relativePath,
        TRequest body,
        string? credentialFile,
        CancellationToken cancellationToken)
    {
        if (!m_settings.Enabled)
        {
            throw new RemoteStationManagementException(
                HttpStatusCode.ServiceUnavailable,
                "Remote station support is not enabled.");
        }

        Uri resourceUri = new(
            RemoteStationSettingsValidator.GetBrokerBaseUri(m_settings),
            relativePath);
        using HttpRequestMessage request = new(HttpMethod.Post, resourceUri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        if (credentialFile is not null)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    RemoteStationSettingsValidator.ReadCredential(
                        credentialFile,
                        "administration"));
        }

        HttpClient client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        byte[] payload = await ReadBoundedAsync(
            response.Content,
            "The remote station management response is too large.",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            BrokerErrorResponse? failure = null;
            try
            {
                failure = JsonSerializer.Deserialize<BrokerErrorResponse>(
                    payload,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                // The gateway emits a fixed safe message for malformed errors.
            }
            throw new RemoteStationManagementException(
                response.StatusCode,
                RemoteStationManagementValidator.SafeBrokerError(
                    failure?.Error));
        }

        TResponse? result = JsonSerializer.Deserialize<TResponse>(
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (result is null)
        {
            throw new InvalidDataException(
                "The remote station management response is invalid.");
        }
        RemoteStationManagementValidator.ValidateResponse(result);
        return result;
    }

    private async Task<byte[]> FetchAsync(
        Uri resourceUri,
        string credential,
        string oversizedMessage,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            resourceUri);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);

        HttpClient client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length &&
            length > MaximumResponseBytes)
        {
            throw new InvalidDataException(
                oversizedMessage);
        }

        return await ReadBoundedAsync(
            response.Content,
            oversizedMessage,
            cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        string oversizedMessage,
        CancellationToken cancellationToken)
    {
        await using Stream stream =
            await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(
                chunk,
                cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }
            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException(
                    oversizedMessage);
            }
            buffer.Write(chunk, 0, read);
        }
    }

    private sealed record BrokerErrorResponse(string? Error, string? Code);
}

internal static partial class RemoteStationManagementValidator
{
    public static void ValidateStationId(string? stationId)
    {
        if (!IsStationId(stationId))
        {
            throw new ArgumentException(
                "Use 1-64 letters, numbers, periods, underscores, colons, " +
                "or hyphens for the station ID.",
                nameof(stationId));
        }
    }

    public static void ValidateEnrollmentRequest(
        RedeemRemoteStationEnrollmentRequest? request)
    {
        if (request is null ||
            !IsHexSecret(request.EnrollmentCode) ||
            !IsHexSecret(request.CredentialSha256))
        {
            throw new ArgumentException(
                "The station enrollment request is invalid.",
                nameof(request));
        }
    }

    public static string SafeBrokerError(string? message)
    {
        return message is { Length: > 0 and <= 256 } &&
               !message.Any(char.IsControl)
            ? message
            : "The remote station request was rejected.";
    }

    public static void ValidateResponse<TResponse>(TResponse response)
    {
        switch (response)
        {
            case RemoteStationEnrollmentCodeResult code:
                if (!IsStationId(code.StationId) ||
                    !IsHexSecret(code.EnrollmentCode) ||
                    code.Purpose is not ("enroll" or "rotate" or "reenroll") ||
                    code.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    throw new InvalidDataException(
                        "The station enrollment code response is invalid.");
                }
                break;
            case RemoteStationCredentialAdministrationEntry credential:
                ValidateCredential(credential);
                break;
            case RemoteStationEnrollmentResult result:
                if (!IsStationId(result.StationId) ||
                    result.State != "enabled" ||
                    result.Purpose is not ("enroll" or "rotate" or "reenroll") ||
                    result.EnrolledAt < DateTimeOffset.UnixEpoch ||
                    result.RotatedAt < result.EnrolledAt)
                {
                    throw new InvalidDataException(
                        "The station enrollment response is invalid.");
                }
                break;
            case RemoteReleaseServiceControlResult result:
                ValidateReleaseServiceControlResult(result);
                break;
            case RemoteReleaseUpdateResult result:
                ValidateReleaseUpdateResult(result);
                break;
        }
    }

    public static void ValidateReleaseUpdate(
        RemoteReleaseUpdateRequest request)
    {
        if (!IsStationId(request.StationId) ||
            !IsCanonicalReleaseIdentity(request.ReleaseIdentity))
        {
            throw new ArgumentException(
                "The remote release-update request is invalid.",
                nameof(request));
        }
    }

    public static void ValidateReleaseUpdateResult(
        RemoteReleaseUpdateResult result)
    {
        ValidateReleaseUpdate(
            new RemoteReleaseUpdateRequest(
                result.StationId,
                result.ReleaseIdentity));
        if (result.CorrelationId is not { Length: 32 } ||
            !result.CorrelationId.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !IsIdentifier(result.Outcome, 64) ||
            !IsCanonicalReleaseIdentity(result.ActiveReleaseIdentity) ||
            result.Succeeded &&
                (!string.Equals(
                    result.ActiveReleaseIdentity,
                    result.ReleaseIdentity,
                    StringComparison.Ordinal) ||
                 result.RolledBack) ||
            result.RolledBack && result.Succeeded)
        {
            throw new InvalidDataException(
                "The remote release-update response is invalid.");
        }
    }

    public static void ValidateReleaseServiceControl(
        RemoteReleaseServiceControlRequest request)
    {
        if (!IsStationId(request.StationId) ||
            !IsCanonicalReleaseIdentity(request.ReleaseIdentity) ||
            request.Phase is not "pre-switch-stop" and
                not "post-switch-start" ||
            request.Action is not "stop" and not "start" ||
            request.ServiceRole is not "aetherremote-agent" and
                not "station-engine" ||
            request.Phase == "pre-switch-stop" && request.Action != "stop" ||
            request.Phase == "post-switch-start" && request.Action != "start" ||
            !IsExactRemoteUnit(request.ServiceRole, request.UnitIdentity))
        {
            throw new ArgumentException(
                "The remote release service-control request is invalid.",
                nameof(request));
        }
    }

    public static void ValidateReleaseServiceControlResult(
        RemoteReleaseServiceControlResult result)
    {
        ValidateReleaseServiceControl(
            new RemoteReleaseServiceControlRequest(
                result.StationId,
                result.ReleaseIdentity,
                result.Phase,
                result.Action,
                result.ServiceRole,
                result.UnitIdentity));
        if (result.CorrelationId is not { Length: 32 } ||
            !result.CorrelationId.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !IsIdentifier(result.Outcome, 64))
        {
            throw new InvalidDataException(
                "The remote release service-control response is invalid.");
        }
    }

    private static bool IsCanonicalReleaseIdentity(string value)
    {
        try
        {
            return string.Equals(
                InstallationReleaseIdentity.Parse(value),
                value,
                StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsExactRemoteUnit(string role, string unit) =>
        role switch
        {
            "aetherremote-agent" => string.Equals(
                unit,
                "aetherremote-agent.service",
                StringComparison.Ordinal),
            "station-engine" => string.Equals(
                unit,
                "aetherremote-station-engine.service",
                StringComparison.Ordinal),
            _ => false
        };

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        IdentifierPattern().IsMatch(value);

    public static void ValidateCredential(
        RemoteStationCredentialAdministrationEntry credential)
    {
        if (!IsStationId(credential.StationId) ||
            credential.State is not ("enabled" or "disabled" or "revoked") ||
            credential.Source is not ("imported" or "enrolled") ||
            credential.EnrolledAt < DateTimeOffset.UnixEpoch ||
            credential.UpdatedAt < credential.EnrolledAt ||
            credential.RotatedAt < credential.EnrolledAt)
        {
            throw new InvalidDataException(
                "The station credential inventory is invalid.");
        }
    }

    public static bool IsHexSecret(string? value) =>
        value is { Length: 64 } &&
        value.All(Uri.IsHexDigit);

    private static bool IsStationId(string? stationId) =>
        stationId is { Length: >= 1 and <= 64 } &&
        IdentifierPattern().IsMatch(stationId);

    [GeneratedRegex(
        "^[A-Za-z0-9][A-Za-z0-9._:-]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}

internal static class RemoteStationSettingsValidator
{
    public static Uri GetBrokerBaseUri(RemoteStationSettings settings)
    {
        if (!Uri.TryCreate(
                settings.BrokerUrl,
                UriKind.Absolute,
                out Uri? brokerUri) ||
            !string.IsNullOrEmpty(brokerUri.UserInfo) ||
            !string.IsNullOrEmpty(brokerUri.Query) ||
            !string.IsNullOrEmpty(brokerUri.Fragment))
        {
            throw new InvalidOperationException(
                "RemoteStations:BrokerUrl is invalid.");
        }
        bool isHttps = string.Equals(
            brokerUri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
        bool isLoopbackHttp =
            string.Equals(
                brokerUri.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(
                 brokerUri.Host,
                 "localhost",
                 StringComparison.OrdinalIgnoreCase) ||
             IPAddress.TryParse(
                 brokerUri.Host,
                 out IPAddress? address) &&
             IPAddress.IsLoopback(address));
        if (!isHttps && !isLoopbackHttp)
        {
            throw new InvalidOperationException(
                "Remote station inventory must use HTTPS or loopback HTTP.");
        }
        if (settings.RefreshSeconds is < 2 or > 60)
        {
            throw new InvalidOperationException(
                "RemoteStations:RefreshSeconds must be between 2 and 60.");
        }
        if (string.IsNullOrWhiteSpace(settings.RuntimeCredentialFile))
        {
            throw new InvalidOperationException(
                "RemoteStations:RuntimeCredentialFile is required.");
        }
        if (string.IsNullOrWhiteSpace(
                settings.AdministrationCredentialFile))
        {
            throw new InvalidOperationException(
                "RemoteStations:AdministrationCredentialFile is required.");
        }
        if (string.Equals(
                Path.GetFullPath(settings.RuntimeCredentialFile),
                Path.GetFullPath(settings.AdministrationCredentialFile),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Remote station runtime and administration credential files " +
                "must be distinct.");
        }

        Uri baseUri = brokerUri.AbsoluteUri.EndsWith(
            "/",
            StringComparison.Ordinal)
            ? brokerUri
            : new Uri($"{brokerUri.AbsoluteUri}/", UriKind.Absolute);
        return baseUri;
    }

    public static string ReadCredential(string path, string purpose)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"The remote station {purpose} credential does not exist.",
                fullPath);
        }
        string credential = File.ReadAllText(fullPath).Trim();
        if (credential.Length is < 32 or > 512 ||
            credential.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"The remote station {purpose} credential is invalid.");
        }
        return credential;
    }

    public static bool CredentialsMatch(string first, string second)
    {
        byte[] firstDigest = SHA256.HashData(Encoding.UTF8.GetBytes(first));
        byte[] secondDigest = SHA256.HashData(Encoding.UTF8.GetBytes(second));
        return CryptographicOperations.FixedTimeEquals(
            firstDigest,
            secondDigest);
    }
}

public static partial class RemoteStationCatalogParser
{
    private const int MaximumStations = 128;
    private const int MaximumRadiosPerStation = 32;

    public static IReadOnlyList<RemoteRadioCatalogEntry> Parse(
        ReadOnlySpan<byte> payload) =>
        ParseSnapshot(payload).Radios;

    public static RemoteStationCatalogSnapshot ParseSnapshot(
        ReadOnlySpan<byte> payload)
    {
        RemoteStationInventoryResponse? response =
            JsonSerializer.Deserialize<RemoteStationInventoryResponse>(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (response?.Stations is null ||
            response.Stations.Count > MaximumStations)
        {
            throw new InvalidDataException(
                "The remote station inventory has an invalid station count.");
        }

        List<RemoteRadioCatalogEntry> radios = [];
        List<RemoteStationAdministrationEntry> stations = [];
        HashSet<string> selectorIds =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (RemoteStationInventory? station in response.Stations)
        {
            if (station is null)
            {
                throw new InvalidDataException(
                    "The remote station inventory contains a null station.");
            }
            ValidateStation(station);
            List<RemoteStationRadioAdministrationEntry>
                administrationRadios = [];
            foreach (RemoteStationRadio? radio in station.Radios!)
            {
                if (radio is null)
                {
                    throw new InvalidDataException(
                        "The remote station inventory contains a null radio.");
                }
                ValidateRadio(station.StationId, radio);
                string selectorId =
                    $"remote:{station.StationId}:{radio.RadioId}";
                if (selectorId.Length > 128 ||
                    !selectorIds.Add(selectorId))
                {
                    throw new InvalidDataException(
                        "The remote station inventory contains a duplicate " +
                        "or oversized radio selector.");
                }
                radios.Add(
                    new RemoteRadioCatalogEntry(
                        selectorId,
                        station.StationId!,
                        radio.RadioId!,
                        radio.Model!,
                        radio.Serial!,
                        radio.Nickname!,
                        radio.Status!,
                        string.Equals(
                            station.State,
                            "online",
                            StringComparison.Ordinal),
                        station.Capabilities!.Contains(
                            "receive-projection-v1",
                            StringComparer.Ordinal),
                        radio.AvailableClients,
                        radio.LicensedClients,
                        station.LastSeen));
                administrationRadios.Add(
                    new RemoteStationRadioAdministrationEntry(
                        radio.RadioId!,
                        radio.Model!,
                        radio.Serial!,
                        radio.Nickname!,
                        radio.Status!,
                        radio.AvailableClients,
                        radio.LicensedClients));
            }
            stations.Add(
                new RemoteStationAdministrationEntry(
                    station.StationId!,
                    station.InstanceId!,
                    station.State!,
                    station.SoftwareVersion!,
                    station.ConnectedAt,
                    station.LastSeen,
                    station.HeartbeatSequence,
                    station.InventorySequence,
                    station.ConnectionCount,
                    station.LastDisconnectedAt,
                    station.LastDisconnectReason,
                    station.LastRecoveredAt,
                    station.LastRecoveryMilliseconds,
                    station.Capabilities!.ToArray(),
                    administrationRadios,
                    [],
                    station.ReleaseIdentity ?? string.Empty,
                    station.StationEngineVersion ?? string.Empty));
        }
        return new RemoteStationCatalogSnapshot(radios, stations);
    }

    private static void ValidateStation(RemoteStationInventory station)
    {
        if (!IsIdentifier(station.StationId, 64) ||
            !IsIdentifier(station.InstanceId, 64) ||
            station.State is not ("online" or "degraded" or "offline") ||
            !IsText(station.SoftwareVersion, 64) ||
            !ValidReleaseMetadata(station) ||
            station.Radios is null ||
            station.Radios.Count > MaximumRadiosPerStation ||
            station.ConnectedAt < DateTimeOffset.UnixEpoch ||
            station.LastSeen < DateTimeOffset.UnixEpoch ||
            station.HeartbeatSequence < 0 ||
            station.InventorySequence < 0 ||
            station.ConnectionCount is < 1 or > 1_000_000 ||
            !ValidRecoveryTelemetry(station) ||
            station.Capabilities is null ||
            station.Capabilities.Count > 16 ||
            station.Capabilities.Any(
                capability => !IsIdentifier(capability, 64)) ||
            station.Capabilities.Distinct(StringComparer.Ordinal).Count() !=
                station.Capabilities.Count)
        {
            throw new InvalidDataException(
                "The remote station inventory contains an invalid station.");
        }
    }

    private static bool ValidReleaseMetadata(
        RemoteStationInventory station)
    {
        bool releaseIdentityPresent =
            !string.IsNullOrEmpty(station.ReleaseIdentity);
        bool engineVersionPresent =
            !string.IsNullOrEmpty(station.StationEngineVersion);
        return releaseIdentityPresent == engineVersionPresent &&
            (!releaseIdentityPresent ||
             IsIdentifier(station.ReleaseIdentity, 96) &&
             IsText(station.StationEngineVersion, 64));
    }

    private static bool ValidRecoveryTelemetry(
        RemoteStationInventory station)
    {
        if (station.LastDisconnectedAt is null)
        {
            return station.LastDisconnectReason is null &&
                   station.LastRecoveredAt is null &&
                   station.LastRecoveryMilliseconds is null;
        }
        if (station.LastDisconnectedAt < DateTimeOffset.UnixEpoch ||
            !IsIdentifier(station.LastDisconnectReason, 64))
        {
            return false;
        }
        if (station.LastRecoveredAt is null)
        {
            return station.LastRecoveryMilliseconds is null;
        }
        return station.LastRecoveredAt >= station.LastDisconnectedAt &&
               station.LastRecoveryMilliseconds is >= 0 and <= 604_800_000;
    }

    private static void ValidateRadio(
        string? stationId,
        RemoteStationRadio radio)
    {
        if (!IsIdentifier(radio.RadioId, 128) ||
            !IsText(radio.Model, 64) ||
            !IsText(radio.Serial, 64) ||
            radio.Nickname is null ||
            radio.Nickname.Length > 64 ||
            radio.Nickname.Any(char.IsControl) ||
            radio.Status is not (
                "available" or "in-use" or "updating" or "unknown") ||
            radio.AvailableClients is < -1 or > 64 ||
            radio.LicensedClients is < -1 or > 64 ||
            radio.AvailableClients >= 0 &&
            radio.LicensedClients >= 0 &&
            radio.AvailableClients > radio.LicensedClients)
        {
            throw new InvalidDataException(
                $"Station '{stationId}' advertised an invalid radio.");
        }
    }

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is not null &&
        value.Length is > 0 &&
        value.Length <= maximumLength &&
        IdentifierPattern().IsMatch(value);

    private static bool IsText(string? value, int maximumLength) =>
        value is not null &&
        value.Length is > 0 &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    [GeneratedRegex(
        "^[A-Za-z0-9][A-Za-z0-9._:-]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    private sealed record RemoteStationInventoryResponse(
        IReadOnlyList<RemoteStationInventory?>? Stations);

    private sealed record RemoteStationInventory(
        string? StationId,
        string? InstanceId,
        string? State,
        string? SoftwareVersion,
        DateTimeOffset ConnectedAt,
        DateTimeOffset LastSeen,
        long HeartbeatSequence,
        long InventorySequence,
        IReadOnlyList<RemoteStationRadio?>? Radios,
        IReadOnlyList<string>? Capabilities = null,
        int ConnectionCount = 1,
        DateTimeOffset? LastDisconnectedAt = null,
        string? LastDisconnectReason = null,
        DateTimeOffset? LastRecoveredAt = null,
        long? LastRecoveryMilliseconds = null,
        string? ReleaseIdentity = "",
        string? StationEngineVersion = "");

    private sealed record RemoteStationRadio(
        string? RadioId,
        string? Model,
        string? Serial,
        string? Nickname,
        string? Status,
        int AvailableClients,
        int LicensedClients);
}

public static partial class RemoteReceiveSessionInventoryParser
{
    private const int MaximumSessions = 256;

    public static IReadOnlyList<RemoteReceiveSessionAdministrationEntry> Parse(
        ReadOnlySpan<byte> payload)
    {
        RemoteReceiveSessionInventoryResponse? response =
            JsonSerializer.Deserialize<
                RemoteReceiveSessionInventoryResponse>(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (response?.Sessions is null ||
            response.Sessions.Count > MaximumSessions)
        {
            throw new InvalidDataException(
                "The remote receive-session inventory has an invalid count.");
        }

        List<RemoteReceiveSessionAdministrationEntry> sessions = [];
        HashSet<string> sessionIds = new(StringComparer.Ordinal);
        foreach (RemoteReceiveSessionInventory? session in response.Sessions)
        {
            if (session is null ||
                !IsSessionId(session.SessionId) ||
                !sessionIds.Add(session.SessionId!) ||
                !IsIdentifier(session.StationId, 64) ||
                !IsIdentifier(session.RadioId, 128) ||
                !Guid.TryParse(session.GuiClientId, out _) ||
                session.State is not "admitted" ||
                !IsText(session.RadioModel, 64) ||
                !IsText(session.Serial, 64) ||
                !IsClientHandle(session.ClientHandle) ||
                session.OpenedAt < DateTimeOffset.UnixEpoch)
            {
                throw new InvalidDataException(
                    "The remote receive-session inventory is invalid.");
            }

            sessions.Add(
                new RemoteReceiveSessionAdministrationEntry(
                    session.SessionId!,
                    session.StationId!,
                    session.RadioId!,
                    session.State,
                    session.RadioModel!,
                    session.Serial!,
                    session.ClientHandle!,
                    session.OpenedAt));
        }
        return sessions;
    }

    private static bool IsSessionId(string? value) =>
        value is { Length: 32 } &&
        value.All(Uri.IsHexDigit);

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is not null &&
        value.Length is > 0 &&
        value.Length <= maximumLength &&
        IdentifierPattern().IsMatch(value);

    private static bool IsText(string? value, int maximumLength) =>
        value is not null &&
        value.Length is > 0 &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool IsClientHandle(string? value) =>
        value is { Length: > 0 and <= 8 } &&
        uint.TryParse(
            value,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);

    [GeneratedRegex(
        "^[A-Za-z0-9][A-Za-z0-9._:-]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    private sealed record RemoteReceiveSessionInventoryResponse(
        IReadOnlyList<RemoteReceiveSessionInventory?>? Sessions);

    private sealed record RemoteReceiveSessionInventory(
        string? SessionId,
        string? StationId,
        string? RadioId,
        string? GuiClientId,
        string? State,
        string? RadioModel,
        string? Serial,
        string? ClientHandle,
        DateTimeOffset OpenedAt);
}

public static class RemoteStationCredentialInventoryParser
{
    private const int MaximumCredentials = 256;

    public static IReadOnlyList<RemoteStationCredentialAdministrationEntry>
        Parse(ReadOnlySpan<byte> payload)
    {
        RemoteStationCredentialInventoryResponse? response =
            JsonSerializer.Deserialize<
                RemoteStationCredentialInventoryResponse>(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (response?.Stations is null ||
            response.Stations.Count > MaximumCredentials)
        {
            throw new InvalidDataException(
                "The station credential inventory has an invalid count.");
        }

        List<RemoteStationCredentialAdministrationEntry> credentials = [];
        HashSet<string> stationIds = new(StringComparer.Ordinal);
        foreach (RemoteStationCredentialAdministrationEntry? credential in
                 response.Stations)
        {
            if (credential is null ||
                !stationIds.Add(credential.StationId))
            {
                throw new InvalidDataException(
                    "The station credential inventory contains duplicates.");
            }
            RemoteStationManagementValidator.ValidateCredential(credential);
            credentials.Add(credential);
        }
        return credentials
            .OrderBy(item => item.StationId, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record RemoteStationCredentialInventoryResponse(
        IReadOnlyList<RemoteStationCredentialAdministrationEntry?>? Stations);
}
