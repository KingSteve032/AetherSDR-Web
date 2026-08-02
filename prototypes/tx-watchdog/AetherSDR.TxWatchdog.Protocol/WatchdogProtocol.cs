using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherSDR.TxWatchdog.Protocol;

public enum WatchdogRequestKind
{
    Status,
    Register,
    Arm,
    Heartbeat,
    Disarm,
    Disconnect
}

public sealed record WatchdogIdentity(
    string RadioId,
    string SessionId,
    string BrowserClientId,
    string GatewayInstanceId,
    string EngineInstanceId,
    string ConnectionClientId,
    string LeaseId,
    uint StationClientHandle);

public sealed record WatchdogRequest(
    int ProtocolVersion,
    string RequestId,
    WatchdogRequestKind Kind,
    long? Sequence,
    WatchdogIdentity? Identity,
    int? HeartbeatTimeoutMilliseconds = null);

public sealed record WatchdogSnapshot(
    string HostInstanceId,
    DateTimeOffset StartedAt,
    string State,
    string Reason,
    bool RadioCommandTransportAvailable,
    bool ArmingAvailable,
    bool Registered,
    bool Connected,
    [property: JsonIgnore] WatchdogIdentity? Identity,
    bool LeaseBound,
    long LastSequence,
    string LastObservation,
    DateTimeOffset? LastObservedAt,
    bool Armed = false,
    DateTimeOffset? ArmedAt = null,
    DateTimeOffset? LastHeartbeatAt = null,
    DateTimeOffset? HeartbeatDeadlineAt = null,
    int? HeartbeatTimeoutMilliseconds = null,
    long UnkeyAttemptCount = 0,
    long UnkeyAcceptedCount = 0,
    long UnkeyRejectedCount = 0,
    long UnkeyUnknownCount = 0,
    string LastUnkeyOutcome = "none",
    string LastUnkeyReason = "none");

public sealed record WatchdogResponse(
    int ProtocolVersion,
    string RequestId,
    bool Ok,
    string? Error,
    WatchdogSnapshot Snapshot);

public static class WatchdogProtocol
{
    public const int Version = 2;
    public const int MaximumMessageCharacters = 4096;
    public const int MaximumRequestIdLength = 64;
    public const int MaximumIdentifierLength = 128;
    public const int MinimumHeartbeatTimeoutMilliseconds = 250;
    public const int MaximumHeartbeatTimeoutMilliseconds = 5000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly HashSet<string> RequestProperties =
        new(StringComparer.Ordinal)
        {
            "protocolVersion",
            "requestId",
            "type",
            "sequence",
            "identity",
            "heartbeatTimeoutMilliseconds"
        };

    private static readonly HashSet<string> IdentityProperties =
        new(StringComparer.Ordinal)
        {
            "radioId",
            "sessionId",
            "browserClientId",
            "gatewayInstanceId",
            "engineInstanceId",
            "connectionClientId",
            "leaseId",
            "stationClientHandle"
        };

    private static readonly HashSet<string> ResponseProperties =
        new(StringComparer.Ordinal)
        {
            "protocolVersion",
            "requestId",
            "ok",
            "error",
            "snapshot"
        };

    private static readonly HashSet<string> SnapshotProperties =
        new(StringComparer.Ordinal)
        {
            "hostInstanceId",
            "startedAt",
            "state",
            "reason",
            "radioCommandTransportAvailable",
            "armingAvailable",
            "registered",
            "connected",
            "leaseBound",
            "lastSequence",
            "lastObservation",
            "lastObservedAt",
            "armed",
            "armedAt",
            "lastHeartbeatAt",
            "heartbeatDeadlineAt",
            "heartbeatTimeoutMilliseconds",
            "unkeyAttemptCount",
            "unkeyAcceptedCount",
            "unkeyRejectedCount",
            "unkeyUnknownCount",
            "lastUnkeyOutcome",
            "lastUnkeyReason"
        };

