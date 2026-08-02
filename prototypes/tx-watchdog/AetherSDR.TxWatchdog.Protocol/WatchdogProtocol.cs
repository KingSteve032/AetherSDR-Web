using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherSDR.TxWatchdog.Protocol;

public enum WatchdogRequestKind
{
    Status,
    Register,
    Heartbeat,
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
    WatchdogIdentity? Identity);

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
    DateTimeOffset? LastObservedAt);

public sealed record WatchdogResponse(
    int ProtocolVersion,
    string RequestId,
    bool Ok,
    string? Error,
    WatchdogSnapshot Snapshot);

public static class WatchdogProtocol
{
    public const int Version = 1;
    public const int MaximumMessageCharacters = 4096;
    public const int MaximumRequestIdLength = 64;
    public const int MaximumIdentifierLength = 128;

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
            "identity"
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
            "lastObservedAt"
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

            if (kind == WatchdogRequestKind.Status)
            {
                if (hasSequence || hasIdentity)
                {
                    error = "status-must-not-carry-authority";
                    return false;
                }
                request = new WatchdogRequest(
                    Version,
                    requestId,
                    kind,
                    Sequence: null,
                    Identity: null);
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

            request = new WatchdogRequest(
                Version,
                requestId,
                kind,
                sequence,
                identity);
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
            WatchdogRequestKind.Heartbeat => "heartbeat",
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
                identity = request.Identity
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
                out DateTimeOffset? lastObservedAt))
        {
            return false;
        }

        bool validTransportState = radioCommandTransportAvailable
            ? string.Equals(
                reason,
                "unkey-transport-ready-disarmed",
                StringComparison.Ordinal)
            : string.Equals(
                reason,
                "unkey-transport-disabled-disarmed",
                StringComparison.Ordinal);
        if (!string.Equals(state, "Disarmed", StringComparison.Ordinal) ||
            !validTransportState || armingAvailable ||
            (connected && !registered) || (leaseBound != registered))
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
            lastObservedAt);
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
            "heartbeat" => WatchdogRequestKind.Heartbeat,
            "disconnect" => WatchdogRequestKind.Disconnect,
            _ => default
        };
        return type is "status" or "register" or "heartbeat" or "disconnect";
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