    public static bool TryParseRequest(
        string json,
        out WatchdogRequest? request,
        out string error)
    {
        request = null;
        error = string.Empty;
        if (json.Length is 0)
        {
            error = "empty-message";
            return false;
        }
        if (json.Length > MaximumMessageCharacters)
        {
            error = "message-too-large";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasOnlyUniqueProperties(root, RequestProperties))
            {
                error = "invalid-request-shape";
                return false;
            }

            if (!TryReadInt32(root, "protocolVersion", out int version) ||
                version != Version)
            {
                error = "unsupported-protocol-version";
                return false;
            }
            if (!TryReadIdentifier(
                    root,
                    "requestId",
                    MaximumRequestIdLength,
                    normalizeUpper: false,
                    out string requestId))
            {
                error = "invalid-request-id";
                return false;
            }
            if (!TryReadString(root, "type", out string type) ||
                !TryParseKind(type, out WatchdogRequestKind kind))
            {
                error = "unknown-request-type";
                return false;
            }

            bool hasSequence = root.TryGetProperty(
                "sequence",
                out JsonElement sequenceElement);
            bool hasIdentity = root.TryGetProperty(
                "identity",
                out JsonElement identityElement);
            bool hasHeartbeatTimeout = root.TryGetProperty(
                "heartbeatTimeoutMilliseconds",
                out JsonElement heartbeatTimeoutElement);

            if (kind == WatchdogRequestKind.Status)
            {
                if (hasSequence || hasIdentity || hasHeartbeatTimeout)
                {
                    error = "status-must-not-carry-authority";
                    return false;
                }
                request = new WatchdogRequest(
                    Version,
                    requestId,
                    kind,
                    Sequence: null,
                    Identity: null,
                    HeartbeatTimeoutMilliseconds: null);
                return true;
            }

            if (!hasSequence ||
                sequenceElement.ValueKind != JsonValueKind.Number ||
                !sequenceElement.TryGetInt64(out long sequence) ||
                sequence <= 0)
            {
                error = "invalid-sequence";
                return false;
            }
            if (!hasIdentity ||
                !TryParseIdentity(identityElement, out WatchdogIdentity? identity))
            {
                error = "invalid-identity";
                return false;
            }

            int? heartbeatTimeoutMilliseconds = null;
            if (hasHeartbeatTimeout)
            {
                if (heartbeatTimeoutElement.ValueKind != JsonValueKind.Number ||
                    !heartbeatTimeoutElement.TryGetInt32(
                        out int parsedHeartbeatTimeout) ||
                    parsedHeartbeatTimeout < MinimumHeartbeatTimeoutMilliseconds ||
                    parsedHeartbeatTimeout > MaximumHeartbeatTimeoutMilliseconds)
                {
                    error = "invalid-heartbeat-timeout";
                    return false;
                }
                heartbeatTimeoutMilliseconds = parsedHeartbeatTimeout;
            }

            if (kind == WatchdogRequestKind.Arm &&
                heartbeatTimeoutMilliseconds is null)
            {
                error = "arm-requires-heartbeat-timeout";
                return false;
            }
            if (kind is WatchdogRequestKind.Register or
                WatchdogRequestKind.Disarm or WatchdogRequestKind.Disconnect &&
                heartbeatTimeoutMilliseconds is not null)
            {
                error = "unexpected-heartbeat-timeout";
                return false;
            }

            request = new WatchdogRequest(
                Version,
                requestId,
                kind,
                sequence,
                identity,
                heartbeatTimeoutMilliseconds);
            return true;
        }
        catch (JsonException)
        {
            error = "invalid-json";
            return false;
        }
    }

    public static string SerializeRequest(WatchdogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string type = request.Kind switch
        {
            WatchdogRequestKind.Status => "status",
            WatchdogRequestKind.Register => "register",
            WatchdogRequestKind.Arm => "arm",
            WatchdogRequestKind.Heartbeat => "heartbeat",
            WatchdogRequestKind.Disarm => "disarm",
            WatchdogRequestKind.Disconnect => "disconnect",
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                "A supported watchdog request kind is required.")
        };
        return JsonSerializer.Serialize(
            new
            {
                protocolVersion = request.ProtocolVersion,
                requestId = request.RequestId,
                type,
                sequence = request.Sequence,
                identity = request.Identity,
                heartbeatTimeoutMilliseconds =
                    request.HeartbeatTimeoutMilliseconds
            },
            SerializerOptions);
    }

    public static string SerializeResponse(WatchdogResponse response) =>
        JsonSerializer.Serialize(response, SerializerOptions);

    public static bool TryParseResponse(
        string json,
        out WatchdogResponse? response,
        out string error)
    {
        response = null;
        error = string.Empty;
        if (json.Length is 0)
        {
            error = "empty-response";
            return false;
        }
        if (json.Length > MaximumMessageCharacters)
        {
            error = "response-too-large";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasOnlyUniqueProperties(root, ResponseProperties) ||
                !TryReadInt32(root, "protocolVersion", out int version) ||
                version != Version ||
                !TryReadIdentifier(
                    root,
                    "requestId",
                    MaximumRequestIdLength,
                    normalizeUpper: false,
                    out string requestId) ||
                !TryReadBoolean(root, "ok", out bool ok) ||
                !TryReadNullableString(root, "error", out string? responseError) ||
                !root.TryGetProperty("snapshot", out JsonElement snapshotElement) ||
                !TryParseSnapshot(snapshotElement, out WatchdogSnapshot? snapshot))
            {
                error = "invalid-response-shape";
                return false;
            }

            if (ok && responseError is not null)
            {
                error = "success-response-carried-error";
                return false;
            }
            if (!ok && string.IsNullOrWhiteSpace(responseError))
            {
                error = "failure-response-missing-error";
                return false;
            }

            response = new WatchdogResponse(
                version,
                requestId,
                ok,
                responseError,
                snapshot!);
            return true;
        }
        catch (JsonException)
        {
            error = "invalid-json";
            return false;
        }
    }

    private static bool TryParseSnapshot(
        JsonElement element,
        out WatchdogSnapshot? snapshot)
    {
        snapshot = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !HasOnlyUniqueProperties(element, SnapshotProperties) ||
            !TryReadIdentifier(
                element,
                "hostInstanceId",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string hostInstanceId) ||
            !element.TryGetProperty("startedAt", out JsonElement startedAtElement) ||
            startedAtElement.ValueKind != JsonValueKind.String ||
            !startedAtElement.TryGetDateTimeOffset(out DateTimeOffset startedAt) ||
            !TryReadIdentifier(
                element,
                "state",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string state) ||
            !TryReadIdentifier(
                element,
                "reason",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string reason) ||
            !TryReadBoolean(
                element,
                "radioCommandTransportAvailable",
                out bool radioCommandTransportAvailable) ||
            !TryReadBoolean(element, "armingAvailable", out bool armingAvailable) ||
            !TryReadBoolean(element, "registered", out bool registered) ||
            !TryReadBoolean(element, "connected", out bool connected) ||
            !TryReadBoolean(element, "leaseBound", out bool leaseBound) ||
            !element.TryGetProperty("lastSequence", out JsonElement sequenceElement) ||
            sequenceElement.ValueKind != JsonValueKind.Number ||
            !sequenceElement.TryGetInt64(out long lastSequence) ||
            lastSequence < 0 ||
            !TryReadIdentifier(
                element,
                "lastObservation",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string lastObservation) ||
            !TryReadNullableDateTimeOffset(
                element,
                "lastObservedAt",
                out DateTimeOffset? lastObservedAt) ||
            !TryReadBoolean(element, "armed", out bool armed) ||
            !TryReadNullableDateTimeOffset(
                element,
                "armedAt",
                out DateTimeOffset? armedAt) ||
            !TryReadNullableDateTimeOffset(
                element,
                "lastHeartbeatAt",
                out DateTimeOffset? lastHeartbeatAt) ||
            !TryReadNullableDateTimeOffset(
                element,
                "heartbeatDeadlineAt",
                out DateTimeOffset? heartbeatDeadlineAt) ||
            !TryReadNullableInt32(
                element,
                "heartbeatTimeoutMilliseconds",
                out int? heartbeatTimeoutMilliseconds) ||
            !TryReadNonNegativeInt64(
                element,
                "unkeyAttemptCount",
                out long unkeyAttemptCount) ||
            !TryReadNonNegativeInt64(
                element,
                "unkeyAcceptedCount",
                out long unkeyAcceptedCount) ||
            !TryReadNonNegativeInt64(
                element,
                "unkeyRejectedCount",
                out long unkeyRejectedCount) ||
            !TryReadNonNegativeInt64(
                element,
                "unkeyUnknownCount",
                out long unkeyUnknownCount) ||
            !TryReadIdentifier(
                element,
                "lastUnkeyOutcome",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string lastUnkeyOutcome) ||
            !TryReadIdentifier(
                element,
                "lastUnkeyReason",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string lastUnkeyReason))
        {
            return false;
        }

        bool disarmed = string.Equals(
            state,
            "Disarmed",
            StringComparison.Ordinal);
        bool activeState = state is
            "Armed" or "Unkeying" or "ReconciliationRequired";
        bool validOutcome = lastUnkeyOutcome is
            "none" or "accepted" or "rejected" or "unknown";
        bool validCompletedOutcomes =
            unkeyAcceptedCount <= unkeyAttemptCount &&
            unkeyRejectedCount <=
                unkeyAttemptCount - unkeyAcceptedCount &&
            unkeyUnknownCount <=
                unkeyAttemptCount - unkeyAcceptedCount - unkeyRejectedCount;
        bool validActiveFields = !activeState ||
            (armed && registered && leaseBound &&
             armedAt is not null && lastHeartbeatAt is not null &&
             heartbeatDeadlineAt is not null &&
             heartbeatTimeoutMilliseconds is >=
                 MinimumHeartbeatTimeoutMilliseconds and <=
                 MaximumHeartbeatTimeoutMilliseconds);
        bool validDisarmedFields = !disarmed ||
            (!armed && armedAt is null && lastHeartbeatAt is null &&
             heartbeatDeadlineAt is null &&
             heartbeatTimeoutMilliseconds is null);

        if ((!disarmed && !activeState) ||
            (armingAvailable && !radioCommandTransportAvailable) ||
            (connected && !registered) ||
            leaseBound != registered ||
            !validOutcome ||
            !validCompletedOutcomes ||
            !validActiveFields ||
            !validDisarmedFields)
        {
            return false;
        }

        snapshot = new WatchdogSnapshot(
            hostInstanceId,
            startedAt,
            state,
            reason,
            radioCommandTransportAvailable,
            armingAvailable,
            registered,
            connected,
            Identity: null,
            leaseBound,
            lastSequence,
            lastObservation,
            lastObservedAt,
            armed,
            armedAt,
            lastHeartbeatAt,
            heartbeatDeadlineAt,
            heartbeatTimeoutMilliseconds,
            unkeyAttemptCount,
            unkeyAcceptedCount,
            unkeyRejectedCount,
            unkeyUnknownCount,
            lastUnkeyOutcome,
            lastUnkeyReason);
        return true;
    }

    private static bool TryParseIdentity(
        JsonElement element,
        out WatchdogIdentity? identity)
    {
        identity = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !HasOnlyUniqueProperties(element, IdentityProperties) ||
            !TryReadIdentifier(
                element,
                "radioId",
                MaximumIdentifierLength,
                normalizeUpper: true,
                out string radioId) ||
            !TryReadIdentifier(
                element,
                "sessionId",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string sessionId) ||
            !TryReadIdentifier(
                element,
                "browserClientId",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string browserClientId) ||
            !TryReadIdentifier(
                element,
                "gatewayInstanceId",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string gatewayInstanceId) ||
            !TryReadIdentifier(
                element,
                "engineInstanceId",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string engineInstanceId) ||
            !TryReadIdentifier(
                element,
                "connectionClientId",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string connectionClientId) ||
            !TryReadIdentifier(
                element,
                "leaseId",
                MaximumIdentifierLength,
                normalizeUpper: false,
                out string leaseId) ||
            !element.TryGetProperty(
                "stationClientHandle",
                out JsonElement handleElement) ||
            handleElement.ValueKind != JsonValueKind.Number ||
            !handleElement.TryGetUInt32(out uint stationClientHandle) ||
            stationClientHandle == 0)
        {
            return false;
        }

        identity = new WatchdogIdentity(
            radioId,
            sessionId,
            browserClientId,
            gatewayInstanceId,
            engineInstanceId,
            connectionClientId,
            leaseId,
            stationClientHandle);
        return true;
    }

    private static bool TryParseKind(
        string type,
        out WatchdogRequestKind kind)
    {
        kind = type switch
        {
            "status" => WatchdogRequestKind.Status,
            "register" => WatchdogRequestKind.Register,
            "arm" => WatchdogRequestKind.Arm,
            "heartbeat" => WatchdogRequestKind.Heartbeat,
            "disarm" => WatchdogRequestKind.Disarm,
            "disconnect" => WatchdogRequestKind.Disconnect,
            _ => default
        };
        return type is "status" or "register" or "arm" or "heartbeat" or
            "disarm" or "disconnect";
    }

    private static bool HasOnlyUniqueProperties(
        JsonElement element,
        IReadOnlySet<string> allowed)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryReadInt32(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static bool TryReadNonNegativeInt64(
        JsonElement element,
        string propertyName,
        out long value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value) &&
            value >= 0;
    }

    private static bool TryReadNullableInt32(
        JsonElement element,
        string propertyName,
        out int? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out int parsed))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool TryReadString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadBoolean(
        JsonElement element,
        string propertyName,
        out bool value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadNullableString(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return true;
        }
        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        string normalized = (property.GetString() ?? string.Empty).Trim();
        if (normalized.Length is 0 ||
            normalized.Length > MaximumIdentifierLength ||
            normalized.Any(char.IsControl))
        {
            return false;
        }
        value = normalized;
        return true;
    }

    private static bool TryReadNullableDateTimeOffset(
        JsonElement element,
        string propertyName,
        out DateTimeOffset? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.String ||
            !property.TryGetDateTimeOffset(out DateTimeOffset parsed))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool TryReadIdentifier(
        JsonElement element,
        string propertyName,
        int maximumLength,
        bool normalizeUpper,
        out string value)
    {
        value = string.Empty;
        if (!TryReadString(element, propertyName, out string raw))
        {
            return false;
        }
        string normalized = raw.Trim();
        if (normalized.Length is 0 ||
            normalized.Length > maximumLength ||
            normalized.Any(char.IsControl))
        {
            return false;
        }
        value = normalizeUpper ? normalized.ToUpperInvariant() : normalized;
        return true;
    }
}
